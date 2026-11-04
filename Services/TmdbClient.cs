using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PaxMediaManager.Services;

/// <summary>What kind of thing a library folder holds; decides which poster database (if any) to ask.</summary>
public enum LibraryKind
{
    /// <summary>Unknown: try movies first, then TV.</summary>
    Auto,
    Movie,
    Tv,
    /// <summary>Music, software and the like: nothing is looked up.</summary>
    None,
    /// <summary>Video games: looked up on Steam.</summary>
    Game,
    /// <summary>Audiobooks: looked up in Audible's catalogue.</summary>
    Audiobook,
}

/// <summary>
/// One matched title: enough to show a tile, a search result, and to fetch the poster. Despite the name it also holds
/// Steam games (Id = app id) and Audible books (<see cref="ExternalId"/> = ASIN, PosterPath = full image URL).
/// </summary>
public sealed record TmdbMatch(int Id, LibraryKind Kind, string Title, int? Year, string? PosterPath, string Overview, string? ExternalId = null)
{
    /// <summary>Stable key for caches: the TMDB/Steam id, or the ASIN for books.</summary>
    public string Key => ExternalId ?? Id.ToString();
}

/// <summary>The extra TMDB information shown in the details window (rating, runtime, genres, cast...).</summary>
public sealed record TmdbDetails(
    string Tagline,
    int? RuntimeMinutes,
    double Rating,
    int Votes,
    string[] Genres,
    string Status,
    string ReleaseDate,
    int? Seasons,
    int? Episodes,
    string[] Studios,
    string? BackdropPath,
    string Certification,
    string[] Directors,
    string[] Cast,
    string OriginalLanguage,
    // "" when TMDB has no IMDb ID.
    string? ImdbId = null,
    // TMDB person ids, parallel to Cast (for "other titles with this actor").
    int[]? CastIds = null,
    // YouTube key of the main trailer, "" if none.
    string? TrailerKey = null,
    // Bumped when fields are added; older cache entries are fetched again once.
    int Version = 0,
    // Scores the source itself supplies beyond Rating (Steam reviews, Metacritic for games, Audible stars...).
    RatingScore[]? Scores = null)
{
    public const int CurrentVersion = 2;
}

/// <summary>One episode of a TV season as TMDB lists it.</summary>
public sealed record TmdbEpisode(int Season, int Episode, string Name, string AirDate);

/// <summary>A title TMDB suggests (recommendations or trending), with what the Discover tab ranks by.</summary>
public sealed record TmdbRec(TmdbMatch Match, double Rating, int Votes, int[] GenreIds, string Language, double Popularity, string? BackdropPath)
{
    /// <summary>TMDB's genre id for Animation; with Japanese as the original language it marks anime.</summary>
    public bool IsAnime => GenreIds.Contains(16) && Language == "ja";
}

/// <summary>A title someone appeared in (for the cast window).</summary>
public sealed record TmdbCredit(int Id, LibraryKind Kind, string Title, int? Year, string Character, string? PosterPath, double Popularity);

/// <summary>
/// Looks up movie and TV titles and posters on TMDB (themoviedb.org). Accepts either kind of credential TMDB issues: the
/// short "API key" (sent as a query parameter) or the long "API Read Access Token" (sent as a bearer token).
/// Matches and posters are cached on disk under hashed names, so each title is only ever looked up once.
/// </summary>
public sealed class TmdbClient
{
    private const string DefaultApi = "https://api.themoviedb.org/3";
    private const string DefaultImages = "https://image.tmdb.org/t/p/w342";
    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = false };

    private readonly string _credential;
    private readonly bool _bearer;
    // Overridable so the client can be tested against a local stand-in server.
    private readonly string _api = Environment.GetEnvironmentVariable("TORRENTMONITOR_TMDB_API")?.TrimEnd('/') ?? DefaultApi;
    private readonly string _images = Environment.GetEnvironmentVariable("TORRENTMONITOR_TMDB_IMAGES")?.TrimEnd('/') ?? DefaultImages;

    public TmdbClient(string credential)
    {
        _credential = credential.Trim();
        _bearer = LooksLikeToken(_credential);
    }

    /// <summary>True once TMDB has answered "unauthorized"; no more requests are made after that.</summary>
    public bool Rejected { get; private set; }

    private static string AppDataDir =>
        AppPaths.DataDir;

    public static string PosterDir => Path.Combine(AppDataDir, "posters");
    public static string MatchDir => Path.Combine(AppDataDir, "matches");
    public static string SearchDir => Path.Combine(AppDataDir, "searches");
    public static string DetailsDir => Path.Combine(AppDataDir, "details");
    public static string SeasonDir => Path.Combine(AppDataDir, "seasons");
    public static string PeopleDir => Path.Combine(AppDataDir, "people");
    public static string RecsDir => Path.Combine(AppDataDir, "recommendations");

    /// <summary>Read Access Tokens are JWTs: long and starting "eyJ". API keys are 32 hex characters.</summary>
    public static bool LooksLikeToken(string credential) => credential.Length > 60 && credential.StartsWith("eyJ", StringComparison.Ordinal);

    /// <summary>Checks the credential against TMDB without looking anything up.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.SendAsync(BuildRequest("/configuration"), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (false, "TMDB rejected this key. Check that it was copied completely.");
            return response.IsSuccessStatusCode
                ? (true, _bearer ? "Valid — read access token accepted." : "Valid — API key accepted.")
                : (false, $"TMDB answered with an unexpected status ({(int)response.StatusCode}).");
        }
        catch (Exception ex)
        {
            return (false, "Couldn't reach TMDB: " + ex.Message);
        }
    }

    /// <summary>
    /// Best automatic match for a release-name title (with the year if known), cached. <paramref name="refresh"/>
    /// ignores the cache, for "rescan". Returns null when nothing matched or TMDB can't be reached right now.
    /// </summary>
    public async Task<TmdbMatch?> FindAsync(string title, int? year, LibraryKind kind, CancellationToken ct, bool refresh = false)
    {
        if (Rejected || kind is not (LibraryKind.Auto or LibraryKind.Movie or LibraryKind.Tv) || string.IsNullOrWhiteSpace(title))
            return null;

        var cacheFile = Path.Combine(MatchDir, Hash($"{kind}|{title.Trim().ToLowerInvariant()}|{year}") + ".json");
        if (!refresh && TryReadCache(cacheFile, out var cached))
            return cached;

        try
        {
            TmdbMatch? pick = null;
            var kinds = kind switch
            {
                LibraryKind.Movie => new[] { LibraryKind.Movie },
                LibraryKind.Tv => new[] { LibraryKind.Tv },
                _ => new[] { LibraryKind.Movie, LibraryKind.Tv },
            };

            foreach (var k in kinds)
            {
                var results = await SearchAsync(k, title, year, ct, refresh).ConfigureAwait(false);
                // The year in a release name is sometimes off by one or wrong for a series; fall back to a title-only search.
                if (results.Count == 0 && year is not null && !Rejected)
                    results = await SearchAsync(k, title, null, ct, refresh).ConfigureAwait(false);
                pick = results.FirstOrDefault(r => r.PosterPath is not null) ?? results.FirstOrDefault();
                if (pick is not null || Rejected)
                    break;
            }

            if (Rejected)
                return null;

            WriteCache(cacheFile, pick);
            return pick;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // offline or a hiccup: try again next time rather than remembering a miss
        }
    }

    /// <summary>Searches TMDB for a specific kind (used by the "fix match" window); returns up to a page of results.</summary>
    public async Task<List<TmdbMatch>> SearchAsync(LibraryKind kind, string query, int? year, CancellationToken ct, bool refresh = false)
    {
        if (Rejected || kind is not (LibraryKind.Auto or LibraryKind.Movie or LibraryKind.Tv))
            return new List<TmdbMatch>();
        if (kind == LibraryKind.Auto)
            return await SearchBothAsync(query, year, ct, refresh).ConfigureAwait(false);

        // Every search TMDB answered is kept on disk for good, so the same query is never sent twice.
        var file = Path.Combine(SearchDir, Hash($"{kind}|{query.Trim().ToLowerInvariant()}|{year}") + ".json");
        if (!refresh && TryReadSearchCache(file, out var cached))
            return cached;

        var fresh = await SearchRemoteAsync(kind, query, year, ct).ConfigureAwait(false);
        if (!Rejected)
            WriteSearchCache(file, fresh);
        return fresh;
    }

    /// <summary>Asks TMDB directly. Throws on an error reply so a failure is never mistaken for "no results" and cached.</summary>
    private async Task<List<TmdbMatch>> SearchRemoteAsync(LibraryKind kind, string query, int? year, CancellationToken ct)
    {
        var results = new List<TmdbMatch>();

        var path = kind == LibraryKind.Tv ? "/search/tv" : "/search/movie";
        var q = new List<(string, string)> { ("query", query), ("include_adult", "false") };
        if (year is { } y)
            q.Add((kind == LibraryKind.Tv ? "first_air_date_year" : "year", y.ToString()));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await Http.SendAsync(BuildRequest(path, q.ToArray()), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Rejected = true;
                return results;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, ct).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"TMDB answered {(int)response.StatusCode}");

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
                .ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var r in arr.EnumerateArray())
            {
                if (!r.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
                    continue;
                var title = Str(r, kind == LibraryKind.Tv ? "name" : "title");
                if (title.Length == 0)
                    continue;
                var date = Str(r, kind == LibraryKind.Tv ? "first_air_date" : "release_date");
                int? yr = date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out var parsed) ? parsed : null;
                var poster = Str(r, "poster_path");
                results.Add(new TmdbMatch(id, kind, title, yr, poster.Length > 0 ? poster : null, Str(r, "overview")));
                if (results.Count >= 15)
                    break;
            }

            return results;
        }

        return results;
    }

    private async Task<List<TmdbMatch>> SearchBothAsync(string query, int? year, CancellationToken ct, bool refresh)
    {
        var movies = await SearchAsync(LibraryKind.Movie, query, year, ct, refresh).ConfigureAwait(false);
        var shows = await SearchAsync(LibraryKind.Tv, query, year, ct, refresh).ConfigureAwait(false);
        return movies.Concat(shows).ToList();
    }

    /// <summary>Local file for a TMDB poster path, downloading it first if needed; null if it can't be fetched.</summary>
    public async Task<string?> GetPosterFileAsync(string? posterPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(posterPath))
            return null;

        // Steam / Audible covers are full URLs (with "|"-separated fallbacks); TMDB ones are paths on its image host.
        if (posterPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return await GetImageFileAsync(posterPath, ct).ConfigureAwait(false);

        var file = Path.Combine(PosterDir, Hash(posterPath) + ".jpg");
        if (File.Exists(file))
            return file;

        try
        {
            var bytes = await Http.GetByteArrayAsync(_images + posterPath, ct).ConfigureAwait(false);
            Directory.CreateDirectory(PosterDir);
            await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
            return file;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads an image from a full URL (trying each of several "|"-separated URLs in turn) into the poster cache,
    /// once. Used for game and audiobook covers, which need no TMDB key.
    /// </summary>
    public static async Task<string?> GetImageFileAsync(string urls, CancellationToken ct)
    {
        var file = Path.Combine(PosterDir, Hash(urls) + ".jpg");
        if (File.Exists(file))
            return file;
        if (File.Exists(file + ".none") && DateTime.UtcNow - File.GetLastWriteTimeUtc(file + ".none") < TimeSpan.FromDays(14))
            return null;

        var anyAnswer = false;
        foreach (var url in urls.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
                anyAnswer = true;
                if (!response.IsSuccessStatusCode)
                    continue;
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                Directory.CreateDirectory(PosterDir);
                await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
                return file;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                /* offline or that URL failed: try the next */
            }
        }

        // Every URL answered "no image": remember for a while so the tiles don't keep asking.
        if (anyAnswer)
        {
            try
            {
                Directory.CreateDirectory(PosterDir);
                File.WriteAllText(file + ".none", "");
            }
            catch
            {
                /* best effort */
            }
        }

        return null;
    }

    // ---- Full details (for the details window) ---------------------------------------------------------------------

    /// <summary>
    /// Everything TMDB knows that the details window shows. Fetched once per title and kept on disk for good;
    /// null if TMDB can't be reached (nothing is cached then, so it is retried next time).
    /// </summary>
    public async Task<TmdbDetails?> GetDetailsAsync(TmdbMatch match, CancellationToken ct, bool refresh = false)
    {
        if (Rejected || match.Kind is not (LibraryKind.Movie or LibraryKind.Tv))
            return null;

        var file = Path.Combine(DetailsDir, Hash($"{match.Kind}|{match.Id}") + ".json");
        if (!refresh)
        {
            try
            {
                if (File.Exists(file) && JsonSerializer.Deserialize<TmdbDetails>(File.ReadAllText(file)) is { Version: >= TmdbDetails.CurrentVersion } cached)
                    return cached;
            }
            catch
            {
                /* unreadable cache entry: fetch again */
            }
        }

        try
        {
            var tv = match.Kind == LibraryKind.Tv;
            var append = tv ? "credits,content_ratings,external_ids,videos" : "credits,release_dates,videos";
            using var response = await Http.SendAsync(BuildRequest($"/{(tv ? "tv" : "movie")}/{match.Id}", ("append_to_response", append)), ct)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Rejected = true;
                return null;
            }

            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
                .ConfigureAwait(false);
            var root = doc.RootElement;

            int? runtime = null;
            if (tv)
            {
                if (root.TryGetProperty("episode_run_time", out var ert) && ert.ValueKind == JsonValueKind.Array && ert.GetArrayLength() > 0
                    && ert[0].TryGetInt32(out var er) && er > 0)
                    runtime = er;
            }
            else if (root.TryGetProperty("runtime", out var rt) && rt.TryGetInt32(out var r) && r > 0)
            {
                runtime = r;
            }

            var directors = new List<string>();
            var cast = new List<string>();
            var castIds = new List<int>();
            if (root.TryGetProperty("credits", out var credits))
            {
                if (!tv && credits.TryGetProperty("crew", out var crew) && crew.ValueKind == JsonValueKind.Array)
                    directors.AddRange(crew.EnumerateArray().Where(c => Str(c, "job") == "Director").Select(c => Str(c, "name")).Where(n => n.Length > 0).Distinct());
                if (credits.TryGetProperty("cast", out var castEl) && castEl.ValueKind == JsonValueKind.Array)
                    foreach (var c in castEl.EnumerateArray().Take(12))
                    {
                        var name = Str(c, "name");
                        var role = Str(c, "character");
                        if (name.Length == 0)
                            continue;
                        cast.Add(role.Length > 0 ? $"{name} as {role}" : name);
                        castIds.Add(c.TryGetProperty("id", out var pid) && pid.TryGetInt32(out var p) ? p : 0);
                    }
            }

            // The official YouTube trailer if there is one, otherwise any trailer, otherwise a teaser.
            var trailer = "";
            if (root.TryGetProperty("videos", out var videos) && videos.TryGetProperty("results", out var vids) && vids.ValueKind == JsonValueKind.Array)
            {
                var yt = vids.EnumerateArray().Where(v => Str(v, "site") == "YouTube" && Str(v, "key").Length > 0).ToList();
                var best = yt.FirstOrDefault(v => Str(v, "type") == "Trailer" && v.TryGetProperty("official", out var o) && o.ValueKind == JsonValueKind.True);
                if (best.ValueKind == JsonValueKind.Undefined)
                    best = yt.FirstOrDefault(v => Str(v, "type") == "Trailer");
                if (best.ValueKind == JsonValueKind.Undefined)
                    best = yt.FirstOrDefault(v => Str(v, "type") == "Teaser");
                if (best.ValueKind != JsonValueKind.Undefined)
                    trailer = Str(best, "key");
            }

            if (tv && root.TryGetProperty("created_by", out var by) && by.ValueKind == JsonValueKind.Array)
                directors.AddRange(by.EnumerateArray().Select(c => Str(c, "name")).Where(n => n.Length > 0));

            var certification = "";
            if (tv)
            {
                if (root.TryGetProperty("content_ratings", out var cr) && cr.TryGetProperty("results", out var crr) && crr.ValueKind == JsonValueKind.Array)
                    certification = crr.EnumerateArray().Where(x => Str(x, "iso_3166_1") == "US").Select(x => Str(x, "rating")).FirstOrDefault(s => s.Length > 0) ?? "";
            }
            else if (root.TryGetProperty("release_dates", out var rd) && rd.TryGetProperty("results", out var rdr) && rdr.ValueKind == JsonValueKind.Array)
            {
                foreach (var country in rdr.EnumerateArray().Where(x => Str(x, "iso_3166_1") == "US"))
                    if (country.TryGetProperty("release_dates", out var rels) && rels.ValueKind == JsonValueKind.Array)
                    {
                        certification = rels.EnumerateArray().Select(x => Str(x, "certification")).FirstOrDefault(s => s.Length > 0) ?? "";
                        break;
                    }
            }

            string[] Names(string prop) =>
                root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Select(x => Str(x, "name")).Where(n => n.Length > 0).ToArray()
                    : Array.Empty<string>();

            var backdrop = Str(root, "backdrop_path");
            var details = new TmdbDetails(
                Str(root, "tagline"),
                runtime,
                root.TryGetProperty("vote_average", out var va) && va.TryGetDouble(out var vote) ? vote : 0,
                root.TryGetProperty("vote_count", out var vc) && vc.TryGetInt32(out var votes) ? votes : 0,
                Names("genres"),
                Str(root, "status"),
                Str(root, tv ? "first_air_date" : "release_date"),
                tv && root.TryGetProperty("number_of_seasons", out var ns) && ns.TryGetInt32(out var seasons) ? seasons : null,
                tv && root.TryGetProperty("number_of_episodes", out var ne) && ne.TryGetInt32(out var episodes) ? episodes : null,
                Names(tv ? "networks" : "production_companies"),
                backdrop.Length > 0 ? backdrop : null,
                certification,
                directors.Distinct().ToArray(),
                cast.ToArray(),
                Str(root, "original_language"),
                tv ? (root.TryGetProperty("external_ids", out var ext) ? Str(ext, "imdb_id") : "") : Str(root, "imdb_id"),
                castIds.ToArray(),
                trailer,
                TmdbDetails.CurrentVersion);

            try
            {
                Directory.CreateDirectory(DetailsDir);
                File.WriteAllText(file, JsonSerializer.Serialize(details, CacheJson));
            }
            catch
            {
                /* best effort */
            }

            return details;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Every episode TMDB lists for one season of a show. Cached on disk: for good once the show has ended, for a week
    /// while it is still airing (new episodes appear). Null if TMDB can't be reached.
    /// </summary>
    public async Task<List<TmdbEpisode>?> GetSeasonAsync(int tvId, int season, bool showEnded, CancellationToken ct)
    {
        if (Rejected)
            return null;

        var file = Path.Combine(SeasonDir, $"{tvId}-{season}.json");
        try
        {
            if (File.Exists(file) && (showEnded || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(7))
                && JsonSerializer.Deserialize<List<TmdbEpisode>>(File.ReadAllText(file)) is { } cached)
                return cached;
        }
        catch
        {
            /* fetch again */
        }

        try
        {
            using var response = await Http.SendAsync(BuildRequest($"/tv/{tvId}/season/{season}"), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Rejected = true;
                return null;
            }

            var episodes = new List<TmdbEpisode>();
            if (response.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
                    .ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("episodes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var e in arr.EnumerateArray())
                        if (e.TryGetProperty("episode_number", out var n) && n.TryGetInt32(out var number))
                            episodes.Add(new TmdbEpisode(season, number, Str(e, "name"), Str(e, "air_date")));
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                return null;
            }

            Directory.CreateDirectory(SeasonDir);
            File.WriteAllText(file, JsonSerializer.Serialize(episodes, CacheJson));
            return episodes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Movies and shows a person appeared in (cast), most popular first. Cached for 30 days.</summary>
    public async Task<(string Name, string Biography, string? ProfilePath, List<TmdbCredit> Credits)?> GetPersonAsync(int personId, CancellationToken ct)
    {
        if (Rejected || personId <= 0)
            return null;

        var file = Path.Combine(PeopleDir, $"{personId}.json");
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(30)
                && JsonSerializer.Deserialize<PersonCache>(File.ReadAllText(file)) is { } c)
                return (c.Name, c.Biography, c.ProfilePath, c.Credits);
        }
        catch
        {
            /* fetch again */
        }

        try
        {
            using var response = await Http.SendAsync(BuildRequest($"/person/{personId}", ("append_to_response", "combined_credits")), ct)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Rejected = true;
                return null;
            }

            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
                .ConfigureAwait(false);
            var root = doc.RootElement;
            var credits = new List<TmdbCredit>();
            if (root.TryGetProperty("combined_credits", out var cc) && cc.TryGetProperty("cast", out var cast) && cast.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cast.EnumerateArray())
                {
                    var tv = Str(c, "media_type") == "tv";
                    if (!c.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
                        continue;
                    var title = Str(c, tv ? "name" : "title");
                    if (title.Length == 0)
                        continue;
                    var date = Str(c, tv ? "first_air_date" : "release_date");
                    int? year = date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
                    var poster = Str(c, "poster_path");
                    var popularity = c.TryGetProperty("popularity", out var p) && p.TryGetDouble(out var pop) ? pop : 0;
                    credits.Add(new TmdbCredit(id, tv ? LibraryKind.Tv : LibraryKind.Movie, title, year, Str(c, "character"),
                        poster.Length > 0 ? poster : null, popularity));
                }
            }

            // A show appears once per role/season; keep one entry per title.
            credits = credits.GroupBy(c => (c.Kind, c.Id)).Select(g => g.First()).OrderByDescending(c => c.Popularity).ToList();
            var profile = Str(root, "profile_path");
            var cache = new PersonCache { Name = Str(root, "name"), Biography = Str(root, "biography"), ProfilePath = profile.Length > 0 ? profile : null, Credits = credits };
            try
            {
                Directory.CreateDirectory(PeopleDir);
                File.WriteAllText(file, JsonSerializer.Serialize(cache, CacheJson));
            }
            catch
            {
                /* best effort */
            }

            return (cache.Name, cache.Biography, cache.ProfilePath, credits);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>"If you like this, try…" for one title (TMDB recommendations). Cached for 7 days.</summary>
    public Task<List<TmdbRec>?> GetRecommendationsAsync(TmdbMatch match, CancellationToken ct) =>
        match.Kind is LibraryKind.Movie or LibraryKind.Tv
            ? GetRecListAsync($"/{(match.Kind == LibraryKind.Tv ? "tv" : "movie")}/{match.Id}/recommendations", match.Kind,
                $"rec-{match.Kind}-{match.Id}", TimeSpan.FromDays(7), ct)
            : Task.FromResult<List<TmdbRec>?>(null);

    /// <summary>What's trending this week on TMDB (movies or TV). Cached for a day.</summary>
    public Task<List<TmdbRec>?> GetTrendingAsync(LibraryKind kind, CancellationToken ct) =>
        GetRecListAsync($"/trending/{(kind == LibraryKind.Tv ? "tv" : "movie")}/week", kind, $"trending-{kind}", TimeSpan.FromDays(1), ct);

    /// <summary>Highest-rated anime series and films on TMDB (Japanese animation with plenty of votes). Cached for a week.</summary>
    public Task<List<TmdbRec>?> GetTopAnimeAsync(LibraryKind kind, CancellationToken ct) =>
        GetRecListAsync($"/discover/{(kind == LibraryKind.Tv ? "tv" : "movie")}", kind, $"topanime-{kind}", TimeSpan.FromDays(7), ct,
            ("with_genres", "16"), ("with_original_language", "ja"), ("sort_by", "vote_average.desc"), ("vote_count.gte", "300"));

    private async Task<List<TmdbRec>?> GetRecListAsync(string path, LibraryKind kind, string cacheName, TimeSpan life, CancellationToken ct,
        params (string Key, string Value)[] query)
    {
        if (Rejected)
            return null;
        var file = Path.Combine(RecsDir, cacheName + ".json");
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < life
                && JsonSerializer.Deserialize<List<TmdbRec>>(File.ReadAllText(file)) is { } cached)
                return cached;
        }
        catch
        {
            /* fetch again */
        }

        try
        {
            using var response = await Http.SendAsync(BuildRequest(path, query), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Rejected = true;
                return null;
            }

            var list = new List<TmdbRec>();
            if (response.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
                    .ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in arr.EnumerateArray())
                    {
                        if (!r.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
                            continue;
                        var tv = kind == LibraryKind.Tv;
                        var title = Str(r, tv ? "name" : "title");
                        if (title.Length == 0)
                            continue;
                        var date = Str(r, tv ? "first_air_date" : "release_date");
                        int? year = date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
                        var poster = Str(r, "poster_path");
                        var backdrop = Str(r, "backdrop_path");
                        var genres = r.TryGetProperty("genre_ids", out var g) && g.ValueKind == JsonValueKind.Array
                            ? g.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToArray()
                            : Array.Empty<int>();
                        list.Add(new TmdbRec(
                            new TmdbMatch(id, kind, title, year, poster.Length > 0 ? poster : null, Str(r, "overview")),
                            r.TryGetProperty("vote_average", out var va) && va.TryGetDouble(out var vote) ? vote : 0,
                            r.TryGetProperty("vote_count", out var vc) && vc.TryGetInt32(out var votes) ? votes : 0,
                            genres,
                            Str(r, "original_language"),
                            r.TryGetProperty("popularity", out var pp) && pp.TryGetDouble(out var pop) ? pop : 0,
                            backdrop.Length > 0 ? backdrop : null));
                    }
                }
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                return null;
            }

            Directory.CreateDirectory(RecsDir);
            File.WriteAllText(file, JsonSerializer.Serialize(list, CacheJson));
            return list;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private sealed class PersonCache
    {
        public string Name { get; set; } = "";
        public string Biography { get; set; } = "";
        public string? ProfilePath { get; set; }
        public List<TmdbCredit> Credits { get; set; } = new();
    }

    /// <summary>Local file for a wide backdrop image (cached forever once downloaded); null if unavailable.</summary>
    public async Task<string?> GetBackdropFileAsync(string? backdropPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(backdropPath))
            return null;

        var file = Path.Combine(PosterDir, Hash("backdrop|" + backdropPath) + ".jpg");
        if (File.Exists(file))
            return file;

        try
        {
            // Posters come from ".../w342"; backdrops are wide, so use the w780 size from the same host.
            var basePath = _images.EndsWith("/w342", StringComparison.Ordinal) ? _images[..^5] + "/w780" : _images;
            var bytes = await Http.GetByteArrayAsync(basePath + backdropPath, ct).ConfigureAwait(false);
            Directory.CreateDirectory(PosterDir);
            await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
            return file;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    // ---- Cache ---------------------------------------------------------------------------------------------------

    private sealed class CacheEntry
    {
        public DateTime At { get; set; }
        public TmdbMatch? Match { get; set; }
    }

    private static bool TryReadCache(string file, out TmdbMatch? match)
    {
        match = null;
        try
        {
            if (!File.Exists(file))
                return false;
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(file));
            if (entry is null)
                return false;
            if (entry.Match is not null)
            {
                match = entry.Match;
                return true;
            }

            // A remembered miss is kept too, so unmatched titles aren't searched again on every launch. "Rescan" retries them.
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadSearchCache(string file, out List<TmdbMatch> results)
    {
        results = new List<TmdbMatch>();
        try
        {
            if (!File.Exists(file))
                return false;
            var list = JsonSerializer.Deserialize<List<TmdbMatch>>(File.ReadAllText(file));
            if (list is null)
                return false;
            results = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteSearchCache(string file, List<TmdbMatch> results)
    {
        try
        {
            Directory.CreateDirectory(SearchDir);
            File.WriteAllText(file, JsonSerializer.Serialize(results, CacheJson));
        }
        catch
        {
            /* best effort */
        }
    }

    private static void WriteCache(string file, TmdbMatch? match)
    {
        try
        {
            Directory.CreateDirectory(MatchDir);
            File.WriteAllText(file, JsonSerializer.Serialize(new CacheEntry { At = DateTime.UtcNow, Match = match }, CacheJson));
        }
        catch
        {
            /* best effort */
        }
    }

    private static string Hash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..24];

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private HttpRequestMessage BuildRequest(string path, params (string Key, string Value)[] query)
    {
        var parts = query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}").ToList();
        if (!_bearer)
            parts.Add("api_key=" + Uri.EscapeDataString(_credential));

        var url = _api + path + (parts.Count > 0 ? "?" + string.Join("&", parts) : "");
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_bearer)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credential);
        return request;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PaxMediaManager/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }
}
