using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PaxMediaManager.Viewer;

/// <summary>One window for both jobs: a picture viewer (zoom, pan, rotate, slideshow) and a video player.</summary>
public partial class ViewerWindow : Window
{
    private static readonly Geometry PlayGeometry = Geometry.Parse("M8 5v14l11-7z");
    private static readonly Geometry PauseGeometry = Geometry.Parse("M6 19h4V5H6v14zm8-14v14h4V5h-4z");
    private static readonly Geometry VolumeGeometry = Geometry.Parse(
        "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z");
    private static readonly Geometry MutedGeometry = Geometry.Parse(
        "M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51A8.796 8.796 0 0 0 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06a8.99 8.99 0 0 0 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z");

    private static readonly double[] SlideSeconds = { 1, 2, 3, 4, 5, 8, 10, 15, 30, 60 };
    private static readonly (string Label, float Rate)[] Rates =
        { ("0.5×", 0.5f), ("0.75×", 0.75f), ("1×", 1f), ("1.25×", 1.25f), ("1.5×", 1.5f), ("2×", 2f) };

    private const int CacheSize = 6;

    private readonly ViewerSettings _settings = ViewerSettings.Shared;
    private readonly DispatcherTimer _slideTimer = new();
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromMilliseconds(2800) };
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stack<int> _history = new();
    private readonly Random _random = new();
    private readonly Dictionary<string, Bitmap> _cache = new();
    private readonly Queue<string> _cacheOrder = new();

    private List<string> _items = new();
    private int _index;
    private MediaMode _mode = MediaMode.None;
    private VideoPlayer? _video;
    private CancellationTokenSource? _loadCts;
    private Bitmap? _shown;
    private bool _slideshow;
    private bool _seeking;
    private bool _loadingUi;

    public ViewerWindow()
    {
        InitializeComponent();
        Icon = SpadeIcon.CreateWindowIcon();
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        // Own title bar (gone in full screen); placement remembered across viewer windows.
        AppChrome.Apply(this, "viewer");
        AppChrome.HideTitleBarInFullScreen(this);

        _loadingUi = true;
        var speed = this.FindControl<ComboBox>("SpeedBox")!;
        foreach (var s in SlideSeconds)
            speed.Items.Add($"{s:0.#} s");
        speed.SelectedIndex = ClosestIndex(SlideSeconds, _settings.SlideshowSeconds);

        var rate = this.FindControl<ComboBox>("RateBox")!;
        foreach (var r in Rates)
            rate.Items.Add(r.Label);
        rate.SelectedIndex = 2;

        this.FindControl<ToggleButton>("LoopToggle")!.IsChecked = _settings.Loop;
        this.FindControl<ToggleButton>("ShuffleToggle")!.IsChecked = _settings.Shuffle;
        this.FindControl<Slider>("VolumeSlider")!.Value = _settings.Volume;
        _loadingUi = false;

        Wire();
        UpdateMuteIcon();
        SetMode(MediaMode.None);

        // Controls and the title fade away when idle (fullscreen / slideshow / playing video) and return on any movement.
        foreach (var name in new[] { "Controls", "TopBar" })
            this.FindControl<Control>(name)!.Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(250) },
            };

        _slideTimer.Tick += (_, _) => Step(+1, fromTimer: true);
        _idleTimer.Tick += (_, _) => HideControlsIfIdle();
        _uiTimer.Tick += (_, _) => UpdateVideoUi();
        _uiTimer.Start();
        _idleTimer.Start();

        PointerMoved += (_, _) => ShowControls();
        Closing += (_, _) => Shutdown();
    }

    // ---- Opening -------------------------------------------------------------------------------------------------

    /// <summary>Opens a file or folder; a folder of pictures can start straight into a slideshow.</summary>
    public void Open(string path, bool slideshow = false)
    {
        var (mode, items, start) = MediaFiles.Resolve(path);
        if (mode == MediaMode.None || items.Count == 0)
        {
            SetMode(MediaMode.None);
            Message("Couldn't find anything to open there.");
            return;
        }

        StopSlideshow();
        _history.Clear();
        _items = items;
        _index = start;
        SetMode(mode);
        LoadCurrent();
        if (slideshow && mode == MediaMode.Image && items.Count > 1)
            StartSlideshow();
    }

    /// <summary>Shows the file picker (used when the window is opened empty, e.g. from the tray menu).</summary>
    public void PromptOpen() => _ = OpenDialogAsync();

    private async Task OpenDialogAsync()
    {
        var types = new[]
        {
            new FilePickerFileType("Pictures and videos")
            {
                Patterns = new[]
                {
                    "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif", "*.webp",
                    "*.mkv", "*.mp4", "*.avi", "*.mov", "*.wmv", "*.m4v", "*.webm", "*.ts", "*.mpg", "*.mpeg",
                },
            },
        };
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a picture or video",
            AllowMultiple = false,
            FileTypeFilter = types,
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            Open(path);
    }

    private void SetMode(MediaMode mode)
    {
        _mode = mode;
        if (mode != MediaMode.Image)
            StopSlideshow();
        if (mode != MediaMode.Video)
            _video?.Pause();

        this.FindControl<ImageStage>("Stage")!.IsVisible = mode == MediaMode.Image;
        this.FindControl<Image>("VideoImage")!.IsVisible = mode == MediaMode.Video;
        this.FindControl<StackPanel>("ImageBar")!.IsVisible = mode == MediaMode.Image;
        this.FindControl<StackPanel>("VideoBar")!.IsVisible = mode == MediaMode.Video;
        this.FindControl<Grid>("SeekRow")!.IsVisible = mode == MediaMode.Video;
        this.FindControl<Border>("Controls")!.IsVisible = mode != MediaMode.None;
        this.FindControl<TextBlock>("CenterMessage")!.IsVisible = mode == MediaMode.None;
        if (mode == MediaMode.None)
        {
            this.FindControl<TextBlock>("FileText")!.Text = "";
            this.FindControl<TextBlock>("InfoText")!.Text = "";
            Title = "PAX Media Manager";
        }
    }

    private void LoadCurrent()
    {
        if (_items.Count == 0)
            return;
        UpdateInfo(null);
        if (_mode == MediaMode.Image)
            _ = LoadImageAsync();
        else if (_mode == MediaMode.Video)
            LoadVideo();
    }

    private void Message(string text)
    {
        var message = this.FindControl<TextBlock>("CenterMessage")!;
        message.Text = text;
        message.IsVisible = true;
    }

    private void UpdateInfo(Bitmap? bitmap)
    {
        if (_items.Count == 0)
            return;
        var path = _items[_index];
        var name = Path.GetFileName(path);
        Title = $"{name} — PAX Media Manager";
        this.FindControl<TextBlock>("FileText")!.Text = name;

        var info = $"{_index + 1} / {_items.Count}";
        if (bitmap is not null)
            info += $"   ·   {bitmap.PixelSize.Width} × {bitmap.PixelSize.Height}";
        try
        {
            info += "   ·   " + FormatSize(new FileInfo(path).Length);
        }
        catch
        {
            /* size is optional */
        }

        this.FindControl<TextBlock>("InfoText")!.Text = info;
    }

    // ---- Stepping through the playlist ---------------------------------------------------------------------------

    private void Step(int delta, bool fromTimer = false)
    {
        if (_items.Count == 0)
            return;

        int target;
        if (_mode == MediaMode.Image && _settings.Shuffle && _items.Count > 1)
        {
            if (delta > 0)
            {
                _history.Push(_index);
                do
                {
                    target = _random.Next(_items.Count);
                } while (target == _index);
            }
            else
            {
                target = _history.Count > 0 ? _history.Pop() : _index;
            }
        }
        else
        {
            target = _index + delta;
            if (target >= _items.Count)
            {
                if (!_settings.Loop)
                {
                    if (fromTimer)
                        StopSlideshow();
                    return;
                }

                target = 0;
            }
            else if (target < 0)
            {
                target = _settings.Loop ? _items.Count - 1 : 0;
            }
        }

        if (target == _index && _items.Count > 1)
            return;

        _index = target;
        LoadCurrent();
        // A manual step restarts the interval so the picture you just chose gets its full time.
        if (_slideshow && !fromTimer)
        {
            _slideTimer.Stop();
            _slideTimer.Start();
        }
    }

    // ---- Pictures ------------------------------------------------------------------------------------------------

    private async Task LoadImageAsync()
    {
        var path = _items[_index];
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        this.FindControl<TextBlock>("CenterMessage")!.IsVisible = false;

        if (!_cache.TryGetValue(path, out var bitmap))
        {
            try
            {
                bitmap = await Task.Run(() => Decode(path), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (bitmap is not null)
                Remember(path, bitmap);
        }

        if (cts.IsCancellationRequested)
            return;

        if (bitmap is null)
        {
            this.FindControl<ImageStage>("Stage")!.SetImage(null);
            _shown = null;
            Message($"Couldn't open {Path.GetFileName(path)}");
            // In a slideshow, a bad file just gets skipped.
            if (_slideshow)
                Step(+1, fromTimer: true);
            return;
        }

        _shown = bitmap;
        this.FindControl<ImageStage>("Stage")!.SetImage(bitmap);
        UpdateInfo(bitmap);
        _ = PreloadNeighboursAsync();
    }

    private static Bitmap? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            // Enormous pictures are decoded at a still-generous size rather than eating hundreds of megabytes.
            return new FileInfo(path).Length > 40L * 1024 * 1024
                ? Bitmap.DecodeToWidth(stream, 8000)
                : new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private void Remember(string path, Bitmap bitmap)
    {
        if (_cache.TryAdd(path, bitmap))
            _cacheOrder.Enqueue(path);

        while (_cacheOrder.Count > CacheSize)
        {
            var old = _cacheOrder.Dequeue();
            if (_cache.TryGetValue(old, out var b) && !ReferenceEquals(b, _shown))
            {
                _cache.Remove(old);
                b.Dispose();
            }
            else if (b is not null)
            {
                _cacheOrder.Enqueue(old); // still on screen: keep it, try an older one next time
                break;
            }
        }
    }

    /// <summary>Decodes the pictures either side of the current one ahead of time, so stepping and slideshows don't stall.</summary>
    private async Task PreloadNeighboursAsync()
    {
        foreach (var offset in new[] { 1, -1 })
        {
            var i = _index + offset;
            if (_settings.Loop && _items.Count > 1)
                i = (i + _items.Count) % _items.Count;
            if (i < 0 || i >= _items.Count || _mode != MediaMode.Image)
                continue;

            var path = _items[i];
            if (_cache.ContainsKey(path))
                continue;

            var bitmap = await Task.Run(() => Decode(path));
            if (bitmap is null)
                continue;
            if (_cache.ContainsKey(path))
                bitmap.Dispose();
            else
                Remember(path, bitmap);
        }
    }

    private void ToggleSlideshow()
    {
        if (_mode != MediaMode.Image || _items.Count < 2)
            return;
        if (_slideshow)
            StopSlideshow();
        else
            StartSlideshow();
    }

    private void StartSlideshow()
    {
        _slideshow = true;
        _slideTimer.Interval = TimeSpan.FromSeconds(_settings.SlideshowSeconds);
        _slideTimer.Start();
        this.FindControl<PathIcon>("SlideshowIcon")!.Data = PauseGeometry;
        ToolTip.SetTip(this.FindControl<Button>("SlideshowButton")!, "Pause slideshow (Space)");
        ShowControls();
    }

    private void StopSlideshow()
    {
        _slideshow = false;
        _slideTimer.Stop();
        this.FindControl<PathIcon>("SlideshowIcon")!.Data = PlayGeometry;
        ToolTip.SetTip(this.FindControl<Button>("SlideshowButton")!, "Start slideshow (Space)");
    }

    // ---- Video ---------------------------------------------------------------------------------------------------

    private bool EnsureVideo()
    {
        if (_video is not null)
            return true;

        try
        {
            var player = new VideoPlayer();
            var image = this.FindControl<Image>("VideoImage")!;
            player.BitmapChanged += bitmap => image.Source = bitmap;
            player.FrameReady += () => image.InvalidateVisual();
            player.Ended += OnVideoEnded;
            player.Error += Message;
            player.StateChanged += UpdateVideoUi;
            player.Volume = _settings.Volume;
            player.Muted = _settings.Muted;
            _video = player;
            return true;
        }
        catch (Exception ex)
        {
            Message("Video playback isn't available here.\n" +
                    (OperatingSystem.IsLinux() ? "Install VLC (for example: sudo apt install vlc) and try again.\n" : "") + ex.Message);
            return false;
        }
    }

    private void LoadVideo()
    {
        if (!EnsureVideo())
            return;

        RecordProgress(force: true); // the video being left, if any
        this.FindControl<TextBlock>("CenterMessage")!.IsVisible = false;
        this.FindControl<ComboBox>("RateBox")!.SelectedIndex = 2;
        _playingPath = _items[_index];
        _pendingResume = Services.WatchHistory.ResumePoint(_playingPath);
        _video!.Open(_playingPath);
        _video.Volume = _settings.Volume;
    }

    private string? _playingPath;
    private TimeSpan? _pendingResume;
    private DateTime _lastRecord = DateTime.MinValue;

    /// <summary>Saves how far the current video got (for resume, "continue watching" and the progress bars).</summary>
    private void RecordProgress(bool force = false)
    {
        if (_video is null || _playingPath is null || _mode != MediaMode.Video)
            return;
        var length = _video.Length;
        if (length > TimeSpan.Zero && _pendingResume is null)
            Services.WatchHistory.Record(_playingPath, _video.Position, length, force);
        _lastRecord = DateTime.UtcNow;
    }

    private void OnVideoEnded()
    {
        if (_playingPath is not null && _video is not null && _video.Length > TimeSpan.Zero)
            Services.WatchHistory.Record(_playingPath, _video.Length, _video.Length, force: true);
        _playingPath = null; // recorded as finished; don't let the next LoadVideo overwrite that with position 0
        // Carry on with the next video in the folder, if there is one.
        if (_index < _items.Count - 1 || (_settings.Loop && _items.Count > 1))
            Step(+1);
    }

    private void UpdateVideoUi()
    {
        if (_mode != MediaMode.Video || _video is null)
            return;

        this.FindControl<PathIcon>("PlayPauseIcon")!.Data = _video.IsPlaying ? PauseGeometry : PlayGeometry;
        var length = _video.Length;

        // Resume where it was left last time, once the file is actually playing (seeking earlier is ignored by LibVLC).
        if (_pendingResume is { } resume && _video.IsPlaying && length > TimeSpan.Zero)
        {
            _pendingResume = null;
            _video.SeekTo(resume);
            this.FindControl<TextBlock>("InfoText")!.Text += $"   ·   resumed at {FormatTime(resume)} (Home to restart)";
        }
        else if (_video.IsPlaying && DateTime.UtcNow - _lastRecord > TimeSpan.FromSeconds(5))
        {
            RecordProgress();
        }

        var position = _video.Position;
        this.FindControl<TextBlock>("TimeText")!.Text = FormatTime(position);
        this.FindControl<TextBlock>("DurationText")!.Text = FormatTime(length);
        if (!_seeking && length > TimeSpan.Zero)
        {
            _loadingUi = true;
            this.FindControl<Slider>("SeekSlider")!.Value = position.TotalMilliseconds / length.TotalMilliseconds * 1000;
            _loadingUi = false;
        }
    }

    private void ShowTrackMenu(Button anchor, IReadOnlyList<(int Id, string Name)> tracks, int current, Action<int> choose)
    {
        var menu = new ContextMenu();
        if (tracks.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Nothing available yet", IsEnabled = false });
        foreach (var (id, name) in tracks)
        {
            var item = new MenuItem { Header = (id == current ? "●  " : "     ") + name };
            var trackId = id;
            item.Click += (_, _) => choose(trackId);
            menu.Items.Add(item);
        }

        menu.Open(anchor);
    }

    // ---- Wiring and input ----------------------------------------------------------------------------------------

    private void Wire()
    {
        var stage = this.FindControl<ImageStage>("Stage")!;
        stage.ViewChanged += () =>
            this.FindControl<Button>("ZoomLabelButton")!.Content = stage.HasImage ? $"{stage.ZoomPercent}%" : "—";

        this.FindControl<Button>("OpenButton")!.Click += async (_, _) => await OpenDialogAsync();

        // Pictures
        this.FindControl<Button>("ImgPrevButton")!.Click += (_, _) => Step(-1);
        this.FindControl<Button>("ImgNextButton")!.Click += (_, _) => Step(+1);
        this.FindControl<Button>("SlideshowButton")!.Click += (_, _) => ToggleSlideshow();
        this.FindControl<Button>("ZoomInButton")!.Click += (_, _) => stage.ZoomBy(1.25);
        this.FindControl<Button>("ZoomOutButton")!.Click += (_, _) => stage.ZoomBy(0.8);
        this.FindControl<Button>("ZoomLabelButton")!.Click += (_, _) => stage.ActualSize();
        this.FindControl<Button>("FitButton")!.Click += (_, _) => stage.Fit();
        this.FindControl<Button>("RotateButton")!.Click += (_, _) => stage.RotateRight();
        this.FindControl<Button>("ImgFullscreenButton")!.Click += (_, _) => ToggleFullscreen();

        this.FindControl<ComboBox>("SpeedBox")!.SelectionChanged += (_, _) =>
        {
            if (_loadingUi)
                return;
            var i = this.FindControl<ComboBox>("SpeedBox")!.SelectedIndex;
            if (i < 0)
                return;
            _settings.SlideshowSeconds = SlideSeconds[i];
            _settings.Save();
            if (_slideshow)
            {
                _slideTimer.Interval = TimeSpan.FromSeconds(SlideSeconds[i]);
                _slideTimer.Stop();
                _slideTimer.Start();
            }
        };
        this.FindControl<ToggleButton>("LoopToggle")!.IsCheckedChanged += (_, _) =>
        {
            if (_loadingUi)
                return;
            _settings.Loop = this.FindControl<ToggleButton>("LoopToggle")!.IsChecked == true;
            _settings.Save();
        };
        this.FindControl<ToggleButton>("ShuffleToggle")!.IsCheckedChanged += (_, _) =>
        {
            if (_loadingUi)
                return;
            _settings.Shuffle = this.FindControl<ToggleButton>("ShuffleToggle")!.IsChecked == true;
            _history.Clear();
            _settings.Save();
        };

        // Video
        this.FindControl<Button>("VidPrevButton")!.Click += (_, _) => Step(-1);
        this.FindControl<Button>("VidNextButton")!.Click += (_, _) => Step(+1);
        this.FindControl<Button>("PlayPauseButton")!.Click += (_, _) => _video?.TogglePause();
        this.FindControl<Button>("MuteButton")!.Click += (_, _) => ToggleMute();
        this.FindControl<Button>("VidFullscreenButton")!.Click += (_, _) => ToggleFullscreen();
        this.FindControl<Slider>("VolumeSlider")!.ValueChanged += (_, e) =>
        {
            if (_loadingUi)
                return;
            _settings.Volume = (int)e.NewValue;
            if (_video is not null)
                _video.Volume = _settings.Volume;
            _settings.Save();
        };
        this.FindControl<ComboBox>("RateBox")!.SelectionChanged += (_, _) =>
        {
            var i = this.FindControl<ComboBox>("RateBox")!.SelectedIndex;
            if (i >= 0 && !_loadingUi)
                _video?.SetRate(Rates[i].Rate);
        };
        this.FindControl<Button>("AudioButton")!.Click += (_, _) =>
        {
            if (_video is not null)
                ShowTrackMenu(this.FindControl<Button>("AudioButton")!, _video.AudioTracks(), _video.CurrentAudio, _video.SetAudio);
        };
        this.FindControl<Button>("SubtitleButton")!.Click += (_, _) =>
        {
            if (_video is not null)
                ShowTrackMenu(this.FindControl<Button>("SubtitleButton")!, _video.Subtitles(), _video.CurrentSubtitle, _video.SetSubtitle);
        };

        // Seeking happens when the thumb is released, not on every pixel of the drag.
        var seek = this.FindControl<Slider>("SeekSlider")!;
        seek.AddHandler(PointerPressedEvent, (_, _) => _seeking = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        seek.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (_video is not null && _video.Length > TimeSpan.Zero)
                _video.SeekTo(TimeSpan.FromMilliseconds(seek.Value / 1000 * _video.Length.TotalMilliseconds));
            _seeking = false;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        var panel = this.FindControl<Panel>("StagePanel")!;
        panel.DoubleTapped += (_, _) =>
        {
            if (_mode == MediaMode.Video)
                ToggleFullscreen();
        };
        panel.PointerWheelChanged += (_, e) =>
        {
            if (_mode != MediaMode.Video || _video is null)
                return;
            AdjustVolume(e.Delta.Y > 0 ? 5 : -5);
            e.Handled = true;
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        ShowControls();
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var stage = this.FindControl<ImageStage>("Stage")!;

        switch (e.Key)
        {
            case Key.O when ctrl:
                _ = OpenDialogAsync();
                break;
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                break;
            case Key.Escape:
                if (WindowState == WindowState.FullScreen)
                    WindowState = WindowState.Normal;
                else
                    StopSlideshow();
                break;
            case Key.Space:
                if (_mode == MediaMode.Image)
                    ToggleSlideshow();
                else
                    _video?.TogglePause();
                break;
            case Key.Left:
                if (_mode == MediaMode.Video)
                    _video?.SeekBy(TimeSpan.FromSeconds(shift ? -30 : -5));
                else
                    Step(-1);
                break;
            case Key.Right:
                if (_mode == MediaMode.Video)
                    _video?.SeekBy(TimeSpan.FromSeconds(shift ? 30 : 5));
                else
                    Step(+1);
                break;
            case Key.Up:
                if (_mode == MediaMode.Video)
                    AdjustVolume(+5);
                else
                    stage.ZoomBy(1.25);
                break;
            case Key.Down:
                if (_mode == MediaMode.Video)
                    AdjustVolume(-5);
                else
                    stage.ZoomBy(0.8);
                break;
            case Key.OemPlus:
            case Key.Add:
                stage.ZoomBy(1.25);
                break;
            case Key.OemMinus:
            case Key.Subtract:
                stage.ZoomBy(0.8);
                break;
            case Key.D0:
            case Key.NumPad0:
                stage.Fit();
                break;
            case Key.D1:
            case Key.NumPad1:
                stage.ActualSize();
                break;
            case Key.R:
                stage.RotateRight();
                break;
            case Key.M:
                ToggleMute();
                break;
            case Key.N:
                Step(+1);
                break;
            case Key.P:
                Step(-1);
                break;
            case Key.Home:
                // A video restarts from the beginning (handy after an automatic resume); pictures jump to the first.
                if (_mode == MediaMode.Video && _video is not null)
                    _video.SeekTo(TimeSpan.Zero);
                else if (_items.Count > 0)
                {
                    _index = 0;
                    LoadCurrent();
                }

                break;
            case Key.End:
                if (_items.Count > 0) { _index = _items.Count - 1; LoadCurrent(); }
                break;
            case Key.OemOpenBrackets:
                NudgeSpeed(-1);
                break;
            case Key.OemCloseBrackets:
                NudgeSpeed(+1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>[ and ] change the slideshow interval without reaching for the mouse.</summary>
    private void NudgeSpeed(int direction)
    {
        var box = this.FindControl<ComboBox>("SpeedBox")!;
        box.SelectedIndex = Math.Clamp(box.SelectedIndex + direction, 0, SlideSeconds.Length - 1);
    }

    private void AdjustVolume(int delta)
    {
        var slider = this.FindControl<Slider>("VolumeSlider")!;
        slider.Value = Math.Clamp(slider.Value + delta, 0, 100);
    }

    private void ToggleMute()
    {
        _settings.Muted = !_settings.Muted;
        if (_video is not null)
            _video.Muted = _settings.Muted;
        UpdateMuteIcon();
        _settings.Save();
    }

    private void UpdateMuteIcon() =>
        this.FindControl<PathIcon>("MuteIcon")!.Data = _settings.Muted ? MutedGeometry : VolumeGeometry;

    private void ToggleFullscreen() =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    // ---- Auto-hiding controls ------------------------------------------------------------------------------------

    private void ShowControls()
    {
        this.FindControl<Border>("Controls")!.Opacity = 1;
        this.FindControl<Border>("TopBar")!.Opacity = 1;
        this.FindControl<Button>("OpenButton")!.Opacity = 1;
        Cursor = Cursor.Default;
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void HideControlsIfIdle()
    {
        var controls = this.FindControl<Border>("Controls")!;
        var busy = WindowState == WindowState.FullScreen || _slideshow || (_mode == MediaMode.Video && _video?.IsPlaying == true);
        if (_mode == MediaMode.None || !busy || controls.IsPointerOver)
            return;

        controls.Opacity = 0;
        this.FindControl<Border>("TopBar")!.Opacity = 0;
        this.FindControl<Button>("OpenButton")!.Opacity = 0;
        Cursor = new Cursor(StandardCursorType.None);
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private void Shutdown()
    {
        StopSlideshow();
        _uiTimer.Stop();
        RecordProgress(force: true);
        Services.WatchHistory.Flush();
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }

        _settings.Save();
        _video?.Dispose();
        _video = null;
    }

    private static int ClosestIndex(double[] values, double target)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
            if (Math.Abs(values[i] - target) < Math.Abs(values[best] - target))
                best = i;
        return best;
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }
}
