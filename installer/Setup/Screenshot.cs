using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace pxdMediaManager.Setup;

/// <summary>
/// Off-screen drawing, so nothing pops up on the desktop: "--screenshot setup.png" draws the setup window into a
/// picture; "--screenshot spade.ico" writes the app's icon (the spade tile at 16 to 256 pixels) for the exe files.
/// </summary>
public static class Screenshot
{
    public static int Render(string path)
    {
        AppBuilder.Configure<SetupApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            WriteIcon(path, [256, 64, 48, 32, 24, 16]);
            return 0;
        }
        var window = new SetupWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(path);
        window.Close();
        return 0;
    }

    // An .ico with a PNG picture per size (what Windows has read since Vista).
    private static void WriteIcon(string path, int[] sizes)
    {
        var images = sizes.Select(s =>
        {
            using var ms = new MemoryStream();
            Brand.Bitmap(s).Save(ms);
            return (Size: s, Png: ms.ToArray());
        }).ToList();
        using var f = File.Create(path);
        using var w = new BinaryWriter(f);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write((uint)png.Length);
            w.Write((uint)offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }
}
