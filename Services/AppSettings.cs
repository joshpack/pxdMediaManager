using System.Text.Json;

namespace PaxMediaManager.Services;

/// <summary>Persisted user settings plus the "ignore forever" list, stored as JSON in the per-user app-data folder.</summary>
public sealed partial class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();

    public string WatchFolder { get; set; } = DefaultWatchFolder;
    public string TargetFolder { get; set; } = DefaultTargetFolder;
    public List<string> IgnoredPaths { get; set; } = new();
    public List<Category> Categories { get; set; } = new();
    public string? LastCategory { get; set; }

    /// <summary>Folder shared between machines (e.g. on the NAS) where each one keeps its own stats file. Empty = no syncing.</summary>
    /// <summary>Older separate stats-sync folder; now only used when no shared settings folder is set.</summary>
    public string SyncFolder { get; set; } = "";

    /// <summary>Combine download stats with other machines through the shared settings folder.</summary>
    public bool ShareStats { get; set; } = true;

    /// <summary>Where the per-machine stats files go: the shared settings folder (or the old sync folder), or nowhere.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveSyncFolder =>
        !ShareStats ? "" : !string.IsNullOrWhiteSpace(SettingsFolder) ? SettingsFolder : SyncFolder;

    /// <summary>
    /// TMDB API key or Read Access Token for movie/TV posters. Deliberately kept out of the shared settings file: it is a
    /// credential, so it stays in this machine's own settings only.
    /// </summary>
    public string TmdbKey { get; set; } = "";

    /// <summary>OMDb API key for IMDb / Rotten Tomatoes / Metacritic ratings. Local only, like <see cref="TmdbKey"/>.</summary>
    public string OmdbKey { get; set; } = "";

    /// <summary>MDBList API key: ratings from many sites (Trakt, Letterboxd, RT audience, Metacritic users...). Local only.</summary>
    public string MdbListKey { get; set; } = "";

    /// <summary>OpenSubtitles.com API key for the subtitle finder. Local only.</summary>
    public string OpenSubtitlesKey { get; set; } = "";

    /// <summary>Subtitle languages to search for, comma separated ISO codes (e.g. "en" or "en,es").</summary>
    public string SubtitleLanguages { get; set; } = "en";

    /// <summary>Show a pop-up with poster and rating when a finished download lands in a library category.</summary>
    public bool ArrivalAlerts { get; set; } = true;



    /// <summary>Library layout preference: "grid" or "carousel". Per machine, so it is not part of the shared file.</summary>
    public string LibraryLayout { get; set; } = "grid";

    /// <summary>Window size remembered for each tab ("Library", "Settings", …). Per machine and screen, so never shared.</summary>
    public Dictionary<string, TabSize> TabSizes { get; set; } = new();

    /// <summary>Identifies this machine in synced stats; generated once on first run.</summary>
    public string MachineId { get; set; } = "";

    public static string DefaultWatchFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    // No default: the user picks their own library folder or share.
    public static string DefaultTargetFolder => "";

    public static string SettingsFile =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PaxMediaManager",
            "settings.json");

    /// <summary>True when no settings file existed yet (first launch).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFirstRun { get; private set; }

    public static AppSettings Load()
    {
        AppSettings settings;
        var firstRun = !File.Exists(SettingsFile);
        // When this machine last changed its settings, read before Load rewrites the file below.
        var localStamp = firstRun ? DateTime.MinValue : File.GetLastWriteTimeUtc(SettingsFile);
        try
        {
            settings = firstRun
                ? new AppSettings()
                : JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
        }
        catch
        {
            settings = new AppSettings();
        }

        if (firstRun)
            settings.ImportLegacyIgnoredPaths();

        settings.IsFirstRun = firstRun;
        // Older versions stored raw paths; convert anything that is not already a 64-char hash.
        settings.IgnoredPaths = settings.IgnoredPaths
            .Select(p => p.Length == 64 && p.All(Uri.IsHexDigit) ? p.ToUpperInvariant() : HashPath(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!firstRun)
            settings.Save();
        if (string.IsNullOrEmpty(settings.MachineId))
        {
            settings.MachineId = Guid.NewGuid().ToString("N");
            settings.Save();
        }

        // Optional shared settings file: pull in this machine's newer section and other machines' category names.
        settings.LoadPointer();
        if (settings.MergeShared(localStamp))
            settings.Save();

        return settings;
    }

    /// <summary>Saves to this machine's copy and shares this machine's section if a shared settings folder is set.</summary>
    public void Save()
    {
        SaveLocal();
        QueueSharedSave();
    }

    /// <summary>Saves this machine's copy only. For per-machine, frequently changing values such as window sizes.</summary>
    public void SaveLocal()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch
            {
                /* best effort */
            }
        }
    }

    // The ignore list stores SHA-256 hashes of paths, so the settings file never reveals what was ignored.
    public static string HashPath(string fullPath)
    {
        var normalized = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? fullPath.ToLowerInvariant() : fullPath;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
    }

    public bool IsIgnored(string fullPath)
    {
        var h = HashPath(fullPath);
        lock (_lock)
            return IgnoredPaths.Contains(h, StringComparer.Ordinal);
    }

    public void AddIgnored(string fullPath)
    {
        lock (_lock)
        {
            var h = HashPath(fullPath);
            if (!IgnoredPaths.Contains(h, StringComparer.Ordinal))
                IgnoredPaths.Add(h);
        }

        Save();
    }

    public void RemoveIgnored(string fullPath)
    {
        lock (_lock)
            IgnoredPaths.RemoveAll(p => p == HashPath(fullPath));
        Save();
    }

    public void ClearIgnored()
    {
        lock (_lock)
            IgnoredPaths.Clear();
        Save();
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public int IgnoredCount
    {
        get
        {
            lock (_lock)
                return IgnoredPaths.Count;
        }
    }

    /// <summary>How this platform's file systems compare names: Linux is case-sensitive, Windows and macOS are not.</summary>
    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>Carries over the ignore list from the old WinForms version of the app.</summary>
    private void ImportLegacyIgnoredPaths()
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TorrentDownloadMonitor",
                "ignored-torrent-paths.txt");
            if (!File.Exists(legacy))
                return;

            foreach (var line in File.ReadAllLines(legacy))
            {
                var t = line.Trim();
                if (t.Length > 0)
                    IgnoredPaths.Add(t);
            }
        }
        catch
        {
            /* ignore */
        }
    }
}
