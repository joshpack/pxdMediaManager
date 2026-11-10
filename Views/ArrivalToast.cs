using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using pxdMediaManager.Services;

namespace pxdMediaManager.Views;

/// <summary>
/// The pop-up in the corner when a finished download lands in a library category: poster, title, rating and a Play
/// button. It looks the title up the same way the library does (TMDB / Steam / Audible, all cached) and closes itself
/// after a while unless the pointer is over it.
/// </summary>
public static class ArrivalToast
{
    private static readonly List<Window> Open = new();

    public static void Show(AppSettings settings, string categoryName, string path, Action<string, bool> openViewer, Action<string> openFolder)
    {
        var isDir = Directory.Exists(path);
        var (title, year, extra) = LibraryScanner.Clean(Path.GetFileName(path), isDir);
        var item = new LibraryItem
        {
            Name = Path.GetFileName(path), Title = title, Year = year, Extra = extra, Path = path, IsDirectory = isDir,
            Modified = DateTime.Now, Kind = LibraryView.KindFor(categoryName), Category = categoryName,
        };

        var poster = new Image { Stretch = Stretch.UniformToFill };
        var titleText = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "neon" } };
        var metaText = new TextBlock { Text = (year is { } y ? $"{y}  ·  " : "") + (extra.Length > 0 ? extra + "  ·  " : "") + "just arrived in " + categoryName, Foreground = Ui.Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        var ratingText = new TextBlock { FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#F5C518")), IsVisible = false };
        var overview = new TextBlock { FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#C9CFDB")), TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis };

        var play = new Button { Content = "▶  Play", Classes = { "primary" }, Padding = new Thickness(12, 4), IsVisible = false };
        var folder = new Button { Content = "Open folder", Padding = new Thickness(12, 4) };
        var close = new Button { Content = "✕", Classes = { "ghost" }, Padding = new Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock { Text = "NEW IN YOUR LIBRARY", Classes = { "label" } });
        text.Children.Add(titleText);
        text.Children.Add(metaText);
        text.Children.Add(ratingText);
        text.Children.Add(overview);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(play);
        buttons.Children.Add(folder);
        text.Children.Add(buttons);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("84,*"), ColumnSpacing = 14 };
        grid.Children.Add(new Border { Width = 84, Height = 126, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = new SolidColorBrush(Color.Parse("#1D212C")), Child = poster, VerticalAlignment = VerticalAlignment.Top });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var layers = new Panel();
        layers.Children.Add(grid);
        layers.Children.Add(close);

        var window = new Window
        {
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowDecorations = WindowDecorations.None,
            Topmost = true,
            ShowInTaskbar = false,
            CanResize = false,
            ShowActivated = false,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#F2161922")),
                BorderBrush = new SolidColorBrush(Color.Parse("#1D5A47")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14),
                BoxShadow = BoxShadows.Parse("0 0 30 0 #3319F58C"),
                Child = layers,
            },
        };
        Bitmap? bitmap = null;
        window.Closed += (_, _) =>
        {
            Open.Remove(window);
            bitmap?.Dispose();
            Restack();
        };
        close.Click += (_, _) => window.Close();
        folder.Click += (_, _) =>
        {
            openFolder(isDir ? path : Path.GetDirectoryName(path) ?? path);
            window.Close();
        };
        play.Click += (_, _) =>
        {
            openViewer(path, false);
            window.Close();
        };

        // Close after 20 seconds, but not while the pointer is resting on it.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) =>
        {
            if (!window.IsPointerOver)
            {
                timer.Stop();
                window.Close();
            }
        };
        window.Opened += (_, _) => timer.Start();

        Open.Add(window);
        window.Show();
        Restack();

        _ = Task.Run(async () =>
        {
            var (video, _) = Viewer.MediaFiles.Probe(path, isDir);
            Dispatcher.UIThread.Post(() => play.IsVisible = video);

            if (item.Kind == LibraryKind.Auto && extra.Length == 0)
                item.Kind = MetadataService.SniffKind(item);
            var tmdb = settings.TmdbKey.Trim().Length > 0 ? new TmdbClient(settings.TmdbKey) : null;
            var meta = new MetadataService(tmdb);
            if (!meta.CanMatch(item))
                return;
            var match = await meta.FindAsync(item, CancellationToken.None);
            if (match is null)
                return;
            var details = await meta.GetDetailsAsync(match, CancellationToken.None);
            item.Match = match;
            item.Details = details;
            if (match.Kind is LibraryKind.Movie or LibraryKind.Tv && settings.MdbListKey.Trim().Length > 0
                && await new MdbListClient(settings.MdbListKey).GetAsync(match, CancellationToken.None) is { } scores)
                item.Scores = scores;
            var posterFile = await meta.GetPosterFileAsync(match, CancellationToken.None);

            Dispatcher.UIThread.Post(() =>
            {
                titleText.Text = match.Title;
                if (match.Year is { } my)
                    metaText.Text = $"{my}  ·  " + (extra.Length > 0 ? extra + "  ·  " : "") + "just arrived in " + categoryName;
                var desc = match.Overview.Length > 0 ? match.Overview : details?.Tagline ?? "";
                overview.Text = desc;
                if (item.BestRating is { } r)
                {
                    var source = item.Scores.Any(s => s.Source == "imdb") ? "IMDb" : match.Kind switch
                    {
                        LibraryKind.Game => "Steam",
                        LibraryKind.Audiobook => "Audible",
                        _ => "TMDB",
                    };
                    ratingText.Text = match.Kind == LibraryKind.Audiobook ? $"★ {r / 2:0.0} / 5 on {source}" : $"★ {r:0.0} / 10 on {source}";
                    ratingText.IsVisible = true;
                }
            });

            if (posterFile is not null)
            {
                try
                {
                    await using var stream = File.OpenRead(posterFile);
                    var bmp = Bitmap.DecodeToWidth(stream, 168);
                    Dispatcher.UIThread.Post(() =>
                    {
                        bitmap = bmp;
                        poster.Source = bmp;
                    });
                }
                catch
                {
                    /* no poster */
                }
            }
        });
    }

    /// <summary>Stacks open pop-ups upwards from the bottom-right corner of the main screen's working area.</summary>
    private static void Restack()
    {
        var screen = Open.FirstOrDefault()?.Screens.Primary;
        if (screen is null)
            return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var bottom = area.Bottom - (int)(16 * scale);
        foreach (var w in Open)
        {
            var height = (int)((w.Bounds.Height > 0 ? w.Bounds.Height : 170) * scale);
            var width = (int)(w.Width * scale);
            bottom -= height;
            w.Position = new PixelPoint(area.Right - width - (int)(16 * scale), bottom);
            bottom -= (int)(10 * scale);
        }
    }
}
