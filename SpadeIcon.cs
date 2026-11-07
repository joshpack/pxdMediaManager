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

    /// <summary>The app icon for the tray and windows (see <see cref="BrandIcon"/>).</summary>
    public static WindowIcon CreateWindowIcon(int size = 128) => BrandIcon.Window(size);
}
