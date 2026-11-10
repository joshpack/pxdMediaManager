using Avalonia;

namespace pxdMediaManager;

internal static class Program
{
    private static FileStream? _instanceLock;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test")
        {
            // Checks without a window or the real settings (see Services/SelfTest.cs); results in <folder>\report.txt.
            Environment.ExitCode = Services.SelfTest.Run(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "pxdMediaManager-selftest"));
            return;
        }

        if (!AcquireSingleInstance())
        {
            // Already running (maybe with its tray icon hidden): open that copy's window instead.
            Services.SingleInstance.SignalRunning();
            return;
        }

        Services.Migration.Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
            var path = Path.Combine(Path.GetTempPath(), Services.SingleInstance.Name + ".instance.lock");
            _instanceLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
