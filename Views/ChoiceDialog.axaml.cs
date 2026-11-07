using Avalonia.Controls;
using PaxMediaManager.Services;

namespace PaxMediaManager.Views;

public partial class ChoiceDialog : Window
{
    private const string NoMove = "Don't move automatically";

    private readonly TaskCompletionSource<ChoiceResult> _result = new();
    private bool _answered;

    public ChoiceDialog()
    {
        InitializeComponent();
        AppChrome.Apply(this, "choice", resizable: false);
    }

    public ChoiceDialog(
        string fileName,
        string from,
        string to,
        IReadOnlyList<Category> categories,
        string? lastCategory,
        Action openSettings) : this()
    {
        this.FindControl<Avalonia.Controls.Shapes.Path>("Spade")!.Data = SpadeIcon.Geometry;
        this.FindControl<TextBlock>("FileNameText")!.Text = fileName;
        this.FindControl<TextBlock>("FromText")!.Text = from;
        this.FindControl<TextBlock>("ToText")!.Text = string.IsNullOrWhiteSpace(to) ? "(no target folder set)" : to;

        var box = this.FindControl<ComboBox>("CategoryBox")!;
        box.Items.Add(NoMove);
        foreach (var c in categories)
            box.Items.Add(c.Name);
        box.Items.Add(CategoryChoices.DeleteLabel);
        // Default to whatever was used last time, since torrents usually come in runs of the same kind.
        box.SelectedItem = categories.Any(c => c.Name == lastCategory) ? lastCategory : NoMove;

        if (categories.Count == 0)
        {
            // Explain why there's nothing to file into instead of leaving an unexplained empty list.
            this.FindControl<Grid>("CategoryHint")!.IsVisible = true;
            this.FindControl<Button>("OpenSettingsButton")!.Click += (_, _) => openSettings();
        }

        Wire("TransferDeleteButton", TorrentChoice.TransferAndDelete);
        Wire("TransferButton", TorrentChoice.TransferOnly);
        Wire("IgnoreOnceButton", TorrentChoice.IgnoreOnce);
        Wire("IgnoreForeverButton", TorrentChoice.IgnoreForever);

        // Closing the window any other way counts as "ignore this time".
        Closed += (_, _) => _result.TrySetResult(new ChoiceResult(TorrentChoice.IgnoreOnce, null));
    }

    public async Task<ChoiceResult> ShowChoiceAsync()
    {
        Show();
        Activate();
        return await _result.Task;
    }

    private void Wire(string buttonName, TorrentChoice choice)
    {
        this.FindControl<Button>(buttonName)!.Click += (_, _) =>
        {
            if (_answered)
                return;
            _answered = true;

            var selected = this.FindControl<ComboBox>("CategoryBox")!.SelectedItem as string;
            var category = selected is null or NoMove ? null : selected;
            _result.TrySetResult(new ChoiceResult(choice, category));
            Close();
        };
    }
}
