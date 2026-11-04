using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaxMediaManager.Services;

/// <summary>How far one video file has been watched.</summary>
public sealed class WatchEntry
{
    public string Path { get; set; } = "";
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public DateTime LastWatched { get; set; }
    public bool Completed { get; set; }

    public double Fraction => Completed ? 1 : DurationMs > 0 ? Math.Clamp((double)PositionMs / DurationMs, 0, 1) : 0;
}

/// <summary>A video file of a show with its season and episode numbers read from the file name.</summary>
public sealed record EpisodeFile(int Season, int Episode, string Path, long Bytes);

/// <summary>
/// Remembers where each video was stopped (for "resume" and "continue watching") and which were finished. Kept in
/// %APPDATA%\PaxMediaManager\watch-history.json on this machine.
/// </summary>
public static partial class WatchHistory
{
    private static readonly object Gate = new();
    private static Dictionary<string, WatchEntry>? _entries;
    private static DateTime _lastSave = DateTime.MinValue;

    /// <summary>Raised (on any thread) when progress was recorded, so the library can refresh its progress bars.</summary>
    public static event Action? Changed;

    private static string FilePath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaxMediaManager", "watch-history.json");

    private static string Key(string path) => System.IO.Path.GetFullPath(path).ToLowerInvariant();

    public static WatchEntry? Get(string path)
    {
        lock (Gate)
        {
            Load();
            return _entries!.TryGetValue(Key(path), out var e) ? e : null;
        }
    }

    /// <summary>
    /// Records playback progress. Within the last 5% (or last 3 minutes of a long video) counts as finished, and the
    /// position is cleared so it starts from the beginning next time. Saved at most every 10 seconds unless forced.
    /// </summary>
    public static void Record(string path, TimeSpan position, TimeSpan duration, bool force = false)
    {
        if (duration <= TimeSpan.Zero || position < TimeSpan.FromSeconds(5))
            return;
        var remaining = duration - position;
        var done = position.TotalMilliseconds / duration.TotalMilliseconds >= 0.95
                   || (duration > TimeSpan.FromMinutes(20) && remaining < TimeSpan.FromMinutes(3));
        lock (Gate)
        {
            Load();
            var key = Key(path);
            if (!_entries!.TryGetValue(key, out var e))
                _entries[key] = e = new WatchEntry { Path = System.IO.Path.GetFullPath(path) };
            e.DurationMs = (long)duration.TotalMilliseconds;
            e.LastWatched = DateTime.Now;
            if (done)
            {
                e.Completed = true;
                e.PositionMs = 0;
            }
            else
            {
                e.PositionMs = (long)position.TotalMilliseconds;
                // Re-watching a finished episode: it stays "watched" but resumes where you are now.
            }

            if (force || DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(10))
                Save();
        }

        Changed?.Invoke();
    }

    public static void MarkWatched(IEnumerable<string> paths, bool watched)
    {
        lock (Gate)
        {
            Load();
            foreach (var path in paths)
            {
                var key = Key(path);
                if (watched)
                {
                    if (!_entries!.TryGetValue(key, out var e))
                        _entries[key] = e = new WatchEntry { Path = System.IO.Path.GetFullPath(path) };
                    e.Completed = true;
                    e.PositionMs = 0;
                    e.LastWatched = DateTime.Now;
                }
                else
                {
                    _entries!.Remove(key);
                }
            }

            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>Where to resume a file, or null to start from the beginning.</summary>
    public static TimeSpan? ResumePoint(string path) =>
        Get(path) is { Completed: false, PositionMs: > 30_000 } e ? TimeSpan.FromMilliseconds(e.PositionMs) : null;

    /// <summary>All entries for files inside an item (a film's folder, a show's season folders and episode files).</summary>
    public static List<WatchEntry> ForItem(LibraryItem item)
    {
        var roots = (item.IsGroup ? item.Members : new[] { item }).Select(m => Key(m.Path)).ToList();
        lock (Gate)
        {
            Load();
            return _entries!.Values.Where(e =>
            {
                var k = e.Path.ToLowerInvariant();
                return roots.Any(r => k == r || k.StartsWith(r.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar));
            }).ToList();
        }
    }

    /// <summary>
    /// Progress for a tile: a film is as far as its furthest-watched file; a show is the share of its episodes
    /// finished (or, when the episode count isn't known, the most recent episode's progress).
    /// </summary>
    public static (double Fraction, DateTime? Last, bool Finished) Progress(LibraryItem item, int? episodeCount = null)
    {
        var entries = ForItem(item);
        if (entries.Count == 0)
            return (0, null, false);
        var last = entries.Max(e => e.LastWatched);
        if (episodeCount is > 1)
        {
            var finished = entries.Count(e => e.Completed);
            var partial = entries.Where(e => !e.Completed).Sum(e => e.Fraction);
            var f = Math.Clamp((finished + partial) / episodeCount.Value, 0, 1);
            return (f, last, finished >= episodeCount.Value);
        }

        var best = entries.OrderByDescending(e => e.Completed).ThenByDescending(e => e.Fraction).First();
        return (best.Fraction, last, best.Completed);
    }

    private static void Load()
    {
        if (_entries is not null)
            return;
        try
        {
            _entries = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, WatchEntry>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            _entries = new();
        }
    }

    // Caller holds Gate.
    private static void Save()
    {
        _lastSave = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries));
        }
        catch
        {
            /* best effort */
        }
    }

    public static void Flush()
    {
        lock (Gate)
            if (_entries is not null)
                Save();
    }

    // ---- Episode files ---------------------------------------------------------------------------------------------

    [GeneratedRegex(@"\bS(\d{1,2})[ ._-]?E(\d{1,3})\b|\b(\d{1,2})x(\d{2,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeMarker();

    /// <summary>
    /// Every video file of a show (in its season folders or loose), with season and episode read from the file name,
    /// sorted. Files without a marker are left out. Reads the disk, so call it off the UI thread.
    /// </summary>
    public static List<EpisodeFile> EpisodeFiles(LibraryItem item)
    {
        var result = new List<EpisodeFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in item.IsGroup ? item.Members : new[] { item })
        {
            IEnumerable<FileInfo> files;
            try
            {
                files = root.IsDirectory
                    ? new DirectoryInfo(root.Path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                    : new[] { new FileInfo(root.Path) };
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (var f in files)
                {
                    if (!Viewer.MediaFiles.IsVideo(f.Name) || f.Name.Contains("sample", StringComparison.OrdinalIgnoreCase) || !seen.Add(f.FullName))
                        continue;
                    var m = EpisodeMarker().Match(f.Name.Replace('.', ' '));
                    if (!m.Success)
                        continue;
                    var season = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value);
                    var episode = int.Parse(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value);
                    result.Add(new EpisodeFile(season, episode, f.FullName, f.Length));
                }
            }
            catch
            {
                /* partially readable folder: keep what was found */
            }
        }

        // The same episode twice (e.g. 720p and 1080p copies): keep the bigger file.
        return result.GroupBy(e => (e.Season, e.Episode)).Select(g => g.OrderByDescending(e => e.Bytes).First())
            .OrderBy(e => e.Season).ThenBy(e => e.Episode).ToList();
    }

    /// <summary>
    /// The episode to play next: the one being watched if unfinished, otherwise the one after the most recently
    /// finished. Null when nothing has been watched yet (then "next up" is simply the first episode) or all are done.
    /// </summary>
    public static (EpisodeFile Episode, bool Resume)? NextUp(List<EpisodeFile> episodes)
    {
        if (episodes.Count == 0)
            return null;
        WatchEntry? latest = null;
        var latestIndex = -1;
        for (var i = 0; i < episodes.Count; i++)
        {
            var e = Get(episodes[i].Path);
            if (e is not null && (latest is null || e.LastWatched > latest.LastWatched))
            {
                latest = e;
                latestIndex = i;
            }
        }

        if (latest is null)
            return (episodes[0], false);
        if (!latest.Completed)
            return (episodes[latestIndex], true);
        for (var i = latestIndex + 1; i < episodes.Count; i++)
            if (Get(episodes[i].Path) is not { Completed: true })
                return (episodes[i], false);
        return null;
    }
}
