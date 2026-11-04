using LibVLCSharp.Shared;
using PaxMediaManager.Viewer;

namespace PaxMediaManager.Services;

/// <summary>Technical details of the main video file of a library item: length, codecs, resolution, audio and subtitle tracks.</summary>
public sealed class MediaSpecs
{
    public string FilePath { get; init; } = "";
    public long FileBytes { get; init; }
    public int VideoFileCount { get; init; }
    public TimeSpan? Duration { get; set; }
    public List<string> Video { get; init; } = new();
    public List<string> Audio { get; init; } = new();
    public List<string> Subtitles { get; init; } = new();
    /// <summary>Pixel size of the main video track (0 when unknown), for the resolution filter and stats.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Last-write time of the file when it was read, so a replaced file is read again.</summary>
    public DateTime FileTime { get; init; }

    /// <summary>"4K", "1080p", "720p", "SD" or "" when unknown.</summary>
    public string Resolution => Width > 0 || Height > 0 ? ResolutionName(Width, Height) : "";

    private static string DiskCacheFile =>
        System.IO.Path.Combine(AppPaths.DataDir, "specs-cache.json");

    private static Dictionary<string, MediaSpecs>? _disk;
    private static DateTime _diskDirtySince = DateTime.MaxValue;

    /// <summary>
    /// Finds the biggest video file in the item (the film itself, not samples or extras) and asks LibVLC to read its
    /// headers, without playing it. Null if the item holds no video. Results are kept on disk, keyed by file path,
    /// size and date, so each file is only ever read once.
    /// </summary>
    public static async Task<MediaSpecs?> ProbeAsync(LibraryItem item, CancellationToken ct)
    {
        var (file, bytes, count) = await Task.Run(() => FindMainVideo(item), ct).ConfigureAwait(false);
        if (file is null)
            return null;
        var fileTime = File.GetLastWriteTimeUtc(file);

        lock (Results)
        {
            if (Results.TryGetValue(file, out var known))
                return known;
            LoadDisk();
            if (_disk!.TryGetValue(file.ToLowerInvariant(), out var stored) && stored.FileBytes == bytes && stored.FileTime == fileTime)
            {
                var copy = new MediaSpecs
                {
                    FilePath = file, FileBytes = bytes, VideoFileCount = count, FileTime = fileTime, Duration = stored.Duration,
                    Video = stored.Video, Audio = stored.Audio, Subtitles = stored.Subtitles, Width = stored.Width, Height = stored.Height,
                };
                Results[file] = copy;
                return copy;
            }
        }

        var specs = new MediaSpecs { FilePath = file, FileBytes = bytes, VideoFileCount = count, FileTime = fileTime };
        await ParseGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var vlc = VideoPlayer.Engine();
            // Deliberately never disposed: LibVLC keeps using a Media for a while after reporting the parse "Done", and
            // disposing it then crashes the whole process (access violation in libvlc). Each one is only a few KB, and
            // results are remembered per file, so a file is parsed at most once per run.
            var media = new Media(vlc, file, FromType.FromPath);
            lock (KeepAlive)
                KeepAlive.Add(media);
            // No cancellation token: stopping a parse half way has the same use-after-free risk. LibVLC's own timeout ends it.
            await media.Parse(MediaParseOptions.ParseLocal, 20000).ConfigureAwait(false);
            if (media.Duration > 0)
                specs.Duration = TimeSpan.FromMilliseconds(media.Duration);

            foreach (var t in media.Tracks)
            {
                switch (t.TrackType)
                {
                    case TrackType.Video:
                    {
                        var v = t.Data.Video;
                        var parts = new List<string>();
                        var codec = Codec(media, t);
                        if (codec.Length > 0)
                            parts.Add(codec);
                        if (v.Width > 0 && v.Height > 0)
                        {
                            parts.Add($"{v.Width}×{v.Height} ({ResolutionName((int)v.Width, (int)v.Height)})");
                            if (specs.Width == 0)
                            {
                                specs.Width = (int)v.Width;
                                specs.Height = (int)v.Height;
                            }
                        }
                        if (v.FrameRateDen > 0 && v.FrameRateNum > 0)
                            parts.Add($"{(double)v.FrameRateNum / v.FrameRateDen:0.###} fps");
                        if (t.Bitrate > 0)
                            parts.Add($"{t.Bitrate / 1_000_000.0:0.0} Mb/s");
                        specs.Video.Add(string.Join("  ·  ", parts));
                        break;
                    }
                    case TrackType.Audio:
                    {
                        var a = t.Data.Audio;
                        var parts = new List<string>();
                        if (HasLanguage(t))
                            parts.Add(t.Language!);
                        var codec = Codec(media, t);
                        if (codec.Length > 0)
                            parts.Add(codec);
                        if (a.Channels > 0)
                            parts.Add(ChannelName((int)a.Channels));
                        if (a.Rate > 0)
                            parts.Add($"{a.Rate / 1000.0:0.#} kHz");
                        if (t.Bitrate > 0)
                            parts.Add($"{t.Bitrate / 1000} kb/s");
                        specs.Audio.Add(string.Join("  ·  ", parts));
                        break;
                    }
                    case TrackType.Text:
                        specs.Subtitles.Add(HasLanguage(t) ? t.Language! : (t.Description ?? "Subtitle"));
                        break;
                }
            }
        }
        catch
        {
            /* the file info (size, name) is still worth showing without the stream details */
        }
        finally
        {
            ParseGate.Release();
        }

        lock (Results)
        {
            Results[file] = specs;
            // Only remember on disk what was actually read (a failed parse is retried next run).
            if (specs.Duration is not null || specs.Video.Count > 0)
            {
                LoadDisk();
                _disk![file.ToLowerInvariant()] = specs;
                if (_diskDirtySince == DateTime.MaxValue)
                    _diskDirtySince = DateTime.UtcNow;
                if (DateTime.UtcNow - _diskDirtySince > TimeSpan.FromSeconds(15))
                    SaveDisk();
            }
        }

        return specs;
    }

    /// <summary>Writes pending results to disk (called after a background pass and on exit).</summary>
    public static void Flush()
    {
        lock (Results)
            if (_diskDirtySince != DateTime.MaxValue)
                SaveDisk();
    }

    // Caller holds the Results lock.
    private static void LoadDisk()
    {
        if (_disk is not null)
            return;
        try
        {
            _disk = File.Exists(DiskCacheFile)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, MediaSpecs>>(File.ReadAllText(DiskCacheFile)) ?? new()
                : new();
        }
        catch
        {
            _disk = new();
        }
    }

    // Caller holds the Results lock.
    private static void SaveDisk()
    {
        _diskDirtySince = DateTime.MaxValue;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiskCacheFile)!);
            File.WriteAllText(DiskCacheFile, System.Text.Json.JsonSerializer.Serialize(_disk));
        }
        catch
        {
            /* best effort */
        }
    }

    private static readonly SemaphoreSlim ParseGate = new(1, 1);
    private static readonly List<Media> KeepAlive = new();
    private static readonly Dictionary<string, MediaSpecs> Results = new(StringComparer.OrdinalIgnoreCase);

    private static string Codec(Media media, MediaTrack t)
    {
        try
        {
            var d = media.CodecDescription(t.TrackType, t.Codec);
            if (!string.IsNullOrWhiteSpace(d))
                return d;
        }
        catch
        {
            /* fall through to the raw four-character code */
        }

        var c = t.Codec;
        var chars = new[] { (char)(c & 0xFF), (char)((c >> 8) & 0xFF), (char)((c >> 16) & 0xFF), (char)((c >> 24) & 0xFF) };
        return new string(chars).Trim().Trim('\0');
    }

    private static bool HasLanguage(MediaTrack t) => !string.IsNullOrEmpty(t.Language) && t.Language is not ("und" or "unknown");

    private static string ResolutionName(int w, int h) =>
        w >= 3400 || h >= 2000 ? "4K" : w >= 2400 || h >= 1300 ? "1440p" : w >= 1800 || h >= 1000 ? "1080p" : w >= 1200 || h >= 700 ? "720p" : "SD";

    private static string ChannelName(int ch) => ch switch
    {
        1 => "mono", 2 => "stereo", 6 => "5.1", 7 => "6.1", 8 => "7.1", _ => ch + " ch",
    };

    private static (string? File, long Bytes, int Count) FindMainVideo(LibraryItem item)
    {
        try
        {
            if (!item.IsDirectory)
                return MediaFiles.IsVideo(item.Path) && File.Exists(item.Path) ? (item.Path, new FileInfo(item.Path).Length, 1) : (null, 0, 0);

            string? best = null;
            long bestSize = -1;
            var count = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
            foreach (var f in new DirectoryInfo(item.Path).EnumerateFiles("*", options))
            {
                if (!MediaFiles.IsVideo(f.Name))
                    continue;
                count++;
                if (f.Length > bestSize)
                {
                    best = f.FullName;
                    bestSize = f.Length;
                }
            }

            return (best, Math.Max(bestSize, 0), count);
        }
        catch
        {
            return (null, 0, 0);
        }
    }
}
