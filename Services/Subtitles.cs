using System.Net;
using System.Text;
using System.Text.Json;

namespace pxdMediaManager.Services;

/// <summary>One subtitle file OpenSubtitles offers for a film or episode.</summary>
public sealed record SubtitleResult(int FileId, string Language, string Release, int Downloads, bool HearingImpaired, string FileName);

/// <summary>
/// OpenSubtitles.com REST API (needs a free API key from opensubtitles.com → API consumers). Searches by IMDb ID
/// (and season/episode for shows), or by file name when there is no ID, and downloads .srt files next to the video,
/// named so the player picks them up automatically.
/// </summary>
public sealed class OpenSubtitlesClient
{
    private const string DefaultApi = "https://api.opensubtitles.com/api/v1";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _key;
    private readonly string _api = Environment.GetEnvironmentVariable("PXDMM_OPENSUBTITLES_API")?.TrimEnd('/') ?? DefaultApi;

    public OpenSubtitlesClient(string key) => _key = key.Trim();

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var r = new HttpRequestMessage(method, _api + path);
        r.Headers.Add("Api-Key", _key);
        // OpenSubtitles requires an identifying User-Agent of the form "AppName vX.Y".
        r.Headers.UserAgent.ParseAdd("pxdMediaManager v1.0");
        r.Headers.Accept.ParseAdd("application/json");
        return r;
    }

    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.SendAsync(Request(HttpMethod.Get, "/infos/formats"), ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, "Valid — OpenSubtitles key accepted.")
                : (false, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "OpenSubtitles rejected this key."
                    : $"OpenSubtitles answered with status {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return (false, "Couldn't reach OpenSubtitles: " + ex.Message);
        }
    }

    /// <summary>
    /// Subtitles for a film (<paramref name="imdbId"/>) or an episode of a show (<paramref name="imdbId"/> is the
    /// show's, plus season and episode). Falls back to searching by the file name. Most downloaded first.
    /// </summary>
    public async Task<List<SubtitleResult>> SearchAsync(string? imdbId, int? season, int? episode, string fileName, string languages, CancellationToken ct)
    {
        var q = new List<string> { "languages=" + Uri.EscapeDataString(languages.ToLowerInvariant().Replace(" ", "")) };
        var numericId = imdbId is { Length: > 2 } ? imdbId.TrimStart('t').TrimStart('0') : "";
        if (numericId.Length > 0 && season is not null && episode is not null)
        {
            q.Add("parent_imdb_id=" + numericId);
            q.Add("season_number=" + season);
            q.Add("episode_number=" + episode);
        }
        else if (numericId.Length > 0)
        {
            q.Add("imdb_id=" + numericId);
        }
        else
        {
            q.Add("query=" + Uri.EscapeDataString(Path.GetFileNameWithoutExtension(fileName)));
        }

        // The API wants its parameters in alphabetical order (it redirects otherwise).
        q.Sort(StringComparer.Ordinal);
        using var response = await Http.SendAsync(Request(HttpMethod.Get, "/subtitles?" + string.Join("&", q)), ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(ErrorText(body, response.StatusCode));

        var results = new List<SubtitleResult>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in data.EnumerateArray())
            {
                if (!d.TryGetProperty("attributes", out var a) || !a.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
                    || files.GetArrayLength() == 0)
                    continue;
                var f = files[0];
                if (!f.TryGetProperty("file_id", out var fid) || !fid.TryGetInt32(out var fileId))
                    continue;
                results.Add(new SubtitleResult(
                    fileId,
                    Str(a, "language"),
                    Str(a, "release"),
                    a.TryGetProperty("download_count", out var dc) && dc.TryGetInt32(out var n) ? n : 0,
                    a.TryGetProperty("hearing_impaired", out var hi) && hi.ValueKind == JsonValueKind.True,
                    Str(f, "file_name")));
            }
        }

        return results.OrderByDescending(r => r.Downloads).ToList();
    }

    /// <summary>
    /// Downloads a subtitle and saves it beside the video as "&lt;video name&gt;.&lt;language&gt;.srt" (numbered if
    /// that exists). Returns the saved path and how many downloads are left today.
    /// </summary>
    public async Task<(string Path, int? Remaining)> DownloadAsync(SubtitleResult sub, string videoPath, CancellationToken ct)
    {
        var request = Request(HttpMethod.Post, "/download");
        request.Content = new StringContent(JsonSerializer.Serialize(new { file_id = sub.FileId }), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(ErrorText(body, response.StatusCode));

        using var doc = JsonDocument.Parse(body);
        var link = Str(doc.RootElement, "link");
        if (link.Length == 0)
            throw new HttpRequestException(ErrorText(body, response.StatusCode));
        int? remaining = doc.RootElement.TryGetProperty("remaining", out var rem) && rem.TryGetInt32(out var r) ? r : null;

        var bytes = await Http.GetByteArrayAsync(link, ct).ConfigureAwait(false);
        var dir = Path.GetDirectoryName(videoPath)!;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var lang = sub.Language.Length > 0 ? sub.Language : "sub";
        var target = Path.Combine(dir, $"{stem}.{lang}.srt");
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(dir, $"{stem}.{lang}.{n}.srt");
        await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
        return (target, remaining);
    }

    private static string ErrorText(string body, HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var name in new[] { "message", "error", "errors" })
                if (doc.RootElement.TryGetProperty(name, out var e))
                    return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString();
        }
        catch
        {
            /* not JSON */
        }

        return status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "OpenSubtitles rejected the key (or today's download limit is used up)."
            : $"OpenSubtitles answered with status {(int)status}.";
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
