using System.Net;
using System.Text.Json;

namespace PaxMediaManager.Services;

/// <summary>Ratings from other sites, as OMDb reports them. Empty strings mean that site has no score for the title.</summary>
public sealed record OmdbRatings(
    string Imdb,
    string ImdbVotes,
    string RottenTomatoes,
    string Metacritic,
    string Awards,
    string BoxOffice);

/// <summary>
/// Looks up IMDb, Rotten Tomatoes and Metacritic scores on OMDb (omdbapi.com) by IMDb ID. Each title is asked for once
/// and the answer (including "OMDb has nothing") is kept on disk for good, which matters on the free 1,000-a-day plan.
/// </summary>
public sealed class OmdbClient
{
    private const string DefaultApi = "https://www.omdbapi.com/";
    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = false };

    private readonly string _key;
    // Overridable so the client can be tested against a local stand-in server.
    private readonly string _api = Environment.GetEnvironmentVariable("PAXMM_OMDB_API") ?? DefaultApi;

    public OmdbClient(string key) => _key = key.Trim();

    /// <summary>True once OMDb has rejected the key (or the daily limit is used up); no more requests this session.</summary>
    public bool Rejected { get; private set; }

    public static string RatingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaxMediaManager", "ratings");

    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct = default)
    {
        try
        {
            // The Shawshank Redemption: always present, so a failure here is about the key.
            using var response = await Http.GetAsync(Url("tt0111161"), ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (Str(doc.RootElement, "Response") == "True")
                return (true, "Valid — OMDb key accepted.");
            var error = Str(doc.RootElement, "Error");
            return (false, error.Length > 0 ? "OMDb: " + error : $"OMDb answered with status {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return (false, "Couldn't reach OMDb: " + ex.Message);
        }
    }

    /// <summary>Ratings for an IMDb ID ("tt…"), from the cache when seen before; null when unavailable right now.</summary>
    public async Task<OmdbRatings?> GetRatingsAsync(string? imdbId, CancellationToken ct, bool refresh = false)
    {
        if (Rejected || string.IsNullOrWhiteSpace(imdbId) || _key.Length == 0)
            return null;

        var file = Path.Combine(RatingsDir, imdbId.Trim() + ".json");
        if (!refresh)
        {
            try
            {
                if (File.Exists(file) && JsonSerializer.Deserialize<OmdbRatings>(File.ReadAllText(file)) is { } cached)
                    return cached;
            }
            catch
            {
                /* unreadable cache entry: fetch again */
            }
        }

        try
        {
            using var response = await Http.GetAsync(Url(imdbId.Trim()), ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;

            OmdbRatings ratings;
            if (Str(root, "Response") != "True")
            {
                var error = Str(root, "Error");
                // A bad key or an exhausted daily limit is not an answer about this title: don't cache it.
                if (response.StatusCode == HttpStatusCode.Unauthorized || error.Contains("key", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("limit", StringComparison.OrdinalIgnoreCase))
                {
                    Rejected = true;
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                    return null;
                ratings = new OmdbRatings("", "", "", "", "", ""); // OMDb doesn't know the title: remember that
            }
            else
            {
                string Source(string name) =>
                    root.TryGetProperty("Ratings", out var arr) && arr.ValueKind == JsonValueKind.Array
                        ? arr.EnumerateArray().Where(r => Str(r, "Source") == name).Select(r => Str(r, "Value")).FirstOrDefault() ?? ""
                        : "";

                ratings = new OmdbRatings(
                    Clean(Str(root, "imdbRating")),
                    Clean(Str(root, "imdbVotes")),
                    Source("Rotten Tomatoes"),
                    Clean(Str(root, "Metascore")),
                    Clean(Str(root, "Awards")),
                    Clean(Str(root, "BoxOffice")));
            }

            try
            {
                Directory.CreateDirectory(RatingsDir);
                File.WriteAllText(file, JsonSerializer.Serialize(ratings, CacheJson));
            }
            catch
            {
                /* best effort */
            }

            return ratings;
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

    private string Url(string imdbId) => $"{_api}?apikey={Uri.EscapeDataString(_key)}&i={Uri.EscapeDataString(imdbId)}";

    /// <summary>OMDb writes "N/A" for missing values.</summary>
    private static string Clean(string s) => s == "N/A" ? "" : s;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PaxMediaManager/1.0");
        return http;
    }
}
