using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PaxMediaManager.Services;

namespace PaxMediaManager.Views;

/// <summary>
/// The "blow out" window for one library item: big poster and backdrop, description, ratings from every source,
/// cast (clickable), trailer, the episodes on disk (watched / next up / missing), subtitles, and the technical specs of
/// the video. Everything fetched is cached on disk, so reopening a title makes no requests.
/// </summary>
public partial class DetailsWindow : Window
{
    private readonly LibraryItem _item;
    private readonly MetadataService _meta;
    private readonly OmdbClient? _omdb;
    private readonly MdbListClient? _mdbList;
    private readonly Action<string, bool>? _openViewer;
    private readonly Action<string>? _openFolder;
    private readonly Func<LibraryItem, Task>? _fixMatch;
    private readonly Func<IReadOnlyList<LibraryItem>> _library;
    private readonly Action<LibraryItem>? _showDetails;
    private readonly AppSettings? _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Bitmap> _bitmaps = new();
    private TmdbDetails? _details;
    private OmdbRatings? _ratings;
    private List<RatingScore> _scores = new();
    private MediaSpecs? _specs;
    private bool _specsDone;
    private string? _posterShown;
    private List<EpisodeFile>? _episodes;
    private readonly Dictionary<int, List<TmdbEpisode>> _seasons = new();
    private bool _watchRefreshQueued;

    // Needed by the XAML loader / previewer.
    public DetailsWindow()
        : this(new LibraryItem { Name = "", Title = "", Path = "", IsDirectory = false, Modified = DateTime.Now }, new MetadataService(null),
            null, null, null, null, null, () => Array.Empty<LibraryItem>(), null, null)
    {
    }

    public DetailsWindow(LibraryItem item, MetadataService meta, OmdbClient? omdb, MdbListClient? mdbList, Action<string, bool>? openViewer,
        Action<string>? openFolder, Func<LibraryItem, Task>? fixMatch, Func<IReadOnlyList<LibraryItem>> library,
        Action<LibraryItem>? showDetails, AppSettings? settings)
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        _item = item;
        _meta = meta;
        _omdb = omdb;
        _mdbList = mdbList;
        _openViewer = openViewer;
        _openFolder = openFolder;
        _fixMatch = fixMatch;
        _library = library;
        _showDetails = showDetails;
        _settings = settings;
        _details = item.Details;
        _scores = item.Scores.ToList();

        Button("CloseButton").Click += (_, _) => Close();
        Button("PlayButton").Click += (_, _) => Play(PlayTarget());
        Button("NextUpButton").Click += (_, _) => Play(PlayTarget());
        Button("PicturesButton").Click += (_, _) => _openViewer?.Invoke(_item.Path, true);
        Button("FolderButton").Click += (_, _) => _openFolder?.Invoke(_item.FolderPath);
        Button("TrailerButton").Click += async (_, _) =>
        {
            if (_details?.TrailerKey is { Length: > 0 } key)
                await Launcher.LaunchUriAsync(new Uri("https://www.youtube.com/watch?v=" + key));
        };
        Button("SubtitlesButton").Click += (_, _) =>
        {
            if (_specs?.FilePath is { } file)
                OpenSubtitles(file, null, null);
        };
        Button("WatchedButton").Click += (_, _) => ToggleWatched();
        Button("FixButton").Click += async (_, _) =>
        {
            if (_fixMatch is null)
                return;
            await _fixMatch(_item);
            _details = null;
            _ratings = null;
            _scores = new();
            _seasons.Clear();
            _posterShown = null;
            Refresh();
            _ = LoadDetailsAsync();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
        WatchHistory.Changed += OnWatchChanged;
        Closed += (_, _) =>
        {
            WatchHistory.Changed -= OnWatchChanged;
            _cts.Cancel();
            foreach (var b in _bitmaps)
                b.Dispose();
        };

        Button("PlayButton").IsVisible = item.HasVideo == true;
        Button("PicturesButton").IsVisible = item.HasPictures == true;
        Button("FixButton").IsVisible = meta.CanMatch(item) && fixMatch is not null;

        Refresh();
        Opened += async (_, _) =>
        {
            _ = LoadDetailsAsync();
            _ = LoadSpecsAsync();
            if (IsShow)
                _ = LoadEpisodesAsync();
            if (_item.HasVideo is null)
            {
                // Which viewer buttons apply is only known once the folder has been looked at (off the UI thread: it may be a NAS).
                var (video, pictures) = await Task.Run(() => Viewer.MediaFiles.Probe(_item.Path, _item.IsDirectory));
                _item.HasVideo = video;
                _item.HasPictures = pictures;
                Button("PicturesButton").IsVisible = pictures;
                UpdateWatchUi();
            }
        };
    }

    private Button Button(string name) => this.FindControl<Button>(name)!;
    private TextBlock Text(string name) => this.FindControl<TextBlock>(name)!;
    private Avalonia.Platform.Storage.ILauncher Launcher => TopLevel.GetTopLevel(this)!.Launcher;

    private LibraryKind Kind => _item.Match?.Kind ?? _item.Kind;
    private bool IsShow => Kind == LibraryKind.Tv || _item.IsGroup;
    private bool IsWatchable => Kind is not (LibraryKind.Game or LibraryKind.None or LibraryKind.Audiobook) && _item.HasVideo != false;

    // ---- Filling in ----------------------------------------------------------------------------------------------

    /// <summary>Fills in everything known right now; called again as details, ratings, specs and episodes arrive.</summary>
    private void Refresh()
    {
        var item = _item;
        var d = _details;
        Title = item.DisplayTitle + " — details";
        Text("TitleText").Text = item.DisplayTitle;
        Text("PosterInitials").Text = string.Concat(item.DisplayTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(2).Select(w => char.ToUpperInvariant(w[0])));

        var meta = new List<string>();
        if (item.DisplayYear is { } y)
            meta.Add(y.ToString());
        meta.Add(Kind switch
        {
            LibraryKind.Game => "Game",
            LibraryKind.Audiobook => "Audiobook",
            LibraryKind.Tv => item.IsAnime ? "Anime series" : "TV series",
            LibraryKind.Movie => item.IsAnime ? "Anime film" : "Movie",
            _ => "",
        });
        if (d is not null)
        {
            if (d.Certification.Length > 0)
                meta.Add(d.Certification);
            if (d.RuntimeMinutes is { } rt)
                meta.Add(Kind == LibraryKind.Tv ? $"{rt} min / episode" : FormatMinutes(rt));
            if (d.Seasons is { } s)
                meta.Add($"{s} season{(s == 1 ? "" : "s")}" + (d.Episodes is { } e ? $", {e} episodes" : ""));
            if (d.Genres.Length > 0)
                meta.Add(string.Join(", ", d.Genres.Take(5)));
            if (d.Status.Length > 0 && Kind == LibraryKind.Tv)
                meta.Add(d.Status);
        }

        if (item.Extra.Length > 0 && !item.IsGroup)
            meta.Add(item.Extra);
        Text("MetaText").Text = string.Join("   ·   ", meta.Where(m => m.Length > 0));

        // For games the description sits in Tagline (Steam's short description); for books it is the series line.
        var tagline = Text("TaglineText");
        tagline.Text = Kind == LibraryKind.Game ? "" : d?.Tagline ?? "";
        tagline.IsVisible = !string.IsNullOrWhiteSpace(tagline.Text);

        var overview = item.Match?.Overview is { Length: > 0 } o ? o : Kind == LibraryKind.Game ? d?.Tagline ?? "" : "";
        Text("OverviewText").Text = overview.Length > 0
            ? overview
            : Kind == LibraryKind.None
                ? ""
                : !_meta.CanMatch(item)
                    ? "Add a TMDB key in Settings to see the description, rating and cast here."
                    : item.Match is null ? "No match found for this title yet. Use Fix match… to pick it by hand." : "No description found.";

        BuildRatings();
        BuildCrew();

        Button("TrailerButton").IsVisible = d?.TrailerKey is { Length: > 0 };
        Button("SubtitlesButton").IsVisible = _settings?.OpenSubtitlesKey is { Length: > 0 } && !IsShow && IsWatchable && _specs?.FilePath is not null;
        Text("PathText").Text = item.Path + (item.IsManualMatch ? "   ·   matched by hand" : "")
                                + (item.Category.Length > 0 ? "   ·   " + item.Category : "");
        Text("SpecsText").Text = BuildSpecsText();
        UpdateWatchUi();
        ShowPoster();
    }

    private void BuildCrew()
    {
        var d = _details;
        var lines = new List<string>();
        var castPanel = this.FindControl<WrapPanel>("CastPanel")!;
        castPanel.Children.Clear();
        var showCastButtons = false;
        if (d is not null)
        {
            switch (Kind)
            {
                case LibraryKind.Game:
                    Text("CrewHeader").Text = "DEVELOPER & PUBLISHER";
                    if (d.Directors.Length > 0)
                        lines.Add("Developed by " + string.Join(", ", d.Directors));
                    if (d.Studios.Length > 0)
                        lines.Add("Published by " + string.Join(", ", d.Studios));
                    if (d.ReleaseDate.Length > 0)
                        lines.Add("Released " + d.ReleaseDate);
                    if (d.Status.Length > 0)
                        lines.Add("Steam reviews: " + d.Status);
                    break;
                case LibraryKind.Audiobook:
                    Text("CrewHeader").Text = "AUTHOR & NARRATOR";
                    if (d.Directors.Length > 0)
                        lines.Add("Written by " + string.Join(", ", d.Directors));
                    if (d.Cast.Length > 0)
                        lines.Add("Narrated by " + string.Join(", ", d.Cast));
                    if (d.Studios.Length > 0)
                        lines.Add("Publisher: " + string.Join(", ", d.Studios));
                    if (d.ReleaseDate.Length > 0)
                        lines.Add("Released " + d.ReleaseDate);
                    break;
                default:
                    Text("CrewHeader").Text = "CAST & CREW";
                    var tv = Kind == LibraryKind.Tv;
                    if (d.Directors.Length > 0)
                        lines.Add((tv ? "Created by " : "Directed by ") + string.Join(", ", d.Directors));
                    if (d.Studios.Length > 0)
                        lines.Add((tv ? "Networks: " : "Studios: ") + string.Join(", ", d.Studios.Take(5)));
                    if (d.ReleaseDate.Length > 0)
                        lines.Add((tv ? "First aired " : "Released ") + d.ReleaseDate);

                    // Cast as buttons: click one to see what else of theirs is in the library.
                    var ids = d.CastIds ?? Array.Empty<int>();
                    for (var i = 0; i < d.Cast.Length; i++)
                    {
                        var id = i < ids.Length ? ids[i] : 0;
                        var label = d.Cast[i];
                        if (id <= 0 || _meta.Tmdb is null)
                        {
                            castPanel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 4, 8, 0) });
                            continue;
                        }

                        showCastButtons = true;
                        var name = label.Split(" as ")[0];
                        var b = new Button { Content = label, Padding = new Thickness(10, 3), FontSize = 12 };
                        ToolTip.SetTip(b, $"Other titles with {name}");
                        b.Click += (_, _) => new PersonWindow(id, name, _meta, _library(), _showDetails).Show(this);
                        castPanel.Children.Add(b);
                    }

                    break;
            }
        }

        if (_ratings is { } r)
        {
            if (r.Awards.Length > 0)
                lines.Add("Awards: " + r.Awards);
            if (r.BoxOffice.Length > 0)
                lines.Add("Box office: " + r.BoxOffice);
        }

        Text("CastLabel").IsVisible = showCastButtons;
        Text("CrewText").Text = string.Join("\n", lines);
        Text("CrewText").IsVisible = lines.Count > 0;
        this.FindControl<Border>("CrewCard")!.IsVisible = lines.Count > 0 || castPanel.Children.Count > 0;
    }

    // ---- Ratings -------------------------------------------------------------------------------------------------

    /// <summary>
    /// One badge per site that has a score. Sources overlap (MDBList and OMDb both know IMDb, for instance), so each
    /// site appears once: the title's own source first, then MDBList, then OMDb, TVmaze and AniList.
    /// </summary>
    private void BuildRatings()
    {
        var panel = this.FindControl<WrapPanel>("RatingsPanel")!;
        panel.Children.Clear();

        var all = new Dictionary<string, RatingScore>();
        if (_details is { } d)
        {
            if (Kind is LibraryKind.Movie or LibraryKind.Tv && d.Votes > 0)
                all["tmdb"] = new RatingScore("tmdb", d.Rating, d.Votes);
            foreach (var s in d.Scores ?? Array.Empty<RatingScore>())
                all.TryAdd(s.Source, s);
        }

        foreach (var s in _scores)
            all.TryAdd(s.Source, s);
        if (_ratings is { } r)
        {
            if (double.TryParse(r.Imdb, NumberStyles.Float, CultureInfo.InvariantCulture, out var imdb))
                all.TryAdd("imdb", new RatingScore("imdb", imdb, int.TryParse(r.ImdbVotes.Replace(",", ""), out var v) ? v : null));
            if (double.TryParse(r.RottenTomatoes.TrimEnd('%'), out var rt))
                all.TryAdd("tomatoes", new RatingScore("tomatoes", rt, null));
            if (double.TryParse(r.Metacritic, out var mc))
                all.TryAdd("metacritic", new RatingScore("metacritic", mc, null));
        }

        foreach (var source in DisplayOrder.Concat(all.Keys.Except(DisplayOrder).Order()))
            if (all.TryGetValue(source, out var score) && Describe(score) is { } badge)
                panel.Children.Add(Badge(badge.Value, badge.Color, badge.Name, badge.Note));
    }

    private static readonly string[] DisplayOrder =
    {
        "imdb", "tmdb", "steam", "audible", "tomatoes", "popcorn", "metacritic", "metacriticuser", "letterboxd", "trakt", "rogerebert",
        "anilist", "myanimelist", "tvmaze", "mdblist",
    };

    /// <summary>How each site's score reads: its own scale, its colours, and a short note (votes or the scale).</summary>
    private static (string Value, string Color, string Name, string Note)? Describe(RatingScore s)
    {
        var inv = CultureInfo.InvariantCulture;
        string Votes(string fallback) => s.Votes is { } n ? CompactVotes(n) + " votes" : fallback;
        string MetaColor(double outOf100) => outOf100 >= 61 ? "#66CC33" : outOf100 >= 40 ? "#FFCC33" : "#FF3B3B";

        return s.Source switch
        {
            "imdb" => (s.Value.ToString("0.0", inv), "#F5C518", "IMDb", Votes("out of 10")),
            "tmdb" => (s.Value > 10 ? (s.Value / 10).ToString("0.0", inv) : s.Value.ToString("0.0", inv), "#01B4E4", "TMDB", Votes("out of 10")),
            "steam" => ($"{s.Value:0}%", s.Value >= 70 ? "#66C0F4" : s.Value >= 40 ? "#B9A074" : "#A34C25", "Steam", s.Votes is { } sv ? CompactVotes(sv) + " reviews" : "positive"),
            "audible" => (s.Value.ToString("0.0", inv), "#F7991C", "Audible", s.Votes is { } av ? CompactVotes(av) + " ratings" : "out of 5"),
            "tomatoes" => ($"{s.Value:0}%", s.Value >= 60 ? "#FA6A4B" : "#8DC63F", "Tomatometer", s.Value >= 60 ? "Fresh (critics)" : "Rotten (critics)"),
            "popcorn" => ($"{s.Value:0}%", s.Value >= 60 ? "#FA6A4B" : "#8DC63F", "RT Audience", Votes("Popcornmeter")),
            "metacritic" => ($"{s.Value:0}", MetaColor(s.Value), "Metascore", "critics, of 100"),
            "metacriticuser" => (s.Value.ToString("0.0", inv), MetaColor(s.Value * 10), "Metacritic", Votes("users, of 10")),
            "letterboxd" => (s.Value.ToString("0.0", inv), "#00E054", "Letterboxd", Votes("out of 5")),
            "trakt" => ($"{s.Value:0}%", "#ED1C24", "Trakt", Votes("liked it")),
            "rogerebert" => (s.Value.ToString("0.#", inv), "#E8423F", "Roger Ebert", "stars, of 4"),
            "anilist" => ($"{s.Value:0}%", "#02A9FF", "AniList", s.Votes is { } pv ? CompactVotes(pv) + " members" : "community"),
            "myanimelist" => (s.Value.ToString("0.00", inv), "#2E51A2", "MyAnimeList", Votes("out of 10")),
            "tvmaze" => (s.Value.ToString("0.0", inv), "#3C948B", "TVmaze", "out of 10"),
            "mdblist" => ($"{s.Value:0}", "#4284CA", "MDBList", "average, of 100"),
            _ => (s.Value.ToString("0.#", inv), "#C0C6D4", char.ToUpperInvariant(s.Source[0]) + s.Source[1..], Votes("")),
        };
    }

    private static string CompactVotes(int n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 10_000 ? $"{n / 1000}K" : n.ToString("N0");

    /// <summary>A small tile: big coloured score, site name, note. Two fit side by side under the poster.</summary>
    private static Border Badge(string value, string color, string source, string note)
    {
        var stack = new StackPanel { Spacing = 1 };
        stack.Children.Add(new TextBlock
        {
            Text = value, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(color)),
        });
        stack.Children.Add(new TextBlock { Text = source, FontWeight = FontWeight.SemiBold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        if (note.Length > 0)
            stack.Children.Add(new TextBlock { Text = note, Classes = { "muted" }, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis });
        return new Border { Classes = { "card" }, Width = 114, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 8), Child = stack };
    }

    // ---- Watching: next up, resume, watched ---------------------------------------------------------------------------

    private void OnWatchChanged()
    {
        if (_watchRefreshQueued)
            return;
        _watchRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _watchRefreshQueued = false;
            UpdateWatchUi();
            if (_episodes is not null)
                BuildEpisodeRows();
        }, DispatcherPriority.Background);
    }

    /// <summary>The file Play starts: the show's next episode, else the film's main file, else the item itself.</summary>
    private string PlayTarget()
    {
        if (_episodes is { Count: > 0 } eps)
            return WatchHistory.NextUp(eps)?.Episode.Path ?? eps[0].Path;
        return _specs?.FilePath ?? _item.Path;
    }

    private void Play(string path) => _openViewer?.Invoke(path, false);

    private void UpdateWatchUi()
    {
        var card = this.FindControl<Border>("NextUpCard")!;
        var watchedButton = Button("WatchedButton");
        var play = Button("PlayButton");
        play.IsVisible = _item.HasVideo == true && _openViewer is not null;
        if (!IsWatchable || _openViewer is null)
        {
            card.IsVisible = false;
            watchedButton.IsVisible = false;
            return;
        }

        var (fraction, last, finished) = _episodes is { Count: > 1 } e
            ? WatchHistory.Progress(_item, e.Count)
            : WatchHistory.Progress(_item);
        watchedButton.IsVisible = _episodes is { Count: > 0 } || _specs?.FilePath is not null;
        watchedButton.Content = finished ? "Mark unwatched" : "Mark watched";

        var track = this.FindControl<Border>("NextUpTrack")!;
        var fill = this.FindControl<Border>("NextUpFill")!;
        if (_episodes is { Count: > 0 } episodes)
        {
            var next = WatchHistory.NextUp(episodes);
            if (next is null)
            {
                card.IsVisible = true;
                Text("NextUpText").Text = "✓ You've watched every episode on disk.";
                track.IsVisible = false;
                Button("NextUpButton").Content = "▶  From the start";
                play.Content = "▶  Play";
                return;
            }

            var (ep, resume) = next.Value;
            var name = EpisodeName(ep.Season, ep.Episode);
            var entry = WatchHistory.Get(ep.Path);
            card.IsVisible = true;
            var verb = resume ? "Continue" : last is null ? "Start with" : "Next up:";
            Text("NextUpText").Text = $"{verb} S{ep.Season:00}E{ep.Episode:00}{(name.Length > 0 ? " · " + name : "")}"
                                      + (resume && entry is { DurationMs: > 0 } ? $"  —  {Remaining(entry)} left" : "");
            track.IsVisible = resume && entry is not null;
            fill.Width = 300 * (entry?.Fraction ?? 0);
            Button("NextUpButton").Content = resume ? "▶  Resume" : "▶  Play";
            play.Content = resume ? "▶  Resume" : $"▶  Play S{ep.Season:00}E{ep.Episode:00}";
            return;
        }

        // A film (or a single video): resume point if partly watched.
        var entries = WatchHistory.ForItem(_item);
        var partial = entries.Where(x => !x.Completed && x.PositionMs > 30_000).OrderByDescending(x => x.LastWatched).FirstOrDefault();
        if (partial is not null)
        {
            card.IsVisible = true;
            Text("NextUpText").Text = $"Continue watching  —  {Remaining(partial)} left";
            track.IsVisible = true;
            fill.Width = 300 * partial.Fraction;
            Button("NextUpButton").Content = "▶  Resume";
            play.Content = "▶  Resume";
        }
        else
        {
            card.IsVisible = false;
            play.Content = finished ? "▶  Watch again" : "▶  Play";
        }
    }

    private static string Remaining(WatchEntry e)
    {
        var left = TimeSpan.FromMilliseconds(Math.Max(0, e.DurationMs - e.PositionMs));
        return left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes:00}m" : $"{Math.Max(1, (int)left.TotalMinutes)} min";
    }

    private void ToggleWatched()
    {
        var files = _episodes is { Count: > 0 } e ? e.Select(x => x.Path).ToList()
            : _specs?.FilePath is { } f ? new List<string> { f } : new List<string>();
        if (files.Count == 0)
            return;
        var finished = _episodes is { Count: > 1 } ? WatchHistory.Progress(_item, _episodes.Count).Finished : WatchHistory.Progress(_item).Finished;
        WatchHistory.MarkWatched(files, !finished);
    }

    // ---- Episodes ------------------------------------------------------------------------------------------------

    private string EpisodeName(int season, int episode) =>
        _seasons.TryGetValue(season, out var list) ? list.FirstOrDefault(x => x.Episode == episode)?.Name ?? "" : "";

    /// <summary>
    /// Reads the show's episode files (season folders included), then asks TMDB for each season's episode list so
    /// episode names and the gaps ("missing S02E04") can be shown.
    /// </summary>
    private async Task LoadEpisodesAsync()
    {
        try
        {
            _episodes = await Task.Run(() => WatchHistory.EpisodeFiles(_item), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        BuildEpisodeRows();
        UpdateWatchUi();
        Refresh();

        // Season lists need the details (season count, ended or not) and a TMDB key.
        for (var tries = 0; _details is null && tries < 40 && !_cts.IsCancellationRequested; tries++)
            await Task.Delay(250);
        if (_meta.Tmdb is not { } tmdb || _item.Match is not { Kind: LibraryKind.Tv } match || _details?.Seasons is not { } seasonCount)
            return;

        var ended = _details.Status is "Ended" or "Canceled";
        var onDisk = _episodes.Select(e => e.Season).ToHashSet();
        for (var s = 1; s <= Math.Min(seasonCount, 40); s++)
        {
            try
            {
                if (await tmdb.GetSeasonAsync(match.Id, s, ended, _cts.Token) is { } list)
                    _seasons[s] = list;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Draw as soon as the seasons that are on disk are known; the rest only adds "not on disk" lines.
            if (onDisk.Contains(s) || s == seasonCount)
                BuildEpisodeRows();
        }

        UpdateWatchUi();
    }

    private void BuildEpisodeRows()
    {
        var card = this.FindControl<Border>("EpisodesCard")!;
        var list = this.FindControl<StackPanel>("EpisodesList")!;
        list.Children.Clear();
        var episodes = _episodes ?? new List<EpisodeFile>();
        if (episodes.Count == 0)
        {
            card.IsVisible = _item.IsGroup;
            if (_item.IsGroup)
            {
                Text("EpisodesHeader").Text = "ON DISK";
                Text("EpisodesSummary").Text = _item.Extra;
                foreach (var m in _item.Members)
                    list.Children.Add(EpisodeRow(m.Extra.Length > 0 ? m.Extra : "Folder", m.Name, m.Size, m.Path, null, null, missing: false));
            }

            return;
        }

        card.IsVisible = true;
        var today = DateTime.Today;
        bool Aired(TmdbEpisode e) => DateTime.TryParse(e.AirDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d <= today;
        var byKey = episodes.ToDictionary(e => (e.Season, e.Episode));
        var seasons = episodes.Select(e => e.Season).Concat(_seasons.Keys).Distinct().Order().ToList();
        var missing = new List<string>();
        var aired = 0;
        var watched = episodes.Count(e => WatchHistory.Get(e.Path) is { Completed: true });

        foreach (var s in seasons)
        {
            var known = _seasons.TryGetValue(s, out var tmdbList) ? tmdbList : null;
            var have = episodes.Where(e => e.Season == s).ToList();
            var airedHere = known?.Count(Aired) ?? have.Count;
            aired += airedHere;

            // A whole season that isn't on disk: one line instead of a row per episode.
            if (have.Count == 0)
            {
                if (airedHere > 0)
                {
                    missing.Add($"all of season {s}");
                    list.Children.Add(new TextBlock
                    {
                        Text = $"Season {s}  —  not on disk ({airedHere} episode{(airedHere == 1 ? "" : "s")})",
                        Foreground = Ui.Muted,
                        Margin = new Thickness(0, 10, 0, 2),
                    });
                }

                continue;
            }

            var seasonWatched = have.Count(e => WatchHistory.Get(e.Path) is { Completed: true });
            list.Children.Add(new TextBlock
            {
                Text = $"Season {s}   ·   {have.Count}{(known is not null ? $" of {airedHere}" : "")} on disk   ·   {seasonWatched} watched",
                FontWeight = FontWeight.Bold,
                Foreground = Ui.Accent,
                Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 12, 0, 4),
            });

            var numbers = (known?.Where(e => Aired(e) || byKey.ContainsKey((s, e.Episode))).Select(e => e.Episode) ?? Enumerable.Empty<int>())
                .Concat(have.Select(e => e.Episode)).Distinct().Order();
            foreach (var n in numbers)
            {
                var name = known?.FirstOrDefault(e => e.Episode == n)?.Name ?? "";
                if (byKey.TryGetValue((s, n), out var file))
                {
                    list.Children.Add(EpisodeRow($"E{n:00}", name, file.Bytes, file.Path, s, n, missing: false));
                }
                else
                {
                    missing.Add($"S{s:00}E{n:00}");
                    list.Children.Add(EpisodeRow($"E{n:00}", name, null, null, s, n, missing: true));
                }
            }

            // Announced but not yet aired.
            foreach (var up in known?.Where(e => !Aired(e) && !byKey.ContainsKey((s, e.Episode))) ?? Enumerable.Empty<TmdbEpisode>())
                list.Children.Add(new TextBlock
                {
                    Text = $"E{up.Episode:00}  {up.Name}  —  upcoming{(up.AirDate.Length > 0 ? " " + up.AirDate : "")}",
                    Foreground = Ui.Muted,
                    FontSize = 11.5,
                    Margin = new Thickness(26, 1, 0, 1),
                });
        }

        Text("EpisodesHeader").Text = "EPISODES";
        var summary = $"{episodes.Count} episode{(episodes.Count == 1 ? "" : "s")} on disk"
                      + (_seasons.Count > 0 ? $" of {aired} aired" : "")
                      + $"   ·   {watched} watched";
        if (missing.Count > 0)
            summary += $"\nMissing: {string.Join(", ", missing.Take(14))}{(missing.Count > 14 ? $" and {missing.Count - 14} more" : "")}";
        else if (_seasons.Count > 0)
            summary += "\n✓ Nothing missing — every aired episode is here.";
        Text("EpisodesSummary").Text = summary;
    }

    private Grid EpisodeRow(string number, string name, long? bytes, string? path, int? season, int? episode, bool missing)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,48,*,Auto,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 1) };
        var entry = path is null ? null : WatchHistory.Get(path);
        var status = new TextBlock
        {
            Text = missing ? "–" : entry is { Completed: true } ? "✓" : entry is { PositionMs: > 0 } ? "◐" : "•",
            Foreground = entry is { Completed: true } ? Ui.Accent : Ui.Muted,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (entry is { Completed: false, PositionMs: > 0 })
            ToolTip.SetTip(status, $"{entry.Fraction:P0} watched");
        row.Children.Add(status);

        // Colours are only set when they differ from the default: a null Foreground would make the text invisible.
        var num = new TextBlock { Text = number, VerticalAlignment = VerticalAlignment.Center };
        if (missing)
            num.Foreground = Ui.Muted;
        Grid.SetColumn(num, 1);
        row.Children.Add(num);

        var title = new TextBlock
        {
            Text = missing ? $"{(name.Length > 0 ? name + "  —  " : "")}missing" : name.Length > 0 ? name : System.IO.Path.GetFileName(path ?? ""),
            FontSize = name.Length > 0 || missing ? 13 : 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (missing)
            title.Foreground = new SolidColorBrush(Color.Parse("#F2666A"));
        else if (name.Length == 0)
            title.Foreground = Ui.Muted;
        if (path is not null)
            ToolTip.SetTip(title, path);
        Grid.SetColumn(title, 2);
        row.Children.Add(title);

        var size = new TextBlock { Text = bytes is > 0 ? StatsStore.FormatBytes(bytes.Value) : "", Foreground = Ui.Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(size, 3);
        row.Children.Add(size);

        if (path is not null)
        {
            if (_settings?.OpenSubtitlesKey is { Length: > 0 } && Viewer.MediaFiles.IsVideo(path))
            {
                var cc = new Button { Content = "CC", Padding = new Thickness(8, 2), FontSize = 11 };
                ToolTip.SetTip(cc, "Find subtitles for this episode");
                cc.Click += (_, _) => OpenSubtitles(path, season, episode);
                Grid.SetColumn(cc, 4);
                row.Children.Add(cc);
            }

            var play = new Button { Content = entry is { Completed: false, PositionMs: > 0 } ? "▶ Resume" : "▶", Padding = new Thickness(10, 2), IsVisible = _openViewer is not null };
            play.Click += (_, _) => Play(path);
            Grid.SetColumn(play, 5);
            row.Children.Add(play);
        }

        return row;
    }

    private void OpenSubtitles(string videoPath, int? season, int? episode)
    {
        if (_settings?.OpenSubtitlesKey is not { Length: > 0 } key)
            return;
        new SubtitleWindow(new OpenSubtitlesClient(key), videoPath, _details?.ImdbId, season, episode, _settings.SubtitleLanguages).Show(this);
    }

    // ---- Specs, images, loading ---------------------------------------------------------------------------------------

    private string BuildSpecsText()
    {
        if (Kind is LibraryKind.Game or LibraryKind.Audiobook && _specs is null)
            return (_item.Size is { } total ? "Size:  " + StatsStore.FormatBytes(total) + "\n" : "") + "Folder:  " + _item.Path;
        if (_specs is null)
            return _specsDone ? "No video file found in this item." : "Reading file details…";

        var s = _specs;
        var lines = new List<string>
        {
            (_episodes is { Count: > 0 } ? "Sample file:  " : "File:  ") + System.IO.Path.GetFileName(s.FilePath)
            + (s.VideoFileCount > 1 && _episodes is not { Count: > 0 } ? $"   (largest of {s.VideoFileCount} video files)" : ""),
            "Size:  " + StatsStore.FormatBytes(s.FileBytes)
                + (_item.Size is { } whole && whole > s.FileBytes ? $"   (whole item {StatsStore.FormatBytes(whole)})" : ""),
        };
        if (s.Duration is { } dur)
            lines.Add("Length:  " + (dur.TotalHours >= 1 ? $"{(int)dur.TotalHours}h {dur.Minutes:00}m {dur.Seconds:00}s" : $"{dur.Minutes}m {dur.Seconds:00}s"));
        foreach (var v in s.Video)
            lines.Add("Video:  " + v);
        foreach (var a in s.Audio)
            lines.Add("Audio:  " + a);
        if (s.Subtitles.Count > 0)
            lines.Add("Subtitles:  " + string.Join(", ", s.Subtitles.Distinct()));
        return string.Join("\n", lines);
    }

    private void ShowPoster()
    {
        var path = _item.DisplayArt;
        if (path is null || path == _posterShown || !File.Exists(path))
            return;
        _posterShown = path;
        SetImage("PosterImage", path, 480, hideInitials: true);
    }

    private void SetImage(string imageName, string path, int width, bool hideInitials = false)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                var bitmap = Bitmap.DecodeToWidth(stream, width);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_cts.IsCancellationRequested)
                    {
                        bitmap.Dispose();
                        return;
                    }

                    _bitmaps.Add(bitmap);
                    var image = this.FindControl<Image>(imageName)!;
                    if (imageName == "PosterImage")
                        image.Stretch = bitmap.PixelSize.Width > bitmap.PixelSize.Height ? Stretch.Uniform : Stretch.UniformToFill;
                    image.Source = bitmap;
                    if (hideInitials)
                        Text("PosterInitials").IsVisible = false;
                });
            }
            catch
            {
                /* unreadable image: leave the placeholder */
            }
        });
    }

    private async Task LoadDetailsAsync()
    {
        var match = _item.Match;
        if (match is null)
            return;

        try
        {
            _details ??= await _meta.GetDetailsAsync(match, _cts.Token);
            if (_details is null)
                return;
            _item.Details = _details;
            Refresh();

            // Every extra source is asked in turn; each answer is cached on disk, so a second visit makes no requests.
            var imdbId = _details.ImdbId is { Length: > 0 } id ? id : null;
            if (match.Kind is LibraryKind.Movie or LibraryKind.Tv)
            {
                if (_mdbList is not null && await _mdbList.GetAsync(match, _cts.Token) is { } mdb)
                    Merge(mdb);
                if (_omdb is not null && imdbId is not null)
                {
                    _ratings = await _omdb.GetRatingsAsync(imdbId, _cts.Token);
                    if (_ratings is not null)
                        Refresh();
                }

                if (match.Kind == LibraryKind.Tv && await TvMazeClient.GetAsync(imdbId, _cts.Token) is { } tvmaze)
                    Merge(new[] { tvmaze });
                if ((_item.IsAnime || _details is { OriginalLanguage: "ja" } && _details.Genres.Contains("Animation"))
                    && await AniListClient.GetScoreAsync(_item.DisplayTitle, _cts.Token) is { } ani)
                    Merge(new[] { ani });
            }

            if (await _meta.GetBackdropFileAsync(_details.BackdropPath, _cts.Token) is { } file)
                SetImage("BackdropImage", file, 1000);
            // A freshly matched item may not have its poster downloaded yet.
            if (_item.TmdbArtPath is null && match.PosterPath is not null)
            {
                _item.TmdbArtPath = await _meta.GetPosterFileAsync(match, _cts.Token);
                ShowPoster();
            }
        }
        catch (OperationCanceledException)
        {
            /* window closed */
        }
    }

    private void Merge(IEnumerable<RatingScore> scores)
    {
        foreach (var s in scores)
        {
            _scores.RemoveAll(x => x.Source == s.Source);
            _scores.Add(s);
        }

        _item.Scores = _scores.ToList();
        Refresh();
    }

    private async Task LoadSpecsAsync()
    {
        if (Kind is LibraryKind.Game or LibraryKind.Audiobook or LibraryKind.None)
        {
            _specsDone = true;
            return;
        }

        try
        {
            _specs = _item.Specs ?? await MediaSpecs.ProbeAsync(_item, _cts.Token);
            _item.Specs ??= _specs;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            _specs = null;
        }

        _specsDone = true;
        Dispatcher.UIThread.Post(Refresh);
    }

    private static string FormatMinutes(int m) => m >= 60 ? $"{m / 60}h {m % 60:00}m" : $"{m} min";
}
