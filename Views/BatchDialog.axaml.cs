using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using pxdMediaManager.Services;

namespace pxdMediaManager.Views;

/// <summary>
/// Startup prompt for torrents that were already in the watch folder. One action applies to every ticked file, but each
/// torrent chooses its own category.
/// </summary>
public partial class BatchDialog : Window
{
    private const string NoMove = "Don't move";

    private readonly TaskCompletionSource<BatchResult?> _result = new();
    private readonly List<(CheckBox Box, ComboBox? Category, BatchItem Item)> _rows = new();
    private bool _answered;

    public BatchDialog()
    {
        InitializeComponent();
        AppChrome.Apply(this, "batch", resizable: false);
    }

    public BatchDialog(
        IReadOnlyList<BatchItem> items,
        IReadOnlyList<Category> categories,
        string? lastCategory,
        Action openSettings) : this()
    {
        this.FindControl<Avalonia.Controls.Shapes.Path>("Spade")!.Data = SpadeIcon.Geometry;
        this.FindControl<TextBlock>("TitleText")!.Text =
            $"{items.Count} torrent{(items.Count == 1 ? "" : "s")} found in your Downloads";
        this.FindControl<TextBlock>("SubtitleText")!.Text =
            "Tick the ones to include and choose what happens to each: a category, or delete.";

        var hasCategories = categories.Count > 0;
        var defaultCategory = categories.Any(c => c.Name == lastCategory) ? lastCategory! : NoMove;

        var panel = this.FindControl<StackPanel>("RowsPanel")!;
        foreach (var item in items)
        {
            // A TextBlock, not a string: CheckBox treats "_" in string content as a keyboard-shortcut marker and swallows it.
            var box = new CheckBox
            {
                IsChecked = true,
                MaxWidth = 380,
                Content = new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis },
            };
            box.IsCheckedChanged += (_, _) => UpdateCount();

            var size = new TextBlock
            {
                Text = item.Size is > 0 ? StatsStore.FormatBytes(item.Size.Value) : "",
                Foreground = new SolidColorBrush(Color.Parse("#8A92A6")),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 14, 0),
                MinWidth = 62,
                TextAlignment = TextAlignment.Right,
            };

            var combo = new ComboBox { Width = 190, MinHeight = 32, Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center };
            combo.Items.Add(NoMove);
            foreach (var c in categories)
                combo.Items.Add(c.Name);
            combo.Items.Add(CategoryChoices.DeleteLabel);
            combo.SelectedItem = defaultCategory;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Margin = new Thickness(8, 2),
            };
            row.Children.Add(box);
            Grid.SetColumn(size, 1);
            row.Children.Add(size);
            Grid.SetColumn(combo, 2);
            row.Children.Add(combo);

            panel.Children.Add(row);
            _rows.Add((box, combo, item));
        }

        UpdateCount();

        var setAll = this.FindControl<ComboBox>("SetAllBox")!;
        setAll.Items.Add(NoMove);
        foreach (var c in categories)
            setAll.Items.Add(c.Name);
        setAll.Items.Add(CategoryChoices.DeleteLabel);
        // Applies to every row (not just ticked ones) so a later re-tick doesn't surprise anyone.
        setAll.SelectionChanged += (_, _) =>
        {
            if (setAll.SelectedItem is not string chosen)
                return;
            foreach (var (_, category, _) in _rows)
                if (category is not null)
                    category.SelectedItem = chosen;
        };

        if (!hasCategories)
        {
            this.FindControl<Grid>("CategoryHint")!.IsVisible = true;
            this.FindControl<Button>("OpenSettingsButton")!.Click += (_, _) => openSettings();
        }

        this.FindControl<Button>("SelectAllButton")!.Click += (_, _) => SetAll(true);
        this.FindControl<Button>("SelectNoneButton")!.Click += (_, _) => SetAll(false);

        Wire("TransferDeleteButton", TorrentChoice.TransferAndDelete);
        Wire("TransferButton", TorrentChoice.TransferOnly);
        Wire("IgnoreOnceButton", TorrentChoice.IgnoreOnce);
        Wire("IgnoreForeverButton", TorrentChoice.IgnoreForever);

        Closed += (_, _) => _result.TrySetResult(null);
    }

    public async Task<BatchResult?> ShowBatchAsync()
    {
        Show();
        Activate();
        return await _result.Task;
    }

    private void SetAll(bool on)
    {
        foreach (var (box, _, _) in _rows)
            box.IsChecked = on;
    }

    private void UpdateCount()
    {
        var n = _rows.Count(r => r.Box.IsChecked == true);
        this.FindControl<TextBlock>("CountText")!.Text = $"{n} OF {_rows.Count} SELECTED";
        foreach (var name in new[] { "TransferDeleteButton", "TransferButton", "IgnoreForeverButton" })
            this.FindControl<Button>(name)!.IsEnabled = n > 0;
    }

    private void Wire(string buttonName, TorrentChoice choice)
    {
        this.FindControl<Button>(buttonName)!.Click += (_, _) =>
        {
            if (_answered)
                return;
            _answered = true;

            var selections = _rows
                .Where(r => r.Box.IsChecked == true)
                .Select(r =>
                {
                    var picked = r.Category?.SelectedItem as string;
                    return new BatchSelection(r.Item.Path, picked is null or NoMove ? null : picked);
                })
                .ToList();
            _result.TrySetResult(new BatchResult(choice, selections));
            Close();
        };
    }
}
