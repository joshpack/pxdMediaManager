using System.Text.Json;
using System.Text.RegularExpressions;

namespace pxdMediaManager.Services;

/// <summary>One movie, show, game, album... found at the top level of a category folder.</summary>
public sealed class LibraryItem
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public int? Year { get; init; }
    /// <summary>Extra info pulled from the release name, such as "S01E02".</summary>
    public string Extra { get; init; } = "";
    public required string Path { get; init; }
    /// <summary>Settable: an uncategorised folder can turn out to be a game or an audiobook once its files are looked at.</summary>
    public LibraryKind Kind { get; set; } = LibraryKind.Auto;

    /// <summary>Name of the category this item was found in.</summary>
    public string Category { get; set; } = "";

    /// <summary>From an anime category (or detected as Japanese animation): AniList is asked for a score too.</summary>
    public bool IsAnime { get; set; }

    /// <summary>Full details from TMDB / Steam / Audible once fetched (cached on disk; filled in the background).</summary>
    public TmdbDetails? Details { get; set; }

    /// <summary>Scores from the extra rating sites (MDBList, OMDb...) once fetched.</summary>
    public List<RatingScore> Scores { get; set; } = new();

    /// <summary>File specs of the main video, once read (cached on disk).</summary>
    public MediaSpecs? Specs { get; set; }

    /// <summary>
    /// One number to sort and filter by, out of 10: IMDb when known (most votes), else TMDB / Steam / Audible
    /// (Audible's 5 stars doubled). Null until something is known.
    /// </summary>
    public double? BestRating
    {
        get
        {
            var imdb = Scores.FirstOrDefault(s => s.Source == "imdb");
            if (imdb is not null)
                return imdb.Value;
            if (Details?.Scores?.FirstOrDefault(s => s.Source == "steam") is { } steam)
                return steam.Value / 10;
            return Details is { Votes: > 0 } d ? d.Rating : null;
        }
    }
    public required bool IsDirectory { get; init; }
    public required DateTime Modified { get; init; }
    public long? Size { get; set; }

    /// <summary>Artwork found in the folder itself (poster.jpg and friends).</summary>
    public string? ArtPath { get; set; }

    /// <summary>The TMDB title this item was matched to, automatically or by hand.</summary>
    public TmdbMatch? Match { get; set; }

    /// <summary>True when the user chose the match themselves; it then survives rescans.</summary>
    public bool IsManualMatch { get; set; }

    /// <summary>Downloaded TMDB poster for <see cref="Match"/>.</summary>
    public string? TmdbArtPath { get; set; }

    /// <summary>What the media viewer could do with this item; null until it has been looked at.</summary>
    public bool? HasVideo { get; set; }
    public bool? HasPictures { get; set; }

    /// <summary>For a TV show tile: the episode files / season folders it groups, in season and episode order.</summary>
    public IReadOnlyList<LibraryItem> Members { get; init; } = Array.Empty<LibraryItem>();
    public bool IsGroup => Members.Count > 0;

    /// <summary>Season and episode numbers pulled from <see cref="Extra"/> ("S02E05" → (2, 5), "Season 3" → (3, null)).</summary>
    public (int? Season, int? Episode) EpisodeNumber
    {
        get
        {
            var m = Regex.Match(Extra, @"^S(\d+)E(\d+)$");
            if (m.Success)
                return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            m = Regex.Match(Extra, @"^Season (\d+)$");
            return m.Success ? (int.Parse(m.Groups[1].Value), null) : (null, null);
        }
    }

    /// <summary>The folder to open for this item (for a show grouped from loose files, the folder they sit in).</summary>
    public string FolderPath => IsDirectory && !IsGroup ? Path : System.IO.Path.GetDirectoryName(IsGroup ? Members[0].Path : Path) ?? Path;

    public string DisplayTitle => Match?.Title ?? Title;
    public int? DisplayYear => Match?.Year ?? Year;

    /// <summary>
    /// What the tile shows: folder artwork wins for automatic matches (it is the user's own), but a hand-picked match
    /// shows its TMDB poster, since the user chose it on purpose.
    /// </summary>
    public string? DisplayArt => IsManualMatch ? TmdbArtPath ?? ArtPath : ArtPath ?? TmdbArtPath;
}

/// <summary>Lists what's in a category folder and tidies release names into readable titles.</summary>
public static partial class LibraryScanner
{
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".iso", ".exe", ".msi",
        ".zip", ".rar", ".7z", ".mp3", ".flac", ".m4a", ".wav", ".epub", ".pdf", ".cbz", ".cbr",
    };

    private static readonly string[] ArtNames = { "poster", "folder", "cover", "thumb", "front", "box", "boxart", "art" };
    private static readonly string[] ArtExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

    /// <summary>Quick top-level listing (no recursion), so a big library appears right away.</summary>
    public static List<LibraryItem> List(string folder, LibraryKind kind, CancellationToken ct)
    {
        var items = new List<LibraryItem>();
        var dir = new DirectoryInfo(folder);
        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.Name;
            // Hidden/system and NAS housekeeping folders (e.g. Synology's @eaDir, Windows' $RECYCLE.BIN).
            if (name.StartsWith('.') || name.StartsWith('@') || name.StartsWith('$')
                || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                continue;

            var isDir = entry is DirectoryInfo;
            if (!isDir && !FileExtensions.Contains(entry.Extension))
                continue;

            var (title, year, extra) = Clean(name, isDir);
            items.Add(new LibraryItem
            {
                Name = name,
                Title = title,
                Year = year,
                Extra = extra,
                Path = entry.FullName,
                Kind = kind,
                IsDirectory = isDir,
                Modified = entry.LastWriteTime,
                Size = isDir ? null : ((FileInfo)entry).Length,
            });
        }

        return kind is LibraryKind.Auto or LibraryKind.Tv or LibraryKind.Movie ? GroupEpisodes(items, kind) : items;
    }

    /// <summary>
    /// Folds episodes and season folders of the same show ("Show.S01E01…", "Show.S01E02…", "Show Season 2") into one
    /// tile. In a TV category, a plain "Show" folder joins its loose episodes too; elsewhere only items with a season or
    /// episode marker are grouped, so a film never gets merged with a show of the same name.
    /// </summary>
    private static List<LibraryItem> GroupEpisodes(List<LibraryItem> items, LibraryKind kind)
    {
        var result = new List<LibraryItem>();
        var candidates = items.Where(i => i.Extra.Length > 0 || kind == LibraryKind.Tv).ToList();
        var grouped = new HashSet<LibraryItem>();

        foreach (var g in candidates.GroupBy(i => GroupKey(i.Title)))
        {
            var members = g.ToList();
            if (members.Count < 2 || g.Key.Length == 0 || members.All(m => m.Extra.Length == 0))
                continue;

            members.Sort((a, b) =>
            {
                var (sa, ea) = a.EpisodeNumber;
                var (sb, eb) = b.EpisodeNumber;
                var c = (sa ?? 0).CompareTo(sb ?? 0);
                if (c == 0)
                    c = (ea ?? 0).CompareTo(eb ?? 0);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            // The most common spelling of the title wins ("Show Name" over a stray "Show name").
            var title = members.GroupBy(m => m.Title).OrderByDescending(t => t.Count()).First().Key;
            var first = members[0];
            result.Add(new LibraryItem
            {
                Name = title,
                Title = title,
                Year = members.Select(m => m.Year).FirstOrDefault(y => y is not null),
                Extra = Summarize(members),
                Path = first.Path,
                Kind = kind == LibraryKind.Auto ? LibraryKind.Tv : kind,
                IsDirectory = first.IsDirectory,
                Modified = members.Max(m => m.Modified),
                Size = members.All(m => m.Size is not null) ? members.Sum(m => m.Size!.Value) : null,
                Members = members,
            });
            grouped.UnionWith(members);
        }

        result.AddRange(items.Where(i => !grouped.Contains(i)));
        return result;
    }

    private static string GroupKey(string title) =>
        Regex.Replace(title.ToLowerInvariant().Replace("&", "and"), @"[^a-z0-9]+", "");

    /// <summary>"Seasons 1–3 · 24 episodes", "Season 2 · 5 episodes".</summary>
    private static string Summarize(List<LibraryItem> members)
    {
        var seasons = members.Select(m => m.EpisodeNumber.Season).Where(s => s is not null).Select(s => s!.Value).Distinct().Order().ToList();
        var episodes = members.Count(m => m.EpisodeNumber.Episode is not null);
        var parts = new List<string>();
        if (seasons.Count == 1)
            parts.Add($"Season {seasons[0]}");
        else if (seasons.Count > 1)
            parts.Add(seasons.Count == seasons[^1] - seasons[0] + 1 ? $"Seasons {seasons[0]}–{seasons[^1]}" : $"{seasons.Count} seasons");
        if (episodes > 0)
            parts.Add($"{episodes} episode{(episodes == 1 ? "" : "s")}");
        var folders = members.Count(m => m.EpisodeNumber.Episode is null);
        if (episodes > 0 && folders > 0)
            parts.Add($"{folders} folder{(folders == 1 ? "" : "s")}");
        return parts.Count > 0 ? string.Join(" · ", parts) : $"{members.Count} items";
    }

    /// <summary>Looks for a poster-style image directly inside a folder (top level only, to stay cheap on a NAS).</summary>
    public static string? FindArtwork(string directory)
    {
        try
        {
            string? best = null;
            var bestRank = int.MaxValue;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var ext = System.IO.Path.GetExtension(file);
                if (!ArtExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    continue;

                var stem = System.IO.Path.GetFileNameWithoutExtension(file);
                var rank = Array.FindIndex(ArtNames, n => stem.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (rank < 0)
                    rank = Array.FindIndex(ArtNames, n => stem.EndsWith("-" + n, StringComparison.OrdinalIgnoreCase)
                                                          || stem.EndsWith("_" + n, StringComparison.OrdinalIgnoreCase)) + ArtNames.Length;
                if (rank < ArtNames.Length * 2 && rank >= 0 && rank < bestRank)
                {
                    best = file;
                    bestRank = rank;
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    public static long FolderSize(string directory, CancellationToken ct)
    {
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.System,
                     }))
            {
                ct.ThrowIfCancellationRequested();
                total += f.Length;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            /* partial size is better than none */
        }

        return total;
    }

    // ---- Name cleaning -------------------------------------------------------------------------------------------

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex BracketGroups();

    [GeneratedRegex(@"\bS(\d{1,2})E(\d{1,3})\b|\bSeason[ ]?(\d{1,2})\b|\bS(\d{1,2})\b(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonMarker();

    [GeneratedRegex(
        @"\b(2160p|1080p|1080i|720p|576p|480p|4K|UHD|WEB-?DL|WEB-?Rip|WEB|Blu-?Ray|BDRip|BRRip|DVDRip|DVD|HDTV|HDRip|HDCAM|REMUX|x26[45]|H ?26[45]|HEVC|AVC|AAC|AC3|DDP?[ ]?\d|DTS|FLAC|MP3|REPACK|PROPER|COMPLETE|MULTi|AMZN|NF|HMAX|DSNP|ATVP|EXTENDED|UNRATED|IMAX|" +
        @"v\d+(\.\d+)+|Build[ ]\d+|Update[ ]\d+|FitGirl|DODI|GOG|CODEX|PLAZA|TENOKE|RUNE|SKIDROW|GOLDBERG|FLT|DARKSiDERS|ElAmigos)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex QualityMarker();

    [GeneratedRegex(@"\b(19\d{2}|20\d{2})\b")]
    private static partial Regex YearToken();

    [GeneratedRegex(@"\s*-\s*[A-Za-z0-9]{2,}$")]
    private static partial Regex TrailingGroup();

    /// <summary>"Super.Troopers.3.2026.1080p.AMZN.WEB-DL-KyoGo" → ("Super Troopers 3", 2026, "").</summary>
    public static (string Title, int? Year, string Extra) Clean(string rawName, bool isDirectory)
    {
        var name = isDirectory ? rawName : System.IO.Path.GetFileNameWithoutExtension(rawName);
        var s = BracketGroups().Replace(name, " ");
        // Version numbers must go before dots become spaces, or "v1.2.3" turns into "v1 2 3".
        s = Regex.Replace(s, @"\bv\d+(\.\d+)+", " ", RegexOptions.IgnoreCase);
        s = s.Replace('.', ' ').Replace('_', ' ');
        s = Regex.Replace(s, @"\s+", " ").Trim();

        var cut = s.Length;
        var extra = "";

        var season = SeasonMarker().Match(s);
        if (season.Success)
        {
            cut = Math.Min(cut, season.Index);
            extra = season.Groups[1].Success
                ? $"S{int.Parse(season.Groups[1].Value):00}E{int.Parse(season.Groups[2].Value):00}"
                : $"Season {int.Parse(season.Groups[3].Success ? season.Groups[3].Value : season.Groups[4].Value)}";
        }

        var quality = QualityMarker().Match(s);
        if (quality.Success)
            cut = Math.Min(cut, quality.Index);

        // The release year is the last year-like token before the quality tags; "2012 (2009)" keeps its title.
        int? year = null;
        var yearIndex = -1;
        foreach (Match m in YearToken().Matches(s))
        {
            if (m.Index >= cut || m.Index == 0)
                continue;
            year = int.Parse(m.Value);
            yearIndex = m.Index;
        }

        if (yearIndex > 0)
            cut = Math.Min(cut, yearIndex);

        var title = s[..cut].Trim(' ', '-', '(', '[', ',');
        // Scene-style trailing "-GROUP" when nothing else marked the end of the title.
        if (cut == s.Length && !quality.Success)
            title = TrailingGroup().Replace(title, "");
        if (title.Length == 0)
            title = s.Length > 0 ? s : rawName;

        return (title, year, extra);
    }
}

/// <summary>Remembers folder sizes between runs so big libraries on a NAS only get measured once. Keys are hashes, not names.</summary>
public static class LibrarySizeCache
{
    private static readonly object Gate = new();
    private static Dictionary<string, long>? _cache;

    private static string CacheFile =>
        System.IO.Path.Combine(AppPaths.DataDir, "library-cache.json");

    private static string Key(LibraryItem item) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{item.Path}|{item.Modified.Ticks}")))[..24];

    public static long? TryGet(LibraryItem item)
    {
        lock (Gate)
        {
            Load();
            return _cache!.TryGetValue(Key(item), out var size) ? size : null;
        }
    }

    public static void Set(LibraryItem item, long size)
    {
        lock (Gate)
        {
            Load();
            _cache![Key(item)] = size;
        }
    }

    public static void Flush()
    {
        lock (Gate)
        {
            if (_cache is null)
                return;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CacheFile)!);
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(_cache));
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private static void Load()
    {
        if (_cache is not null)
            return;
        try
        {
            _cache = File.Exists(CacheFile)
                ? JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(CacheFile)) ?? new()
                : new();
        }
        catch
        {
            _cache = new();
        }
    }
}
