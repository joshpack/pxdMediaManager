using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PaxMediaManager.Services;

namespace PaxMediaManager.Views;

/// <summary>An actor's page: what of theirs is already in the library (clickable), then their best-known other work.</summary>
public sealed class PersonWindow
{
    private readonly Window _window;

    public PersonWindow(int personId, string name, MetadataService meta, IReadOnlyList<LibraryItem> library, Action<LibraryItem>? showDetails)
    {
        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(Ui.Text(name, 24, null, FontWeight.Bold));
        var status = Ui.Text("Loading from TMDB…", 12, Ui.Muted);
        root.Children.Add(status);
        _window = Ui.ToolWindow(name, 760, 700, root, "person");

        _window.Opened += async (_, _) =>
        {
            if (meta.Tmdb is null)
            {
                status.Text = "Needs a TMDB key in Settings.";
                return;
            }

            var person = await meta.Tmdb.GetPersonAsync(personId, CancellationToken.None);
            if (person is not { } p)
            {
                status.Text = "Couldn't load this person from TMDB right now.";
                return;
            }

            status.Text = p.Biography.Length > 0
                ? p.Biography.Length > 700 ? p.Biography[..700].TrimEnd() + "…" : p.Biography
                : "No biography on TMDB.";
            status.ClearValue(TextBlock.ForegroundProperty);
            status.FontSize = 13;

            // Library items keyed by their TMDB match, to spot what's already here.
            var owned = library.Where(i => i.Match is { Kind: LibraryKind.Movie or LibraryKind.Tv })
                .GroupBy(i => (i.Match!.Kind, i.Match.Id)).ToDictionary(g => g.Key, g => g.First());
            var inLibrary = p.Credits.Where(c => owned.ContainsKey((c.Kind, c.Id))).OrderByDescending(c => c.Year ?? 0).ToList();
            // Talk-show and award-show appearances ("Self", "Himself") crowd out the real roles: leave them out.
            var others = p.Credits.Where(c => !owned.ContainsKey((c.Kind, c.Id))
                                              && !System.Text.RegularExpressions.Regex.IsMatch(c.Character, @"^\s*(Self|Himself|Herself|Themselves)\b",
                                                  System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .Take(30).ToList();

            var mine = new StackPanel { Spacing = 4 };
            mine.Children.Add(Ui.Label($"In your library ({inLibrary.Count})"));
            if (inLibrary.Count == 0)
                mine.Children.Add(Ui.Text("Nothing else of theirs in the library categories that are loaded.", 12, Ui.Muted));
            foreach (var c in inLibrary)
            {
                var item = owned[(c.Kind, c.Id)];
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
                row.Children.Add(Ui.Text($"{c.Title}{(c.Year is { } y ? $" ({y})" : "")}{(c.Character.Length > 0 ? "  —  as " + c.Character : "")}"));
                var open = new Button { Content = "Details", Padding = new Thickness(10, 3), IsVisible = showDetails is not null };
                open.Click += (_, _) => showDetails?.Invoke(item);
                Grid.SetColumn(open, 1);
                row.Children.Add(open);
                mine.Children.Add(row);
            }

            root.Children.Add(Ui.Card(mine));

            var rest = new StackPanel { Spacing = 3 };
            rest.Children.Add(Ui.Label("Best known for (not in your library)"));
            foreach (var c in others)
                rest.Children.Add(Ui.Text($"{c.Title}{(c.Year is { } y ? $" ({y})" : "")}{(c.Kind == LibraryKind.Tv ? "  · TV" : "")}"
                                          + (c.Character.Length > 0 ? "  —  " + c.Character : ""), 12.5));
            root.Children.Add(Ui.Card(rest));
        };
    }

    public void Show(Window owner) => _window.Show(owner);
}

/// <summary>Finds subtitles on OpenSubtitles for one video file and saves the chosen one next to it.</summary>
public sealed class SubtitleWindow
{
    private readonly Window _window;

    public SubtitleWindow(OpenSubtitlesClient client, string videoPath, string? imdbId, int? season, int? episode, string languages)
    {
        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(Ui.Text("Subtitles", 22, null, FontWeight.Bold));
        root.Children.Add(Ui.Text(Path.GetFileName(videoPath), 12, Ui.Muted));
        var status = Ui.Text($"Searching OpenSubtitles ({languages})…", 13);
        root.Children.Add(status);
        var list = new StackPanel { Spacing = 4 };
        root.Children.Add(list);
        _window = Ui.ToolWindow("Subtitles", 760, 600, root, "subtitles");

        _window.Opened += async (_, _) =>
        {
            List<SubtitleResult> results;
            try
            {
                results = await client.SearchAsync(imdbId, season, episode, Path.GetFileName(videoPath), languages, CancellationToken.None);
            }
            catch (Exception ex)
            {
                status.Text = "Search failed: " + ex.Message;
                return;
            }

            if (results.Count == 0)
            {
                status.Text = "No subtitles found in " + languages + ".";
                return;
            }

            // Release names that share words with the file name usually line up best with it.
            var fileWords = Path.GetFileNameWithoutExtension(videoPath).ToLowerInvariant().Split('.', ' ', '-', '_');
            results = results.OrderByDescending(r => fileWords.Count(w => w.Length > 2 && r.Release.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .ThenByDescending(r => r.Downloads).Take(40).ToList();
            status.Text = $"{results.Count} found. Best matches for this file's release come first. The saved file sits next to the video, where the player finds it.";
            foreach (var r in results)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 2) };
                row.Children.Add(new TextBlock { Text = r.Language.ToUpperInvariant(), FontWeight = FontWeight.Bold, Foreground = Ui.Accent, VerticalAlignment = VerticalAlignment.Center });
                var name = new TextBlock
                {
                    Text = (r.Release.Length > 0 ? r.Release : r.FileName) + (r.HearingImpaired ? "   [HI]" : ""),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTip.SetTip(name, r.FileName);
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                var dl = new TextBlock { Text = $"{r.Downloads:N0} ↓", Foreground = Ui.Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(dl, 2);
                row.Children.Add(dl);
                var get = new Button { Content = "Download", Padding = new Thickness(10, 3) };
                get.Click += async (_, _) =>
                {
                    get.IsEnabled = false;
                    get.Content = "…";
                    try
                    {
                        var (saved, remaining) = await client.DownloadAsync(r, videoPath, CancellationToken.None);
                        get.Content = "✓ Saved";
                        status.Text = $"Saved {Path.GetFileName(saved)}." + (remaining is { } n ? $" {n} download{(n == 1 ? "" : "s")} left today." : "")
                                      + " If the video is open, reopen it to pick the subtitle up.";
                    }
                    catch (Exception ex)
                    {
                        get.Content = "Download";
                        get.IsEnabled = true;
                        status.Text = "Download failed: " + ex.Message;
                    }
                };
                Grid.SetColumn(get, 3);
                row.Children.Add(get);
                list.Children.Add(row);
            }
        };
    }

    public void Show(Window owner) => _window.Show(owner);
}
