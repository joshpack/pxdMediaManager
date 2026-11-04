using System.Collections.ObjectModel;
using System.Diagnostics;
using ActivityKind = PaxMediaManager.Services.ActivityKind;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;
using PaxMediaManager.Services;

namespace PaxMediaManager.Views;

public sealed class ActivityItem
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string Time { get; init; }
    public required IBrush Brush { get; init; }
}

/// <summary>Editable row in the Settings category list.</summary>
public sealed class CategoryItem
{
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    /// <summary>Shown in the Library tab; unticked categories are excluded.</summary>
    public bool InLibrary { get; set; } = true;
}

/// <summary>Anonymous row for a download being waited on — shows the category and size, never the torrent's name.</summary>
public sealed class TrackingItem
{
    public required string Text { get; init; }
    public required string Size { get; init; }
}

public partial class MainWindow : Window
{
    private const int MaxActivity = 200;
    private const int ChartDays = 14;

    private readonly AppSettings _settings;
    private readonly TorrentWatcher _watcher;
    private readonly StatsStore _stats;
    private readonly ObservableCollection<ActivityItem> _activity = new();
    private readonly CompletionMover _mover;
    private readonly ObservableCollection<CategoryItem> _categories = new();
    private readonly ObservableCollection<TrackingItem> _tracking = new();

    public MainWindow()
    {
        InitializeComponent();
        _settings = new AppSettings();
        _watcher = null!;
        _stats = new StatsStore();
        _mover = null!;
    }

    public MainWindow(AppSettings settings, TorrentWatcher watcher, StatsStore stats, CompletionMover mover)
    {
        InitializeComponent();
        _settings = settings;
        _watcher = watcher;
        _stats = stats;
        _mover = mover;

        var library = this.FindControl<LibraryView>("LibraryPane")!;
        library.Initialize(_settings, OpenFolder, OpenInViewer);
        var discover = this.FindControl<DiscoverView>("DiscoverPane")!;
        discover.Initialize(_settings);
        _sizeSaveTimer.Tick += (_, _) =>
        {
            _sizeSaveTimer.Stop();
            SaveCurrentTabSize();
        };
        // Resizes while the window is first appearing are layout, not the user; start listening a moment after it opens.
        Opened += (_, _) => DispatcherTimer.RunOnce(() => _windowReady = true, TimeSpan.FromMilliseconds(800));
        var tabs = this.FindControl<TabControl>("MainTabs")!;
        tabs.SelectionChanged += (_, e) =>
        {
            // SelectionChanged also bubbles up from combo boxes inside the tabs; only react to the tab strip itself.
            if (!ReferenceEquals(e.Source, tabs))
                return;
            ApplyTabSize();
            if (tabs.SelectedItem is TabItem { Header: "Library" })
                library.Refresh();
            else if (tabs.SelectedItem is TabItem { Header: "Discover" })
                discover.Refresh();
        };

        this.FindControl<ItemsControl>("CategoryList")!.ItemsSource = _categories;
        this.FindControl<ItemsControl>("TrackingList")!.ItemsSource = _tracking;
        foreach (var c in _settings.Categories)
            _categories.Add(new CategoryItem { Name = c.Name, Folder = c.Path, InLibrary = !c.ExcludeFromLibrary });
        Button("AddCategoryButton").Click += (_, _) => _categories.Add(new CategoryItem());
        Button("ClearTrackingButton").Click += (_, _) =>
        {
            _mover.ClearAll();
            AddActivity(ActivityKind.Info, "Stopped tracking downloads", "Nothing will be moved automatically.");
        };
        _mover.PendingChanged += () => Dispatcher.UIThread.Post(RefreshTracking);
        RefreshTracking();

        this.FindControl<Path>("Spade")!.Data = SpadeIcon.Geometry;
        this.FindControl<ItemsControl>("ActivityList")!.ItemsSource = _activity;
        _activity.CollectionChanged += (_, _) => UpdateEmptyState();

        LoadFields();
        UpdateIgnoredText();
        UpdateStatus();
        RefreshStats();

        _stats.Changed += () => Dispatcher.UIThread.Post(RefreshStats);
        // Keep "today / this week" correct across midnight while the app sits in the tray.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += (_, _) => RefreshStats();
        timer.Start();

        RefreshAutostart(announceIfOff: true);
        Button("AutostartButton").Click += (_, _) => ToggleAutostart();
        Button("SaveButton").Click += async (_, _) => await SaveAsync(close: true);
        Button("SaveOnlyButton").Click += async (_, _) => await SaveAsync(close: false);
        Button("TmdbTestButton").Click += async (_, _) => await TestTmdbAsync();
        Button("OmdbTestButton").Click += async (_, _) => await TestOmdbAsync();
        Button("MdbListTestButton").Click += async (_, _) => await TestMdbListAsync();
        Button("OpenSubsTestButton").Click += async (_, _) =>
        {
            var text = this.FindControl<TextBlock>("OpenSubsText")!;
            var key = this.FindControl<TextBox>("OpenSubsBox")!.Text?.Trim() ?? "";
            if (key.Length == 0)
            {
                text.Text = "Paste your OpenSubtitles API key first.";
                return;
            }

            var button = Button("OpenSubsTestButton");
            button.IsEnabled = false;
            text.Text = "Checking with OpenSubtitles…";
            var (ok, message) = await new OpenSubtitlesClient(key).TestAsync();
            text.Text = message;
            text.Foreground = new SolidColorBrush(Color.Parse(ok ? "#19F58C" : "#F2666A"));
            button.IsEnabled = true;
        };
        Button("BrowseSettingsFolderButton").Click += async (_, _) => await BrowseAsync("SettingsFolderBox");
        Button("ClearSettingsFolderButton").Click += (_, _) => this.FindControl<TextBox>("SettingsFolderBox")!.Text = "";
        _settings.SharedInfoChanged += () => Dispatcher.UIThread.Post(UpdateSettingsFileText);
        Button("BrowseWatchButton").Click += async (_, _) => await BrowseAsync("WatchBox");
        Button("BrowseTargetButton").Click += async (_, _) => await BrowseAsync("TargetBox");
        Button("OpenWatchButton").Click += (_, _) => OpenFolder(_settings.WatchFolder);
        Button("OpenTargetButton").Click += (_, _) => OpenFolder(_settings.TargetFolder);
        Button("ClearActivityButton").Click += (_, _) => _activity.Clear();
        Button("ClearIgnoredButton").Click += (_, _) =>
        {
            _settings.ClearIgnored();
            UpdateIgnoredText();
            AddActivity(ActivityKind.Info, "Ignore list cleared", "Previously ignored torrents will prompt again.");
        };
        Button("ResetStatsButton").Click += (_, _) =>
        {
            _stats.Reset();
            AddActivity(ActivityKind.Info, "Statistics reset", "");
        };
    }

    // ---- Remembered window size per tab --------------------------------------------------------------------------

    private readonly DispatcherTimer _sizeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private DateTime _ignoreResizeUntil = DateTime.MinValue;
    private bool _windowReady;

    private string CurrentTabName() =>
        (this.FindControl<TabControl>("MainTabs")?.SelectedItem as TabItem)?.Header as string ?? "";

    /// <summary>Gives the window the size last used on the current tab (if one was saved).</summary>
    private void ApplyTabSize()
    {
        if (_settings is null || WindowState != WindowState.Normal)
            return;
        if (!_settings.TabSizes.TryGetValue(CurrentTabName(), out var saved) || saved.Width < 100 || saved.Height < 100)
            return;

        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        var maxW = screen is null ? double.MaxValue : screen.WorkingArea.Width / screen.Scaling;
        var maxH = screen is null ? double.MaxValue : screen.WorkingArea.Height / screen.Scaling;
        var w = Math.Clamp(saved.Width, MinWidth, Math.Max(MinWidth, maxW));
        var h = Math.Clamp(saved.Height, MinHeight, Math.Max(MinHeight, maxH));
        if (Math.Abs(w - ClientSize.Width) < 1 && Math.Abs(h - ClientSize.Height) < 1)
            return;

        // Our own resize must not be mistaken for the user resizing and saved against the wrong tab.
        _ignoreResizeUntil = DateTime.UtcNow.AddMilliseconds(700);
        _sizeSaveTimer.Stop();
        Width = w;
        Height = h;
    }

    private void SaveCurrentTabSize()
    {
        var tab = CurrentTabName();
        if (_settings is null || tab.Length == 0 || WindowState != WindowState.Normal)
            return;

        _settings.TabSizes[tab] = new TabSize { Width = Math.Round(ClientSize.Width), Height = Math.Round(ClientSize.Height) };
        _settings.SaveLocal();
    }

    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);
        if (!_windowReady || DateTime.UtcNow < _ignoreResizeUntil || WindowState != WindowState.Normal)
            return;

        // Wait until the user lets go of the edge before writing anything.
        _sizeSaveTimer.Stop();
        _sizeSaveTimer.Start();
    }

    /// <summary>When false (the default) closing the window hides it to the tray instead.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>Brings the window forward on the Settings tab.</summary>
    public void ShowSettings()
    {
        ShowFromTray();
        var tabs = this.FindControl<TabControl>("MainTabs")!;
        var settings = tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header as string == "Settings");
        if (settings is not null)
            tabs.SelectedItem = settings;
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        ApplyTabSize();
        RefreshAutostart(announceIfOff: false);
    }

    private AutostartState _autostartState = AutostartState.Disabled;

    /// <summary>Re-reads the login-startup state off the UI thread and updates the Settings card.</summary>
    private void RefreshAutostart(bool announceIfOff)
    {
        _ = Task.Run(Autostart.GetStatus).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            var status = t.IsCompletedSuccessfully ? t.Result : new AutostartStatus(AutostartState.Disabled, "Could not check.");
            _autostartState = status.State;

            var (color, button) = status.State switch
            {
                AutostartState.Enabled => ("#19F58C", "Disable"),
                AutostartState.Outdated => ("#F5B14C", "Switch to this version"),
                _ => ("#8A92A6", "Start at login"),
            };
            this.FindControl<Ellipse>("AutostartDot")!.Fill = new SolidColorBrush(Color.Parse(color));
            this.FindControl<TextBlock>("AutostartText")!.Text = status.Message;
            var b = Button("AutostartButton");
            b.Content = button;
            b.IsEnabled = true;
            if (status.State == AutostartState.Disabled)
                b.Classes.Add("primary");
            else
                b.Classes.Remove("primary");

            if (announceIfOff && status.State != AutostartState.Enabled)
                AddActivity(ActivityKind.Warning, "Not set to start at login", "Open Settings to turn it on.");
        }));
    }

    private void ToggleAutostart()
    {
        var button = Button("AutostartButton");
        button.IsEnabled = false;
        var turnOn = _autostartState != AutostartState.Enabled;
        var wasOutdated = _autostartState == AutostartState.Outdated;

        _ = Task.Run(() =>
        {
            if (turnOn)
                Autostart.Enable();
            else
                Autostart.Disable();
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (t.IsFaulted)
                AddActivity(ActivityKind.Error, "Could not change startup setting", t.Exception!.GetBaseException().Message);
            else
                AddActivity(ActivityKind.Success,
                    turnOn ? (wasOutdated ? "Startup switched to this version" : "Will start at login") : "Will no longer start at login",
                    "");
            RefreshAutostart(announceIfOff: false);
        }));
    }

    public void AddActivity(ActivityKind kind, string title, string detail)
    {
        var brush = new SolidColorBrush(Color.Parse(kind switch
        {
            ActivityKind.Success => "#19F58C",
            ActivityKind.Warning => "#F5B14C",
            ActivityKind.Error => "#F2666A",
            _ => "#00C8FF",
        }));

        _activity.Insert(0, new ActivityItem
        {
            Title = title,
            Detail = detail,
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Brush = brush,
        });
        while (_activity.Count > MaxActivity)
            _activity.RemoveAt(_activity.Count - 1);

        UpdateIgnoredText();
        UpdateStatus();

        // Surface problems that need attention rather than leaving them in a hidden window.
        if (kind == ActivityKind.Error)
            ShowFromTray();
    }

    public void OpenFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;

        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", folder);
            else
                Process.Start("xdg-open", folder);
        }
        catch (Exception ex)
        {
            AddActivity(ActivityKind.Warning, "Could not open folder", ex.Message);
        }
    }

    /// <summary>Opens a picture/video viewer window for a file or folder.</summary>
    private void OpenInViewer(string path, bool slideshow) => ((App)Application.Current!).OpenViewer(path, slideshow);

    private async Task TestTmdbAsync()
    {
        var text = this.FindControl<TextBlock>("TmdbText")!;
        var credential = this.FindControl<TextBox>("TmdbBox")!.Text?.Trim() ?? "";
        if (credential.Length == 0)
        {
            text.Text = "Paste your TMDB API key or Read Access Token first.";
            return;
        }

        var button = Button("TmdbTestButton");
        button.IsEnabled = false;
        text.Text = "Checking with TMDB…";
        var (ok, message) = await new TmdbClient(credential).TestAsync();
        text.Text = message;
        text.Foreground = new SolidColorBrush(Color.Parse(ok ? "#19F58C" : "#F2666A"));
        button.IsEnabled = true;
    }

    private async Task TestOmdbAsync()
    {
        var text = this.FindControl<TextBlock>("OmdbText")!;
        var key = this.FindControl<TextBox>("OmdbBox")!.Text?.Trim() ?? "";
        if (key.Length == 0)
        {
            text.Text = "Paste your OMDb API key first.";
            return;
        }

        var button = Button("OmdbTestButton");
        button.IsEnabled = false;
        text.Text = "Checking with OMDb…";
        var (ok, message) = await new OmdbClient(key).TestAsync();
        text.Text = message;
        text.Foreground = new SolidColorBrush(Color.Parse(ok ? "#19F58C" : "#F2666A"));
        button.IsEnabled = true;
    }

    private async Task TestMdbListAsync()
    {
        var text = this.FindControl<TextBlock>("MdbListText")!;
        var key = this.FindControl<TextBox>("MdbListBox")!.Text?.Trim() ?? "";
        if (key.Length == 0)
        {
            text.Text = "Paste your MDBList API key first.";
            return;
        }

        var button = Button("MdbListTestButton");
        button.IsEnabled = false;
        text.Text = "Checking with MDBList…";
        var (ok, message) = await new MdbListClient(key).TestAsync();
        text.Text = message;
        text.Foreground = new SolidColorBrush(Color.Parse(ok ? "#19F58C" : "#F2666A"));
        button.IsEnabled = true;
    }

    private void ReloadCategoryRows()
    {
        _categories.Clear();
        foreach (var c in _settings.Categories)
            _categories.Add(new CategoryItem { Name = c.Name, Folder = c.Path, InLibrary = !c.ExcludeFromLibrary });
    }

    private void UpdateSettingsFileText()
    {
        var info = _settings.SharedInfo;
        this.FindControl<TextBlock>("SettingsFileText")!.Text =
            info.Enabled && info.FilePath.Length > 0 ? $"{info.Message}\n{info.FilePath}" : info.Message;
    }

    private void UpdateSyncText()
    {
        var s = _stats.Sync;
        this.FindControl<TextBlock>("SyncText")!.Text = s.Message;
        var dash = this.FindControl<TextBlock>("DashSyncText")!;
        dash.IsVisible = s.Enabled && s.OtherMachines.Count > 0;
        dash.Text = $"Totals combine this machine with: {string.Join(", ", s.OtherMachines)}.";
    }

    private void RefreshStats()
    {
        var now = DateTime.Now;
        var s = _stats.Summarize(now);
        SetStat("Today", s.Today);
        SetStat("Week", s.Week);
        SetStat("Month", s.Month);
        SetStat("All", s.AllTime);
        BuildChart(now);
        UpdateSyncText();
    }

    private void SetStat(string prefix, PeriodStats p)
    {
        this.FindControl<TextBlock>(prefix + "Count")!.Text = p.Count.ToString("N0");
        this.FindControl<TextBlock>(prefix + "Size")!.Text =
            p.Count == 0 ? "—" : p.Bytes > 0 ? StatsStore.FormatBytes(p.Bytes) : "size unknown";
    }

    private void BuildChart(DateTime now)
    {
        var grid = this.FindControl<Grid>("ChartGrid")!;
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();

        var days = _stats.Daily(now, ChartDays);
        var max = Math.Max(1, days.Max(d => d.Count));
        var total = days.Sum(d => d.Count);
        this.FindControl<TextBlock>("ChartSummary")!.Text =
            total == 0 ? "no activity yet" : $"{total} torrent{(total == 1 ? "" : "s")} · {StatsStore.FormatBytes(days.Sum(d => d.Bytes))}";

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        const double plotHeight = 130;
        for (var i = 0; i < days.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var (day, count, bytes) = days[i];
            var isToday = i == days.Length - 1;

            var barHeight = count == 0 ? 3 : Math.Max(8, plotHeight * count / max);
            IBrush fill = count == 0
                ? new SolidColorBrush(Color.Parse("#272C39"))
                : new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.Parse("#19F58C"), 0),
                        new GradientStop(Color.Parse("#00C8FF"), 1),
                    },
                };

            var bar = new Border
            {
                Height = barHeight,
                Margin = new Thickness(3, 0),
                CornerRadius = new CornerRadius(4, 4, 2, 2),
                Background = fill,
                VerticalAlignment = VerticalAlignment.Bottom,
                // Bars glow; today's burns a little brighter.
                BoxShadow = count == 0
                    ? default
                    : BoxShadows.Parse(isToday ? "0 0 20 2 #8019F58C" : "0 0 14 0 #4019F58C"),
            };
            ToolTip.SetTip(bar,
                $"{day:ddd, MMM d}\n{count} torrent{(count == 1 ? "" : "s")}" +
                (bytes > 0 ? $" · {StatsStore.FormatBytes(bytes)}" : ""));
            Grid.SetColumn(bar, i);
            grid.Children.Add(bar);

            var label = new TextBlock
            {
                Text = (days.Length - 1 - i) % 2 == 0 ? day.Day.ToString() : "",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = isToday
                    ? new SolidColorBrush(Color.Parse("#19F58C"))
                    : new SolidColorBrush(Color.Parse("#8A92A6")),
            };
            Grid.SetColumn(label, i);
            Grid.SetRow(label, 1);
            grid.Children.Add(label);
        }
    }

    private void LoadFields()
    {
        this.FindControl<TextBox>("WatchBox")!.Text = _settings.WatchFolder;
        var target = this.FindControl<TextBox>("TargetBox")!;
        target.Text = _settings.TargetFolder;
        this.FindControl<CheckBox>("ShareStatsBox")!.IsChecked = _settings.ShareStats;
        this.FindControl<TextBox>("TmdbBox")!.Text = _settings.TmdbKey;
        this.FindControl<TextBox>("OmdbBox")!.Text = _settings.OmdbKey;
        this.FindControl<TextBox>("MdbListBox")!.Text = _settings.MdbListKey;
        this.FindControl<TextBox>("OpenSubsBox")!.Text = _settings.OpenSubtitlesKey;
        this.FindControl<TextBox>("SubLangBox")!.Text = _settings.SubtitleLanguages;
        this.FindControl<CheckBox>("ArrivalAlertsBox")!.IsChecked = _settings.ArrivalAlerts;
        this.FindControl<TextBox>("SettingsFolderBox")!.Text = _settings.SettingsFolder;
        UpdateSettingsFileText();
        if (OperatingSystem.IsLinux())
            target.PlaceholderText = "Where torrents are sent (a folder, or a share like smb://nas.local/Downloads)";
    }

    private void RefreshTracking()
    {
        _tracking.Clear();
        foreach (var p in _mover.Snapshot())
        {
            _tracking.Add(new TrackingItem
            {
                Text = $"{p.CategoryName}  ·  added {p.Added:MMM d, h:mm tt}",
                Size = p.ExpectedBytes > 0 ? StatsStore.FormatBytes(p.ExpectedBytes) : "",
            });
        }

        this.FindControl<Border>("TrackingCard")!.IsVisible = _tracking.Count > 0;
    }

    private async void OnBrowseCategory(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: CategoryItem item })
            return;

        var options = new FolderPickerOpenOptions { AllowMultiple = false, Title = "Choose a destination folder" };
        if (!string.IsNullOrWhiteSpace(item.Folder) && Directory.Exists(item.Folder))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(item.Folder);

        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        // The row has no change notification, so swap in a fresh item to refresh the textbox.
        var index = _categories.IndexOf(item);
        if (index < 0)
            return;
        _categories[index] = new CategoryItem
        {
            Name = string.IsNullOrWhiteSpace(item.Name) ? System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) : item.Name,
            Folder = NetworkPath.ToStored(path),
            InLibrary = item.InLibrary,
        };
    }

    private void OnRemoveCategory(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CategoryItem item })
            _categories.Remove(item);
    }

    /// <summary>Saves the folders and restarts the watcher; with <paramref name="close"/>, then tucks the window away in the tray.</summary>
    private async Task SaveAsync(bool close)
    {
        var targetBox = this.FindControl<TextBox>("TargetBox")!;
        _settings.WatchFolder = this.FindControl<TextBox>("WatchBox")!.Text?.Trim() ?? "";
        _settings.TargetFolder = NetworkPath.ToStored(targetBox.Text?.Trim() ?? "");
        targetBox.Text = _settings.TargetFolder;
        _settings.ShareStats = this.FindControl<CheckBox>("ShareStatsBox")!.IsChecked == true;
        _settings.TmdbKey = this.FindControl<TextBox>("TmdbBox")!.Text?.Trim() ?? "";
        _settings.OmdbKey = this.FindControl<TextBox>("OmdbBox")!.Text?.Trim() ?? "";
        _settings.MdbListKey = this.FindControl<TextBox>("MdbListBox")!.Text?.Trim() ?? "";
        _settings.OpenSubtitlesKey = this.FindControl<TextBox>("OpenSubsBox")!.Text?.Trim() ?? "";
        var langs = this.FindControl<TextBox>("SubLangBox")!.Text?.Trim() ?? "";
        _settings.SubtitleLanguages = langs.Length > 0 ? langs : "en";
        _settings.ArrivalAlerts = this.FindControl<CheckBox>("ArrivalAlertsBox")!.IsChecked == true;
        _settings.Categories = _categories
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new Category
            {
                Name = g.Key,
                Path = NetworkPath.ToStored(g.First().Folder.Trim()),
                ExcludeFromLibrary = !g.First().InLibrary,
            })
            .ToList();
        var newSettingsFolder = NetworkPath.ToStored(this.FindControl<TextBox>("SettingsFolderBox")!.Text?.Trim() ?? "");
        if (!string.Equals(newSettingsFolder, _settings.SettingsFolder, StringComparison.Ordinal))
        {
            _settings.SettingsFolder = newSettingsFolder;
            // May wait on the network, so keep the window responsive; it also pulls in other machines' category names.
            await Task.Run(_settings.AdoptSettingsFolder);
            ReloadCategoryRows();
        }

        _settings.Save();
        _stats.SyncSoon();
        _watcher.Start();
        UpdateStatus();

        // Network folders can take a moment to answer, so check them without freezing the window.
        var save = Button("SaveButton");
        var saveOnly = Button("SaveOnlyButton");
        save.IsEnabled = saveOnly.IsEnabled = false;
        var categories = _settings.Categories.ToList();
        var target = _settings.TargetFolder;
        var (unreachable, targetOk) = await Task.Run(() =>
            (categories.Where(c => c.Path.Length > 0 && !NetworkPath.Exists(c.Path)).ToList(), NetworkPath.Exists(target)));
        save.IsEnabled = saveOnly.IsEnabled = true;

        foreach (var c in unreachable)
            AddActivity(ActivityKind.Warning, $"Category folder not reachable: {c.Name}", c.Path);

        if (!targetOk)
        {
            // Stay open so the problem is visible instead of silently hiding a broken setup.
            AddActivity(ActivityKind.Warning, "Target folder not reachable",
                _settings.TargetFolder.Length == 0 ? "No target set." : _settings.TargetFolder);
            return;
        }

        AddActivity(ActivityKind.Success, "Settings saved", "Watching and sending to the new folders.");
        if (close)
        {
            Hide();
            return;
        }

        // Staying open: confirm on the button itself, since the activity feed is on another tab.
        saveOnly.Content = "Saved ✓";
        await Task.Delay(1500);
        saveOnly.Content = "Save";
    }

    private async Task BrowseAsync(string boxName)
    {
        var box = this.FindControl<TextBox>(boxName)!;
        var options = new FolderPickerOpenOptions { AllowMultiple = false, Title = "Choose a folder" };
        if (!string.IsNullOrWhiteSpace(box.Text) && Directory.Exists(box.Text))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(box.Text);

        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            box.Text = NetworkPath.ToStored(path);
    }

    private void UpdateStatus()
    {
        var ok = _watcher is not null && _watcher.IsWatching;
        this.FindControl<TextBlock>("StatusText")!.Text = ok ? "Watching" : "Not watching";
        var dot = this.FindControl<Ellipse>("StatusDot")!;
        var pill = this.FindControl<Border>("StatusPill")!;
        dot.Fill = new SolidColorBrush(Color.Parse(ok ? "#19F58C" : "#F5B14C"));
        pill.Background = new SolidColorBrush(Color.Parse(ok ? "#0C2A1D" : "#33260F"));
        pill.BorderBrush = new SolidColorBrush(Color.Parse(ok ? "#146B44" : "#5C4515"));
        pill.BoxShadow = BoxShadows.Parse(ok ? "0 0 16 0 #4419F58C" : "0 0 16 0 #44F5B14C");
        dot.Effect = Avalonia.Media.Effect.Parse(ok ? "drop-shadow(0 0 7 #19F58C)" : "drop-shadow(0 0 7 #F5B14C)");
    }

    private void UpdateIgnoredText()
    {
        var n = _settings.IgnoredCount;
        this.FindControl<TextBlock>("IgnoredText")!.Text =
            n == 0 ? "No ignored torrents" : $"{n} ignored torrent{(n == 1 ? "" : "s")}";
        this.FindControl<Button>("ClearIgnoredButton")!.IsEnabled = n > 0;
    }

    private void UpdateEmptyState() =>
        this.FindControl<TextBlock>("EmptyText")!.IsVisible = _activity.Count == 0;

    private Button Button(string name) => this.FindControl<Button>(name)!;
}
