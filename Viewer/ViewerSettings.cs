using System.Text.Json;

namespace pxdMediaManager.Viewer;

/// <summary>Viewer preferences, remembered between runs in the per-user app-data folder.</summary>
public sealed class ViewerSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Seconds each picture stays up during a slideshow.</summary>
    public double SlideshowSeconds { get; set; } = 4;

    public bool Loop { get; set; } = true;
    public bool Shuffle { get; set; }

    /// <summary>0–100.</summary>
    public int Volume { get; set; } = 80;
    public bool Muted { get; set; }

    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 760;

    /// <summary>One instance for the whole app, so several viewer windows agree on volume, slideshow speed and so on.</summary>
    public static ViewerSettings Shared { get; } = Load();

    private static string FilePath =>
        System.IO.Path.Combine(pxdMediaManager.Services.AppPaths.DataDir, "viewer.json");

    public static ViewerSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            /* fall back to defaults */
        }

        return new ViewerSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            /* best effort */
        }
    }
}
