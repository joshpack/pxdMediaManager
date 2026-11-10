using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using pxdMediaManager.Services;

namespace pxdMediaManager.Views;

/// <summary>What the user decided in the fix-match window: a chosen title, or "forget my choice".</summary>
public sealed record MatchDialogResult(TmdbMatch? Match, bool Reset);

/// <summary>
/// Searches by hand (TMDB for films and TV, Steam for games, Audible for audiobooks) so a wrongly matched library item
/// can be pointed at the right title.
/// </summary>
public partial class MatchDialog : Window
{
    private readonly MetadataService? _meta;
    private readonly LibraryKind _fixedKind = LibraryKind.Auto;
    private readonly List<Bitmap> _bitmaps = new();
    private CancellationTokenSource? _cts;

    public MatchDialog()
    {
        InitializeComponent();
        AppChrome.Apply(this, "match");
    }

    public MatchDialog(MetadataService meta, LibraryItem item) : this()
    {
        _meta = meta;
        this.FindControl<TextBlock>("ForText")!.Text = $"For: {item.Name}";

        // Start from the cleaned release name (not the current match, which is what's being corrected).
        this.FindControl<TextBox>("QueryBox")!.Text = item.Title;
        this.FindControl<TextBox>("YearBox")!.Text = item.Year?.ToString() ?? "";

        var tv = item.Match?.Kind == LibraryKind.Tv || item.Kind == LibraryKind.Tv || item.Extra.Length > 0;
        var movie = this.FindControl<ToggleButton>("MovieChip")!;
        var show = this.FindControl<ToggleButton>("TvChip")!;
        movie.IsChecked = !tv;
        show.IsChecked = tv;
        // Games and audiobooks each have one source, so the Movie/TV switch only applies to films and shows.
        if (item.Kind is LibraryKind.Game or LibraryKind.Audiobook)
        {
            _fixedKind = item.Kind;
            movie.IsVisible = show.IsVisible = false;
            this.FindControl<TextBox>("YearBox")!.IsVisible = item.Kind == LibraryKind.Game;
            if (item.Kind == LibraryKind.Audiobook)
                this.FindControl<TextBox>("QueryBox")!.Text = AudibleClient.QueryFrom(item.Name);
        }

        movie.Click += (_, _) => { movie.IsChecked = true; show.IsChecked = false; _ = SearchAsync(); };
        show.Click += (_, _) => { show.IsChecked = true; movie.IsChecked = false; _ = SearchAsync(); };

        this.FindControl<Button>("SearchButton")!.Click += (_, _) => _ = SearchAsync();
        foreach (var name in new[] { "QueryBox", "YearBox" })
            this.FindControl<TextBox>(name)!.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    _ = SearchAsync();
            };

        var results = this.FindControl<ListBox>("ResultsList")!;
        var use = this.FindControl<Button>("UseButton")!;
        results.SelectionChanged += (_, _) => use.IsEnabled = results.SelectedItem is ListBoxItem;
        results.DoubleTapped += (_, _) => UseSelection();
        use.Click += (_, _) => UseSelection();
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
        this.FindControl<Button>("ResetButton")!.Click += (_, _) => Close(new MatchDialogResult(null, true));
        this.FindControl<Button>("ResetButton")!.IsEnabled = item.IsManualMatch;

        Opened += (_, _) => _ = SearchAsync();
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            foreach (var b in _bitmaps)
                b.Dispose();
        };
    }

    private void UseSelection()
    {
        if (this.FindControl<ListBox>("ResultsList")!.SelectedItem is ListBoxItem { Tag: TmdbMatch match })
            Close(new MatchDialogResult(match, false));
    }

    private void SetStatus(string? text)
    {
        var status = this.FindControl<TextBlock>("StatusText")!;
        status.Text = text ?? "";
        status.IsVisible = text is not null;
    }

    private async Task SearchAsync()
    {
        if (_meta is null)
            return;
        var query = this.FindControl<TextBox>("QueryBox")!.Text?.Trim() ?? "";
        if (query.Length == 0)
            return;

        var yearBox = this.FindControl<TextBox>("YearBox")!;
        int? year = yearBox.IsVisible && int.TryParse(yearBox.Text?.Trim(), out var y) && y > 1800 ? y : null;
        var kind = _fixedKind != LibraryKind.Auto ? _fixedKind
            : this.FindControl<ToggleButton>("TvChip")!.IsChecked == true ? LibraryKind.Tv : LibraryKind.Movie;
        var source = kind switch { LibraryKind.Game => "Steam", LibraryKind.Audiobook => "Audible", _ => "TMDB" };
        var hint = source == "TMDB" ? ", or switch between Movie and TV." : ".";

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var list = this.FindControl<ListBox>("ResultsList")!;
        list.Items.Clear();
        SetStatus($"Searching {source}…");

        List<TmdbMatch> results;
        try
        {
            results = await _meta.SearchAsync(kind, query, year, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't reach {source}: " + ex.Message);
            return;
        }

        if (cts.IsCancellationRequested)
            return;
        if (source == "TMDB" && _meta.Tmdb is null)
        {
            SetStatus("Add a TMDB key in Settings to search films and TV.");
            return;
        }

        if (source == "TMDB" && _meta.Tmdb!.Rejected)
        {
            SetStatus("TMDB rejected the key. Check it in Settings.");
            return;
        }

        if (results.Count == 0)
        {
            SetStatus(year is null ? "No results. Try a shorter title" + hint : "No results. Try clearing the year" + hint);
            return;
        }

        SetStatus(null);
        foreach (var r in results)
        {
            var thumb = new Image { Stretch = Stretch.UniformToFill };
            var poster = new Border
            {
                Width = 56,
                Height = 84,
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.Parse("#1D212C")),
                Child = thumb,
            };
            var title = new TextBlock { Text = r.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var meta = new TextBlock
            {
                Text = (r.Year?.ToString() ?? "year unknown") + r.Kind switch
                {
                    LibraryKind.Tv => "  ·  TV series",
                    LibraryKind.Game => "  ·  Game (Steam)",
                    LibraryKind.Audiobook => "  ·  Audiobook (Audible)",
                    _ => "  ·  Movie",
                },
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#19F58C")),
            };
            var overview = new TextBlock
            {
                Text = r.Overview.Length > 0 ? r.Overview : "No description.",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.Parse("#8A92A6")),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 48,
            };

            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(title);
            text.Children.Add(meta);
            text.Children.Add(overview);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(4, 4) };
            row.Children.Add(poster);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            list.Items.Add(new ListBoxItem { Content = row, Tag = r });
            _ = LoadThumbAsync(r, thumb, cts.Token);
        }
    }

    private async Task LoadThumbAsync(TmdbMatch match, Image target, CancellationToken ct)
    {
        try
        {
            var file = await _meta!.GetPosterFileAsync(match, ct);
            if (file is null || ct.IsCancellationRequested)
                return;

            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(file);
                return Bitmap.DecodeToWidth(stream, 112);
            }, ct);

            Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    return;
                }

                target.Source = bitmap;
                _bitmaps.Add(bitmap);
            });
        }
        catch
        {
            /* no thumbnail; the row is still usable */
        }
    }
}
