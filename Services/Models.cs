namespace PaxMediaManager.Services;

public enum TorrentChoice
{
    TransferAndDelete,
    TransferOnly,
    IgnoreOnce,
    IgnoreForever,
}

public enum ActivityKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Special entries in the category dropdowns that aren't destinations.</summary>
public static class CategoryChoices
{
    /// <summary>Remove the .torrent file instead of sending it anywhere.</summary>
    public const string DeleteLabel = "Delete torrent file";
}

/// <summary>A named destination for completed downloads, e.g. "Movies" → \\NAS\Movies.</summary>
public sealed class Category
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>Hide this category from the Library tab (it still receives finished downloads).</summary>
    public bool ExcludeFromLibrary { get; set; }
}

/// <summary>What the user picked in the new-torrent dialog: the action plus an optional category for the finished data.</summary>
public sealed record ChoiceResult(TorrentChoice Choice, string? CategoryName);

/// <summary>A .torrent file already sitting in the watch folder when the app starts.</summary>
public sealed record BatchItem(string Path, string Name, long? Size);

/// <summary>One ticked torrent in the batch dialog and the category chosen for it (null = leave the download where it lands).</summary>
public sealed record BatchSelection(string Path, string? CategoryName);

/// <summary>The user's decision for a group of existing torrents: one action for all ticked files, a category for each.</summary>
public sealed record BatchResult(TorrentChoice Choice, IReadOnlyList<BatchSelection> Items);

/// <summary>A remembered window size (client area, in device-independent pixels).</summary>
public sealed class TabSize
{
    public double Width { get; set; }
    public double Height { get; set; }
}
