using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace pxdMediaManager.Services;

/// <summary>A download we're waiting on. The name is needed to find the data and is deleted once the move finishes.</summary>
public sealed class PendingMove
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string CategoryPath { get; set; } = "";
    /// <summary>Folder the torrent client downloads into (the target folder at the time the torrent was sent).</summary>
    public string SourceRoot { get; set; } = "";
    public long ExpectedBytes { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
}

public readonly record struct MoveProgress(long Done, long Total, double BytesPerSecond);

/// <summary>
/// Tracks sent torrents, waits for their data to finish downloading into the target folder, then moves it into the
/// category folder the user chose, reporting progress.
/// </summary>
public sealed class CompletionMover : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StableFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UnknownSizeStableFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Expiry = TimeSpan.FromDays(60);
    private static readonly string[] TempSuffixes = { ".part", ".!qb", ".!ut", ".crdownload", ".aria2", ".bc!" };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly Dictionary<Guid, (long Size, DateTime Since)> _lastSeen = new();
    private readonly CancellationTokenSource _cts = new();
    private List<PendingMove> _pending = new();
    private bool _busy;

    public event Action<ActivityKind, string, string>? Activity;
    public event Action? PendingChanged;
    public event Action<string, string, long>? MoveStarted;  // category name, item name, total bytes
    public event Action<MoveProgress>? MoveProgressed;
    public event Action<bool, string>? MoveFinished;          // success, message
    public event Action<string, string>? ItemArrived;         // category name, where it landed (file or folder)

    public static string PendingFile =>
        System.IO.Path.Combine(AppPaths.DataDir, "pending.json");

    public IReadOnlyList<PendingMove> Snapshot()
    {
        lock (_lock)
            return _pending.ToList();
    }

    public void Start()
    {
        try
        {
            if (File.Exists(PendingFile))
                _pending = JsonSerializer.Deserialize<List<PendingMove>>(File.ReadAllText(PendingFile)) ?? new();
        }
        catch
        {
            _pending = new();
        }

        PendingChanged?.Invoke();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Track(PendingMove move)
    {
        lock (_lock)
        {
            _pending.Add(move);
            Save();
        }

        PendingChanged?.Invoke();
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _pending.Clear();
            _lastSeen.Clear();
            Save();
        }

        PendingChanged?.Invoke();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                await CheckAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Activity?.Invoke(ActivityKind.Warning, "Download tracking hiccup", ex.Message);
            }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        if (_busy)
            return;

        List<PendingMove> items;
        lock (_lock)
            items = _pending.ToList();

        foreach (var p in items)
        {
            if (DateTime.Now - p.Added > Expiry)
            {
                Remove(p);
                Activity?.Invoke(ActivityKind.Warning, "Stopped tracking a download", "It never appeared in the target folder.");
                continue;
            }

            if (!IsComplete(p, out var path, out var isDir))
                continue;

            _busy = true;
            try
            {
                await MoveAsync(p, path, isDir, ct).ConfigureAwait(false);
            }
            finally
            {
                _busy = false;
            }

            return; // one move per cycle
        }
    }

    private void Remove(PendingMove p)
    {
        lock (_lock)
        {
            _pending.RemoveAll(x => x.Id == p.Id);
            _lastSeen.Remove(p.Id);
            Save();
        }

        PendingChanged?.Invoke();
    }

    /// <summary>
    /// A download counts as finished when it exists at full expected size, has no client temp files, and its size has
    /// stopped changing. Clients that pre-allocate full-size files can't be told apart from finished ones this way.
    /// </summary>
    private bool IsComplete(PendingMove p, out string path, out bool isDir)
    {
        path = "";
        isDir = false;
        long size;

        try
        {
            // The download folder may be a share URL (Linux); null means the share isn't reachable right now.
            if (NetworkPath.ToLocal(p.SourceRoot) is not { Length: > 0 } root)
                return false;

            var local = Path.Combine(root, p.Name);
            path = local;
            if (File.Exists(local))
            {
                if (TempSuffixes.Any(s => File.Exists(local + s)))
                    return false;
                size = new FileInfo(local).Length;
            }
            else if (Directory.Exists(local))
            {
                isDir = true;
                size = 0;
                foreach (var f in new DirectoryInfo(local).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (TempSuffixes.Any(s => f.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                        return false;
                    size += f.Length;
                }
            }
            else
            {
                return false;
            }
        }
        catch
        {
            return false; // share hiccup; try again next cycle
        }

        if (size <= 0)
            return false;
        if (p.ExpectedBytes > 0 && size < p.ExpectedBytes)
        {
            _lastSeen[p.Id] = (size, DateTime.Now);
            return false;
        }

        var now = DateTime.Now;
        if (!_lastSeen.TryGetValue(p.Id, out var seen) || seen.Size != size)
        {
            _lastSeen[p.Id] = (size, now);
            return false;
        }

        return now - seen.Since >= (p.ExpectedBytes > 0 ? StableFor : UnknownSizeStableFor);
    }

    private async Task MoveAsync(PendingMove p, string source, bool isDir, CancellationToken ct)
    {
        try
        {
            var categoryDir = NetworkPath.ToLocal(p.CategoryPath);
            if (string.IsNullOrEmpty(categoryDir) || !Directory.Exists(categoryDir))
            {
                Activity?.Invoke(ActivityKind.Error, $"Can't reach the {p.CategoryName} folder", p.CategoryPath);
                return; // keep tracking; it may come back
            }

            var total = isDir ? DirSize(source) : new FileInfo(source).Length;
            var dest = UniquePath(Path.Combine(categoryDir, p.Name), isDir);
            MoveStarted?.Invoke(p.CategoryName, Path.GetFileName(dest), total);

            if (TryRenameInPlace(source, dest, categoryDir, isDir))
            {
                // Same share/drive: a rename is instant and needs no copying.
                MoveProgressed?.Invoke(new MoveProgress(total, total, 0));
            }
            else
            {
                await CopyAsync(source, dest, isDir, total, ct).ConfigureAwait(false);
                try
                {
                    if (isDir)
                        Directory.Delete(source, recursive: true);
                    else
                        File.Delete(source);
                }
                catch (Exception ex)
                {
                    Activity?.Invoke(ActivityKind.Warning, "Copied, but couldn't remove the original", ex.Message);
                }
            }

            Remove(p);
            var msg = $"{StatsStore.FormatBytes(total)} moved to {p.CategoryName}";
            Activity?.Invoke(ActivityKind.Success, "Download moved", msg);
            MoveFinished?.Invoke(true, msg);
            ItemArrived?.Invoke(p.CategoryName, dest);
        }
        catch (OperationCanceledException)
        {
            MoveFinished?.Invoke(false, "Cancelled");
        }
        catch (Exception ex)
        {
            Activity?.Invoke(ActivityKind.Error, "Move failed", ex.Message);
            MoveFinished?.Invoke(false, "Move failed");
        }
    }

    private async Task CopyAsync(string source, string dest, bool isDir, long total, CancellationToken ct)
    {
        var temp = dest + ".moving";
        long done = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long lastDone = 0;
        var speed = 0.0;

        void Report(bool force = false)
        {
            if (!force && clock.Elapsed - lastReport < TimeSpan.FromMilliseconds(200))
                return;
            var dt = (clock.Elapsed - lastReport).TotalSeconds;
            if (dt > 0)
                speed = speed == 0 ? (done - lastDone) / dt : speed * 0.7 + (done - lastDone) / dt * 0.3;
            lastReport = clock.Elapsed;
            lastDone = done;
            MoveProgressed?.Invoke(new MoveProgress(done, total, speed));
        }

        async Task CopyFile(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            await using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
            var buffer = new byte[4 << 20];
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                Report();
            }
        }

        try
        {
            if (isDir)
            {
                Directory.CreateDirectory(temp);
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    await CopyFile(file, Path.Combine(temp, Path.GetRelativePath(source, file))).ConfigureAwait(false);
                // Keep empty folders too.
                foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(Path.Combine(temp, Path.GetRelativePath(source, dir)));
                Directory.Move(temp, dest);
            }
            else
            {
                await CopyFile(source, temp).ConfigureAwait(false);
                File.Move(temp, dest);
            }

            Report(force: true);
        }
        catch
        {
            // Don't leave a half-copied ".moving" item behind in the category folder.
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, true);
                else if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                /* best effort */
            }

            throw;
        }
    }

    private static long DirSize(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    /// <summary>Moves by renaming when source and destination are on the same volume; false means "copy instead".</summary>
    private static bool TryRenameInPlace(string source, string dest, string destFolder, bool isDir)
    {
        // Unix paths don't show which volume they are on (everything is under "/"), so just ask for a rename: the
        // system refuses one that would cross volumes, where File.Move would quietly copy with no progress to show.
        if (!OperatingSystem.IsWindows())
            return rename(source, dest) == 0;

        if (!SameVolume(source, destFolder))
            return false;

        if (isDir)
            Directory.Move(source, dest);
        else
            File.Move(source, dest);
        return true;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int rename(string oldPath, string newPath);

    private static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetPathRoot(Path.GetFullPath(a)),
                Path.GetPathRoot(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string UniquePath(string dest, bool isDir)
    {
        bool Exists(string p) => isDir ? Directory.Exists(p) : File.Exists(p);
        if (!Exists(dest))
            return dest;

        var dir = Path.GetDirectoryName(dest)!;
        var name = isDir ? Path.GetFileName(dest) : Path.GetFileNameWithoutExtension(dest);
        var ext = isDir ? "" : Path.GetExtension(dest);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    // Caller holds _lock.
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingFile)!);
            File.WriteAllText(PendingFile, JsonSerializer.Serialize(_pending, JsonOptions));
        }
        catch
        {
            /* best effort */
        }
    }

    public void Dispose() => _cts.Cancel();
}
