using System.Text.Json;

namespace PaxMediaManager.Services;

/// <summary>
/// Matches the user picked by hand ("fix match"). They override whatever the automatic search finds and survive rescans.
/// Keyed by a hash of the folder or file name, so the file doesn't spell out what's in the library.
/// </summary>
public static class LibraryMatches
{
    private static readonly object Gate = new();
    private static Dictionary<string, TmdbMatch>? _overrides;

    private static string FilePath =>
        System.IO.Path.Combine(AppPaths.DataDir, "library-matches.json");

    public static string KeyFor(LibraryItem item) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(item.Name.ToLowerInvariant())))[..24];

    public static TmdbMatch? Get(LibraryItem item)
    {
        lock (Gate)
        {
            Load();
            return _overrides!.TryGetValue(KeyFor(item), out var m) ? m : null;
        }
    }

    public static void Set(LibraryItem item, TmdbMatch match)
    {
        lock (Gate)
        {
            Load();
            _overrides![KeyFor(item)] = match;
            Save();
        }
    }

    public static void Remove(LibraryItem item)
    {
        lock (Gate)
        {
            Load();
            if (_overrides!.Remove(KeyFor(item)))
                Save();
        }
    }

    private static void Load()
    {
        if (_overrides is not null)
            return;
        try
        {
            _overrides = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, TmdbMatch>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            _overrides = new();
        }
    }

    // Caller holds Gate.
    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_overrides, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            /* best effort */
        }
    }
}
