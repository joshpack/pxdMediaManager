using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Threading;
using PaxMediaManager.Services;
using PaxMediaManager.Viewer;

namespace PaxMediaManager.Views;

/// <summary>
/// Browsable library of what's in the category folders: a tile per top-level movie, show, game or album, either as a
/// scrolling grid or a coverflow-style carousel with a details panel. Artwork comes from the folder itself, from TMDB
/// when a key is set (matches can be corrected by hand), or is generated in neon. Categories can be left out in Settings.
/// </summary>
public partial class LibraryView : UserControl
{
    private const int BatchSize = 90;
    private const double TileArtWidth = 152;
    private const double TileArtHeight = 228;
    private const double CarouselWidth = 176;
    private const double CarouselHeight = 264;

    // Carousel geometry by distance from the selected poster: horizontal offset, scale, opacity.
    private static readonly double[] CarOffsets = { 0, 175, 300, 392, 455 };
    private static readonly double[] CarScales = { 1, 0.8, 0.66, 0.55, 0.48 };
    private static readonly double[] CarOpacity = { 1, 0.95, 0.8, 0.55, 0.35 };

    // Neon-only palette (blue ↔ green) for generated cover art.
    private static readonly (string From, string To)[] Palette =
    {
        ("#00C8FF", "#19F58C"), ("#19F58C", "#B6FF3B"), ("#00E5C8", "#1B8CFF"),
        ("#2BFFB0", "#00A3FF"), ("#00B8FF", "#00FFD0"), ("#5CFF8A", "#00D4FF"),
    };

    private static readonly string[] SortLabels = { "Recently added", "Name A–Z", "Largest first", "Newest release", "Highest rated" };

    private readonly Dictionary<LibraryItem, TileRefs> _tiles = new();
    private readonly Dictionary<int, Border> _carTiles = new();
    private readonly List<Bitmap> _bitmaps = new();
    private readonly DispatcherTimer _summaryTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private AppSettings? _settings;
    private Action<string>? _openFolder;
    private Action<string, bool>? _openViewer;
    private CancellationTokenSource? _cts;
    private List<LibraryItem> _items = new();
    private List<LibraryItem> _view = new();
    private List<string> _problems = new();
    private int _shown;
    private int _carIndex;
    private bool _carousel;
    private bool _summaryDirty;
    private bool _noCategories;
    private string? _selected; // null = all included categories
    private string _loadedKey = "";
    private DateTime _loadedAt = DateTime.MinValue;
    private DateTime _lastWheel = DateTime.MinValue;
    private TmdbClient? _tmdb;
    private string _tmdbCredential = "";
    private OmdbClient? _omdb;
    private string _omdbKey = "";
    private MdbListClient? _mdbList;
    private MetadataService? _meta;
    private string _mdbListKey = "";
    private string _posterStatus = "";

    private sealed class TileRefs
    {
        public required Image Art { get; init; }
        public required TextBlock Initials { get; init; }
        public required Control Card { get; init; }
        public TextBlock? Title { get; init; }
        public TextBlock? Detail { get; init; }
        public string? ShownArt { get; set; }
        public Border? ProgressTrack { get; init; }
        public Border? ProgressFill { get; init; }
        public Border? WatchedBadge { get; init; }
        public Border? RatingChip { get; init; }
    }

    public LibraryView()
    {
        InitializeComponent();
    }

    public void Initialize(AppSettings settings, Action<string> openFolder, Action<string, bool> openViewer)
    {
        _settings = settings;
        _openFolder = openFolder;
        _openViewer = openViewer;
        _carousel = string.Equals(settings.LibraryLayout, "carousel", StringComparison.OrdinalIgnoreCase);

        var sort = this.FindControl<ComboBox>("SortBox")!;
        foreach (var label in SortLabels)
            sort.Items.Add(label);
        sort.SelectedIndex = 0;
        sort.SelectionChanged += (_, _) => ApplyFilter();
        this.FindControl<TextBox>("SearchBox")!.TextChanged += (_, _) => ApplyFilter();
        SetupFilters();
        this.FindControl<Button>("MoreButton")!.Click += (_, _) =>
        {
            _shown = Math.Min(_view.Count, _shown + BatchSize);
            RebuildTiles();
        };

        this.FindControl<Button>("RescanButton")!.Click += (_, _) => _ = RescanAsync(
            _items.Where(i => i.Kind != LibraryKind.None && !i.IsManualMatch).ToList());

        this.FindControl<ToggleButton>("GridButton")!.Click += (_, _) => SetLayout(carousel: false);
        this.FindControl<ToggleButton>("CarouselButton")!.Click += (_, _) => SetLayout(carousel: true);

        this.FindControl<Button>("PrevButton")!.Click += (_, _) => GoTo(_carIndex - 1);
        this.FindControl<Button>("NextButton")!.Click += (_, _) => GoTo(_carIndex + 1);
        this.FindControl<Button>("DetOpenButton")!.Click += (_, _) => { if (CurrentItem() is { } i) OpenItem(i); };
        this.FindControl<Button>("DetPlayButton")!.Click += (_, _) => { if (CurrentItem() is { } i) _openViewer?.Invoke(i.Path, false); };
        this.FindControl<Button>("DetPicturesButton")!.Click += (_, _) => { if (CurrentItem() is { } i) _openViewer?.Invoke(i.Path, true); };
        this.FindControl<Button>("DetFixButton")!.Click += async (_, _) => { if (CurrentItem() is { } i) await FixMatchAsync(i); };

        var stage = this.FindControl<Panel>("Stage")!;
        stage.PointerPressed += (_, _) => stage.Focus();
        stage.PointerWheelChanged += (_, e) =>
        {
            // Touchpads fire a burst of tiny wheel events; one step per gesture is plenty.
            if (e.Delta.Y == 0 || DateTime.UtcNow - _lastWheel < TimeSpan.FromMilliseconds(110))
                return;
            _lastWheel = DateTime.UtcNow;
            GoTo(_carIndex + (e.Delta.Y < 0 ? 1 : -1));
            e.Handled = true;
        };
        KeyDown += OnKeyDown;

        _summaryTimer.Tick += (_, _) =>
        {
            if (!_summaryDirty)
                return;
            _summaryDirty = false;
            UpdateSummary();
        };
        _summaryTimer.Start();
        UpdateLayoutUi();
    }

    /// <summary>Call when the tab is shown. Reloads only if the category setup changed or the data is a couple of minutes old.</summary>
    public void Refresh(bool force = false)
    {
        if (_settings is null)
            return;

        var cats = _settings.ActiveCategories.Where(c => !c.ExcludeFromLibrary).ToList();
        EnsureTmdb();
        if (_selected is not null && cats.All(c => c.Name != _selected))
            _selected = null;
        BuildChips(cats);

        var key = string.Join("|", cats.Select(c => c.Name + "=" + c.Path)) + "#" + _selected + "#" + _tmdbCredential.GetHashCode();
        if (!force && key == _loadedKey && DateTime.Now - _loadedAt < TimeSpan.FromMinutes(2))
            return;

        _loadedKey = key;
        _loadedAt = DateTime.Now;
        _ = LoadAsync(cats);
    }

    // ---- Layout (grid / carousel) --------------------------------------------------------------------------------

    private void SetLayout(bool carousel)
    {
        _carIndex = Math.Clamp(_carIndex, 0, Math.Max(0, _view.Count - 1));
        _carousel = carousel;
        if (_settings is not null)
        {
            _settings.LibraryLayout = carousel ? "carousel" : "grid";
            _settings.SaveLocal();
        }

        UpdateLayoutUi();
        RebuildTiles();
        if (carousel)
            this.FindControl<Panel>("Stage")!.Focus();
    }

    private void UpdateLayoutUi()
    {
        this.FindControl<ToggleButton>("GridButton")!.IsChecked = !_carousel;
        this.FindControl<ToggleButton>("CarouselButton")!.IsChecked = _carousel;
        this.FindControl<ScrollViewer>("Scroller")!.IsVisible = !_carousel;
        this.FindControl<Grid>("CarouselHost")!.IsVisible = _carousel;
        this.FindControl<ComboBox>("SortBox")!.IsVisible = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Leave arrow keys alone while typing in the search box.
        if (!_carousel || _view.Count == 0 || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            return;

        switch (e.Key)
        {
            case Key.Left: GoTo(_carIndex - 1); break;
            case Key.Right: GoTo(_carIndex + 1); break;
            case Key.Home: GoTo(0); break;
            case Key.End: GoTo(_view.Count - 1); break;
            case Key.Enter: if (CurrentItem() is { } i) ShowDetails(i); break;
            default: return;
        }

        e.Handled = true;
    }

    private LibraryItem? CurrentItem() => _carousel && _carIndex >= 0 && _carIndex < _view.Count ? _view[_carIndex] : null;

    private void GoTo(int index)
    {
        if (_view.Count == 0)
            return;
        var target = Math.Clamp(index, 0, _view.Count - 1);
        if (target == _carIndex)
            return;
        _carIndex = target;
        UpdateCarousel();
    }

    // ---- Category chips ------------------------------------------------------------------------------------------

    private void BuildChips(List<Category> cats)
    {
        var panel = this.FindControl<WrapPanel>("Chips")!;
        panel.Children.Clear();
        if (cats.Count == 0)
            return;

        void Add(string label, string? value)
        {
            var chip = new ToggleButton
            {
                Content = label,
                Classes = { "chip" },
                IsChecked = _selected == value,
                Margin = new Thickness(0, 0, 8, 4),
            };
            chip.Click += (_, _) =>
            {
                _selected = value;
                _carIndex = 0;
                Refresh(force: true);
            };
            panel.Children.Add(chip);
        }

        if (cats.Count > 1)
            Add("All", null);
        foreach (var c in cats)
            Add(c.Name, c.Name);
    }

    // ---- Loading -------------------------------------------------------------------------------------------------

    private async Task LoadAsync(List<Category> cats)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;

        _noCategories = cats.Count == 0;
        var pick = _selected is null ? cats : cats.Where(c => c.Name == _selected).ToList();
        if (pick.Count == 0)
        {
            _items = new();
            _problems = new();
            ApplyFilter();
            return;
        }

        SetStatus("Scanning your library…");
        var problems = new List<string>();
        List<LibraryItem> items;
        try
        {
            items = await Task.Run(() =>
            {
                var all = new List<LibraryItem>();
                foreach (var c in pick)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        // On Linux an smb:// category is mounted here; this can block while the share connects.
                        var local = NetworkPath.ToLocal(c.Path);
                        if (local is null || !Directory.Exists(local))
                        {
                            lock (problems)
                                problems.Add(c.Name);
                            continue;
                        }

                        var found = LibraryScanner.List(local, KindFor(c.Name), ct);
                        var anime = c.Name.Contains("anime", StringComparison.OrdinalIgnoreCase);
                        foreach (var i in found)
                        {
                            i.Category = c.Name;
                            i.IsAnime = anime;
                        }

                        all.AddRange(found);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        lock (problems)
                            problems.Add(c.Name);
                    }
                }

                return all;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        _problems = problems;
        _items = items;
        ApplyFilter();
        _ = Task.Run(() => EnrichAsync(items, ct));
    }

    /// <summary>
    /// Background pass: works out what uncategorised folders hold, finds folder artwork and measures sizes (cached), a
    /// few at a time so a NAS isn't hammered; then matching, details, ratings and file specs.
    /// </summary>
    private async Task EnrichAsync(List<LibraryItem> items, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(3);
        var tasks = items.OrderByDescending(i => i.Modified).Select(async item =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                // An uncategorised folder may turn out to be a game or an audiobook once its files are looked at.
                if (item.Kind == LibraryKind.Auto && item.Extra.Length == 0)
                {
                    var sniffed = MetadataService.SniffKind(item);
                    if (sniffed != LibraryKind.Auto)
                        item.Kind = sniffed;
                }

                if (item.IsGroup)
                {
                    // A show grouped from episodes: artwork from the first season folder that has some, size is the sum.
                    foreach (var m in item.Members.Where(m => m.IsDirectory))
                        if ((item.ArtPath ??= LibraryScanner.FindArtwork(m.Path)) is not null)
                            break;
                    if (item.Size is null)
                    {
                        long total = 0;
                        foreach (var m in item.Members)
                            total += m.Size ??= LibrarySizeCache.TryGet(m) ?? MeasureAndCache(m, ct);
                        item.Size = total;
                    }
                }
                else if (item.IsDirectory)
                {
                    item.ArtPath ??= LibraryScanner.FindArtwork(item.Path);
                    item.Size ??= LibrarySizeCache.TryGet(item) ?? MeasureAndCache(item, ct);
                }

                Dispatcher.UIThread.Post(() => OnItemUpdated(item));
            }
            catch (OperationCanceledException)
            {
                /* switched category or closed */
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (!ct.IsCancellationRequested)
            LibrarySizeCache.Flush();
        if (!ct.IsCancellationRequested)
            await PosterPassAsync(items, ct, force: false).ConfigureAwait(false);
        if (!ct.IsCancellationRequested)
            await MetadataPassAsync(items, ct).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            _summaryDirty = false;
            UpdateSummary();
        });
    }

    private static long MeasureAndCache(LibraryItem item, CancellationToken ct)
    {
        var size = LibraryScanner.FolderSize(item.Path, ct);
        LibrarySizeCache.Set(item, size);
        return size;
    }

    // ---- Metadata (TMDB / Steam / Audible, ratings, file specs) --------------------------------------------------------

    private void EnsureTmdb()
    {
        var credential = _settings?.TmdbKey?.Trim() ?? "";
        if (credential != _tmdbCredential || _meta is null)
        {
            _tmdbCredential = credential;
            _tmdb = credential.Length > 0 ? new TmdbClient(credential) : null;
            _meta = new MetadataService(_tmdb);
        }

        var omdbKey = _settings?.OmdbKey?.Trim() ?? "";
        if (omdbKey != _omdbKey)
        {
            _omdbKey = omdbKey;
            _omdb = omdbKey.Length > 0 ? new OmdbClient(omdbKey) : null;
        }

        var mdbKey = _settings?.MdbListKey?.Trim() ?? "";
        if (mdbKey != _mdbListKey)
        {
            _mdbListKey = mdbKey;
            _mdbList = mdbKey.Length > 0 ? new MdbListClient(mdbKey) : null;
        }

        this.FindControl<TextBlock>("CreditText")!.IsVisible = _tmdb is not null;
        ToolTip.SetTip(this.FindControl<Button>("RescanButton")!,
            _tmdb is null ? "Look games and audiobooks up again (add a TMDB key in Settings for films and TV)" : "Look every title up again");
    }

    /// <summary>What a category holds, from its name. Uncategorised folders are sniffed by their files later.</summary>
    internal static LibraryKind KindFor(string categoryName)
    {
        var n = categoryName.ToLowerInvariant();
        if (n.Contains("audiobook") || n.Contains("audio book") || n.Contains("book"))
            return LibraryKind.Audiobook;
        if (n.Contains("game"))
            return LibraryKind.Game;
        if (n.Contains("music") || n.Contains("audio") || n.Contains("album") || n.Contains("software"))
            return LibraryKind.None;
        if (n.Contains("tv") || n.Contains("show") || n.Contains("series") || n.Contains("anime"))
            return LibraryKind.Tv;
        if (n.Contains("movie") || n.Contains("film"))
            return LibraryKind.Movie;
        return LibraryKind.Auto;
    }

    /// <summary>
    /// Matches each item (hand-picked matches win) and downloads its cover: TMDB for films and TV, Steam for games,
    /// Audible for audiobooks. <paramref name="force"/> ignores the cache, for "rescan"; an item that finds nothing then
    /// keeps whatever it had.
    /// </summary>
    private async Task PosterPassAsync(List<LibraryItem> items, CancellationToken ct, bool force)
    {
        var meta = _meta;
        if (meta is null)
            return;
        var todo = items.Where(meta.CanMatch).OrderByDescending(i => i.Modified).ToList();
        if (todo.Count == 0)
            return;

        var done = 0;
        SetPosterStatus($"matching titles (0/{todo.Count})");
        using var gate = new SemaphoreSlim(3);
        var tasks = todo.Select(async item =>
        {
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var match = LibraryMatches.Get(item);
                if (match is not null)
                {
                    item.IsManualMatch = true;
                }
                else
                {
                    match = await meta.FindAsync(item, ct, refresh: force).ConfigureAwait(false);
                    if (match is null && force)
                        match = item.Match; // nothing came back (or the source was unreachable): don't throw away what we had
                }

                item.Match = match;
                if (match?.PosterPath is not null)
                    item.TmdbArtPath = await meta.GetPosterFileAsync(match, ct).ConfigureAwait(false) ?? item.TmdbArtPath;
                else if (match is null)
                    item.TmdbArtPath = null;

                Dispatcher.UIThread.Post(() => OnItemUpdated(item));
            }
            catch (OperationCanceledException)
            {
                /* switched category */
            }
            finally
            {
                gate.Release();
                var d = Interlocked.Increment(ref done);
                if (d % 5 == 0 || d == todo.Count)
                    SetPosterStatus($"matching titles ({d}/{todo.Count})");
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        SetPosterStatus(meta.Tmdb?.Rejected == true ? "TMDB rejected the key, check Settings" : "");
    }

    /// <summary>
    /// Fills in what the filters, shelves, stats and duplicate finder need: full details (genres, runtime...), scores
    /// from the extra rating sites, and the file specs (resolution, length). Everything comes from disk caches after the
    /// first time, so later visits finish in moments; new titles are looked up a few at a time.
    /// </summary>
    private async Task MetadataPassAsync(List<LibraryItem> items, CancellationToken ct)
    {
        var meta = _meta;
        if (meta is null)
            return;
        var omdb = _omdb;
        var mdb = _mdbList;
        var matched = items.Where(i => i.Match is not null).OrderByDescending(i => i.Modified).ToList();
        var done = 0;
        using (var gate = new SemaphoreSlim(3))
        {
            await Task.WhenAll(matched.Select(async item =>
            {
                try
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    var match = item.Match!;
                    item.Details ??= await meta.GetDetailsAsync(match, ct).ConfigureAwait(false);
                    if (match.Kind is LibraryKind.Movie or LibraryKind.Tv)
                    {
                        var scores = new List<RatingScore>();
                        if (mdb is not null && await mdb.GetAsync(match, ct).ConfigureAwait(false) is { } m)
                            scores.AddRange(m);
                        var imdbId = item.Details?.ImdbId is { Length: > 0 } id ? id : null;
                        if (omdb is not null && imdbId is not null && scores.All(s => s.Source != "imdb")
                            && await omdb.GetRatingsAsync(imdbId, ct).ConfigureAwait(false) is { } o
                            && double.TryParse(o.Imdb, NumberStyles.Float, CultureInfo.InvariantCulture, out var imdb))
                            scores.Add(new RatingScore("imdb", imdb, int.TryParse(o.ImdbVotes.Replace(",", ""), out var v) ? v : null));
                        // Japanese animation counts as anime even outside an anime category.
                        if (!item.IsAnime && item.Details is { OriginalLanguage: "ja" } d && d.Genres.Contains("Animation"))
                            item.IsAnime = true;
                        if (item.IsAnime && await AniListClient.GetScoreAsync(item.DisplayTitle, ct).ConfigureAwait(false) is { } ani)
                            scores.Add(ani);
                        item.Scores = scores;
                    }

                    Dispatcher.UIThread.Post(() => OnItemUpdated(item));
                }
                catch (OperationCanceledException)
                {
                    /* switched category */
                }
                catch
                {
                    /* one title failing shouldn't stop the rest */
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref done);
                    if (n % 10 == 0 || n == matched.Count)
                        SetPosterStatus(n == matched.Count ? "" : $"fetching details ({n}/{matched.Count})");
                }
            })).ConfigureAwait(false);
        }

        Dispatcher.UIThread.Post(RefreshFilterOptions);

        // File specs last: LibVLC reads one file at a time, and each answer is cached on disk.
        var videos = items.Where(i => i.Kind is LibraryKind.Movie or LibraryKind.Tv or LibraryKind.Auto && i.Specs is null).ToList();
        var count = 0;
        foreach (var item in videos)
        {
            if (ct.IsCancellationRequested)
                return;
            try
            {
                item.Specs = await MediaSpecs.ProbeAsync(item, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                /* skip unreadable files */
            }

            if (++count % 10 == 0)
                SetPosterStatus($"reading file details ({count}/{videos.Count})");
        }

        MediaSpecs.Flush();
        SetPosterStatus("");
        Dispatcher.UIThread.Post(RefreshFilterOptions);
    }

    private async Task RescanAsync(List<LibraryItem> items)
    {
        if (items.Count == 0)
            return;
        var ct = _cts?.Token ?? CancellationToken.None;
        await PosterPassAsync(items, ct, force: true);
        foreach (var i in items)
            i.Details = null;
        await MetadataPassAsync(items, ct);
    }

    private async Task FixMatchAsync(LibraryItem item)
    {
        if (_meta is null || TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var result = await new MatchDialog(_meta, item).ShowDialog<MatchDialogResult?>(owner);
        if (result is null)
            return;

        if (result.Reset)
        {
            ResetMatch(item);
            return;
        }

        if (result.Match is { } match)
        {
            LibraryMatches.Set(item, match);
            item.Match = match;
            item.IsManualMatch = true;
            item.TmdbArtPath = null;
            item.Details = null;
            item.Scores = new();
            OnItemUpdated(item);
            item.TmdbArtPath = await _meta.GetPosterFileAsync(match, CancellationToken.None);
            OnItemUpdated(item);
            _ = MetadataPassAsync(new List<LibraryItem> { item }, CancellationToken.None);
        }
    }

    private void ResetMatch(LibraryItem item)
    {
        LibraryMatches.Remove(item);
        item.IsManualMatch = false;
        item.Match = null;
        item.TmdbArtPath = null;
        item.Details = null;
        item.Scores = new();
        OnItemUpdated(item);
        _ = RescanAsync(new List<LibraryItem> { item });
    }

    private void SetPosterStatus(string status) =>
        Dispatcher.UIThread.Post(() =>
        {
            _posterStatus = status;
            _summaryDirty = true;
        });

    // ---- Updating what's on screen -------------------------------------------------------------------------------

    private void OnItemUpdated(LibraryItem item)
    {
        _summaryDirty = true;
        if (_tiles.TryGetValue(item, out var tile))
        {
            if (tile.Title is not null)
                tile.Title.Text = item.DisplayTitle;
            if (tile.Detail is not null)
                tile.Detail.Text = DetailText(item);
            tile.Initials.Text = Initials(item.DisplayTitle);
            ToolTip.SetTip(tile.Card, TooltipText(item));

            var art = item.DisplayArt;
            if (art is null)
            {
                if (tile.ShownArt is not null)
                {
                    tile.Art.Source = null;
                    tile.Initials.IsVisible = true;
                    tile.ShownArt = null;
                }
            }
            else if (art != tile.ShownArt)
            {
                LoadArt(tile, art);
            }

            UpdateTileBadges(item, tile);
        }

        if (_carousel && CurrentItem() is { } current && ReferenceEquals(current, item))
            UpdateDetails();
    }

    // ---- Shelves and filters ---------------------------------------------------------------------------------------

    private static readonly string[] ShelfLabels =
    {
        "Everything", "Continue watching", "Recently added", "Unwatched", "Watched", "Top rated (7.5+)",
        "Movies", "TV shows", "Anime", "Games", "Audiobooks",
    };

    private static readonly string[] DecadeLabels = { "Any decade", "2020s", "2010s", "2000s", "1990s", "1980s", "Older" };
    private static readonly string[] ResLabels = { "Any quality", "4K", "1440p", "1080p", "720p", "SD" };
    private static readonly (string Label, double Min)[] RatingSteps = { ("Any rating", 0), ("6+", 6), ("7+", 7), ("8+", 8), ("9+", 9) };
    private bool _fillingFilters;
    private bool _progressRefreshQueued;

    private ComboBox Combo(string name) => this.FindControl<ComboBox>(name)!;

    private void SetupFilters()
    {
        _fillingFilters = true;
        foreach (var l in ShelfLabels)
            Combo("ShelfBox").Items.Add(l);
        Combo("GenreBox").Items.Add("All genres");
        foreach (var l in DecadeLabels)
            Combo("DecadeBox").Items.Add(l);
        foreach (var l in ResLabels)
            Combo("ResBox").Items.Add(l);
        foreach (var (label, _) in RatingSteps)
            Combo("RatingBox").Items.Add(label);
        foreach (var name in new[] { "ShelfBox", "GenreBox", "DecadeBox", "ResBox", "RatingBox" })
        {
            Combo(name).SelectedIndex = 0;
            Combo(name).SelectionChanged += (_, _) =>
            {
                if (!_fillingFilters)
                    ApplyFilter();
            };
        }

        _fillingFilters = false;

        this.FindControl<Button>("ClearFiltersButton")!.Click += (_, _) =>
        {
            _fillingFilters = true;
            foreach (var name in new[] { "ShelfBox", "GenreBox", "DecadeBox", "ResBox", "RatingBox" })
                Combo(name).SelectedIndex = 0;
            _fillingFilters = false;
            ApplyFilter();
        };
        this.FindControl<Button>("SurpriseButton")!.Click += (_, _) => SurpriseMe();
        this.FindControl<Button>("StatsButton")!.Click += (_, _) => ShowTool(new StatsWindow(_items, ScopeLabel()));
        this.FindControl<Button>("DupesButton")!.Click += (_, _) => ShowTool(new DuplicatesWindow(_items, ScopeLabel(), _openFolder, ShowDetails));

        // Progress bars follow playback: the viewer records progress every few seconds.
        WatchHistory.Changed += () =>
        {
            if (_progressRefreshQueued)
                return;
            _progressRefreshQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _progressRefreshQueued = false;
                foreach (var (item, tile) in _tiles)
                    UpdateTileBadges(item, tile);
            }, DispatcherPriority.Background);
        };
    }

    private string ScopeLabel() => _selected ?? "all library categories";

    private void ShowTool(Window window)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }

    /// <summary>Genres come from the fetched details, so the list grows as the background pass fills them in.</summary>
    private void RefreshFilterOptions()
    {
        var box = Combo("GenreBox");
        var current = box.SelectedItem as string;
        var genres = _items.SelectMany(i => i.Details?.Genres ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        _fillingFilters = true;
        box.Items.Clear();
        box.Items.Add("All genres");
        foreach (var g in genres)
            box.Items.Add(g);
        box.SelectedItem = current is not null && genres.Contains(current) ? current : "All genres";
        _fillingFilters = false;

        // Filters that depend on fetched data may now match more: refresh, but only if one is in use (keeps scroll position).
        if (FiltersActive())
            ApplyFilter();
    }

    private bool FiltersActive() =>
        new[] { "ShelfBox", "GenreBox", "DecadeBox", "ResBox", "RatingBox" }.Any(n => Combo(n).SelectedIndex > 0);

    private (double Fraction, DateTime? Last, bool Finished) ProgressOf(LibraryItem item)
    {
        var episodes = item.IsGroup ? item.Members.Count(m => m.EpisodeNumber.Episode is not null) : 0;
        return WatchHistory.Progress(item, episodes > 1 ? episodes : null);
    }

    private IEnumerable<LibraryItem> ApplyShelfAndFilters(IEnumerable<LibraryItem> list)
    {
        switch (Combo("ShelfBox").SelectedIndex)
        {
            case 1:
                list = list.Select(i => (i, p: ProgressOf(i))).Where(x => x.p.Last is not null && !x.p.Finished)
                    .OrderByDescending(x => x.p.Last).Select(x => x.i);
                break;
            case 2:
                list = list.Where(i => i.Modified > DateTime.Now.AddDays(-30));
                break;
            case 3:
                list = list.Where(i => i.Kind is not (LibraryKind.Game or LibraryKind.None) && ProgressOf(i).Last is null);
                break;
            case 4:
                list = list.Where(i => ProgressOf(i).Finished);
                break;
            case 5:
                list = list.Where(i => i.BestRating >= 7.5);
                break;
            case 6:
                list = list.Where(i => i.Kind == LibraryKind.Movie || i.Match?.Kind == LibraryKind.Movie);
                break;
            case 7:
                list = list.Where(i => (i.Kind == LibraryKind.Tv || i.Match?.Kind == LibraryKind.Tv) && !i.IsAnime);
                break;
            case 8:
                list = list.Where(i => i.IsAnime);
                break;
            case 9:
                list = list.Where(i => i.Kind == LibraryKind.Game);
                break;
            case 10:
                list = list.Where(i => i.Kind == LibraryKind.Audiobook);
                break;
        }

        if (Combo("GenreBox").SelectedIndex > 0 && Combo("GenreBox").SelectedItem is string genre)
            list = list.Where(i => i.Details?.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase) == true);

        var decade = Combo("DecadeBox").SelectedIndex;
        if (decade > 0)
        {
            list = decade == DecadeLabels.Length - 1
                ? list.Where(i => i.DisplayYear < 1980)
                : list.Where(i => i.DisplayYear is { } y && y / 10 * 10 == 2020 - (decade - 1) * 10);
        }

        if (Combo("ResBox").SelectedIndex > 0 && Combo("ResBox").SelectedItem is string res)
            list = list.Where(i => i.Specs?.Resolution == res);

        var minRating = RatingSteps[Math.Max(0, Combo("RatingBox").SelectedIndex)].Min;
        if (minRating > 0)
            list = list.Where(i => i.BestRating >= minRating);
        return list;
    }

    /// <summary>Opens the details of something unwatched and well rated from what's currently shown.</summary>
    private void SurpriseMe()
    {
        var watchable = _view.Where(i => i.Kind is not (LibraryKind.None or LibraryKind.Game)).ToList();
        var unwatched = watchable.Where(i => ProgressOf(i).Last is null).ToList();
        var pool = unwatched.Where(i => i.BestRating >= 7).ToList();
        if (pool.Count == 0)
            pool = unwatched.Count > 0 ? unwatched : watchable.Count > 0 ? watchable : _view;
        if (pool.Count == 0)
            return;
        ShowDetails(pool[Random.Shared.Next(pool.Count)]);
    }

    private void ApplyFilter()
    {
        var q = this.FindControl<TextBox>("SearchBox")!.Text?.Trim() ?? "";
        IEnumerable<LibraryItem> list = _items;
        if (q.Length > 0)
            list = list.Where(i => i.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase));

        this.FindControl<Button>("ClearFiltersButton")!.IsVisible = FiltersActive();
        list = ApplyShelfAndFilters(list);

        // "Continue watching" keeps its most-recent-first order; everything else follows the sort box.
        if (Combo("ShelfBox").SelectedIndex != 1)
            list = this.FindControl<ComboBox>("SortBox")!.SelectedIndex switch
            {
                1 => list.OrderBy(i => i.DisplayTitle, StringComparer.OrdinalIgnoreCase),
                2 => list.OrderByDescending(i => i.Size ?? 0),
                3 => list.OrderByDescending(i => i.DisplayYear ?? 0).ThenBy(i => i.DisplayTitle, StringComparer.OrdinalIgnoreCase),
                4 => list.OrderByDescending(i => i.BestRating ?? 0).ThenBy(i => i.DisplayTitle, StringComparer.OrdinalIgnoreCase),
                _ => list.OrderByDescending(i => i.Modified),
            };

        _view = list.ToList();
        _shown = Math.Min(_view.Count, BatchSize);
        _carIndex = Math.Clamp(_carIndex, 0, Math.Max(0, _view.Count - 1));
        RebuildTiles();
    }

    private void RebuildTiles()
    {
        var tiles = this.FindControl<WrapPanel>("Tiles")!;
        var layer = this.FindControl<Panel>("CarouselTiles")!;
        tiles.Children.Clear();
        layer.Children.Clear();
        _tiles.Clear();
        _carTiles.Clear();
        foreach (var b in _bitmaps)
            b.Dispose();
        _bitmaps.Clear();

        if (_carousel)
        {
            UpdateCarousel();
            this.FindControl<Button>("MoreButton")!.IsVisible = false;
        }
        else
        {
            foreach (var item in _view.Take(_shown))
                tiles.Children.Add(BuildTile(item));

            var more = this.FindControl<Button>("MoreButton")!;
            more.IsVisible = _view.Count > _shown;
            more.Content = $"Show more ({_view.Count - _shown} left)";
        }

        if (_view.Count == 0)
        {
            SetStatus(
                _noCategories
                    ? "No categories in the library yet.\nAdd a category with a folder in Settings (and keep its Library box ticked)."
                    : _problems.Count > 0 && _items.Count == 0
                        ? $"Couldn't reach the folder for: {string.Join(", ", _problems)}."
                        : _items.Count > 0 ? "No matches for that search." : "Nothing in these folders yet.");
        }
        else
        {
            SetStatus(null);
        }

        UpdateSummary();
    }

    private void SetStatus(string? text)
    {
        var status = this.FindControl<TextBlock>("StatusText")!;
        status.Text = text ?? "";
        status.IsVisible = text is not null;
    }

    private void UpdateSummary()
    {
        var total = _view.Sum(i => i.Size ?? 0);
        var measuring = _view.Any(i => i.IsDirectory && i.Size is null);
        var text = _view.Count == 0
            ? ""
            : $"{_view.Count:N0} item{(_view.Count == 1 ? "" : "s")}  ·  {StatsStore.FormatBytes(total)}" + (measuring ? "  ·  measuring sizes…" : "");
        if (_problems.Count > 0 && _items.Count > 0)
            text += $"  ·  couldn't reach: {string.Join(", ", _problems)}";
        if (_posterStatus.Length > 0 && _view.Count > 0)
            text += "  ·  " + _posterStatus;
        this.FindControl<TextBlock>("SummaryText")!.Text = text;
    }

    // ---- Grid tiles ----------------------------------------------------------------------------------------------

    private Control BuildTile(LibraryItem item)
    {
        var (frame, image, initials) = BuildArt(item, TileArtWidth, TileArtHeight, 46, 10);

        var title = new TextBlock
        {
            Text = item.DisplayTitle,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 36,
            Margin = new Thickness(2, 8, 2, 0),
        };
        var detail = new TextBlock
        {
            Text = DetailText(item),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#8A92A6")),
            Margin = new Thickness(2, 2, 2, 0),
        };

        // Overlays on the poster: rating (top left), watched tick (top right), progress bar (bottom).
        var ratingChip = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#D90B0F14")),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2),
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
            Child = new TextBlock { FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#F5C518")) },
        };
        var watched = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#19F58C")),
            CornerRadius = new CornerRadius(11),
            Width = 22,
            Height = 22,
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
            Child = new TextBlock
            {
                Text = "✓", FontWeight = FontWeight.Bold, FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#04140C")),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var fill = new Border { Background = new SolidColorBrush(Color.Parse("#19F58C")), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var track = new Border
        {
            Height = 4,
            Background = new SolidColorBrush(Color.Parse("#99000000")),
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(0, 0, 10, 10),
            ClipToBounds = true,
            IsVisible = false,
            Child = fill,
        };
        var poster = new Panel { Width = TileArtWidth, Height = TileArtHeight };
        poster.Children.Add(frame);
        poster.Children.Add(ratingChip);
        poster.Children.Add(watched);
        poster.Children.Add(track);

        var stack = new StackPanel();
        stack.Children.Add(poster);
        stack.Children.Add(title);
        stack.Children.Add(detail);

        var card = new Border
        {
            Classes = { "tile" },
            Width = TileArtWidth + 14,
            Margin = new Thickness(4),
            Padding = new Thickness(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = stack,
            ContextMenu = BuildMenu(item),
        };
        ToolTip.SetTip(card, TooltipText(item));
        card.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
                ShowDetails(item);
        };

        var refs = new TileRefs
        {
            Art = image, Initials = initials, Card = card, Title = title, Detail = detail,
            ProgressTrack = track, ProgressFill = fill, WatchedBadge = watched, RatingChip = ratingChip,
        };
        _tiles[item] = refs;
        if (item.DisplayArt is { } art)
            LoadArt(refs, art);
        UpdateTileBadges(item, refs);
        return card;
    }

    /// <summary>Rating chip, watched tick and progress bar on a grid tile.</summary>
    private void UpdateTileBadges(LibraryItem item, TileRefs tile)
    {
        if (tile.RatingChip is { Child: TextBlock rt })
        {
            var r = item.BestRating;
            tile.RatingChip.IsVisible = r is > 0;
            // Books are rated out of 5 on Audible (kept doubled internally so all filters share one scale).
            rt.Text = r is not > 0 ? "" : item.Kind == LibraryKind.Audiobook ? $"★ {r / 2:0.0}" : $"★ {r:0.0}";
        }

        if (tile.ProgressTrack is null || tile.ProgressFill is null || tile.WatchedBadge is null)
            return;
        var (fraction, last, finished) = ProgressOf(item);
        tile.WatchedBadge.IsVisible = finished;
        tile.ProgressTrack.IsVisible = last is not null && !finished && fraction > 0.01;
        tile.ProgressFill.Width = TileArtWidth * fraction;
    }

    /// <summary>The poster rectangle: generated neon gradient with initials, with the real image on top once it loads.</summary>
    private static (Border Frame, Image Image, TextBlock Initials) BuildArt(LibraryItem item, double width, double height, double initialsSize, double radius)
    {
        var (from, to) = Palette[(StableHash(item.Title) & int.MaxValue) % Palette.Length];
        var initials = new TextBlock
        {
            Text = Initials(item.DisplayTitle),
            FontSize = initialsSize,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#04140C")),
            Opacity = 0.55,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var image = new Image { Stretch = Stretch.UniformToFill };

        var art = new Grid { Width = width, Height = height };
        art.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse(from), 0), new GradientStop(Color.Parse(to), 1) },
            },
        });
        art.Children.Add(initials);
        art.Children.Add(image);
        return (new Border { CornerRadius = new CornerRadius(radius), ClipToBounds = true, Child = art }, image, initials);
    }

    private ContextMenu BuildMenu(LibraryItem item)
    {
        var fix = new MenuItem { Header = "Fix match…" };
        fix.Click += async (_, _) => await FixMatchAsync(item);
        var rescan = new MenuItem { Header = "Rescan this title" };
        rescan.Click += (_, _) => _ = RescanAsync(new List<LibraryItem> { item });
        var reset = new MenuItem { Header = "Reset to automatic match" };
        reset.Click += (_, _) => ResetMatch(item);
        var open = new MenuItem { Header = "Open folder" };
        open.Click += (_, _) => OpenItem(item);
        var play = new MenuItem { Header = "Play" };
        play.Click += (_, _) => _openViewer?.Invoke(item.Path, false);
        var pictures = new MenuItem { Header = "View pictures (slideshow)" };
        pictures.Click += (_, _) => _openViewer?.Invoke(item.Path, true);

        var details = new MenuItem { Header = "Details…", FontWeight = FontWeight.Bold };
        details.Click += (_, _) => ShowDetails(item);

        var menu = new ContextMenu();
        menu.Items.Add(details);
        menu.Items.Add(new Separator());
        menu.Items.Add(fix);
        menu.Items.Add(rescan);
        menu.Items.Add(reset);
        menu.Items.Add(new Separator());
        menu.Items.Add(play);
        menu.Items.Add(pictures);
        menu.Items.Add(open);

        // Enabled state depends on whether a key is set and whether this item was matched by hand, so refresh on open.
        menu.Opening += (_, _) =>
        {
            var canMatch = _meta?.CanMatch(item) == true;
            fix.IsEnabled = canMatch;
            rescan.IsEnabled = canMatch && !item.IsManualMatch;
            reset.IsEnabled = canMatch && item.IsManualMatch;
        };
        return menu;
    }

    private void OpenItem(LibraryItem item) => _openFolder?.Invoke(item.FolderPath);

    /// <summary>Opens the big details window for an item (one per item; clicking again just brings it forward).</summary>
    private void ShowDetails(LibraryItem item)
    {
        if (_detailWindows.TryGetValue(item, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new DetailsWindow(item, _meta ?? new MetadataService(_tmdb), _omdb, _mdbList, _openViewer, _openFolder, FixMatchAsync,
            () => _items, ShowDetails, _settings!);
        _detailWindows[item] = window;
        window.Closed += (_, _) => _detailWindows.Remove(item);
        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }

    private readonly Dictionary<LibraryItem, DetailsWindow> _detailWindows = new();

    // ---- Carousel ------------------------------------------------------------------------------------------------

    private void UpdateCarousel()
    {
        var layer = this.FindControl<Panel>("CarouselTiles")!;
        if (_view.Count == 0)
        {
            layer.Children.Clear();
            _carTiles.Clear();
            UpdateDetails();
            return;
        }

        _carIndex = Math.Clamp(_carIndex, 0, _view.Count - 1);
        var lo = Math.Max(0, _carIndex - 4);
        var hi = Math.Min(_view.Count - 1, _carIndex + 4);

        // Keep only the posters near the selection; the rest are rebuilt on demand.
        foreach (var k in _carTiles.Keys.Where(k => k < lo || k > hi).ToList())
        {
            layer.Children.Remove(_carTiles[k]);
            if (k < _view.Count)
                _tiles.Remove(_view[k]);
            _carTiles.Remove(k);
        }

        for (var i = lo; i <= hi; i++)
        {
            if (_carTiles.ContainsKey(i))
                continue;
            var tile = BuildCarouselTile(i);
            // Positioned before it is added, so a poster entering the window appears in place instead of sliding in from the middle.
            PositionCarouselTile(tile, i - _carIndex);
            _carTiles[i] = tile;
            layer.Children.Add(tile);
        }

        foreach (var (i, tile) in _carTiles)
            PositionCarouselTile(tile, i - _carIndex);

        UpdateDetails();
    }

    private Border BuildCarouselTile(int index)
    {
        var item = _view[index];
        var (frame, image, initials) = BuildArt(item, CarouselWidth, CarouselHeight, 64, 14);
        frame.HorizontalAlignment = HorizontalAlignment.Center;
        frame.VerticalAlignment = VerticalAlignment.Center;
        frame.Cursor = new Cursor(StandardCursorType.Hand);
        frame.ContextMenu = BuildMenu(item);
        frame.Transitions = new Transitions
        {
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(300), Easing = new CubicEaseOut() },
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(300) },
        };
        ToolTip.SetTip(frame, TooltipText(item));

        frame.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left)
                return;
            // Clicking a neighbour brings it to the middle; clicking the middle one opens its details.
            var current = _view.IndexOf(item);
            if (current == _carIndex)
                ShowDetails(item);
            else
                GoTo(current);
        };

        var refs = new TileRefs { Art = image, Initials = initials, Card = frame };
        _tiles[item] = refs;
        if (item.DisplayArt is { } art)
            LoadArt(refs, art);
        return frame;
    }

    private static void PositionCarouselTile(Border tile, int distance)
    {
        var a = Math.Min(Math.Abs(distance), CarOffsets.Length - 1);
        var x = Math.Sign(distance) * CarOffsets[a];
        var inv = CultureInfo.InvariantCulture;
        tile.RenderTransform = TransformOperations.Parse($"translate({x.ToString(inv)}px, 0px) scale({CarScales[a].ToString(inv)})");
        tile.Opacity = CarOpacity[a];
        tile.ZIndex = 10 - a;
        tile.BoxShadow = distance == 0
            ? BoxShadows.Parse("0 0 32 4 #6619F58C, 0 0 60 0 #3300C8FF")
            : default;
    }

    private void UpdateDetails()
    {
        var card = this.FindControl<Border>("DetailsCard")!;
        if (CurrentItem() is not { } item)
        {
            card.IsVisible = false;
            return;
        }

        card.IsVisible = true;
        this.FindControl<TextBlock>("DetTitle")!.Text = item.DisplayTitle;
        this.FindControl<TextBlock>("DetMeta")!.Text = DetailText(item);
        this.FindControl<TextBlock>("DetOverview")!.Text =
            item.Match?.Overview is { Length: > 0 } overview
                ? overview
                : _meta?.CanMatch(item) != true ? "Add a TMDB key in Settings to see descriptions and posters here." : "No description yet.";
        this.FindControl<TextBlock>("DetName")!.Text = item.Name + (item.IsManualMatch ? "   ·   matched by hand" : "");
        this.FindControl<TextBlock>("DetCounter")!.Text = $"{_carIndex + 1} / {_view.Count}";
        this.FindControl<Button>("DetFixButton")!.IsEnabled = _meta?.CanMatch(item) == true;

        // Offer Play / Pictures only for what the viewer can actually open; the folder is checked once, in the background.
        this.FindControl<Button>("DetPlayButton")!.IsVisible = item.HasVideo == true;
        this.FindControl<Button>("DetPicturesButton")!.IsVisible = item.HasPictures == true;
        if (item.HasVideo is null && _probing.Add(item))
            _ = ProbeAsync(item);
    }

    private readonly HashSet<LibraryItem> _probing = new();

    private async Task ProbeAsync(LibraryItem item)
    {
        var (video, pictures) = await Task.Run(() => MediaFiles.Probe(item.Path, item.IsDirectory));
        item.HasVideo = video;
        item.HasPictures = pictures;
        _probing.Remove(item);
        if (CurrentItem() is { } current && ReferenceEquals(current, item))
            UpdateDetails();
    }

    // ---- Artwork and text helpers --------------------------------------------------------------------------------

    private void LoadArt(TileRefs tile, string path)
    {
        tile.ShownArt = path;
        _ = Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                // Decode small: a few hundred tiles at full poster resolution would eat memory for no visible gain.
                var bitmap = Bitmap.DecodeToWidth(stream, 320);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_tiles.ContainsValue(tile) && tile.ShownArt == path)
                    {
                        // A wide picture (a game's store banner when there's no tall cover) is shown whole on the
                        // neon background instead of being cropped to a blurry sliver.
                        tile.Art.Stretch = bitmap.PixelSize.Width > bitmap.PixelSize.Height ? Stretch.Uniform : Stretch.UniformToFill;
                        tile.Art.Source = bitmap;
                        tile.Initials.IsVisible = false;
                        _bitmaps.Add(bitmap);
                    }
                    else
                    {
                        bitmap.Dispose();
                    }
                });
            }
            catch
            {
                /* unreadable image: keep the generated art */
            }
        });
    }

    private static string TooltipText(LibraryItem item)
    {
        var overview = item.Match?.Overview ?? "";
        if (overview.Length > 260)
            overview = overview[..260].TrimEnd() + "…";
        return overview.Length > 0 ? $"{overview}\n\n{item.Name}" : item.Name;
    }

    private static string DetailText(LibraryItem item)
    {
        var parts = new List<string>();
        if (item.DisplayYear is { } y)
            parts.Add(y.ToString());
        if (item.Extra.Length > 0)
            parts.Add(item.Extra);
        parts.Add(item.Size is { } s ? StatsStore.FormatBytes(s) : item.IsDirectory ? "…" : "");
        return string.Join("  ·  ", parts.Where(p => p.Length > 0));
    }

    private static string Initials(string title)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.FirstOrDefault(char.IsLetterOrDigit))
            .Where(c => c != default)
            .Take(2)
            .ToArray();

        // One-word titles ("Whiplash") get their first two letters so the tile isn't just a lone letter.
        if (words.Length == 1)
        {
            var letters = title.Where(char.IsLetterOrDigit).Take(2).ToArray();
            if (letters.Length > 0)
                return new string(letters).ToUpperInvariant();
        }

        return words.Length == 0 ? "?" : new string(words).ToUpperInvariant();
    }

    /// <summary>string.GetHashCode is randomized per process; art colors should stay the same between runs.</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in s)
                h = h * 31 + c;
            return h;
        }
    }
}
