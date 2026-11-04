using System.Net;
using System.Text.Json;

namespace PaxMediaManager.Services;

/// <summary>One site's score for a title, in that site's own scale (see the details window for how each is shown).</summary>
public sealed record RatingScore(string Source, double Value, int? Votes);

/// <summary>
/// MDBList (mdblist.com) gathers scores from many sites in one call: IMDb, TMDB, Trakt, Letterboxd, Rotten Tomatoes
/// (critics and audience), Metacritic (critics and users), Roger Ebert, MyAnimeList, plus its own average. Looked up by
/// TMDB ID; each title is asked for once and kept on disk for good (the free plan has a daily limit).
/// </summary>
public sealed class MdbListClient
{
    private const string DefaultApi = "https://api.mdblist.com";
    private readonly string _key;
    private readonly string _api = Environment.GetEnvironmentVariable("PAXMM_MDBLIST_API")?.TrimEnd('/') ?? DefaultApi;

    public MdbListClient(string key) => _key = key.Trim();

    public bool Rejected { get; private set; }

    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct = default)
    {
        try
        {
            // The Shawshank Redemption (TMDB 278): always present, so a failure here is about the key.
            using var response = await RatingsHttp.Client.GetAsync(Url(LibraryKind.Movie, 278), ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return (true, "Valid — MDBList key accepted.");
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (false, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "MDBList rejected this key. Check that it was copied completely."
                : $"MDBList answered with status {(int)response.StatusCode}. {RatingsHttp.ErrorText(body)}".Trim());
        }
        catch (Exception ex)
        {
            return (false, "Couldn't reach MDBList: " + ex.Message);
        }
    }

    /// <summary>All scores MDBList has for a TMDB title; cached forever. Null if unavailable right now (not cached).</summary>
    public async Task<List<RatingScore>?> GetAsync(TmdbMatch match, CancellationToken ct)
    {
        if (Rejected || _key.Length == 0 || match.Kind is not (LibraryKind.Movie or LibraryKind.Tv))
            return null;

        var file = Path.Combine(RatingsHttp.Dir, $"mdblist-{(match.Kind == LibraryKind.Tv ? "show" : "movie")}-{match.Id}.json");
        if (RatingsHttp.TryRead(file, out var cached))
            return cached;

        try
        {
            using var response = await RatingsHttp.Client.GetAsync(Url(match.Kind, match.Id), ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                Rejected = true; // bad key or daily limit: stop for this session, cache nothing
                return null;
            }

            var scores = new List<RatingScore>();
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                RatingsHttp.Write(file, scores); // MDBList doesn't know the title: remember that
                return scores;
            }

            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("ratings", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in arr.EnumerateArray())
                {
                    var source = r.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
                    if (source.Length == 0 || !r.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var value)
                        || value <= 0)
                        continue;
                    int? votes = r.TryGetProperty("votes", out var vo) && vo.ValueKind == JsonValueKind.Number && vo.TryGetInt32(out var n) && n > 0 ? n : null;
                    scores.Add(new RatingScore(source.ToLowerInvariant(), value, votes));
                }
            }

            // MDBList's own blend of all the above, 0–100.
            if (root.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number && sc.TryGetDouble(out var score) && score > 0)
                scores.Add(new RatingScore("mdblist", score, null));

            RatingsHttp.Write(file, scores);
            return scores;
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

    private string Url(LibraryKind kind, int tmdbId) =>
        $"{_api}/tmdb/{(kind == LibraryKind.Tv ? "show" : "movie")}/{tmdbId}?apikey={Uri.EscapeDataString(_key)}";
}

/// <summary>TVmaze (tvmaze.com): free, no key. Its user rating for TV shows, looked up by IMDb ID and cached forever.</summary>
public static class TvMazeClient
{
    private static readonly string Api = Environment.GetEnvironmentVariable("PAXMM_TVMAZE_API")?.TrimEnd('/') ?? "https://api.tvmaze.com";

    public static async Task<RatingScore?> GetAsync(string? imdbId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
            return null;

        var file = Path.Combine(RatingsHttp.Dir, $"tvmaze-{imdbId.Trim()}.json");
        if (RatingsHttp.TryRead(file, out var cached))
            return cached.FirstOrDefault();

        try
        {
            // Answers with a redirect to the show itself, which HttpClient follows.
            using var response = await RatingsHttp.Client.GetAsync($"{Api}/lookup/shows?imdb={Uri.EscapeDataString(imdbId.Trim())}", ct)
                .ConfigureAwait(false);
            var scores = new List<RatingScore>();
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                RatingsHttp.Write(file, scores);
                return null;
            }

            if (!response.IsSuccessStatusCode)
                return null; // includes 429 (rate limit): try again another time

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("rating", out var rating) && rating.TryGetProperty("average", out var avg)
                && avg.ValueKind == JsonValueKind.Number && avg.TryGetDouble(out var value) && value > 0)
                scores.Add(new RatingScore("tvmaze", value, null));

            RatingsHttp.Write(file, scores);
            return scores.FirstOrDefault();
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

/// <summary>Shared HTTP client and on-disk cache for the extra rating sources.</summary>
internal static class RatingsHttp
{
    public static readonly HttpClient Client = Create();

    public static string Dir => OmdbClient.RatingsDir;

    public static bool TryRead(string file, out List<RatingScore> scores)
    {
        scores = new List<RatingScore>();
        try
        {
            if (!File.Exists(file) || JsonSerializer.Deserialize<List<RatingScore>>(File.ReadAllText(file)) is not { } list)
                return false;
            scores = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Write(string file, List<RatingScore> scores)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(file, JsonSerializer.Serialize(scores));
        }
        catch
        {
            /* best effort */
        }
    }

    public static string ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var name in new[] { "error", "message", "detail" })
                if (doc.RootElement.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String)
                    return e.GetString() ?? "";
        }
        catch
        {
            /* not JSON */
        }

        return "";
    }

    private static HttpClient Create()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PaxMediaManager/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }
}
