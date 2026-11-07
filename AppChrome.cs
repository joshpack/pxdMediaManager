using Avalonia.Controls;
using Avalonia.Media;
using PaxStandard;

namespace PaxMediaManager;

/// <summary>
/// The PAX title bar and remembered placement for every window the app shows (see PaxStandard.cs). The toasts (move
/// overlay, arrival notice) have no title bar at all and place themselves, so they don't use this.
/// </summary>
public static class AppChrome
{
    public const string AppId = "PaxMediaManager";
    public const string AppName = "PAX Media Manager";

    private static IImage? _icon;

    /// <summary>The app's icon for the title bar (drawn at twice its 18 px size so it stays sharp on high-DPI screens).</summary>
    public static IImage Icon => _icon ??= BrandIcon.Render(36);

    /// <summary>
    /// Replaces the system title bar with the PAX one and, with a <paramref name="placementKey"/>, restores and keeps
    /// saving the window's size, position and maximized state (in windows.json in the data folder).
    /// </summary>
    public static void Apply(Window w, string? placementKey, bool resizable = true)
    {
        var content = w.Content as Control ?? new Panel();
        w.Content = null;
        w.Content = PaxChrome.Wrap(w, content, AppName, Icon, resizable);
        if (placementKey is not null)
            PaxChrome.RememberPlacement(w, placementKey);
    }

    /// <summary>The title bar of a window given one by <see cref="Apply"/> (null if it has none).</summary>
    public static Control? TitleBar(Window w) => (w.Content as DockPanel)?.Children.FirstOrDefault();

    /// <summary>Hides the title bar while the window is full screen (videos and slideshows use the whole screen).</summary>
    public static void HideTitleBarInFullScreen(Window w)
    {
        w.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty && TitleBar(w) is { } bar)
                bar.IsVisible = w.WindowState != WindowState.FullScreen;
        };
    }
}
