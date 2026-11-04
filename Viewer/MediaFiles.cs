using System.Globalization;

namespace PaxMediaManager.Viewer;

public enum MediaMode
{
    None,
    Image,
    Video,
}

/// <summary>What kinds of files the viewer opens, and how a file or folder turns into a playlist.</summary>
public static class MediaFiles
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".ico",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".m2ts", ".webm", ".flv", ".mpg", ".mpeg", ".ogv", ".3gp", ".vob",
    };

    private const int MaxPlaylist = 20000;

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Does this file or folder contain something the viewer can play or show? Looks a few folders deep and stops as soon
    /// as it has the answer, so it stays cheap on a network share.
    /// </summary>
    public static (bool HasVideo, bool HasPictures) Probe(string path, bool isDirectory)
    {
        if (!isDirectory)
            return (IsVideo(path), IsImage(path));

        bool video = false, pictures = false;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
            var seen = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                video |= IsVideo(file);
                pictures |= IsImage(file);
                if ((video && pictures) || ++seen > 4000)
                    break;
            }
        }
        catch
        {
            /* unreadable: report what was found */
        }

        return (video, pictures);
    }

    /// <summary>File-name order the way people expect it: "img2" before "img10".</summary>
    public static readonly IComparer<string> Natural = Comparer<string>.Create((a, b) =>
        string.Compare(a, b, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering));

    /// <summary>
    /// Turns what the user opened into a playlist and the index to start at. A file plays within its folder; a folder
    /// of videos opens its main film (the largest file), and a folder of pictures opens them all, subfolders included.
    /// </summary>
    public static (MediaMode Mode, List<string> Items, int Start) Resolve(string path)
    {
        if (File.Exists(path))
        {
            var mode = IsVideo(path) ? MediaMode.Video : IsImage(path) ? MediaMode.Image : MediaMode.None;
            if (mode == MediaMode.None)
                return (MediaMode.None, new(), 0);

            var folder = Path.GetDirectoryName(path)!;
            var items = Enumerate(folder, mode == MediaMode.Video ? IsVideo : IsImage, recursive: false);
            var index = items.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                items.Insert(0, path);
                index = 0;
            }

            return (mode, items, index);
        }

        if (!Directory.Exists(path))
            return (MediaMode.None, new(), 0);

        var videos = Enumerate(path, IsVideo, recursive: true, maxDepth: 2);
        if (videos.Count > 0)
        {
            // The main film is the biggest file; "sample" clips and extras are smaller.
            var main = videos.OrderByDescending(v => SafeLength(v)).First();
            videos.Sort(Natural);
            return (MediaMode.Video, videos, Math.Max(0, videos.IndexOf(main)));
        }

        var pictures = Enumerate(path, IsImage, recursive: true, maxDepth: 6);
        return pictures.Count > 0 ? (MediaMode.Image, pictures, 0) : (MediaMode.None, new(), 0);
    }

    private static List<string> Enumerate(string folder, Func<string, bool> accept, bool recursive, int maxDepth = 6)
    {
        var results = new List<string>();
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                MaxRecursionDepth = maxDepth,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                if (accept(file))
                    results.Add(file);
                if (results.Count >= MaxPlaylist)
                    break;
            }
        }
        catch
        {
            /* unreadable folder: return what we have */
        }

        results.Sort(Natural);
        return results;
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}
