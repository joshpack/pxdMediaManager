using Avalonia;
using Avalonia.Controls;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PaxMediaManager;

/// <summary>The app's spade mark, drawn from vector geometry so it stays crisp at every size.</summary>
public static class SpadeIcon
{
    /// <summary>Spade outline in a 100x100 box.</summary>
    public const string PathData =
        "M50,5 C50,5 11,34 11,59 C11,75 23,83 35,83 C41,83 46,80.5 48.5,76.5 " +
        "C48,86 45,92 37,96 L63,96 C55,92 52,86 51.5,76.5 C54,80.5 59,83 65,83 " +
        "C77,83 89,75 89,59 C89,34 50,5 50,5 Z";

    public static readonly Geometry Geometry = Geometry.Parse(PathData);

    /// <summary>Renders the app icon (spade on a rounded accent tile) to a bitmap for the tray and window icon.</summary>
    public static WindowIcon CreateWindowIcon(int size = 128)
    {
        var tile = new Grid { Width = size, Height = size };
        tile.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size * 0.22),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#00C8FF"), 0),
                    new GradientStop(Color.Parse("#19F58C"), 1),
                },
            },
        });
        tile.Children.Add(new Viewbox
        {
            Margin = new Thickness(size * 0.2),
            Child = new ShapePath
            {
                Data = Geometry,
                Fill = new SolidColorBrush(Color.Parse("#04140C")),
                Width = 100,
                Height = 100,
                Stretch = Stretch.Uniform,
            },
        });

        tile.Measure(new Size(size, size));
        tile.Arrange(new Rect(0, 0, size, size));

        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(tile);
        return new WindowIcon(bitmap);
    }
}
