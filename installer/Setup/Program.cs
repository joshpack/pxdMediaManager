using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

namespace PaxMediaManager.Setup;

/// <summary>
/// pax-media-manager-setup.exe
///   (no arguments)            the setup window
///   --quiet [--dir FOLDER]    installs without a window (Start menu shortcut, no desktop shortcut)
///   --unpack FOLDER           just puts the app's files in FOLDER: no shortcuts, no Apps &amp; features entry (portable)
///   --screenshot FILE.png     draws the setup window into a picture, off screen (for checking the design)
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        if (args.Contains("--quiet"))
        {
            try
            {
                Installer.InstallAsync(Arg("--dir") ?? Installer.Installed.Folder ?? Installer.DefaultFolder, desktop: false, (_, _) => { }).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("PAX Media Manager setup: " + ex.Message);
                return 1;
            }
        }
        if (Arg("--unpack") is { } unpack)
        {
            try
            {
                Installer.UnpackAsync(unpack, (_, _) => { }).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("PAX Media Manager setup: " + ex.Message);
                return 1;
            }
        }
        if (Arg("--screenshot") is { } shot) return Screenshot.Render(shot);

        BuildApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildApp() => AppBuilder.Configure<SetupApp>().UsePlatformDetect().WithInterFont();
}

public sealed class SetupApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new SetupWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
