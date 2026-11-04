using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PaxMediaManager.Services;

namespace PaxMediaManager.Views;

/// <summary>
/// The Discover tab: rows of suggestions (films, shows, anime, games, audiobooks) worked out from what's in the
/// library and what's been watched. Click a card for details, right-click for "not interested".
/// </summary>
public sealed class DiscoverView : UserControl
{
    private static readonly string[] Groups = { "All", "Movies", "TV", "Anime", "Games", "Audiobooks" };

    private readonly StackPanel _sections = new() { Spacing = 22, Margin = new Thickness(0, 4, 14, 20) };
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _status = new() { Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _chips = new() { ItemSpacing = 6, LineSpacing = 6 };
    private readonly List<Bitmap> _bitmaps = new();
    private readonly SemaphoreSlim _posterGate = new(4);
    private AppSettings? _settings;
    private MetadataService? _meta;
    private List<DiscoverSection> _data = new();
    private string _group = "All";
    private DateTime _builtAt = DateTime.MinValue;
    private string _builtKey = "";
    private CancellationTokenSource? _cts;
    private bool _building;

    public DiscoverView()
    {
        foreach (var g in Groups)
        {
            var chip = new ToggleButton { Classes = { "chip" }, Content = g == "TV" ? "TV shows" : g, IsChecked = g == "All", Padding = new Thickness(14, 5) };
            var group = g;
            chip.Click += (_, _) =>
            {
                _group = group;
                foreach (var c in _chips.Children.OfType<ToggleButton>())
                    c.IsChecked = ReferenceEquals(c, chip);
                Render();
            };
            _chips.Children.Add(chip);
        }

        var refresh = new Button { Content = "Refresh", Padding = new Thickness(12, 5) };
        ToolTip.SetTip(refresh, "Work the suggestions out again (lookups stay cached, so this is quick)");
        refresh.Click += (_, _) => _ = BuildAsync(force: true);
        var reset = new Button { Content = "Show hidden again", Classes = { "ghost" }, Padding = new Thickness(12, 5) };
        ToolTip.SetTip(reset, "Bring back everything marked \"Not interested\"");
        reset.Click += (_, _) =>
        {
            DiscoverService.ClearDismissed();
            _ = BuildAsync(force: true);
        };

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        bar.Children.Add(_chips);
        Grid.SetColumn(reset, 1);
        bar.Children.Add(reset);
        Grid.SetColumn(refresh, 2);
        bar.Children.Add(refresh);

        _scroller = new ScrollViewer { Content = _sections };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(0, 14, 0, 0) };
        root.Children.Add(bar);
        _status.Margin = new Thickness(2, 10, 0, 8);
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);
        Grid.SetRow(_scroller, 2);
        root.Children.Add(_scroller);
        Content = root;
    }

    public void Initialize(AppSettings settings) => _settings = settings;

    /// <summary>Called when the tab is shown: builds the first time, and again after six hours or a key change.</summary>
    public void Refresh()
    {
        var key = (_settings?.TmdbKey ?? "") + "|" + string.Join(",", _settings?.ActiveCategories.Where(c => !c.ExcludeFromLibrary).Select(c => c.Path) ?? Array.Empty<string>());
        if (_building || (key == _builtKey && DateTime.Now - _builtAt < TimeSpan.FromHours(6)))
            return;
        _ = BuildAsync(force: false);
    }

    private async Task BuildAsync(bool force)
    {
        if (_settings is null || (_building && !force))
            return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _building = true;
        var tmdbKey = _settings.TmdbKey.Trim();
        _meta = new MetadataService(tmdbKey.Length > 0 ? new TmdbClient(tmdbKey) : null);
        _builtKey = tmdbKey + "|" + string.Join(",", _settings.ActiveCategories.Where(c => !c.ExcludeFromLibrary).Select(c => c.Path));
        if (_data.Count == 0)
            ShowMessage("Working out suggestions from your library… the first time takes a minute or two; after that it's quick.");

        var service = new DiscoverService(_settings, _meta);
        service.Status += s => Dispatcher.UIThread.Post(() =>
        {
            if (!cts.IsCancellationRequested)
                SetStatus(s);
        });
        try
        {
            var data = await Task.Run(() => service.BuildAsync(cts.Token), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            _data = data;
            _builtAt = DateTime.Now;
            Render();
        }
        catch (OperationCanceledException)
        {
            /* replaced by a newer build */
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn't work out suggestions: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
                _building = false;
        }
    }

    private void SetStatus(string text) =>
        _status.Text = text.Length > 0 ? text : _data.Count == 0 ? "" : $"{_data.Sum(s => s.Items.Count)} suggestions · updated {_builtAt:HH:mm}"
            + (_meta?.Tmdb is null ? " · add a TMDB key in Settings for film, TV and anime suggestions" : "");

    private void ShowMessage(string text)
    {
        ClearBitmaps();
        _sections.Children.Clear();
        _sections.Children.Add(new TextBlock { Text = text, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 30, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, MaxWidth = 460 });
    }

    private void ClearBitmaps()
    {
        foreach (var b in _bitmaps)
            b.Dispose();
        _bitmaps.Clear();
    }

    private void Render()
    {
        ClearBitmaps();
        _sections.Children.Clear();
        var shown = _data.Where(s => _group == "All" || s.Group == _group || (_group == "Movies" && s.Group == "Watched")).ToList();
        SetStatus("");
        if (shown.Count == 0)
        {
            ShowMessage(_group switch
            {
                "Movies" or "TV" or "Anime" when _meta?.Tmdb is null => "Add a TMDB key in Settings to get film, TV and anime suggestions.",
                "Audiobooks" => "No audiobook suggestions yet: they come from the authors and series of the audiobooks in your library.",
                _ => "Nothing to suggest here yet.",
            });
            return;
        }

        foreach (var section in shown)
            _sections.Children.Add(BuildRow(section));
    }

    private Control BuildRow(DiscoverSection section)
    {
        var header = new StackPanel { Spacing = 1, Margin = new Thickness(2, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = section.Title, FontSize = 17, FontWeight = FontWeight.Bold });
        header.Children.Add(new TextBlock { Text = section.Subtitle, Classes = { "muted" }, FontSize = 11.5 });

        var cards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var s in section.Items)
            cards.Children.Add(BuildCard(s, cards));

        var row = new ScrollViewer
        {
            Content = cards,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 0, 12),
        };
        // The mouse wheel scrolls the page; Shift+wheel (or a touchpad swipe sideways) scrolls the row.
        row.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0)
            {
                var dx = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
                row.Offset = new Vector(Math.Max(0, row.Offset.X - dx * 120), 0);
            }
            else
            {
                _scroller.Offset = new Vector(_scroller.Offset.X, Math.Max(0, _scroller.Offset.Y - e.Delta.Y * 60));
            }

            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(row);
        return panel;
    }

    private Control BuildCard(Suggestion s, StackPanel parent)
    {
        const double w = 140, h = 210;
        var initials = new TextBlock
        {
            Text = string.Concat(s.Match.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => char.ToUpperInvariant(x[0]))),
            FontSize = 40, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#55FFFFFF")),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var image = new Image { Stretch = Stretch.UniformToFill };
        var art = new Panel();
        art.Children.Add(initials);
        art.Children.Add(image);
        var rating = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#D90B0F14")), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2), Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsVisible = s.Rating > 0,
            Child = new TextBlock
            {
                Text = s.Kind == LibraryKind.Audiobook ? $"★ {s.Rating / 2:0.0}" : s.Kind == LibraryKind.Game ? $"{s.Rating * 10:0}%" : $"★ {s.Rating:0.0}",
                FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(s.Kind == LibraryKind.Game ? "#66C0F4" : "#F5C518")),
            },
        };
        art.Children.Add(rating);
        var poster = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(10), ClipToBounds = true, Background = new SolidColorBrush(Color.Parse("#1D212C")), Child = art };

        var stack = new StackPanel { Width = w, Spacing = 2 };
        stack.Children.Add(poster);
        stack.Children.Add(new TextBlock { Text = s.Match.Title, FontWeight = FontWeight.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 6, 2, 0) });
        var meta = new List<string>();
        if (s.Match.Year is { } y)
            meta.Add(y.ToString());
        meta.Add(s.IsAnime ? "Anime" : s.Kind switch { LibraryKind.Tv => "TV", LibraryKind.Game => "Game", LibraryKind.Audiobook => "Audiobook", _ => "Movie" });
        stack.Children.Add(new TextBlock { Text = string.Join(" · ", meta), Classes = { "muted" }, FontSize = 11, Margin = new Thickness(2, 0) });
        if (s.Because.Count > 0)
            stack.Children.Add(new TextBlock
            {
                Text = s.Reason,
                FontSize = 10.5, Foreground = Ui.Accent, TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0),
            });

        var card = new Border { Classes = { "tile" }, Padding = new Thickness(5), Child = stack, Cursor = new Cursor(StandardCursorType.Hand) };
        ToolTip.SetTip(card, s.Match.Title + (s.FromLibrary && s.Because.Count > 1 ? "\nBecause you have: " + string.Join(", ", s.Because) : ""));

        var menu = new ContextMenu();
        var notInterested = new MenuItem { Header = "Not interested" };
        notInterested.Click += (_, _) => Hide(s, card, parent);
        var open = new MenuItem { Header = $"Open on {s.SiteName}" };
        open.Click += async (_, _) => await TopLevel.GetTopLevel(this)!.Launcher.LaunchUriAsync(new Uri(s.Url));
        menu.Items.Add(open);
        menu.Items.Add(notInterested);
        card.ContextMenu = menu;
        card.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && _meta is not null && TopLevel.GetTopLevel(this) is Window owner)
                new SuggestionWindow(s, _meta, () => Hide(s, card, parent)).Show(owner);
        };

        _ = LoadPosterAsync(s, image, initials);
        return card;
    }

    private void Hide(Suggestion s, Control card, StackPanel parent)
    {
        DiscoverService.Dismiss(s);
        parent.Children.Remove(card);
        foreach (var section in _data)
            section.Items.Remove(s);
    }

    private async Task LoadPosterAsync(Suggestion s, Image target, TextBlock initials)
    {
        if (_meta is null)
            return;
        await _posterGate.WaitAsync();
        try
        {
            var file = await _meta.GetPosterFileAsync(s.Match, CancellationToken.None);
            if (file is null)
                return;
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(file);
                return Bitmap.DecodeToWidth(stream, 280);
            });
            Dispatcher.UIThread.Post(() =>
            {
                target.Stretch = bitmap.PixelSize.Width > bitmap.PixelSize.Height ? Stretch.Uniform : Stretch.UniformToFill;
                target.Source = bitmap;
                initials.IsVisible = false;
                _bitmaps.Add(bitmap);
            });
        }
        catch
        {
            /* no poster: initials stay */
        }
        finally
        {
            _posterGate.Release();
        }
    }
}

/// <summary>A suggested title up close: poster, why it's suggested, description, trailer and a link to its page.</summary>
public sealed class SuggestionWindow
{
    private readonly Window _window;

    public SuggestionWindow(Suggestion s, MetadataService meta, Action notInterested)
    {
        var poster = new Image { Stretch = Stretch.UniformToFill };
        var posterBox = new Border { Width = 220, Height = 330, CornerRadius = new CornerRadius(12), ClipToBounds = true, Background = new SolidColorBrush(Color.Parse("#1D212C")), Child = poster, VerticalAlignment = VerticalAlignment.Top };

        var text = new StackPanel { Spacing = 10 };
        text.Children.Add(new TextBlock { Text = s.Match.Title, FontSize = 26, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Classes = { "neon" } });
        var metaLine = Ui.Text("", 13, Ui.Muted);
        text.Children.Add(metaLine);
        var ratingLine = Ui.Text(s.Rating > 0
            ? s.Kind switch
            {
                LibraryKind.Game => $"{s.Rating * 10:0}% positive on Steam ({s.Votes:N0} reviews)",
                LibraryKind.Audiobook => $"★ {s.Rating / 2:0.0} / 5 on Audible ({s.Votes:N0} ratings)",
                _ => $"★ {s.Rating:0.0} / 10 on TMDB ({s.Votes:N0} votes)",
            }
            : "", 14, new SolidColorBrush(Color.Parse("#F5C518")), FontWeight.SemiBold);
        text.Children.Add(ratingLine);
        if (s.Because.Count > 0)
        {
            var why = s.FromLibrary ? "Recommended because you have " + string.Join(", ", s.Because) : string.Join("  ·  ", s.Because);
            text.Children.Add(Ui.Card(Ui.Text(why, 13, Ui.Accent)));
        }

        var overview = Ui.Text(s.Match.Overview.Length > 0 ? s.Match.Overview : "Loading description…", 13.5);
        text.Children.Add(overview);
        var extra = Ui.Text("", 12.5, Ui.Muted);
        text.Children.Add(extra);

        var trailer = new Button { Content = "▶ Trailer", IsVisible = false };
        var open = new Button { Content = $"Open on {s.SiteName}", Classes = { "primary" } };
        var hide = new Button { Content = "Not interested", Classes = { "ghost" } };
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(open);
        buttons.Children.Add(trailer);
        buttons.Children.Add(hide);
        text.Children.Add(buttons);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), ColumnSpacing = 24 };
        grid.Children.Add(posterBox);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        _window = Ui.ToolWindow(s.Match.Title, 820, 520, grid);

        Bitmap? bitmap = null;
        string? trailerKey = null;
        open.Click += async (_, _) => await _window.Launcher.LaunchUriAsync(new Uri(s.Url));
        trailer.Click += async (_, _) =>
        {
            if (trailerKey is not null)
                await _window.Launcher.LaunchUriAsync(new Uri("https://www.youtube.com/watch?v=" + trailerKey));
        };
        hide.Click += (_, _) =>
        {
            notInterested();
            _window.Close();
        };
        _window.Closed += (_, _) => bitmap?.Dispose();
        _window.Opened += async (_, _) =>
        {
            var parts = new List<string>();
            if (s.Match.Year is { } y)
                parts.Add(y.ToString());
            parts.Add(s.IsAnime ? "Anime" : s.Kind switch { LibraryKind.Tv => "TV series", LibraryKind.Game => "Game", LibraryKind.Audiobook => "Audiobook", _ => "Movie" });
            metaLine.Text = string.Join("  ·  ", parts);

            if (await meta.GetPosterFileAsync(s.Match, CancellationToken.None) is { } file)
            {
                try
                {
                    await using var stream = File.OpenRead(file);
                    bitmap = Bitmap.DecodeToWidth(stream, 440);
                    poster.Stretch = bitmap.PixelSize.Width > bitmap.PixelSize.Height ? Stretch.Uniform : Stretch.UniformToFill;
                    poster.Source = bitmap;
                }
                catch
                {
                    /* no poster */
                }
            }

            var d = await meta.GetDetailsAsync(s.Match, CancellationToken.None);
            if (d is null)
            {
                if (s.Match.Overview.Length == 0)
                    overview.Text = "";
                return;
            }

            if (s.Match.Overview.Length == 0)
                overview.Text = s.Kind == LibraryKind.Game ? d.Tagline : d.Tagline;
            if (d.Genres.Length > 0)
                parts.Add(string.Join(", ", d.Genres.Take(4)));
            if (d.RuntimeMinutes is { } rt)
                parts.Add(s.Kind == LibraryKind.Tv ? $"{rt} min / episode" : rt >= 60 ? $"{rt / 60}h {rt % 60:00}m" : $"{rt} min");
            if (d.Seasons is { } seasons)
                parts.Add($"{seasons} season{(seasons == 1 ? "" : "s")}");
            metaLine.Text = string.Join("  ·  ", parts);

            var lines = new List<string>();
            if (d.Directors.Length > 0)
                lines.Add((s.Kind switch { LibraryKind.Game => "Developed by ", LibraryKind.Audiobook => "Written by ", LibraryKind.Tv => "Created by ", _ => "Directed by " })
                          + string.Join(", ", d.Directors.Take(3)));
            if (d.Cast.Length > 0)
                lines.Add((s.Kind == LibraryKind.Audiobook ? "Narrated by " : "Starring ") + string.Join(", ", d.Cast.Take(4).Select(c => c.Split(" as ")[0])));
            extra.Text = string.Join("\n", lines);
            if (d.TrailerKey is { Length: > 0 } key)
            {
                trailerKey = key;
                trailer.IsVisible = true;
            }
        };
    }

    public void Show(Window owner) => _window.Show(owner);
}
