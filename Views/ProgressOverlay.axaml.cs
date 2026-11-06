using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;
using PaxMediaManager.Services;

namespace PaxMediaManager.Views;

/// <summary>Small borderless always-on-top card at the top-center of the screen that shows a move in progress.</summary>
public partial class ProgressOverlay : Window
{
    private readonly DispatcherTimer _hideTimer = new();

    public ProgressOverlay()
    {
        InitializeComponent();
        this.FindControl<Path>("Spade")!.Data = SpadeIcon.Geometry;
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
        // Don't let a stray click steal focus or drag anything; it's purely informational.
        IsHitTestVisible = false;
    }

    public void Begin(string category, string name, long total)
    {
        _hideTimer.Stop();
        this.FindControl<TextBlock>("TitleText")!.Text = $"Moving to {category}";
        this.FindControl<TextBlock>("NameText")!.Text = name;
        this.FindControl<TextBlock>("DetailText")!.Text = $"0 B of {StatsStore.FormatBytes(total)}";
        SetPercent(0);
        ShowAtTopCenter();
    }

    public void Update(MoveProgress p)
    {
        var pct = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
        SetPercent(pct);
        var speed = p.BytesPerSecond > 0 ? $"  ·  {StatsStore.FormatBytes((long)p.BytesPerSecond)}/s" : "";
        var eta = "";
        if (p.BytesPerSecond > 1 && p.Total > p.Done)
        {
            var left = TimeSpan.FromSeconds((p.Total - p.Done) / p.BytesPerSecond);
            eta = left.TotalHours >= 1 ? $"  ·  {left:h\\:mm\\:ss} left" : $"  ·  {left:m\\:ss} left";
        }

        this.FindControl<TextBlock>("DetailText")!.Text =
            $"{StatsStore.FormatBytes(p.Done)} of {StatsStore.FormatBytes(p.Total)}{speed}{eta}";
    }

    public void Finish(bool success, string message)
    {
        this.FindControl<TextBlock>("TitleText")!.Text = success ? "Move complete" : "Move didn't finish";
        this.FindControl<TextBlock>("DetailText")!.Text = message;
        if (success)
            SetPercent(100);

        _hideTimer.Interval = TimeSpan.FromSeconds(success ? 3 : 6);
        _hideTimer.Start();
    }

    private void SetPercent(double pct)
    {
        this.FindControl<ProgressBar>("Bar")!.Value = pct;
        this.FindControl<TextBlock>("PercentText")!.Text = $"{pct:0}%";
    }

    private void ShowAtTopCenter()
    {
        if (!IsVisible)
            Show();

        // Re-measure after showing so the position uses the real height and the screen's DPI scaling.
        var screen = Screens.Primary;
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var widthPx = (int)(Width * scale);
        Position = new PixelPoint(area.X + (area.Width - widthPx) / 2, area.Y + (int)(8 * scale));
    }
}
