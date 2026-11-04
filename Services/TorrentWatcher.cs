using System.Diagnostics;

namespace PaxMediaManager.Services;

/// <summary>
/// Watches a folder for new .torrent files, asks the user what to do, and transfers them to the target folder.
/// </summary>
public sealed class TorrentWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StableWindow = TimeSpan.FromMilliseconds(1500);

    private readonly AppSettings _settings;
    private readonly StatsStore _stats;
    private readonly CompletionMover _mover;
    private readonly Func<string, string, string, Task<ChoiceResult>> _prompt;
    private readonly object _watcherLock = new();
    private readonly HashSet<string> _inFlight = new(AppSettings.PathComparer);

    // "*.torrent" must also find ".TORRENT" on case-sensitive file systems, like the live watcher does.
    private static readonly EnumerationOptions TorrentFiles = new() { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 };
    private readonly SemaphoreSlim _promptLock = new(1, 1);
    private FileSystemWatcher? _watcher;

    public TorrentWatcher(
        AppSettings settings,
        StatsStore stats,
        CompletionMover mover,
        Func<string, string, string, Task<ChoiceResult>> prompt)
    {
        _settings = settings;
        _stats = stats;
        _mover = mover;
        _prompt = prompt;
    }

    /// <summary>Raised from background threads; marshal to the UI thread before touching controls.</summary>
    public event Action<ActivityKind, string, string>? Activity;

    public bool IsWatching { get; private set; }

    public void Start()
    {
        lock (_watcherLock)
        {
            StopCore();

            var folder = _settings.WatchFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                Activity?.Invoke(ActivityKind.Warning, "Watch folder unavailable", folder ?? "(not set)");
                return;
            }

            _watcher = new FileSystemWatcher(folder)
            {
                // Browsers create temp/placeholder names and rename them to .torrent, so watch every name.
                Filter = "*",
                NotifyFilter = NotifyFilters.FileName,
                InternalBufferSize = 65536,
                IncludeSubdirectories = false,
            };
            _watcher.Created += (_, e) => OnPath(e.FullPath);
            _watcher.Renamed += (_, e) => OnPath(e.FullPath);
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;
            IsWatching = true;
        }

        Activity?.Invoke(ActivityKind.Info, "Watching for torrents", _settings.WatchFolder);
    }

    public void Stop()
    {
        lock (_watcherLock)
            StopCore();
    }

    private void StopCore()
    {
        IsWatching = false;
        if (_watcher is null)
            return;

        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        catch
        {
            /* best effort */
        }

        _watcher = null;
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Activity?.Invoke(ActivityKind.Warning, "Watcher overflow — rescanning", e.GetException().Message);
        _ = Task.Run(() =>
        {
            Start();
            try
            {
                foreach (var path in Directory.GetFiles(_settings.WatchFolder, "*.torrent", TorrentFiles))
                    OnPath(path);
            }
            catch
            {
                /* ignore */
            }
        });
    }

    private void OnPath(string fullPath)
    {
        if (!fullPath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
            return;

        string path;
        try
        {
            path = Path.GetFullPath(fullPath);
        }
        catch
        {
            path = fullPath;
        }

        lock (_inFlight)
        {
            if (!_inFlight.Add(path))
                return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await HandleAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Activity?.Invoke(ActivityKind.Error, "Error handling torrent", ex.Message);
            }
            finally
            {
                lock (_inFlight)
                    _inFlight.Remove(path);
            }
        });
    }

    /// <summary>
    /// Finds .torrent files already in the watch folder (e.g. from before the app started) and lets the user decide what to
    /// do with them all at once. Empty files are skipped: they are leftovers from interrupted browser downloads.
    /// </summary>
    public async Task ScanExistingAsync(Func<IReadOnlyList<BatchItem>, Task<BatchResult?>> promptBatch)
    {
        var folder = _settings.WatchFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        var claimed = new List<string>();
        var items = new List<BatchItem>();
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.torrent", TorrentFiles)
                         .Where(f => f.Length > 0)
                         .OrderBy(f => f.LastWriteTime))
            {
                var path = Path.GetFullPath(file.FullName);
                if (_settings.IsIgnored(path))
                    continue;

                // Claim it so the live watcher doesn't also pop up a single-file prompt for the same file.
                lock (_inFlight)
                {
                    if (!_inFlight.Add(path))
                        continue;
                }

                claimed.Add(path);
                items.Add(new BatchItem(path, file.Name, TorrentInfo.TryGetTotalSize(path)));
            }

            if (items.Count == 0)
                return;

            await _promptLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var result = await promptBatch(items).ConfigureAwait(false);
                if (result is null || result.Items.Count == 0)
                    return;

                var done = 0;
                foreach (var selection in result.Items.Where(i => File.Exists(i.Path)))
                {
                    var path = selection.Path;
                    try
                    {
                        // Rows set to "Delete torrent file" are deleted, whatever action button was pressed
                        // (except "leave them for now", which touches nothing).
                        if (selection.CategoryName == CategoryChoices.DeleteLabel && result.Choice != TorrentChoice.IgnoreOnce)
                        {
                            DeleteTorrentFile(path);
                            done++;
                            continue;
                        }

                        switch (result.Choice)
                        {
                            case TorrentChoice.IgnoreForever:
                                _settings.AddIgnored(path);
                                break;
                            case TorrentChoice.IgnoreOnce:
                                break;
                            default:
                                await TransferAsync(path, new ChoiceResult(result.Choice, selection.CategoryName)).ConfigureAwait(false);
                                break;
                        }

                        done++;
                    }
                    catch (Exception ex)
                    {
                        Activity?.Invoke(ActivityKind.Error, "Transfer failed", ex.Message);
                    }
                }

                var verb = result.Choice switch
                {
                    TorrentChoice.IgnoreForever => "ignored permanently",
                    TorrentChoice.IgnoreOnce => "left alone",
                    _ => "handled",
                };
                Activity?.Invoke(ActivityKind.Info, $"{done} existing torrent{(done == 1 ? "" : "s")} {verb}", "");
            }
            finally
            {
                _promptLock.Release();
            }
        }
        finally
        {
            lock (_inFlight)
            {
                foreach (var p in claimed)
                    _inFlight.Remove(p);
            }
        }
    }
    private async Task HandleAsync(string path)
    {
        if (_settings.IsIgnored(path))
            return;

        var name = Path.GetFileName(path);
        if (!await WaitUntilReadyAsync(path).ConfigureAwait(false))
        {
            Activity?.Invoke(ActivityKind.Warning, "Torrent skipped", "A .torrent file stayed empty or unfinished for 30s.");
            return;
        }

        await _promptLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_settings.IsIgnored(path) || !File.Exists(path))
                return;

            var target = _settings.TargetFolder;
            var result = await _prompt(name, _settings.WatchFolder, target).ConfigureAwait(false);
            var choice = result.Choice;

            // "Delete torrent file" in the dropdown wins over either Transfer button (ignoring still just ignores).
            if (result.CategoryName == CategoryChoices.DeleteLabel
                && choice is TorrentChoice.TransferAndDelete or TorrentChoice.TransferOnly)
            {
                DeleteTorrentFile(path);
                return;
            }

            switch (choice)
            {
                case TorrentChoice.IgnoreForever:
                    _settings.AddIgnored(path);
                    Activity?.Invoke(ActivityKind.Info, "Torrent ignored permanently", "");
                    return;
                case TorrentChoice.IgnoreOnce:
                    Activity?.Invoke(ActivityKind.Info, "Torrent ignored this time", "");
                    return;
            }

            await TransferAsync(path, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Activity?.Invoke(ActivityKind.Error, "Transfer failed", ex.Message);
        }
        finally
        {
            _promptLock.Release();
        }
    }

    /// <summary>Sends one torrent to the target folder and queues its finished download for the chosen category.</summary>
    private async Task TransferAsync(string path, ChoiceResult result)
    {
        var name = Path.GetFileName(path);
        var target = _settings.TargetFolder;
        var choice = result.Choice;

            // The target may be a share URL (Linux); connecting to it can take a moment.
            var targetDir = string.IsNullOrWhiteSpace(target)
                ? null
                : await Task.Run(() => NetworkPath.ToLocal(target)).ConfigureAwait(false);
            if (targetDir is null || !Directory.Exists(targetDir))
            {
                Activity?.Invoke(ActivityKind.Error, "Target folder unreachable", string.IsNullOrWhiteSpace(target) ? "No target folder is set." : target);
                return;
            }

            var dest = UniquePath(Path.Combine(targetDir, name));
            var deleteSource = choice == TorrentChoice.TransferAndDelete;
            // Read the content size from the torrent itself before the local copy can be deleted.
            var meta = TorrentInfo.TryRead(path);
            var size = meta?.Size;

            await Task.Run(() =>
            {
                // Copy under a temporary name so anything watching the target never sees a half-written .torrent.
                var temp = dest + ".tmp";
                CopyContents(path, temp);
                File.Move(temp, dest);
                if (deleteSource)
                    File.Delete(path);
            }).ConfigureAwait(false);

            if (deleteSource)
                _settings.RemoveIgnored(path);

            _stats.Add(size ?? 0);
            var sizeText = size is > 0 ? StatsStore.FormatBytes(size.Value) : "size unknown";
            var detail = $"{sizeText}  →  {target}";

            // Queue the finished download for automatic filing into the chosen category.
            var category = _settings.ActiveCategories.FirstOrDefault(c =>
                string.Equals(c.Name, result.CategoryName, StringComparison.OrdinalIgnoreCase));
            if (category is not null)
            {
                var contentName = SafeName(meta?.Name);
                if (contentName is null)
                {
                    detail += "  ·  couldn't read the torrent's name, so it won't be auto-moved";
                }
                else
                {
                    _settings.LastCategory = category.Name;
                    _settings.Save();
                    _mover.Track(new PendingMove
                    {
                        Name = contentName,
                        CategoryName = category.Name,
                        CategoryPath = category.Path,
                        SourceRoot = target,
                        ExpectedBytes = size ?? 0,
                    });
                    detail += $"  ·  will move to {category.Name} when finished";
                }
            }

            Activity?.Invoke(
                ActivityKind.Success,
                deleteSource ? "Torrent sent, local copy removed" : "Torrent sent",
                detail);
    }

    /// <summary>
    /// Plain byte copy. File.Copy first tries to clone the file on Linux, and FUSE-mounted network shares reject
    /// that with "Inappropriate ioctl for device" instead of letting it fall back.
    /// </summary>
    private static void CopyContents(string from, string to)
    {
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    /// <summary>Removes a .torrent file, using the Recycle Bin (Windows) or Trash (Linux) so it can be recovered.</summary>
    private void DeleteTorrentFile(string path)
    {
        try
        {
            var recoverable = true;
            if (OperatingSystem.IsWindows())
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else if (!(OperatingSystem.IsLinux() && TryMoveToTrash(path)))
            {
                File.Delete(path);
                recoverable = false;
            }

            _settings.RemoveIgnored(path);
            Activity?.Invoke(
                ActivityKind.Info,
                "Torrent file deleted",
                !recoverable ? "" : OperatingSystem.IsWindows() ? "Moved to the Recycle Bin." : "Moved to the Trash.");
        }
        catch (Exception ex)
        {
            Activity?.Invoke(ActivityKind.Error, "Couldn't delete torrent file", ex.Message);
        }
    }

    /// <summary>Sends a file to the desktop's Trash with whichever standard tool is installed.</summary>
    private static bool TryMoveToTrash(string path)
    {
        foreach (var (tool, args) in new[]
                 {
                     ("gio", new[] { "trash", path }),
                     ("kioclient", new[] { "move", path, "trash:/" }),
                 })
        {
            try
            {
                var info = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in args)
                    info.ArgumentList.Add(a);
                using var p = Process.Start(info);
                if (p is not null && p.WaitForExit(10_000) && p.ExitCode == 0 && !File.Exists(path))
                    return true;
            }
            catch
            {
                /* tool not installed; try the next one */
            }
        }

        return false;
    }

    /// <summary>The torrent's top-level name as a single safe path segment, or null if it isn't usable.</summary>
    private static string? SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var leaf = Path.GetFileName(name.Replace('\\', '/').TrimEnd('/'));
        return leaf.Length == 0 || leaf is "." or ".." || leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : leaf;
    }

    private static string UniquePath(string dest)
    {
        if (!File.Exists(dest))
            return dest;

        var dir = Path.GetDirectoryName(dest)!;
        var name = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>
    /// Waits until the file is non-empty and its size has stopped changing. Uses metadata only and never opens the
    /// file: Firefox creates an empty placeholder and renames a .part file over it, and any handle we hold on the
    /// placeholder makes that final rename — and so the browser download — fail.
    /// </summary>
    private static async Task<bool> WaitUntilReadyAsync(string path)
    {
        var total = Stopwatch.StartNew();
        var stable = Stopwatch.StartNew();
        long last = -1;

        while (total.Elapsed < ReadyTimeout)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 0)
                {
                    if (info.Length != last)
                    {
                        last = info.Length;
                        stable.Restart();
                    }
                    else if (stable.Elapsed >= StableWindow)
                    {
                        return true;
                    }
                }
                else
                {
                    last = -1;
                    stable.Restart();
                }
            }
            catch (IOException)
            {
                last = -1;
            }
            catch (UnauthorizedAccessException)
            {
                last = -1;
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }

        return false;
    }

    public void Dispose()
    {
        Stop();
        _promptLock.Dispose();
    }
}
