using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaxMediaManager.Services;

/// <summary>Where shared settings stand, for display in the UI.</summary>
public sealed record SharedSettingsInfo(bool Enabled, bool Reachable, string FilePath, IReadOnlyList<string> Machines, string Message);

/// <summary>
/// Optional settings shared between machines. One JSON file in a shared folder holds a section per machine, because
/// paths differ per machine (<c>\\NAS\x</c> on Windows, <c>smb://</c> or a mount on Linux). Every machine reads and
/// writes only its own section, and sees the category names the other machines use so they can be filled in locally.
/// The machine's own copy in the app-data folder always stays as an offline fallback.
/// </summary>
public sealed partial class AppSettings
{
    public const string SharedFileName = "torrentmonitor-settings.json";

    private static readonly JsonSerializerOptions SharedJson = new() { WriteIndented = true };
    private static readonly TimeSpan SharedReadTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _sharedGate = new(1, 1);

    /// <summary>Folder holding the shared settings file (a path, or an smb:// URL on Linux). Empty = this machine only.</summary>
    [JsonIgnore]
    public string SettingsFolder { get; set; } = "";

    [JsonIgnore]
    public SharedSettingsInfo SharedInfo { get; private set; } =
        new(false, false, "", Array.Empty<string>(), "Settings are kept on this machine only.");

    /// <summary>Raised from background threads when <see cref="SharedInfo"/> changes.</summary>
    public event Action? SharedInfoChanged;

    /// <summary>Categories that have a destination on this machine (names learned from other machines may still lack one).</summary>
    [JsonIgnore]
    public IReadOnlyList<Category> ActiveCategories
    {
        get
        {
            lock (_lock)
                return Categories.Where(c => !string.IsNullOrWhiteSpace(c.Path)).ToList();
        }
    }

    private static string PointerFile =>
        Path.Combine(Path.GetDirectoryName(SettingsFile)!, "location.json");

    // ---- Pointer: which folder this machine uses (always stored locally) ----------------------------------------

    private void LoadPointer()
    {
        try
        {
            if (File.Exists(PointerFile))
                SettingsFolder = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(PointerFile))?.SettingsFolder ?? "";
        }
        catch
        {
            SettingsFolder = "";
        }
    }

    private void SavePointer()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PointerFile)!);
            File.WriteAllText(PointerFile, JsonSerializer.Serialize(new Pointer { SettingsFolder = SettingsFolder }, SharedJson));
        }
        catch
        {
            /* best effort */
        }
    }

    // ---- Reading -------------------------------------------------------------------------------------------------

    private enum ReadStatus { Ok, Missing, Unreachable, Unreadable }

    private (SharedFile? File, ReadStatus Status, string Path) ReadShared()
    {
        var local = NetworkPath.ToLocal(SettingsFolder);
        if (local is null || !Directory.Exists(local))
            return (null, ReadStatus.Unreachable, "");

        var path = System.IO.Path.Combine(local, SharedFileName);
        if (!File.Exists(path))
            return (new SharedFile(), ReadStatus.Missing, path);

        try
        {
            var file = JsonSerializer.Deserialize<SharedFile>(File.ReadAllText(path));
            return file is null ? (null, ReadStatus.Unreadable, path) : (file, ReadStatus.Ok, path);
        }
        catch
        {
            return (null, ReadStatus.Unreadable, path);
        }
    }

    /// <summary>Same as <see cref="ReadShared"/> but gives up after a few seconds so a dead share can't stall startup.</summary>
    private (SharedFile? File, ReadStatus Status, string Path) ReadSharedWithTimeout()
    {
        var task = Task.Run(ReadShared);
        return task.Wait(SharedReadTimeout) ? task.Result : (null, ReadStatus.Unreachable, "");
    }

    /// <summary>
    /// Brings in the shared file: this machine's section wins if it is newer than the local copy, and category names used
    /// on other machines are added (without a path) so they can be filled in here.
    /// </summary>
    /// <returns>True when the local copy is newer (or there is no section yet) and should be written back.</returns>
    private bool MergeShared(DateTime localStampUtc)
    {
        if (string.IsNullOrWhiteSpace(SettingsFolder))
        {
            SetShared(new SharedSettingsInfo(false, false, "", Array.Empty<string>(), "Settings are kept on this machine only."));
            return false;
        }

        var (file, status, path) = ReadSharedWithTimeout();
        var shownPath = string.IsNullOrEmpty(path) ? System.IO.Path.Combine(SettingsFolder, SharedFileName) : path;

        if (status == ReadStatus.Unreachable)
        {
            SetShared(new SharedSettingsInfo(true, false, shownPath, Array.Empty<string>(),
                "The shared settings folder isn't reachable right now. Using this machine's saved copy."));
            return false;
        }

        if (status == ReadStatus.Unreadable || file is null)
        {
            SetShared(new SharedSettingsInfo(true, false, shownPath, Array.Empty<string>(),
                "The shared settings file couldn't be read, so it was left untouched."));
            return false;
        }

        var pushBack = true;
        if (file.Machines.TryGetValue(MachineId, out var mine))
        {
            if (mine.UpdatedUtc > localStampUtc)
            {
                lock (_lock)
                {
                    WatchFolder = mine.WatchFolder;
                    TargetFolder = mine.TargetFolder;
                    SyncFolder = mine.SyncFolder;
                    Categories = mine.Categories.ToList();
                    LastCategory = mine.LastCategory;
                    IgnoredPaths = mine.IgnoredPaths.ToList();
                }

                pushBack = false;
            }
        }

        // Category names from the other machines, so a category added elsewhere shows up here to fill in.
        lock (_lock)
        {
            foreach (var other in file.Machines.Where(m => m.Key != MachineId).SelectMany(m => m.Value.Categories))
            {
                if (!string.IsNullOrWhiteSpace(other.Name)
                    && !Categories.Any(c => string.Equals(c.Name, other.Name, StringComparison.OrdinalIgnoreCase)))
                    Categories.Add(new Category { Name = other.Name, Path = "" });
            }
        }

        SetShared(Describe(file, shownPath, includeSelf: true));
        return pushBack;
    }

    // ---- Writing -------------------------------------------------------------------------------------------------

    /// <summary>Called from <see cref="Save"/>: writes this machine's section to the shared file in the background.</summary>
    private void QueueSharedSave()
    {
        if (string.IsNullOrWhiteSpace(SettingsFolder))
            return;

        MachineSection section;
        lock (_lock)
        {
            section = new MachineSection
            {
                Name = Environment.MachineName,
                UpdatedUtc = DateTime.UtcNow,
                WatchFolder = WatchFolder,
                TargetFolder = TargetFolder,
                SyncFolder = SyncFolder,
                Categories = Categories.Select(c => new Category { Name = c.Name, Path = c.Path, ExcludeFromLibrary = c.ExcludeFromLibrary }).ToList(),
                LastCategory = LastCategory,
                IgnoredPaths = IgnoredPaths.ToList(),
            };
        }

        _ = Task.Run(() => WriteShared(section));
    }

    private async Task WriteShared(MachineSection section)
    {
        await _sharedGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var (file, status, path) = ReadShared();
            var shownPath = string.IsNullOrEmpty(path) ? System.IO.Path.Combine(SettingsFolder, SharedFileName) : path;

            if (status == ReadStatus.Unreachable)
            {
                SetShared(new SharedSettingsInfo(true, false, shownPath, Array.Empty<string>(),
                    "The shared settings folder isn't reachable. Changes are saved on this machine and will be shared next time."));
                return;
            }

            // Never overwrite a file we couldn't understand: it may hold other machines' settings.
            if (status == ReadStatus.Unreadable || file is null)
            {
                SetShared(new SharedSettingsInfo(true, false, shownPath, Array.Empty<string>(),
                    "The shared settings file couldn't be read, so it was left untouched."));
                return;
            }

            file.Machines[MachineId] = section;
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, SharedJson));
            File.Move(temp, path, overwrite: true);
            SetShared(Describe(file, path, includeSelf: true));
        }
        catch (Exception ex)
        {
            SetShared(new SharedSettingsInfo(true, false, "", Array.Empty<string>(), "Couldn't update the shared settings: " + ex.Message));
        }
        finally
        {
            _sharedGate.Release();
        }
    }

    /// <summary>
    /// Applies a changed <see cref="SettingsFolder"/> (call off the UI thread; it may wait on the network): remembers it,
    /// pulls in other machines' category names, and leaves the caller to <see cref="Save"/> so this machine's section is written.
    /// </summary>
    public void AdoptSettingsFolder()
    {
        SavePointer();
        // What the user has just entered counts as newest, so adopting a folder never overwrites it with older data.
        MergeShared(DateTime.UtcNow);
    }

    private SharedSettingsInfo Describe(SharedFile file, string path, bool includeSelf)
    {
        var names = file.Machines
            .Where(m => includeSelf || m.Key != MachineId)
            .Select(m => m.Key == MachineId ? m.Value.Name + " (this machine)" : m.Value.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var others = file.Machines.Count(m => m.Key != MachineId);
        return new SharedSettingsInfo(
            true,
            true,
            path,
            names,
            others == 0
                ? "Sharing is on. No other machines have joined yet."
                : $"Shared with {others} other machine{(others == 1 ? "" : "s")}: " +
                  string.Join(", ", file.Machines.Where(m => m.Key != MachineId).Select(m => m.Value.Name)) + ".");
    }

    private void SetShared(SharedSettingsInfo info)
    {
        SharedInfo = info;
        SharedInfoChanged?.Invoke();
    }

    // ---- File format ---------------------------------------------------------------------------------------------

    private sealed class Pointer
    {
        public string SettingsFolder { get; set; } = "";
    }

    private sealed class SharedFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, MachineSection> Machines { get; set; } = new();
    }

    private sealed class MachineSection
    {
        public string Name { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
        public string WatchFolder { get; set; } = "";
        public string TargetFolder { get; set; } = "";
        public string SyncFolder { get; set; } = "";
        public List<Category> Categories { get; set; } = new();
        public string? LastCategory { get; set; }
        public List<string> IgnoredPaths { get; set; } = new();
    }
}
