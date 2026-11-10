// pxd App Standard: shared code every Avalonia pxd app copies unchanged (namespace pxdStandard).
// - pxdChrome.Wrap: the app's own title bar (icon, title, minimize / maximize / close), drag + double-click maximize.
// - pxdChrome.RememberPlacement: window size (if resizable), position and maximized state, kept on screen.
// - pxdLink: the pxd Toolkit link. %APPDATA%\pxd\toolkit.json says whether each app shows its tray icon; the app
//   watches it and shows/hides the icon live. PXD_SHARED_DIR overrides the folder (tests).
// - pxdMigrate: one-time moves from the apps' former names (settings folders, the shared toolkit.json, Run entries).
// Call pxdChrome.Configure(appId, dataDir) once at start-up, and add pxdChrome.Styles() to Application.Styles.
// Canonical copy: lives in joshpack/pxdToolkit docs/standard; apps copy it unchanged.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace pxdStandard;

public static class pxdChrome
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

    /// <summary>Removes the system title bar and puts the pxd one above <paramref name="content"/>.</summary>
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
        b.Classes.Add("pxd-caption");
        if (close) b.Classes.Add("pxd-close");
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
        return new Styles { Hover("pxd-caption", "#1F2533"), Hover("pxd-close", "#E81123") };
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

/// <summary>The pxd Toolkit link: per-app options the Toolkit sets (today: whether the tray icon shows).</summary>
public static class pxdLink
{
    public static string SharedDir =>
        Environment.GetEnvironmentVariable("PXD_SHARED_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pxd");

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
        pxdMigrate.SharedFile();
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

/// <summary>
/// One-time moves from an app's former name. Every call is safe to repeat: it does nothing once the new place exists
/// or the old one is gone, and never throws.
/// </summary>
public static class pxdMigrate
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private static int _sharedDone;

    /// <summary>
    /// The prefix the apps had before the pxd rename. Used only here, to find what older versions left behind
    /// (settings folders, the shared file, startup entries).
    /// </summary>
    public const string FormerPrefix = "Pax";

    private static string FormerSharedDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FormerPrefix.ToUpperInvariant());

    /// <summary>An app id or folder name under its former prefix: "pxdAudioSwitcher" → "PaxAudioSwitcher".</summary>
    public static string FormerName(string pxdName) =>
        pxdName.StartsWith("pxd", StringComparison.Ordinal) ? FormerPrefix + pxdName[3..] : pxdName;

    /// <summary>
    /// Copies the former shared toolkit.json to %APPDATA%\pxd\toolkit.json (app ids renamed) when the new one doesn't
    /// exist yet. Skipped when PXD_SHARED_DIR points elsewhere (tests).
    /// </summary>
    public static void SharedFile()
    {
        if (Interlocked.Exchange(ref _sharedDone, 1) == 1 || Environment.GetEnvironmentVariable("PXD_SHARED_DIR") is { Length: > 0 })
            return;
        try
        {
            var old = Path.Combine(FormerSharedDir, "toolkit.json");
            if (File.Exists(pxdLink.FilePath) || !File.Exists(old))
                return;
            var text = Regex.Replace(File.ReadAllText(old), "\"" + FormerPrefix + "([A-Za-z]+)\"", m => "\"pxd" + m.Groups[1].Value + "\"");
            Directory.CreateDirectory(pxdLink.SharedDir);
            File.WriteAllText(pxdLink.FilePath, text);
        }
        catch
        {
            /* the defaults apply */
        }
    }

    /// <summary>Moves <paramref name="oldDir"/> to <paramref name="newDir"/> when only the old one exists. True if it moved.</summary>
    public static bool Folder(string oldDir, string newDir)
    {
        try
        {
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(newDir)!);
            try
            {
                Directory.Move(oldDir, newDir);
            }
            catch (IOException)
            {
                // Another drive, or a file in use: copy what can be copied and leave the old folder.
                CopyTree(oldDir, newDir);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false); } catch { /* skip */ }
        foreach (var d in Directory.GetDirectories(from))
            CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>
    /// Windows: if the per-user Run entry <paramref name="oldName"/> exists, replaces it with <paramref name="newName"/>
    /// = <paramref name="newCommand"/> (the old entry's arguments are kept when <paramref name="keepArgs"/>), and carries
    /// over Task Manager's enabled/disabled state. True if it moved.
    /// </summary>
    public static bool RunEntry(string oldName, string newName, string newExe, bool keepArgs = true)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(oldName) is not string old)
                return false;
            var args = keepArgs ? ArgsOf(old) : "";
            run.SetValue(newName, $"\"{newExe}\"" + (args.Length > 0 ? " " + args : ""));
            run.DeleteValue(oldName, throwOnMissingValue: false);
            using var approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            if (approved?.GetValue(oldName) is byte[] state)
            {
                approved.SetValue(newName, state, Microsoft.Win32.RegistryValueKind.Binary);
                approved.DeleteValue(oldName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The arguments after the program in a Run command line.</summary>
    public static string ArgsOf(string command)
    {
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end < 0 ? "" : c[(end + 1)..].Trim();
        }

        var space = c.IndexOf(' ');
        return space < 0 ? "" : c[(space + 1)..].Trim();
    }
}

/// <summary>
/// Updates an installed app before it opens: call <see cref="BeforeStart"/> first thing in Main, before the
/// single-instance check, and exit when it returns true (the new version has been started in its place).
/// It asks GitHub for the app's latest release (a few seconds at most; skipped offline or when GitHub doesn't answer),
/// and when that is newer than this copy it downloads it, checks its SHA-256, swaps the app's files (a running file can
/// be renamed, not overwritten, so the old ones are set aside as *.pxdold and removed at the next start) and starts the
/// new copy with the same arguments. Developer builds (bin\ folders), --self-test runs and PXD_NO_UPDATE=1 are left alone.
/// </summary>
public static class pxdUpdate
{
    private const string Old = ".pxdold";
    private const string WaitEnv = "PXD_UPDATED_FROM";

    /// <summary>The release folder this copy came from, e.g. joshpack/pxdWatermark and ^pxdWatermark-.*-win-x64\.zip$.</summary>
    public static bool BeforeStart(string repo, string assetPattern, string[] args, Version? current = null)
    {
        try
        {
            WaitForPrevious();
            var exe = Environment.ProcessPath;
            if (exe is null || !OperatingSystem.IsWindows())
                return false;
            var dir = Path.GetDirectoryName(exe)!;
            CleanUp(dir);
            // Already running (this start will just hand over to it): never swap files under a running copy.
            if (OtherCopyRunning(exe))
                return false;
            if (Environment.GetEnvironmentVariable("PXD_NO_UPDATE") == "1"
                || args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase) || a.Equals("--no-update", StringComparison.OrdinalIgnoreCase))
                || dir.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) || dir.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
                return false;

            current ??= ThisVersion();
            var plan = FindUpdateAsync(repo, assetPattern, current).GetAwaiter().GetResult();
            if (plan is null)
                return false;
            return ApplyAsync(plan.Value.Zip, plan.Value.Sum, dir, exe, args).GetAwaiter().GetResult();
        }
        catch
        {
            return false;   // never stop the app from opening
        }
    }

    /// <summary>This copy's version: the entry assembly's informational version without the "+commit" part.</summary>
    public static Version ThisVersion()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var info = asm?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        return ParseVersion(info) ?? asm?.GetName().Version ?? new Version(0, 0);
    }

    public static Version? ParseVersion(string? s)
    {
        var m = Regex.Match(s ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0) : null;
    }

    /// <summary>The newer release's download (and checksum) URLs, or null when this copy is current or GitHub can't be asked.</summary>
    public static async Task<(string Zip, string? Sum)?> FindUpdateAsync(string repo, string assetPattern, Version current)
    {
        var api = Environment.GetEnvironmentVariable("PXD_UPDATE_API") is { Length: > 0 } a ? a.TrimEnd('/') : "https://api.github.com";
        using var http = Client(TimeSpan.FromSeconds(4));
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"{api}/repos/{repo}/releases/latest"));
        var root = doc.RootElement;
        if (ParseVersion(root.GetProperty("tag_name").GetString()) is not { } latest || latest <= current)
            return null;
        string? zip = null, sum = null, zipName = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (zipName is null && Regex.IsMatch(name, assetPattern))
            {
                zipName = name;
                zip = asset.GetProperty("browser_download_url").GetString();
            }
        }

        if (zip is null)
            return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("name").GetString() == zipName + ".sha256")
                sum = asset.GetProperty("browser_download_url").GetString();
        return (zip, sum);
    }

    /// <summary>Downloads, checks, swaps the files in <paramref name="dir"/> and starts the new <paramref name="exe"/>. False if anything failed (nothing is left half-done).</summary>
    public static async Task<bool> ApplyAsync(string zipUrl, string? sumUrl, string dir, string exe, string[] args, bool start = true)
    {
        var stage = Path.Combine(Path.GetTempPath(), "pxd-update-" + Guid.NewGuid().ToString("N"));
        var moved = new List<string>();
        var added = new List<string>();
        try
        {
            Directory.CreateDirectory(stage);
            var zip = Path.Combine(stage, "update.zip");
            using (var http = Client(TimeSpan.FromMinutes(5)))
            {
                await using (var src = await http.GetStreamAsync(zipUrl))
                await using (var dst = File.Create(zip))
                    await src.CopyToAsync(dst);
                if (sumUrl is not null)
                {
                    var expected = Regex.Match(await http.GetStringAsync(sumUrl), "[0-9a-fA-F]{64}").Value;
                    await using var fs = File.OpenRead(zip);
                    var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs));
                    if (expected.Length == 64 && !expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            var unz = Path.Combine(stage, "x");
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, unz);
            var root = unz;
            if (Directory.GetFiles(unz).Length == 0 && Directory.GetDirectories(unz) is [var only])
                root = only;
            if (!File.Exists(Path.Combine(root, Path.GetFileName(exe))))
                return false;   // not this app's download

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(dir, Path.GetRelativePath(root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest))
                {
                    File.Move(dest, dest + Old, overwrite: true);
                    moved.Add(dest);
                }
                else
                    added.Add(dest);
                File.Copy(file, dest);
            }

            if (start)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
                foreach (var a in args)
                    psi.ArgumentList.Add(a);
                psi.Environment[WaitEnv] = Environment.ProcessId.ToString();
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }

            return true;
        }
        catch
        {
            // Put the old files back.
            foreach (var f in added)
                try { File.Delete(f); } catch { /* best effort */ }
            foreach (var f in moved)
                try { File.Move(f + Old, f, overwrite: true); } catch { /* best effort */ }
            return false;
        }
        finally
        {
            try { Directory.Delete(stage, true); } catch { /* temp */ }
        }
    }

    /// <summary>Removes the files an update set aside (they are free once the old copy has closed).</summary>
    public static void CleanUp(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*" + Old, SearchOption.AllDirectories))
                try { File.Delete(f); } catch { /* still in use: next start */ }
        }
        catch
        {
            /* not readable */
        }
    }

    /// <summary>Another process started from the same exe file is running.</summary>
    public static bool OtherCopyRunning(string exe)
    {
        var found = false;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (p)
            {
                if (found || p.Id == Environment.ProcessId)
                    continue;
                try
                {
                    found = string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    found = true;   // can't tell (elevated copy): assume it's ours and leave the files alone
                }
            }
        }

        return found;
    }

    /// <summary>A copy started by an update waits for the old one to close, so its single-instance check doesn't find it.</summary>
    private static void WaitForPrevious()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(WaitEnv), out var pid))
            return;
        Environment.SetEnvironmentVariable(WaitEnv, null);
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.WaitForExit(15000);
        }
        catch
        {
            /* already gone */
        }
    }

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("pxd-update/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
