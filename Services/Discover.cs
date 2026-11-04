using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaxMediaManager.Services;

/// <summary>One suggested title, why it was suggested, and how strongly.</summary>
public sealed class Suggestion
{
    public required TmdbMatch Match { get; init; }
    public bool IsAnime { get; init; }
    public double Rating { get; init; }      // out of 10 (Audible doubled)
    public int Votes { get; init; }
    public double Score { get; set; }
    public List<string> Because { get; } = new();
    /// <summary>True when <see cref="Because"/> lists owned titles that point here (else it holds ready-made reasons).</summary>
    public bool FromLibrary { get; init; }

    /// <summary>One line on why this is suggested.</summary>
    public string Reason => Because.Count == 0 ? "" : FromLibrary ? "Because you have " + string.Join(", ", Because.Take(2)) : Because[0];
    public string? BackdropPath { get; init; }

    public LibraryKind Kind => Match.Kind;
    public string Key => $"{Match.Kind}:{Match.Key}";

    /// <summary>The title's page on the site it came from.</summary>
    public string Url => Match.Kind switch
    {
        LibraryKind.Game => $"https://store.steampowered.com/app/{Match.Id}",
        LibraryKind.Audiobook => $"https://www.audible.com/pd/{Match.ExternalId}",
        LibraryKind.Tv => $"https://www.themoviedb.org/tv/{Match.Id}",
        _ => $"https://www.themoviedb.org/movie/{Match.Id}",
    };

    public string SiteName => Match.Kind switch { LibraryKind.Game => "Steam", LibraryKind.Audiobook => "Audible", _ => "TMDB" };
}

/// <summary>A row on the Discover tab.</summary>
public sealed record DiscoverSection(string Title, string Subtitle, string Group, List<Suggestion> Items);

/// <summary>
/// Builds the Discover tab from what's in the library and what has been watched: TMDB recommendations pooled across
/// every owned film and show (a title many of your things point to ranks highest), "because you watched" rows, trending
/// and top anime; Steam games that fit the genres you own most; and more audiobooks by your authors and in your series.
/// Everything owned or marked "not interested" is left out. All lookups are cached on disk.
/// </summary>
public sealed class DiscoverService
{
    private readonly AppSettings _settings;
    private readonly MetadataService _meta;

    public DiscoverService(AppSettings settings, MetadataService meta)
    {
        _settings = settings;
        _meta = meta;
    }

    /// <summary>Progress text while building (raised on background threads).</summary>
    public event Action<string>? Status;

    // ---- Not interested -------------------------------------------------------------------------------------------

    private static string DismissedFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaxMediaManager", "discover-dismissed.json");

    private static HashSet<string>? _dismissed;

    public static HashSet<string> Dismissed
    {
        get
        {
            if (_dismissed is not null)
                return _dismissed;
            try
            {
                _dismissed = File.Exists(DismissedFile) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(DismissedFile)) ?? new() : new();
            }
            catch
            {
                _dismissed = new();
            }

            return _dismissed;
        }
    }

    public static void Dismiss(Suggestion s)
    {
        Dismissed.Add(s.Key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DismissedFile)!);
            File.WriteAllText(DismissedFile, JsonSerializer.Serialize(Dismissed));
        }
        catch
        {
            /* best effort */
        }
    }

    public static void ClearDismissed()
    {
        Dismissed.Clear();
        try
        {
            File.Delete(DismissedFile);
        }
        catch
        {
            /* best effort */
        }
    }

    // ---- Library -------------------------------------------------------------------------------------------------

    /// <summary>Every top-level item in the library categories, matched (from cache where possible) with details.</summary>
    private async Task<List<LibraryItem>> LoadLibraryAsync(CancellationToken ct)
    {
        var items = new List<LibraryItem>();
        foreach (var c in _settings.ActiveCategories.Where(c => !c.ExcludeFromLibrary))
        {
            try
            {
                var local = NetworkPath.ToLocal(c.Path);
                if (local is null || !Directory.Exists(local))
                    continue;
                var found = await Task.Run(() => LibraryScanner.List(local, Views.LibraryView.KindFor(c.Name), ct), ct).ConfigureAwait(false);
                foreach (var i in found)
                {
                    i.Category = c.Name;
                    i.IsAnime = c.Name.Contains("anime", StringComparison.OrdinalIgnoreCase);
                }

                items.AddRange(found);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                /* unreachable category: skip */
            }
        }

        var done = 0;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                item.Match = LibraryMatches.Get(item) ?? (_meta.CanMatch(item) ? await _meta.FindAsync(item, ct).ConfigureAwait(false) : null);
                if (item.Match is { } m && m.Kind is LibraryKind.Game or LibraryKind.Audiobook)
                    item.Details = await _meta.GetDetailsAsync(m, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                /* skip */
            }
            finally
            {
                gate.Release();
                var n = Interlocked.Increment(ref done);
                if (n % 25 == 0)
                    Status?.Invoke($"Reading your library ({n}/{items.Count})…");
            }
        })).ConfigureAwait(false);
        return items;
    }

    private static string Norm(string s) => Regex.Replace(s.ToLowerInvariant().Replace("&", "and"), "[^a-z0-9]+", "");

    // ---- Building --------------------------------------------------------------------------------------------------

    public async Task<List<DiscoverSection>> BuildAsync(CancellationToken ct)
    {
        Status?.Invoke("Reading your library…");
        var library = await LoadLibraryAsync(ct).ConfigureAwait(false);
        var ownedKeys = library.Where(i => i.Match is not null).Select(i => $"{i.Match!.Kind}:{i.Match.Key}").ToHashSet();
        var ownedTitles = library.Select(i => Norm(i.Match?.Title ?? i.Title)).ToHashSet();
        bool Owned(TmdbMatch m) => ownedKeys.Contains($"{m.Kind}:{m.Key}") || ownedTitles.Contains(Norm(m.Title));
        bool Skip(TmdbMatch m) => Owned(m) || Dismissed.Contains($"{m.Kind}:{m.Key}");

        var sections = new List<DiscoverSection>();
        if (_meta.Tmdb is { } tmdb)
            sections.AddRange(await BuildScreenAsync(tmdb, library, Skip, ct).ConfigureAwait(false));
        sections.AddRange(await BuildGamesAsync(library, Skip, ct).ConfigureAwait(false));
        sections.AddRange(await BuildBooksAsync(library, Skip, ct).ConfigureAwait(false));
        Status?.Invoke("");
        return sections.Where(s => s.Items.Count > 0).ToList();
    }

    private async Task<List<DiscoverSection>> BuildScreenAsync(TmdbClient tmdb, List<LibraryItem> library, Func<TmdbMatch, bool> skip, CancellationToken ct)
    {
        var sections = new List<DiscoverSection>();
        var sources = library.Where(i => i.Match is { Kind: LibraryKind.Movie or LibraryKind.Tv }).ToList();

        // Recommendations for every owned title (cached a week), a few at a time.
        var recs = new Dictionary<LibraryItem, List<TmdbRec>>();
        var done = 0;
        using (var gate = new SemaphoreSlim(4))
        {
            await Task.WhenAll(sources.Select(async item =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (await tmdb.GetRecommendationsAsync(item.Match!, ct).ConfigureAwait(false) is { } list)
                        lock (recs)
                            recs[item] = list;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    /* skip */
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref done);
                    if (n % 10 == 0)
                        Status?.Invoke($"Finding films and shows you'd like ({n}/{sources.Count})…");
                }
            })).ConfigureAwait(false);
        }

        // What's been watched counts for more, finished things most of all.
        double Weight(LibraryItem i)
        {
            var (_, last, finished) = WatchHistory.Progress(i);
            return 1 + (finished ? 1.5 : last is not null ? 0.75 : 0);
        }

        List<Suggestion> Pool(Func<TmdbRec, bool> keep, IEnumerable<LibraryItem> from)
        {
            var map = new Dictionary<string, (TmdbRec Rec, double Score, List<string> Because)>();
            foreach (var src in from)
            {
                if (!recs.TryGetValue(src, out var list))
                    continue;
                var w = Weight(src);
                for (var rank = 0; rank < list.Count; rank++)
                {
                    var r = list[rank];
                    if (!keep(r) || skip(r.Match) || r.Votes < 30)
                        continue;
                    var key = $"{r.Match.Kind}:{r.Match.Id}";
                    // Higher in a title's own list counts for a little more.
                    var add = w * (1.2 - rank / 40.0);
                    if (map.TryGetValue(key, out var e))
                    {
                        e.Because.Add(src.DisplayTitle);
                        map[key] = (e.Rec, e.Score + add, e.Because);
                    }
                    else
                    {
                        map[key] = (r, add, new List<string> { src.DisplayTitle });
                    }
                }
            }

            return map.Values
                .Select(v =>
                {
                    var s = new Suggestion
                    {
                        Match = v.Rec.Match, IsAnime = v.Rec.IsAnime, Rating = v.Rec.Rating, Votes = v.Rec.Votes, BackdropPath = v.Rec.BackdropPath, FromLibrary = true,
                        // Pooled weight, nudged by how well it's rated.
                        Score = v.Score * (0.6 + v.Rec.Rating / 25),
                    };
                    s.Because.AddRange(v.Because.Distinct().Take(3));
                    return s;
                })
                .OrderByDescending(s => s.Score).Take(30).ToList();
        }

        // Because you watched: the three things watched most recently.
        var recent = sources.Select(i => (i, p: WatchHistory.Progress(i))).Where(x => x.p.Last is not null)
            .OrderByDescending(x => x.p.Last).Take(3).Select(x => x.i).ToList();
        foreach (var item in recent)
        {
            var row = Pool(_ => true, new[] { item });
            sections.Add(new DiscoverSection($"Because you watched {item.DisplayTitle}", "Similar titles you don't have yet", "Watched", row));
        }

        var animeSources = sources.Where(i => i.IsAnime || (recs.TryGetValue(i, out var l) && l.Count(r => r.IsAnime) > l.Count / 2)).ToList();
        sections.Add(new DiscoverSection("Movies for you", "Recommended by the films and shows in your library", "Movies",
            Pool(r => r.Match.Kind == LibraryKind.Movie && !r.IsAnime, sources)));
        sections.Add(new DiscoverSection("TV shows for you", "Series that people who like your shows also watch", "TV",
            Pool(r => r.Match.Kind == LibraryKind.Tv && !r.IsAnime, sources)));

        var anime = Pool(r => r.IsAnime, sources);
        if (anime.Count < 10)
        {
            // Not much anime in the library to go on: top up with the best-rated anime on TMDB.
            foreach (var kind in new[] { LibraryKind.Tv, LibraryKind.Movie })
                foreach (var r in await tmdb.GetTopAnimeAsync(kind, ct).ConfigureAwait(false) ?? new())
                    if (!skip(r.Match) && anime.All(a => a.Match.Id != r.Match.Id || a.Kind != r.Match.Kind))
                    {
                        var s = new Suggestion { Match = r.Match, IsAnime = true, Rating = r.Rating, Votes = r.Votes, BackdropPath = r.BackdropPath, Score = r.Rating };
                        s.Because.Add("Among the highest-rated anime");
                        anime.Add(s);
                    }
        }

        sections.Add(new DiscoverSection("Anime for you", animeSources.Count > 0 ? "Based on the anime you have" : "Highly rated anime to start with", "Anime",
            anime.Take(30).ToList()));

        foreach (var (kind, title) in new[] { (LibraryKind.Movie, "Trending movies this week"), (LibraryKind.Tv, "Trending shows this week") })
        {
            var trending = (await tmdb.GetTrendingAsync(kind, ct).ConfigureAwait(false) ?? new())
                .Where(r => !skip(r.Match))
                .Select(r =>
                {
                    var s = new Suggestion { Match = r.Match, IsAnime = r.IsAnime, Rating = r.Rating, Votes = r.Votes, BackdropPath = r.BackdropPath, Score = r.Popularity };
                    s.Because.Add("Trending on TMDB");
                    return s;
                }).ToList();
            sections.Add(new DiscoverSection(title, "What everyone's watching right now", kind == LibraryKind.Tv ? "TV" : "Movies", trending));
        }

        return sections;
    }

    private async Task<List<DiscoverSection>> BuildGamesAsync(List<LibraryItem> library, Func<TmdbMatch, bool> skip, CancellationToken ct)
    {
        var owned = library.Where(i => i.Kind == LibraryKind.Game || i.Match?.Kind == LibraryKind.Game).ToList();
        Status?.Invoke("Looking through Steam…");
        var pool = await SteamClient.DiscoverPoolAsync(ct).ConfigureAwait(false);
        if (pool.Count == 0)
            return new();

        // The genres you own most, weighted by how many games carry them.
        var genreCounts = owned.SelectMany(i => i.Details?.Genres ?? Array.Empty<string>())
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var top = genreCounts.OrderByDescending(g => g.Value).Take(3).Select(g => g.Key).ToList();

        var candidates = pool.Where(p => !skip(p.Match)).Take(70).ToList();
        var scored = new List<Suggestion>();
        var newOnes = new List<Suggestion>();
        var done = 0;
        using (var gate = new SemaphoreSlim(3))
        {
            await Task.WhenAll(candidates.Select(async c =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var d = await SteamClient.GetDetailsAsync(c.Match, ct).ConfigureAwait(false);
                    if (d is null)
                        return;
                    // Steam's store lists include DLC and soundtracks: keep games with a real review count.
                    var steam = d.Scores?.FirstOrDefault(s => s.Source == "steam");
                    var reviews = steam?.Votes ?? 0;
                    var pct = steam?.Value ?? 0;
                    var overlap = d.Genres.Where(g => genreCounts.ContainsKey(g)).ToList();
                    var s = new Suggestion
                    {
                        Match = c.Match with { Overview = d.Tagline, Year = YearOf(d.ReleaseDate) },
                        Rating = pct / 10,
                        Votes = reviews,
                        BackdropPath = d.BackdropPath,
                        Score = overlap.Sum(g => genreCounts[g]) * (0.4 + pct / 100) + Math.Log10(reviews + 1),
                    };
                    if (overlap.Count > 0)
                        s.Because.Add("You have " + string.Join(", ", overlap.OrderByDescending(g => genreCounts[g]).Take(2)
                            .Select(g => $"{genreCounts[g]} {g.ToLowerInvariant()} games")));
                    s.Because.Add(c.From);
                    lock (scored)
                    {
                        // A brand-new game with five reviews can sit at 100%: only trust a real number of reviews.
                        if (reviews >= 200)
                            scored.Add(s);
                        if (c.From == "New on Steam" && reviews >= 20)
                            newOnes.Add(s);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    /* skip */
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref done);
                    if (n % 10 == 0)
                        Status?.Invoke($"Looking through Steam ({n}/{candidates.Count})…");
                }
            })).ConfigureAwait(false);
        }

        return new List<DiscoverSection>
        {
            new("Games for you", top.Count > 0 ? $"Popular on Steam now, picked for your favourite genres: {string.Join(", ", top)}" : "Popular on Steam right now",
                "Games", scored.OrderByDescending(s => s.Score).Take(30).ToList()),
            new("New on Steam", "Just released, best reviewed first", "Games",
                newOnes.OrderByDescending(s => s.Rating * Math.Log10(s.Votes + 1)).Take(20).ToList()),
        };
    }

    private async Task<List<DiscoverSection>> BuildBooksAsync(List<LibraryItem> library, Func<TmdbMatch, bool> skip, CancellationToken ct)
    {
        var books = library.Where(i => i.Match?.Kind == LibraryKind.Audiobook && i.Details is not null).ToList();
        if (books.Count == 0)
            return new();
        Status?.Invoke("Looking through Audible…");

        var byAuthor = new List<Suggestion>();
        var series = new List<Suggestion>();
        var seen = new HashSet<string>();
        try
        {
            // More by the authors you have most of.
            var authors = books.SelectMany(b => b.Details!.Directors.Take(1)).GroupBy(a => a).OrderByDescending(g => g.Count()).Take(6).Select(g => g.Key);
            foreach (var author in authors)
            {
                foreach (var m in await AudibleClient.SearchCachedAsync(author, ct).ConfigureAwait(false))
                {
                    if (skip(m) || !seen.Add(m.Key))
                        continue;
                    var d = await AudibleClient.GetDetailsAsync(m, ct).ConfigureAwait(false);
                    if (d is null || !d.Directors.Any(a => a.Equals(author, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var stars = d.Scores?.FirstOrDefault()?.Value ?? 0;
                    var s = new Suggestion { Match = m, Rating = stars * 2, Votes = d.Votes, Score = stars + Math.Log10(d.Votes + 1) };
                    s.Because.Add($"More by {author}");
                    byAuthor.Add(s);
                }
            }

            // The rest of a series you've started ("Book 2 of Dungeon Crawler Carl" → the other books).
            static (double Number, string Series)? SeriesOf(string tagline)
            {
                var m = Regex.Match(tagline, @"^Book ([\d.]+) of (.+)$");
                return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
                    ? (n, m.Groups[2].Value.Trim())
                    : null;
            }

            foreach (var b in books)
            {
                if (SeriesOf(b.Details!.Tagline) is not { } mine)
                    continue;
                foreach (var r in await AudibleClient.SearchCachedAsync(mine.Series, ct).ConfigureAwait(false))
                {
                    if (skip(r) || seen.Contains(r.Key))
                        continue;
                    var d = await AudibleClient.GetDetailsAsync(r, ct).ConfigureAwait(false);
                    // The very same series ("Legends of Dune" is not "Dune").
                    if (d is null || SeriesOf(d.Tagline) is not { } other || !other.Series.Equals(mine.Series, StringComparison.OrdinalIgnoreCase))
                        continue;
                    seen.Add(r.Key);
                    var stars = d.Scores?.FirstOrDefault()?.Value ?? 0;
                    // The next book first, then later ones, then any earlier ones you skipped.
                    var gap = other.Number - mine.Number;
                    var s = new Suggestion { Match = r, Rating = stars * 2, Votes = d.Votes, Score = gap > 0 ? 100 - gap : 10 + gap };
                    s.Because.Add(gap > 0 && gap <= 1 ? $"Next in {mine.Series} (book {other.Number:0.#}) — you have book {mine.Number:0.#}"
                        : $"Book {other.Number:0.#} of {mine.Series} — you have book {mine.Number:0.#}");
                    series.Add(s);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            /* Audible unreachable: show what was found */
        }

        return new List<DiscoverSection>
        {
            new("Continue your series", "Other books in series you've started", "Audiobooks", series.OrderByDescending(s => s.Score).Take(30).ToList()),
            new("More from authors you listen to", "Other audiobooks by your authors", "Audiobooks", byAuthor.OrderByDescending(s => s.Score).Take(30).ToList()),
        };
    }

    private static int? YearOf(string date)
    {
        var m = Regex.Match(date, @"(19|20)\d{2}");
        return m.Success ? int.Parse(m.Value) : null;
    }
}
