using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PaxMediaManager.Services;
using PaxMediaManager.Viewer;
using PaxMediaManager.Views;

namespace PaxMediaManager;

public partial class App : Application
{
    private AppSettings _settings = null!;
    private TorrentWatcher _watcher = null!;
    private StatsStore _stats = null!;
    private CompletionMover _mover = null!;
    private ProgressOverlay? _overlay;
    private MainWindow _main = null!;
    private TrayIcon _tray = null!;
    private readonly List<ViewerWindow> _viewers = new();
    private IClassicDesktopStyleApplicationLifetime _lifetime = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            _lifetime = lifetime;
            // The tray keeps the app alive when the window is closed.
            lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _settings = AppSettings.Load();
            var icon = SpadeIcon.CreateWindowIcon();

            _stats = StatsStore.Load(_settings);
            _mover = new CompletionMover();
            _watcher = new TorrentWatcher(_settings, _stats, _mover, PromptAsync);
            _main = new MainWindow(_settings, _watcher, _stats, _mover) { Icon = icon };
            _watcher.Activity += (kind, title, detail) =>
                Dispatcher.UIThread.Post(() => _main.AddActivity(kind, title, detail));
            WireMover(icon);

            BuildTray(icon);
            _watcher.Start();
            _mover.Start();
            _stats.Start();
            _ = ScanExistingAsync();

            // Start hidden in the tray unless the app still needs configuring. The window is deliberately not
            // registered as lifetime.MainWindow: Avalonia shows that one automatically at startup.
            // "--show" opens the window straight away (for a shortcut that should bring the app up, not just start it).
            if (_settings.IsFirstRun || string.IsNullOrWhiteSpace(_settings.TargetFolder)
                || Environment.GetCommandLineArgs().Contains("--show", StringComparer.OrdinalIgnoreCase))
                _main.ShowFromTray();
            else
                _ = ShowIfTargetUnreachableAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task ShowIfTargetUnreachableAsync()
    {
        // Checked in the background: a network target can take a while to answer (or to time out).
        var target = _settings.TargetFolder;
        if (!await Task.Run(() => NetworkPath.Exists(target)))
            _main.ShowFromTray();
    }

    private void BuildTray(WindowIcon icon)
    {
        var open = new NativeMenuItem("Open PAX Media Manager");
        open.Click += (_, _) => _main.ShowFromTray();

        var openWatch = new NativeMenuItem("Open watched folder");
        openWatch.Click += (_, _) => _main.OpenFolder(_settings.WatchFolder);

        var openTarget = new NativeMenuItem("Open target folder");
        openTarget.Click += (_, _) => _main.OpenFolder(_settings.TargetFolder);

        var media = new NativeMenuItem("Open media…");
        media.Click += (_, _) => OpenViewer(null);

        var scan = new NativeMenuItem("Scan Downloads for torrents");
        scan.Click += (_, _) => _ = ScanExistingAsync();

        var exit = new NativeMenuItem("Quit");
        exit.Click += (_, _) => Quit();

        _tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "PAX Media Manager",
            Menu = new NativeMenu { open, media, new NativeMenuItemSeparator(), scan, openWatch, openTarget, new NativeMenuItemSeparator(), exit },
        };
        _tray.Clicked += (_, _) => _main.ShowFromTray();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private Task<ChoiceResult> PromptAsync(string fileName, string from, string to) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new ChoiceDialog(fileName, from, to, _settings.ActiveCategories, _settings.LastCategory, _main.ShowSettings)
            {
                Icon = _main.Icon,
            };
            return await dialog.ShowChoiceAsync();
        });

    /// <summary>Checks the watch folder for torrents that were already there and offers one batch decision for them.</summary>
    private async Task ScanExistingAsync()
    {
        try
        {
            await _watcher.ScanExistingAsync(items => Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dialog = new BatchDialog(items, _settings.ActiveCategories, _settings.LastCategory, _main.ShowSettings)
                {
                    Icon = _main.Icon,
                };
                return await dialog.ShowBatchAsync();
            }));
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => _main.AddActivity(ActivityKind.Error, "Scan failed", ex.Message));
        }
    }

    /// <summary>Routes mover events (raised on background threads) to the activity log and the top-of-screen overlay.</summary>
    private void WireMover(WindowIcon icon)
    {
        _mover.Activity += (kind, title, detail) =>
            Dispatcher.UIThread.Post(() => _main.AddActivity(kind, title, detail));
        _mover.MoveStarted += (category, total) => Dispatcher.UIThread.Post(() =>
        {
            _overlay ??= new ProgressOverlay { Icon = icon };
            _overlay.Begin(category, total);
        });
        _mover.MoveProgressed += p => Dispatcher.UIThread.Post(() => _overlay?.Update(p));
        _mover.MoveFinished += (ok, message) => Dispatcher.UIThread.Post(() => _overlay?.Finish(ok, message));

        // A finished download landed in a category: show what it is (poster, rating) with a Play button.
        _mover.ItemArrived += (category, path) => Dispatcher.UIThread.Post(() =>
        {
            var cat = _settings.Categories.FirstOrDefault(c => string.Equals(c.Name, category, StringComparison.OrdinalIgnoreCase));
            if (!_settings.ArrivalAlerts || cat is null || cat.ExcludeFromLibrary)
                return;
            try
            {
                ArrivalToast.Show(_settings, category, path, OpenViewer, _main.OpenFolder);
            }
            catch (Exception ex)
            {
                _main.AddActivity(ActivityKind.Warning, "Couldn't show the arrival notice", ex.Message);
            }
        });
    }

    /// <summary>
    /// Opens a picture/video window for a file or folder (optionally straight into a slideshow). Each call gets its own
    /// window, so several things can be open side by side; with no path the window asks what to open.
    /// </summary>
    public void OpenViewer(string? path, bool slideshow = false)
    {
        var window = new ViewerWindow();
        _viewers.Add(window);
        window.Closed += (_, _) => _viewers.Remove(window);
        window.Show();
        if (path is null)
            window.PromptOpen();
        else
            window.Open(path, slideshow);
    }

    private void Quit()
    {
        foreach (var viewer in _viewers.ToList())
            viewer.Close();
        _main.AllowClose = true;
        _watcher.Dispose();
        _mover.Dispose();
        _stats.Dispose();
        _tray.IsVisible = false;
        _tray.Dispose();
        _lifetime.Shutdown();
    }
}
