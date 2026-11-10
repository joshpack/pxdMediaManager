using pxdStandard;

namespace pxdMediaManager.Services;

/// <summary>
/// One-time moves from the app's former names, run at start-up before anything reads settings. Every step is safe to
/// repeat (it does nothing once the new place exists or the old one is gone) and never throws.
/// </summary>
public static class Migration
{
    /// <summary>The app's very first name, whose data folder is copied when no newer one exists.</summary>
    private const string FirstName = "TorrentMonitor";

    /// <summary>Data folders and the login entry. Skipped when PXDMM_DATA_DIR points the data somewhere else (tests).</summary>
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("PXDMM_DATA_DIR") is { Length: > 0 })
            return; // data lives somewhere else on purpose (tests, screenshots): leave the real folders alone
        DataFolder(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Autostart.MigrateFormer();
    }

    /// <summary>
    /// Brings the settings folder under <paramref name="root"/> (%APPDATA%, or ~/.config on Linux) to its current name:
    /// the folder of the former name (see pxdMigrate.FormerName) is moved; failing that, the folder of the app's first
    /// name, Torrent Monitor, is copied (never moved, so an old install keeps working until it is removed).
    /// Returns what it did, for the self-test.
    /// </summary>
    public static string DataFolder(string root)
    {
        try
        {
            var newDir = Path.Combine(root, AppChrome.AppId);
            if (pxdMigrate.Folder(Path.Combine(root, pxdMigrate.FormerName(AppChrome.AppId)), newDir))
                return "moved";

            var oldDir = Path.Combine(root, FirstName);
            if (Directory.Exists(newDir) || !Directory.Exists(oldDir))
                return "none";

            foreach (var dir in Directory.EnumerateDirectories(oldDir, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(newDir, Path.GetRelativePath(oldDir, dir)));
            Directory.CreateDirectory(newDir);
            foreach (var file in Directory.EnumerateFiles(oldDir, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(newDir, Path.GetRelativePath(oldDir, file)), overwrite: false);
            return "copied";
        }
        catch
        {
            return "failed"; // starting fresh is better than not starting
        }
    }
}
