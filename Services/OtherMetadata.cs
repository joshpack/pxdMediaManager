using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pxdMediaManager.Services;

/// <summary>Shared plumbing for the keyless metadata sources (Steam, Audible, AniList): HTTP, disk cache, title matching.</summary>
internal static partial class MetaHttp
{
    public static readonly HttpClient Client = Create();

    public static string Dir(string name) =>
        System.IO.Path.Combine(AppPaths.DataDir, name);

    public static string Hash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24];

    public static bool TryRead<T>(string file, out T? value)
    {
        value = default;
        try
        {
            if (!File.Exists(file))
                return false;
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(file));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Write<T>(string file, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(value));
        }
        catch
        {
            /* best effort */
        }
    }

    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static string Norm(string s) => NonAlnum().Replace(s.ToLowerInvariant().Replace("&", "and"), "");

    /// <summary>
    /// Is a search result plausibly the thing we searched for? Guards against junk matches (an IT folder of installers
    /// being "matched" to some random game). Exact or containment matches pass; otherwise most words must overlap.
    /// </summary>
    public static bool Similar(string wanted, string found)
    {
        var a = Norm(wanted);
        var b = Norm(found);
        if (a.Length == 0 || b.Length == 0)
            return false;
        if (a == b || (a.Length >= 4 && b.Contains(a)) || (b.Length >= 4 && a.Contains(b)))
            return true;
        var wa = Words(wanted);
        var wb = Words(found);
        if (wa.Count == 0)
            return false;
        return wa.Count(wb.Contains) >= Math.Max(1, (int)Math.Ceiling(wa.Count * 0.6));
    }

    private static HashSet<string> Words(string s) =>
        WordSplit().Split(s.ToLowerInvariant()).Where(w => w.Length > 1 && w is not ("the" or "of" or "and" or "a")).ToHashSet();

    public static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
            return "";
        var s = Regex.Replace(html, @"<br\s*/?>|</p>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        return Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex WordSplit();

    private static HttpClient Create()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("pxdMediaManager/1.0");
        return http;
    }
}

/// <summary>
/// Steam store (no key needed): finds a game by name and reads its store page data — description, developers,
/// genres, release date, Metacritic score and the Steam user review score. Everything is cached for good.
/// </summary>
public static class SteamClient
{
    private static readonly string Store = Environment.GetEnvironmentVariable("PXDMM_STEAM_API")?.TrimEnd('/') ?? "https://store.steampowered.com";

    public static async Task<List<TmdbMatch>> SearchAsync(string query, CancellationToken ct)
    {
        var results = new List<TmdbMatch>();
        using var response = await MetaHttp.Client.GetAsync($"{Store}/api/storesearch/?term={Uri.EscapeDataString(query)}&l=english&cc=US", ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Steam answered {(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var i in items.EnumerateArray())
                if (MetaHttp.Str(i, "type") == "app" && i.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
                    results.Add(new TmdbMatch(id, LibraryKind.Game, MetaHttp.Str(i, "name"), null, PosterUrls(id, MetaHttp.Str(i, "tiny_image")), ""));
        return results;
    }

    /// <summary>Best match for a cleaned folder name; cached (misses too) until "Rescan".</summary>
    public static async Task<TmdbMatch?> FindAsync(string title, int? year, CancellationToken ct, bool refresh = false)
    {
        // A "year" in a game name is often part of the title (Cyberpunk 2077, FIFA 2019): search with it first.
        var query = year is { } y ? $"{title} {y}" : title;
        var file = Path.Combine(MetaHttp.Dir("matches"), "steam-" + MetaHttp.Hash(query.ToLowerInvariant()) + ".json");
        if (!refresh && MetaHttp.TryRead<Holder>(file, out var cached) && cached is not null)
            return cached.Match;

        try
        {
            var results = await SearchAsync(query, ct).ConfigureAwait(false);
            var pick = results.FirstOrDefault(r => MetaHttp.Norm(r.Title) == MetaHttp.Norm(query))
                       ?? results.FirstOrDefault(r => MetaHttp.Similar(query, r.Title));
            if (pick is null && year is not null)
            {
                results = await SearchAsync(title, ct).ConfigureAwait(false);
                pick = results.FirstOrDefault(r => MetaHttp.Similar(title, r.Title));
            }

            if (pick is not null && await GetDetailsAsync(pick, ct).ConfigureAwait(false) is { } d)
                pick = pick with { Overview = d.Tagline.Length > 0 ? d.Tagline : pick.Overview, Year = YearOf(d.ReleaseDate) };
            MetaHttp.Write(file, new Holder { Match = pick });
            return pick;
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

    /// <summary>Store data for an app, mapped onto the shared details record. Cached for good once read.</summary>
    public static async Task<TmdbDetails?> GetDetailsAsync(TmdbMatch match, CancellationToken ct)
    {
        var file = Path.Combine(MetaHttp.Dir("details"), $"steam-{match.Id}.json");
        if (MetaHttp.TryRead<TmdbDetails>(file, out var cached) && cached is not null)
            return cached;

        try
        {
            using var response = await MetaHttp.Client.GetAsync($"{Store}/api/appdetails?appids={match.Id}&l=english&cc=US", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty(match.Id.ToString(), out var app) || !app.TryGetProperty("data", out var data))
                return null;

            string[] Names(string prop, string field) =>
                data.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : MetaHttp.Str(x, field))
                        .Where(n => n.Length > 0).ToArray()
                    : Array.Empty<string>();

            var scores = new List<RatingScore>();
            if (data.TryGetProperty("metacritic", out var mc) && mc.TryGetProperty("score", out var mcs) && mcs.TryGetInt32(out var metascore))
                scores.Add(new RatingScore("metacritic", metascore, null));

            // Steam user reviews: % positive, the way the store shows it.
            double rating = 0;
            var votes = 0;
            var reviewText = "";
            try
            {
                using var rev = await MetaHttp.Client.GetAsync($"{Store}/appreviews/{match.Id}?json=1&language=all&purchase_type=all&num_per_page=0", ct)
                    .ConfigureAwait(false);
                if (rev.IsSuccessStatusCode)
                {
                    using var rdoc = JsonDocument.Parse(await rev.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (rdoc.RootElement.TryGetProperty("query_summary", out var q)
                        && q.TryGetProperty("total_reviews", out var tr) && tr.TryGetInt32(out var total) && total > 0
                        && q.TryGetProperty("total_positive", out var tp) && tp.TryGetInt32(out var positive))
                    {
                        var pct = positive * 100.0 / total;
                        scores.Add(new RatingScore("steam", Math.Round(pct), total));
                        rating = pct / 10;
                        votes = total;
                        reviewText = MetaHttp.Str(q, "review_score_desc");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                /* reviews are optional */
            }

            var age = data.TryGetProperty("required_age", out var ra) && ra.ValueKind == JsonValueKind.Number && ra.TryGetInt32(out var a) && a > 0 ? $"{a}+" : "";
            var date = data.TryGetProperty("release_date", out var rd) ? MetaHttp.Str(rd, "date") : "";
            var background = MetaHttp.Str(data, "background_raw");
            if (background.Length == 0)
                background = MetaHttp.Str(data, "background");
            var description = MetaHttp.StripHtml(MetaHttp.Str(data, "short_description"));
            var details = new TmdbDetails(
                description,
                null,
                rating,
                votes,
                Names("genres", "description"),
                reviewText,
                date,
                null,
                null,
                Names("publishers", "name"),
                background.Length > 0 ? background : null,
                age,
                Names("developers", "name"),
                Array.Empty<string>(),
                "",
                "",
                null,
                "",
                TmdbDetails.CurrentVersion,
                scores.ToArray());
            MetaHttp.Write(file, details);
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
    /// Games worth suggesting: Steam's current top sellers, new releases and specials, plus SteamSpy's most played in
    /// the last two weeks. Each list is tagged with where it came from. Cached for a day.
    /// </summary>
    public static async Task<List<(TmdbMatch Match, string From)>> DiscoverPoolAsync(CancellationToken ct)
    {
        var file = Path.Combine(MetaHttp.Dir("recommendations"), "steam-pool.json");
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(1)
            && MetaHttp.TryRead<List<PoolEntry>>(file, out var cached) && cached is not null)
            return cached.Select(e => (e.Match, e.From)).ToList();

        var pool = new List<PoolEntry>();
        try
        {
            using var response = await MetaHttp.Client.GetAsync($"{Store}/api/featuredcategories?cc=US&l=english", ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                foreach (var (prop, label) in new[] { ("top_sellers", "Top seller on Steam"), ("new_releases", "New on Steam"), ("specials", "On sale on Steam") })
                {
                    if (!doc.RootElement.TryGetProperty(prop, out var cat) || !cat.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var i in items.EnumerateArray())
                        if (i.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) && MetaHttp.Str(i, "name") is { Length: > 0 } name)
                            pool.Add(new PoolEntry { Match = new TmdbMatch(id, LibraryKind.Game, name, null, PosterUrls(id, MetaHttp.Str(i, "header_image")), ""), From = label });
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            /* Steam unreachable: SteamSpy may still answer */
        }

        try
        {
            var spy = Environment.GetEnvironmentVariable("PXDMM_STEAMSPY_API") ?? "https://steamspy.com/api.php";
            using var response = await MetaHttp.Client.GetAsync($"{spy}?request=top100in2weeks", ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.TryGetProperty("appid", out var idEl) && idEl.TryGetInt32(out var id) && MetaHttp.Str(p.Value, "name") is { Length: > 0 } name)
                        pool.Add(new PoolEntry { Match = new TmdbMatch(id, LibraryKind.Game, name, null, PosterUrls(id, ""), ""), From = "Popular right now" });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            /* optional */
        }

        // Same game from two lists: keep one entry, first label wins.
        pool = pool.GroupBy(e => e.Match.Id).Select(g => g.First()).ToList();
        if (pool.Count > 0)
            MetaHttp.Write(file, pool);
        return pool.Select(e => (e.Match, e.From)).ToList();
    }

    private sealed class PoolEntry
    {
        public TmdbMatch Match { get; set; } = null!;
        public string From { get; set; } = "";
    }

    /// <summary>Tall library art first (fits the 2:3 tiles), then the wide store header as a fallback.</summary>
    private static string PosterUrls(int id, string tiny) =>
        $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id}/library_600x900.jpg" +
        $"|https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/library_600x900.jpg" +
        $"|https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/header.jpg" +
        (tiny.Length > 0 ? "|" + tiny : "");

    private static int? YearOf(string date)
    {
        var m = Regex.Match(date, @"(19|20)\d{2}");
        return m.Success ? int.Parse(m.Value) : null;
    }

    private sealed class Holder
    {
        public TmdbMatch? Match { get; set; }
    }
}

/// <summary>
/// Audible's public catalogue (no key needed, the same one Audiobookshelf uses): finds an audiobook by its folder
/// name and reads author, narrator, length, series, description, cover and the listener rating. Cached for good.
/// </summary>
public static partial class AudibleClient
{
    private static readonly string Api = Environment.GetEnvironmentVariable("PXDMM_AUDIBLE_API")?.TrimEnd('/') ?? "https://api.audible.com";
    private const string Groups = "contributors,product_desc,product_attrs,media,rating,series,category_ladders";

    /// <summary>Turns a folder name like "Andy Weir - Project Hail Mary (2021) [Ray Porter] 64k" into search words.</summary>
    public static string QueryFrom(string name)
    {
        var s = Path.GetExtension(name) is ".m4b" or ".mp3" or ".m4a" ? Path.GetFileNameWithoutExtension(name) : name;
        s = Regex.Replace(s, @"\[[^\]]*\]|\{[^}]*\}|\([^)]*\)", " ");
        s = Regex.Replace(s, @"\b(unabridged|abridged|audiobook|audio ?book|m4b|mp3|aac|flac|\d{2,3}k(bps)?|retail|read by|narrated by|by)\b", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(19|20)\d{2}\b", " ");
        s = s.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    public static async Task<List<TmdbMatch>> SearchAsync(string query, CancellationToken ct)
    {
        var results = new List<TmdbMatch>();
        var url = $"{Api}/1.0/catalog/products?keywords={Uri.EscapeDataString(query)}&num_results=15&products_sort_by=Relevance&response_groups={Groups}";
        using var response = await MetaHttp.Client.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Audible answered {(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in products.EnumerateArray())
            {
                var (match, details) = Parse(p);
                if (match is null)
                    continue;
                results.Add(match);
                // The search already carries everything the details window shows: keep it so no second request is needed.
                MetaHttp.Write(DetailsFile(match.Key), details);
            }
        }

        return results;
    }

    public static async Task<TmdbMatch?> FindAsync(string folderName, CancellationToken ct, bool refresh = false)
    {
        var query = QueryFrom(folderName);
        if (query.Length < 2)
            return null;
        var file = Path.Combine(MetaHttp.Dir("matches"), "audible-" + MetaHttp.Hash(query.ToLowerInvariant()) + ".json");
        if (!refresh && MetaHttp.TryRead<Holder>(file, out var cached) && cached is not null)
            return cached.Match;

        try
        {
            var results = await SearchAsync(query, ct).ConfigureAwait(false);
            // The folder usually holds the title and the author; the result's title should appear among those words.
            var pick = results.FirstOrDefault(r => MetaHttp.Norm(query).Contains(MetaHttp.Norm(r.Title)) && MetaHttp.Norm(r.Title).Length >= 3)
                       ?? results.FirstOrDefault(r => MetaHttp.Similar(r.Title, query));
            MetaHttp.Write(file, new Holder { Match = pick });
            return pick;
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

    public static async Task<TmdbDetails?> GetDetailsAsync(TmdbMatch match, CancellationToken ct)
    {
        if (match.ExternalId is not { Length: > 0 } asin)
            return null;
        if (MetaHttp.TryRead<TmdbDetails>(DetailsFile(asin), out var cached) && cached is not null)
            return cached;

        try
        {
            using var response = await MetaHttp.Client.GetAsync($"{Api}/1.0/catalog/products/{Uri.EscapeDataString(asin)}?response_groups={Groups}", ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("product", out var p))
                return null;
            var (_, details) = Parse(p);
            if (details is not null)
                MetaHttp.Write(DetailsFile(asin), details);
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

    private static string DetailsFile(string asin) => Path.Combine(MetaHttp.Dir("details"), $"audible-{asin}.json");

    /// <summary>A search whose results are kept for a week (used by Discover: more by an author, the rest of a series).</summary>
    public static async Task<List<TmdbMatch>> SearchCachedAsync(string query, CancellationToken ct)
    {
        var file = Path.Combine(MetaHttp.Dir("recommendations"), "audible-" + MetaHttp.Hash(query.ToLowerInvariant()) + ".json");
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(7)
            && MetaHttp.TryRead<List<TmdbMatch>>(file, out var cached) && cached is not null)
            return cached;
        var results = await SearchAsync(query, ct).ConfigureAwait(false);
        MetaHttp.Write(file, results);
        return results;
    }

    private static (TmdbMatch? Match, TmdbDetails? Details) Parse(JsonElement p)
    {
        var asin = MetaHttp.Str(p, "asin");
        var title = MetaHttp.Str(p, "title");
        if (asin.Length == 0 || title.Length == 0)
            return (null, null);

        string[] People(string prop) =>
            p.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Select(x => MetaHttp.Str(x, "name")).Where(n => n.Length > 0).Distinct().ToArray()
                : Array.Empty<string>();

        var image = p.TryGetProperty("product_images", out var imgs) ? MetaHttp.Str(imgs, "500") : "";
        if (image.Length == 0 && imgs.ValueKind == JsonValueKind.Object)
            image = imgs.EnumerateObject().Select(o => o.Value.GetString() ?? "").FirstOrDefault(u => u.Length > 0) ?? "";
        var date = MetaHttp.Str(p, "release_date");
        int? year = date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
        var summary = MetaHttp.StripHtml(MetaHttp.Str(p, "merchandising_summary"));
        if (summary.Length == 0)
            summary = MetaHttp.StripHtml(MetaHttp.Str(p, "publisher_summary"));
        var authors = People("authors");

        double stars = 0;
        var votes = 0;
        if (p.TryGetProperty("rating", out var rating) && rating.TryGetProperty("overall_distribution", out var od))
        {
            var avg = od.TryGetProperty("display_average_rating", out var da) ? da : default;
            if (avg.ValueKind == JsonValueKind.String)
                double.TryParse(avg.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out stars);
            else if (avg.ValueKind == JsonValueKind.Number)
                stars = avg.GetDouble();
            if (od.TryGetProperty("num_ratings", out var nr) && nr.TryGetInt32(out var n))
                votes = n;
        }

        var series = "";
        if (p.TryGetProperty("series", out var ser) && ser.ValueKind == JsonValueKind.Array && ser.GetArrayLength() > 0)
        {
            var s0 = ser[0];
            var seq = MetaHttp.Str(s0, "sequence");
            series = (seq.Length > 0 ? $"Book {seq} of " : "") + MetaHttp.Str(s0, "title");
        }

        var genres = new List<string>();
        if (p.TryGetProperty("category_ladders", out var ladders) && ladders.ValueKind == JsonValueKind.Array)
            foreach (var l in ladders.EnumerateArray())
                if (l.TryGetProperty("ladder", out var ladder) && ladder.ValueKind == JsonValueKind.Array)
                    genres.AddRange(ladder.EnumerateArray().Select(x => MetaHttp.Str(x, "name")).Where(n => n.Length > 0));

        var subtitle = MetaHttp.Str(p, "subtitle");
        var runtime = p.TryGetProperty("runtime_length_min", out var rl) && rl.TryGetInt32(out var mins) && mins > 0 ? mins : (int?)null;
        var match = new TmdbMatch(0, LibraryKind.Audiobook, title, year, image.Length > 0 ? image : null,
            summary, asin);
        var details = new TmdbDetails(
            series.Length > 0 ? series : subtitle,
            runtime,
            stars * 2,
            votes,
            genres.Distinct().Take(6).ToArray(),
            "",
            date,
            null,
            null,
            new[] { MetaHttp.Str(p, "publisher_name") }.Where(n => n.Length > 0).ToArray(),
            null,
            "",
            authors,
            People("narrators"),
            MetaHttp.Str(p, "language"),
            "",
            null,
            "",
            TmdbDetails.CurrentVersion,
            stars > 0 ? new[] { new RatingScore("audible", stars, votes > 0 ? votes : null) } : Array.Empty<RatingScore>());
        return (match, details);
    }

    private sealed class Holder
    {
        public TmdbMatch? Match { get; set; }
    }
}

/// <summary>AniList (no key needed): the community score for an anime, looked up by title. Cached for good.</summary>
public static class AniListClient
{
    private static readonly string Api = Environment.GetEnvironmentVariable("PXDMM_ANILIST_API") ?? "https://graphql.anilist.co";

    public static async Task<RatingScore?> GetScoreAsync(string title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var file = Path.Combine(MetaHttp.Dir("ratings"), "anilist-" + MetaHttp.Hash(title.Trim().ToLowerInvariant()) + ".json");
        if (MetaHttp.TryRead<List<RatingScore>>(file, out var cached) && cached is not null)
            return cached.FirstOrDefault();

        try
        {
            // A page of candidates: the single best "search match" is often a short special, so take the most popular
            // entry whose title really matches.
            const string query = "query ($s: String) { Page(perPage: 8) { media(search: $s, type: ANIME, sort: [SEARCH_MATCH]) " +
                                 "{ averageScore popularity title { romaji english } synonyms } } }";
            var body = JsonSerializer.Serialize(new { query, variables = new { s = title } });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await MetaHttp.Client.PostAsync(Api, content, ct).ConfigureAwait(false);
            var list = new List<RatingScore>();
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("Page", out var page)
                    && page.TryGetProperty("media", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var best = arr.EnumerateArray()
                        .Where(m => m.TryGetProperty("averageScore", out var a) && a.ValueKind == JsonValueKind.Number)
                        .Where(m =>
                        {
                            var names = new List<string>();
                            if (m.TryGetProperty("title", out var t))
                                names.AddRange(new[] { MetaHttp.Str(t, "english"), MetaHttp.Str(t, "romaji") });
                            if (m.TryGetProperty("synonyms", out var syn) && syn.ValueKind == JsonValueKind.Array)
                                names.AddRange(syn.EnumerateArray().Select(x => x.GetString() ?? ""));
                            return names.Any(n => n.Length > 0 && MetaHttp.Similar(title, n));
                        })
                        .OrderByDescending(m => m.TryGetProperty("popularity", out var p) && p.TryGetInt32(out var pp) ? pp : 0)
                        .FirstOrDefault();
                    if (best.ValueKind == JsonValueKind.Object)
                        list.Add(new RatingScore("anilist", best.GetProperty("averageScore").GetInt32(),
                            best.TryGetProperty("popularity", out var pop) && pop.TryGetInt32(out var pv) ? pv : null));
                }
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                return null; // rate limited or down: try another time
            }

            MetaHttp.Write(file, list);
            return list.FirstOrDefault();
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
}

/// <summary>
/// One front door for matching and details, whatever the item is: TMDB for films and TV (needs the key), Steam for
/// games and Audible for audiobooks (no key needed).
/// </summary>
public sealed class MetadataService
{
    public MetadataService(TmdbClient? tmdb) => Tmdb = tmdb;

    public TmdbClient? Tmdb { get; }

    public bool CanMatch(LibraryItem item) =>
        item.Kind switch
        {
            LibraryKind.Game or LibraryKind.Audiobook => true,
            LibraryKind.None => false,
            _ => Tmdb is not null,
        };

    public Task<TmdbMatch?> FindAsync(LibraryItem item, CancellationToken ct, bool refresh = false)
    {
        switch (item.Kind)
        {
            case LibraryKind.Game:
                return SteamClient.FindAsync(item.Title, item.Year, ct, refresh);
            case LibraryKind.Audiobook:
                return AudibleClient.FindAsync(item.Name, ct, refresh);
            case LibraryKind.None:
                return Task.FromResult<TmdbMatch?>(null);
            default:
                if (Tmdb is null)
                    return Task.FromResult<TmdbMatch?>(null);
                // A season or episode marker in the name means it is a show, even in an uncategorised folder.
                var kind = item.Kind == LibraryKind.Auto && item.Extra.Length > 0 ? LibraryKind.Tv : item.Kind;
                return Tmdb.FindAsync(item.Title, item.Year, kind, ct, refresh);
        }
    }

    public async Task<List<TmdbMatch>> SearchAsync(LibraryKind kind, string query, int? year, CancellationToken ct) =>
        kind switch
        {
            LibraryKind.Game => await SteamClient.SearchAsync(year is null ? query : $"{query} {year}", ct).ConfigureAwait(false),
            LibraryKind.Audiobook => await AudibleClient.SearchAsync(query, ct).ConfigureAwait(false),
            _ => Tmdb is null ? new List<TmdbMatch>() : await Tmdb.SearchAsync(kind, query, year, ct).ConfigureAwait(false),
        };

    public Task<TmdbDetails?> GetDetailsAsync(TmdbMatch match, CancellationToken ct) =>
        match.Kind switch
        {
            LibraryKind.Game => SteamClient.GetDetailsAsync(match, ct),
            LibraryKind.Audiobook => AudibleClient.GetDetailsAsync(match, ct),
            _ => Tmdb?.GetDetailsAsync(match, ct) ?? Task.FromResult<TmdbDetails?>(null),
        };

    public async Task<string?> GetPosterFileAsync(TmdbMatch match, CancellationToken ct)
    {
        if (match.PosterPath is null)
            return null;
        if (match.PosterPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return await TmdbClient.GetImageFileAsync(match.PosterPath, ct).ConfigureAwait(false);
        return Tmdb is null ? null : await Tmdb.GetPosterFileAsync(match.PosterPath, ct).ConfigureAwait(false);
    }

    public async Task<string?> GetBackdropFileAsync(string? backdrop, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(backdrop))
            return null;
        if (backdrop.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return await TmdbClient.GetImageFileAsync(backdrop, ct).ConfigureAwait(false);
        return Tmdb is null ? null : await Tmdb.GetBackdropFileAsync(backdrop, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// For an uncategorised folder: does it look like a game (Steam/Unity/Unreal files, repack installers) or an
    /// audiobook (.m4b files, or "audiobook" in the name)? Looks two levels deep at most, so it stays cheap on a NAS.
    /// </summary>
    public static LibraryKind SniffKind(LibraryItem item)
    {
        if (!item.IsDirectory)
        {
            var ext = Path.GetExtension(item.Path).ToLowerInvariant();
            return ext == ".m4b" ? LibraryKind.Audiobook : LibraryKind.Auto;
        }

        var name = item.Name.ToLowerInvariant();
        if (Regex.IsMatch(name, @"\b(audiobook|unabridged|narrated)\b"))
            return LibraryKind.Audiobook;
        if (Regex.IsMatch(name, @"\b(fitgirl|dodi|elamigos|gog|codex|plaza|skidrow|tenoke|rune|flt|darksiders|repack)\b"))
            return LibraryKind.Game;

        try
        {
            int audio = 0, video = 0, m4b = 0, gameMarkers = 0, seen = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
            foreach (var f in Directory.EnumerateFiles(item.Path, "*", options))
            {
                if (++seen > 3000)
                    break;
                var fn = Path.GetFileName(f).ToLowerInvariant();
                var ext = Path.GetExtension(fn);
                if (ext == ".m4b")
                    m4b++;
                else if (ext is ".mp3" or ".m4a" or ".flac" or ".ogg" or ".opus" or ".aac")
                    audio++;
                else if (Viewer.MediaFiles.IsVideo(fn))
                    video++;
                if (fn is "steam_api.dll" or "steam_api64.dll" or "unityplayer.dll" or "gameassembly.dll" or "steam_appid.txt"
                    || fn.StartsWith("setup-fitgirl") || ext is ".pak" or ".bik" or ".bsa" or ".ba2" or ".forge" or ".rpf")
                    gameMarkers++;
            }

            if (video == 0 && m4b > 0)
                return LibraryKind.Audiobook;
            if (video == 0 && gameMarkers > 0)
                return LibraryKind.Game;
        }
        catch
        {
            /* unreadable: leave it alone */
        }

        return LibraryKind.Auto;
    }
}
