// PAX App Standard: shared code every Avalonia PAX app copies unchanged (namespace PaxStandard).
// - PaxChrome.Wrap: the app's own title bar (icon, title, minimize / maximize / close), drag + double-click maximize.
// - PaxChrome.RememberPlacement: window size (if resizable), position and maximized state, kept on screen.
// - PaxLink: the PAX Toolkit link. %APPDATA%\PAX\toolkit.json says whether each app shows its tray icon; the app
//   watches it and shows/hides the icon live. PAX_SHARED_DIR overrides the folder (tests).
// Call PaxChrome.Configure(appId, dataDir) once at start-up, and add PaxChrome.Styles() to Application.Styles.
// Canonical copy: lives in joshpack/PaxToolkit docs/standard; apps copy it unchanged.

using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace PaxStandard;

public static class PaxChrome
{
    private const string MaximizeGlyph = "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z";
    private const string RestoreGlyph = "M2.5,0.5 L9.5,0.5 L9.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z";

    public static string AppId { get; private set; } = "";
    public static string DataDir { get; private set; } = "";

    public static void Configure(string appId, string dataDir)
    {
        AppId = appId;
        DataDir = dataDir;
    }

    /// <summary>Removes the system title bar and puts the PAX one above <paramref name="content"/>.</summary>
    public static Control Wrap(Window w, Control content, string title, IImage? icon, bool resizable = true, Control? extras = null)
    {
        w.ExtendClientAreaToDecorationsHint = true;
        w.WindowDecorations = WindowDecorations.BorderOnly;
        w.CanResize = resizable;

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null)
            left.Children.Add(new Image { Source = icon, Width = 18, Height = 18 });
        left.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#C9CED9")), VerticalAlignment = VerticalAlignment.Center });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (extras is not null)
            buttons.Children.Add(extras);
        buttons.Children.Add(Caption("M0,5.5 L10,5.5", "Minimize", WindowDecorationsElementRole.MinimizeButton, () => w.WindowState = WindowState.Minimized));
        if (resizable)
        {
            var max = Caption(MaximizeGlyph, "Maximize", WindowDecorationsElementRole.MaximizeButton, () => Toggle(w));
            w.PropertyChanged += (_, e) =>
            {
                if (e.Property != Window.WindowStateProperty) return;
                var m = w.WindowState == WindowState.Maximized;
                ((Avalonia.Controls.Shapes.Path)max.Content!).Data = Geometry.Parse(m ? RestoreGlyph : MaximizeGlyph);
                w.Padding = m ? w.OffScreenMargin : default;
            };
            // A window restored as maximized gets its state before it is shown, when OffScreenMargin is still 0.
            w.Resized += (_, _) => w.Padding = w.WindowState == WindowState.Maximized ? w.OffScreenMargin : default;
            buttons.Children.Add(max);
        }

        buttons.Children.Add(Caption("M0,0 L10,10 M10,0 L0,10", "Close", WindowDecorationsElementRole.CloseButton, w.Close, close: true));

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 38, Background = new SolidColorBrush(Color.Parse("#0A0D12")) };
        bar.Children.Add(left);
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(buttons);
        bar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            if (e.ClickCount == 2 && resizable) Toggle(w);
            else w.BeginMoveDrag(e);
            e.Handled = true;
        };

        var root = new DockPanel();
        var edge = new Border { BorderBrush = new SolidColorBrush(Color.Parse("#1B2030")), BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
        DockPanel.SetDock(edge, Dock.Top);
        root.Children.Add(edge);
        root.Children.Add(content);
        return root;
    }

    private static void Toggle(Window w) =>
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private static Button Caption(string glyph, string tip, WindowDecorationsElementRole role, Action click, bool close = false)
    {
        var b = new Button
        {
            Content = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(glyph), Width = 10, Height = 10, Stroke = new SolidColorBrush(Color.Parse("#C9CED9")), StrokeThickness = 1 },
            Width = 46, Height = 38, Padding = default, CornerRadius = default, MinHeight = 0,
            Background = Brushes.Transparent, BorderThickness = default,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Classes.Add("pax-caption");
        if (close) b.Classes.Add("pax-close");
        ToolTip.SetTip(b, tip);
        Avalonia.Controls.Chrome.WindowDecorationProperties.SetElementRole(b, role);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Hover colours for the caption buttons; add to Application.Styles.</summary>
    public static Styles Styles()
    {
        Style Hover(string cls, string bg) => new(x => x.OfType<Button>().Class(cls).Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(Color.Parse(bg))), new Setter(ContentPresenter.BoxShadowProperty, default(BoxShadows)) },
        };
        return new Styles { Hover("pax-caption", "#1F2533"), Hover("pax-close", "#E81123") };
    }

    // ── remembered placement ──

    private sealed class Placement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    private static string PlacementFile => Path.Combine(DataDir, "windows.json");

    /// <summary>Restores and keeps saving the window's placement under <paramref name="key"/>.</summary>
    public static void RememberPlacement(Window w, string key)
    {
        var all = LoadPlacements();
        if (all.TryGetValue(key, out var p))
        {
            if (w.CanResize && p.Width > 50 && p.Height > 50)
            {
                w.Width = Math.Max(w.MinWidth, p.Width);
                w.Height = Math.Max(w.MinHeight, p.Height);
            }

            var screens = w.Screens?.All;
            if (screens is null || screens.Count == 0 || screens.Any(s => s.WorkingArea.Contains(new PixelPoint(p.X + 60, p.Y + 20))))
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Position = new PixelPoint(p.X, p.Y);
            }

            if (p.Maximized && w.CanResize)
                w.WindowState = WindowState.Maximized;
        }

        var cur = p ?? new Placement();
        var ready = false;
        void Save()
        {
            if (!ready) return;
            var latest = LoadPlacements();
            latest[key] = cur;
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(PlacementFile, JsonSerializer.Serialize(latest));
            }
            catch
            {
                /* next move tries again */
            }
        }

        w.Opened += (_, _) =>
        {
            ready = true;
            // Start from where the window opened, so a first resize or maximize doesn't save a 0,0 position.
            if (w.WindowState == WindowState.Normal)
            {
                cur.X = w.Position.X;
                cur.Y = w.Position.Y;
                cur.Width = w.ClientSize.Width;
                cur.Height = w.ClientSize.Height;
            }
        };
        w.PositionChanged += (_, e) => { if (w.WindowState == WindowState.Normal) { cur.X = e.Point.X; cur.Y = e.Point.Y; Save(); } };
        w.Resized += (_, e) => { if (w.WindowState == WindowState.Normal && e.ClientSize.Width > 50) { cur.Width = e.ClientSize.Width; cur.Height = e.ClientSize.Height; Save(); } };
        w.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty || w.WindowState == WindowState.Minimized) return;
            cur.Maximized = w.WindowState == WindowState.Maximized;
            Save();
        };
    }

    private static Dictionary<string, Placement> LoadPlacements()
    {
        try
        {
            if (File.Exists(PlacementFile))
                return JsonSerializer.Deserialize<Dictionary<string, Placement>>(File.ReadAllText(PlacementFile)) ?? [];
        }
        catch
        {
            /* damaged: start fresh */
        }

        return [];
    }
}

/// <summary>The PAX Toolkit link: per-app options the Toolkit sets (today: whether the tray icon shows).</summary>
public static class PaxLink
{
    public static string SharedDir =>
        Environment.GetEnvironmentVariable("PAX_SHARED_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PAX");

    public static string FilePath => Path.Combine(SharedDir, "toolkit.json");

    /// <summary>Whether the app shows its tray icon (true unless the Toolkit or the app turned it off).</summary>
    public static bool TrayIconVisible(string appId) =>
        Read()?["apps"]?[appId]?["trayIcon"] is JsonValue v && v.TryGetValue(out bool on) ? on : true;

    public static void SetTrayIconVisible(string appId, bool visible)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var root = Read() ?? new JsonObject { ["schema"] = 1 };
                var apps = root["apps"] as JsonObject ?? new JsonObject();
                root["apps"] = apps;
                var entry = apps[appId] as JsonObject ?? new JsonObject();
                apps[appId] = entry;
                entry["trayIcon"] = visible;
                Directory.CreateDirectory(SharedDir);
                var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Calls <paramref name="changed"/> (on a worker thread) whenever toolkit.json changes.</summary>
    public static IDisposable Watch(Action changed)
    {
        Directory.CreateDirectory(SharedDir);
        var w = new FileSystemWatcher(SharedDir, "toolkit.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime };
        Timer? debounce = null;
        void Fire(object? _, FileSystemEventArgs __)
        {
            debounce?.Dispose();
            debounce = new Timer(_ => changed(), null, 250, Timeout.Infinite);
        }

        w.Changed += Fire;
        w.Created += Fire;
        w.Deleted += Fire;
        w.Renamed += (s, e) => Fire(s, e);
        w.EnableRaisingEvents = true;
        return w;
    }

    private static JsonNode? Read()
    {
        try
        {
            return File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) : null;
        }
        catch
        {
            return null;
        }
    }
}
