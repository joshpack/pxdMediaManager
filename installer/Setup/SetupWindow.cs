using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace pxdMediaManager.Setup;

/// <summary>The setup window, in pxd Media Manager's own look: where to install, a desktop shortcut or not, Install, then Open.</summary>
public sealed class SetupWindow : Window
{
    private static readonly IBrush Bg = Brush.Parse("#0B0F14");
    private static readonly IBrush Panel = Brush.Parse("#121821");
    private static readonly IBrush Line = Brush.Parse("#232A38");
    private static readonly IBrush Text = Brush.Parse("#E8EEF5");
    private static readonly IBrush Dim = Brush.Parse("#8B97A8");
    private static readonly IBrush Accent = Brush.Parse("#19F58C");

    private readonly TextBox _folder;
    private readonly CheckBox _desktop;
    private readonly Button _go;
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;
    private string? _installed;

    public SetupWindow()
    {
        Title = "pxd Media Manager setup";
        Width = 560;
        Height = 430;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg;
        Icon = Brand.WindowIcon();

        var (existing, existingVersion) = Installer.Installed;
        _folder = new TextBox { Text = existing ?? Installer.DefaultFolder, FontSize = 13, Background = Panel, BorderBrush = Line };
        _desktop = new CheckBox { Content = "Put a shortcut on the desktop", IsChecked = existing is null, Foreground = Text };
        _go = new Button
        {
            Content = existing is null ? "Install" : existingVersion == Installer.Version ? "Reinstall" : "Update",
            HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(26, 9), FontWeight = FontWeight.SemiBold,
            Background = Accent, Foreground = Brush.Parse("#04140C"), CornerRadius = new CornerRadius(9),
        };
        _bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false, Foreground = Accent, Background = Line };
        _status = new TextBlock { Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        if (!Installer.HasPayload) { _status.Text = "This copy of setup has nothing inside it to install (it was built without the app)."; _go.IsEnabled = false; }
        else if (existing is not null) _status.Text = $"Version {existingVersion} is installed here. Your settings and library stay as they are.";

        _go.Click += async (_, _) => await GoAsync();

        Content = new Border
        {
            Padding = new Thickness(30, 26),
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 16,
                        Children =
                        {
                            Brand.Tile(64),
                            new StackPanel
                            {
                                VerticalAlignment = VerticalAlignment.Center, Spacing = 2,
                                Children =
                                {
                                    new TextBlock { Text = "pxd Media Manager", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Text },
                                    new TextBlock { Text = $"Version {Installer.Version}  ·  torrents, your library, a video and picture viewer", FontSize = 12.5, Foreground = Dim },
                                },
                            },
                        },
                    },
                    new Border { Height = 1, Background = Line, Margin = new Thickness(0, 4) },
                    new TextBlock { Text = "Install to", FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Dim },
                    _folder,
                    _desktop,
                    new TextBlock
                    {
                        Text = "Installs just for you: no administrator rights, nothing else to download. Uninstall any time from Settings → Apps.",
                        FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap,
                    },
                    _bar,
                    _status,
                    _go,
                },
            },
        };

        // The pxd title bar, like the app's windows (pxdStandard.cs). Setup runs once, so its placement isn't remembered.
        var body = (Control)Content;
        Content = null;
        Height += 39;
        Content = pxdStandard.pxdChrome.Wrap(this, body, "pxd Media Manager setup", Brand.Bitmap(36), resizable: false);
    }

    private async Task GoAsync()
    {
        if (_installed is not null)
        {
            Process.Start(new ProcessStartInfo(_installed) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(_installed) });
            Close();
            return;
        }
        _go.IsEnabled = false;
        _folder.IsEnabled = _desktop.IsEnabled = false;
        _bar.IsVisible = true;
        try
        {
            _installed = await Installer.InstallAsync(_folder.Text ?? Installer.DefaultFolder, _desktop.IsChecked == true,
                (text, done) => Dispatcher.UIThread.Post(() => { _status.Text = text; _bar.Value = done; }));
            _status.Text = "Installed. pxd Media Manager is in your Start menu.";
            _status.Foreground = Accent;
            _go.Content = "Open pxd Media Manager";
        }
        catch (Exception ex)
        {
            _status.Text = "Couldn't install: " + ex.Message;
            _status.Foreground = Brush.Parse("#FF6B6B");
            _folder.IsEnabled = _desktop.IsEnabled = true;
            _bar.IsVisible = false;
        }
        _go.IsEnabled = true;
    }
}

/// <summary>pxd Media Manager's spade on its accent tile (the same drawing as the app's SpadeIcon).</summary>
public static class Brand
{
    private const string Spade =
        "M50,5 C50,5 11,34 11,59 C11,75 23,83 35,83 C41,83 46,80.5 48.5,76.5 " +
        "C48,86 45,92 37,96 L63,96 C55,92 52,86 51.5,76.5 C54,80.5 59,83 65,83 " +
        "C77,83 89,75 89,59 C89,34 50,5 50,5 Z";

    /// <summary>The app's icon (BrandIcon.cs is a copy of the app's own file).</summary>
    public static Control Tile(double size) => BrandIcon.Build(size);

    public static Avalonia.Media.Imaging.RenderTargetBitmap Bitmap(int size)
    {
        var tile = Tile(size);
        tile.Measure(new Size(size, size));
        tile.Arrange(new Rect(0, 0, size, size));
        var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(tile);
        return bitmap;
    }

    public static WindowIcon WindowIcon() => new(Bitmap(128));
}
