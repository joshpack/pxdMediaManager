using Avalonia;

namespace PaxMediaManager;

internal static class Program
{
    private static FileStream? _instanceLock;

    [STAThread]
    public static void Main(string[] args)
    {
        if (!AcquireSingleInstance())
            return;

        MigrateOldData();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// The app used to be called Torrent Monitor and kept its data in a "TorrentMonitor" folder. On the first run under the
    /// new name that folder is copied across (never moved, so the old install keeps working until it is removed).
    /// </summary>
    private static void MigrateOldData()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var oldDir = Path.Combine(appData, "TorrentMonitor");
            var newDir = Path.Combine(appData, "PaxMediaManager");
            if (Directory.Exists(newDir) || !Directory.Exists(oldDir))
                return;

            foreach (var dir in Directory.EnumerateDirectories(oldDir, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(newDir, Path.GetRelativePath(oldDir, dir)));
            Directory.CreateDirectory(newDir);
            foreach (var file in Directory.EnumerateFiles(oldDir, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(newDir, Path.GetRelativePath(oldDir, file)), overwrite: false);
        }
        catch
        {
            /* starting fresh is better than not starting */
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Cross-platform single-instance guard: an exclusively opened lock file (named mutexes are not shared across
    /// processes on Linux/macOS). The OS releases it automatically if the process dies.
    /// </summary>
    private static bool AcquireSingleInstance()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "PaxMediaManager.instance.lock");
            _instanceLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
