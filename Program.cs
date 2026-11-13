using Avalonia;

namespace pxdMediaManager;

internal static class Program
{
    private static FileStream? _instanceLock;

    /// <summary>Where updates come from: the Windows portable zip (the Toolkit installs it; the setup holds the same files).</summary>
    public const string UpdateRepo = "pxdtek/pxdMediaManager";
    public const string UpdateAsset = @"^pxdMediaManager-.*-win-x64-portable\.zip$";

    [STAThread]
    public static void Main(string[] args)
    {
        // Update before anything else (Windows only; Linux copies skip it): the portable zip carries the same files the
        // setup program installs, so this updates both the portable copy and one installed by the setup program. A
        // no-op for --self-test, bin\ builds, PXD_NO_UPDATE=1, offline or a slow GitHub.
        if (OperatingSystem.IsWindows() && pxdStandard.pxdUpdate.BeforeStart(UpdateRepo, UpdateAsset, args))
            return;   // the updated copy is starting with the same arguments

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
        Services.Migration.SetupEntryVersion();
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
