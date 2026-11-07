using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PaxMediaManager.Services;

namespace PaxMediaManager.Views;

/// <summary>Small building blocks shared by the code-built tool windows (stats, duplicates, cast).</summary>
internal static class Ui
{
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8A92A6"));
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#19F58C"));
    public static readonly IBrush Info = new SolidColorBrush(Color.Parse("#00C8FF"));

    public static TextBlock Label(string text) => new() { Text = text.ToUpperInvariant(), Classes = { "label" }, Margin = new Thickness(0, 6, 0, 4) };

    public static TextBlock Text(string text, double size = 13, IBrush? brush = null, FontWeight weight = FontWeight.Normal)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        // Only set a colour when one is given: a null Foreground means "no brush" (invisible), not "inherit".
        if (brush is not null)
            t.Foreground = brush;
        return t;
    }

    public static Border Card(Control child) => new() { Classes = { "card" }, Child = child, Padding = new Thickness(16, 12) };

    /// <summary>A big number with a caption, for the row of headline figures.</summary>
    public static Border Figure(string value, string caption) =>
        new()
        {
            Classes = { "card" },
            Padding = new Thickness(16, 10),
            MinWidth = 150,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeight.Bold, Classes = { "neon" } },
                    new TextBlock { Text = caption, Classes = { "muted" }, FontSize = 11 },
                },
            },
        };

    /// <summary>One labelled horizontal bar, scaled against the largest value in its group.</summary>
    public static Grid Bar(string label, double value, double max, string valueText, IBrush? color = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,90"), ColumnSpacing = 10, Margin = new Thickness(0, 2) };
        row.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var share = max > 0 ? Math.Clamp(value / max, 0, 1) : 0;
        var track = new Grid { ColumnDefinitions = new ColumnDefinitions($"{Math.Max(share, 0.001).ToString(System.Globalization.CultureInfo.InvariantCulture)}*,{Math.Max(1 - share, 0.001).ToString(System.Globalization.CultureInfo.InvariantCulture)}*"), Height = 12 };
        track.Children.Add(new Border { Background = color ?? Accent, CornerRadius = new CornerRadius(4) });
        Grid.SetColumn(track, 1);
        row.Children.Add(track);
        var v = new TextBlock { Text = valueText, Foreground = Muted, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(v, 2);
        row.Children.Add(v);
        return row;
    }

    /// <summary>A code-built window with the PAX title bar; its placement is remembered under <paramref name="placementKey"/>.</summary>
    public static Window ToolWindow(string title, double width, double height, Control content, string placementKey)
    {
        var window = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = 520,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = new Border { Padding = new Thickness(24), Child = content } },
        };
        AppChrome.Apply(window, placementKey);
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                window.Close();
        };
        return window;
    }

    /// <summary>Total running time of an item: file length, or episodes × episode length, or the listed runtime.</summary>
    public static TimeSpan RunningTime(LibraryItem item)
    {
        var episodes = item.IsGroup ? item.Members.Count(m => m.EpisodeNumber.Episode is not null) : 0;
        if (episodes > 1 && item.Details?.RuntimeMinutes is { } epMins)
            return TimeSpan.FromMinutes(episodes * epMins);
        if (item.Specs?.Duration is { } d && episodes <= 1)
            return d;
        if (item.Details?.RuntimeMinutes is { } mins && item.Details.Seasons is null)
            return TimeSpan.FromMinutes(mins);
        return TimeSpan.Zero;
    }

    public static string Hours(TimeSpan t) =>
        t.TotalHours >= 48 ? $"{t.TotalDays:0.#} days" : t.TotalHours >= 1 ? $"{t.TotalHours:0} h" : $"{t.TotalMinutes:0} min";

    public static string KindName(LibraryItem i) =>
        i.IsAnime ? "Anime"
        : (i.Match?.Kind ?? i.Kind) switch
        {
            LibraryKind.Movie => "Movies",
            LibraryKind.Tv => "TV shows",
            LibraryKind.Game => "Games",
            LibraryKind.Audiobook => "Audiobooks",
            LibraryKind.None => "Other",
            _ => "Unsorted",
        };
}

/// <summary>Headline numbers and breakdowns for what's in the library (and how much of it has been watched).</summary>
public sealed class StatsWindow
{
    private readonly Window _window;

    public StatsWindow(IReadOnlyList<LibraryItem> items, string scope)
    {
        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(Ui.Text("Library statistics", 22, null, FontWeight.Bold));
        root.Children.Add(Ui.Text($"For {scope}. Running times, quality and genres fill in as the library finishes reading file details in the background.", 12, Ui.Muted));

        var totalSize = items.Sum(i => i.Size ?? 0);
        var running = items.Aggregate(TimeSpan.Zero, (t, i) => t + Ui.RunningTime(i));
        var rated = items.Where(i => i.BestRating is > 0).ToList();
        var watchedEntries = items.SelectMany(WatchHistory.ForItem).ToList();
        var watchedTime = TimeSpan.FromMilliseconds(watchedEntries.Sum(e => e.Completed ? e.DurationMs : e.PositionMs));
        var finished = items.Count(i => WatchHistory.Progress(i).Finished);

        var figures = new WrapPanel { ItemSpacing = 10, LineSpacing = 10 };
        figures.Children.Add(Ui.Figure(items.Count.ToString("N0"), "titles"));
        figures.Children.Add(Ui.Figure(StatsStore.FormatBytes(totalSize), "on disk"));
        figures.Children.Add(Ui.Figure(running > TimeSpan.Zero ? Ui.Hours(running) : "—", "of content (where known)"));
        figures.Children.Add(Ui.Figure(rated.Count > 0 ? $"★ {rated.Average(i => i.BestRating!.Value):0.0}" : "—", $"average rating ({rated.Count} rated)"));
        figures.Children.Add(Ui.Figure(watchedTime > TimeSpan.Zero ? Ui.Hours(watchedTime) : "—", "watched here"));
        figures.Children.Add(Ui.Figure(finished.ToString("N0"), "titles finished"));
        root.Children.Add(figures);

        void Section(string title, IEnumerable<(string Label, double Value, string Text)> rows, IBrush? color = null)
        {
            var list = rows.ToList();
            if (list.Count == 0)
                return;
            var max = list.Max(r => r.Value);
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(Ui.Label(title));
            foreach (var r in list)
                panel.Children.Add(Ui.Bar(r.Label, r.Value, max, r.Text, color));
            root.Children.Add(Ui.Card(panel));
        }

        Section("Storage by category", items.GroupBy(i => i.Category.Length > 0 ? i.Category : "Other")
            .Select(g => (g.Key, (double)g.Sum(i => i.Size ?? 0), $"{StatsStore.FormatBytes(g.Sum(i => i.Size ?? 0))} · {g.Count()}"))
            .OrderByDescending(r => r.Item2));
        Section("What kind of things", items.GroupBy(Ui.KindName).Select(g => (g.Key, (double)g.Count(), g.Count().ToString("N0")))
            .OrderByDescending(r => r.Item2), Ui.Info);
        var withSpecs = items.Where(i => i.Specs?.Resolution is { Length: > 0 }).ToList();
        Section($"Video quality ({withSpecs.Count} files read)", withSpecs.GroupBy(i => i.Specs!.Resolution)
            .Select(g => (g.Key, (double)g.Count(), $"{g.Count()} · {StatsStore.FormatBytes(g.Sum(i => i.Size ?? 0))}"))
            .OrderBy(r => Array.IndexOf(new[] { "4K", "1440p", "1080p", "720p", "SD" }, r.Key)));
        Section("Top genres", items.SelectMany(i => (i.Details?.Genres ?? Array.Empty<string>()).Select(g => (g, i)))
            .GroupBy(x => x.g, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, (double)g.Count(), g.Count().ToString("N0")))
            .OrderByDescending(r => r.Item2).Take(12), Ui.Info);
        Section("By decade", items.Where(i => i.DisplayYear is > 1900).GroupBy(i => i.DisplayYear!.Value / 10 * 10)
            .OrderByDescending(g => g.Key).Select(g => ($"{g.Key}s", (double)g.Count(), g.Count().ToString("N0"))));
        Section("Highest rated", rated.OrderByDescending(i => i.BestRating).Take(10)
            .Select(i => (i.DisplayTitle, i.BestRating!.Value, $"★ {i.BestRating:0.0}")), new SolidColorBrush(Color.Parse("#F5C518")));
        Section("Biggest titles", items.Where(i => i.Size is > 0).OrderByDescending(i => i.Size).Take(10)
            .Select(i => (i.DisplayTitle, (double)i.Size!.Value, StatsStore.FormatBytes(i.Size.Value))));

        _window = Ui.ToolWindow("Library statistics", 820, 780, root, "stats");
    }

    public static implicit operator Window(StatsWindow w) => w._window;
}

/// <summary>
/// Titles that are in the library more than once (same TMDB / Steam / Audible match, or same name and year), with
/// sizes and quality so the weaker copy can be removed; plus low-quality films worth replacing.
/// </summary>
public sealed class DuplicatesWindow
{
    private readonly Window _window;

    public DuplicatesWindow(IReadOnlyList<LibraryItem> items, string scope, Action<string>? openFolder, Action<LibraryItem> showDetails)
    {
        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(Ui.Text("Duplicates & upgrades", 22, null, FontWeight.Bold));
        root.Children.Add(Ui.Text($"Looking through {scope}. Nothing is deleted here; open the folder to remove the copy you don't want.", 12, Ui.Muted));

        static string Key(LibraryItem i) =>
            i.Match is { } m ? $"{m.Kind}:{m.Key}" : $"{MetaKey(i.Title)}:{i.Year}";

        var groups = items.Where(i => i.Kind != LibraryKind.None).GroupBy(Key).Where(g => g.Count() > 1)
            // Width, not height: wide films are often cropped (3840×1600 is still 4K), then the bigger file.
            .Select(g => g.OrderByDescending(i => i.Specs?.Width ?? 0).ThenByDescending(i => i.Size ?? 0).ToList())
            .OrderByDescending(g => g.Skip(1).Sum(i => i.Size ?? 0)).ToList();
        var reclaim = groups.Sum(g => g.Skip(1).Sum(i => i.Size ?? 0));

        root.Children.Add(Ui.Card(Ui.Text(groups.Count == 0
            ? "No duplicates found."
            : $"{groups.Count} title{(groups.Count == 1 ? "" : "s")} stored more than once. Keeping only the best copy of each would free about {StatsStore.FormatBytes(reclaim)}.",
            14, groups.Count == 0 ? Ui.Accent : null)));

        foreach (var g in groups)
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(Ui.Text($"{g[0].DisplayTitle}{(g[0].DisplayYear is { } y ? $" ({y})" : "")}", 15, null, FontWeight.SemiBold));
            for (var n = 0; n < g.Count; n++)
                panel.Children.Add(CopyRow(g[n], n == 0, openFolder, showDetails));
            root.Children.Add(Ui.Card(panel));
        }

        // Films and shows whose main file is below 1080p: candidates for a better copy.
        var low = items.Where(i => i.Specs is { Height: > 0 and < 1000, Width: < 1800 } && i.Kind is not (LibraryKind.Game or LibraryKind.Audiobook))
            .OrderBy(i => i.Specs!.Height).ThenBy(i => i.DisplayTitle).ToList();
        if (low.Count > 0)
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(Ui.Label($"Upgrade candidates — below 1080p ({low.Count})"));
            foreach (var i in low)
                panel.Children.Add(CopyRow(i, false, openFolder, showDetails, showTitle: true));
            root.Children.Add(Ui.Card(panel));
        }

        _window = Ui.ToolWindow("Duplicates & upgrades", 860, 720, root, "duplicates");
    }

    private static string MetaKey(string s) => System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "");

    private static Grid CopyRow(LibraryItem i, bool best, Action<string>? openFolder, Action<LibraryItem> showDetails, bool showTitle = false)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 2) };
        var name = new StackPanel();
        name.Children.Add(new TextBlock
        {
            Text = (showTitle ? i.DisplayTitle + "  ·  " : "") + (i.Category.Length > 0 ? i.Category + "  ·  " : "") + i.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        name.Children.Add(new TextBlock { Text = i.Path, FontSize = 10.5, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTip.SetTip(name, i.Path);
        row.Children.Add(name);

        var quality = new TextBlock
        {
            Text = i.Specs?.Resolution is { Length: > 0 } r ? r : "?",
            FontWeight = FontWeight.Bold,
            Foreground = best ? Ui.Accent : Ui.Muted,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 46,
        };
        Grid.SetColumn(quality, 1);
        row.Children.Add(quality);
        var size = new TextBlock { Text = i.Size is > 0 ? StatsStore.FormatBytes(i.Size.Value) : "…", Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, MinWidth = 70 };
        Grid.SetColumn(size, 2);
        row.Children.Add(size);
        var details = new Button { Content = "Details", Padding = new Thickness(10, 3) };
        details.Click += (_, _) => showDetails(i);
        Grid.SetColumn(details, 3);
        row.Children.Add(details);
        var open = new Button { Content = "Open folder", Padding = new Thickness(10, 3), IsVisible = openFolder is not null };
        open.Click += (_, _) => openFolder?.Invoke(i.FolderPath);
        Grid.SetColumn(open, 4);
        row.Children.Add(open);
        if (best)
            ToolTip.SetTip(quality, "Best copy (widest picture, then largest file)");
        return row;
    }

    public static implicit operator Window(DuplicatesWindow w) => w._window;
}
