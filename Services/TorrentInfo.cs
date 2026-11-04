using System.Text;

namespace PaxMediaManager.Services;

/// <summary>Reads the total content size out of a .torrent file (a bencoded dictionary) without any torrent library.</summary>
public static class TorrentInfo
{
    private const long MaxFileBytes = 32L * 1024 * 1024;
    private const int MaxDepth = 32;

    public sealed record Meta(string? Name, long? Size);

    /// <summary>Total size of all files described by the torrent, or null if the file can't be parsed.</summary>
    public static long? TryGetTotalSize(string path) => TryRead(path)?.Size;

    /// <summary>Reads the torrent's content name (its top-level file or folder) and total size.</summary>
    public static Meta? TryRead(string path)
    {
        try
        {
            if (new FileInfo(path).Length is 0 or > MaxFileBytes)
                return null;

            byte[] data;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                data = new byte[fs.Length];
                fs.ReadExactly(data);
            }

            var pos = 0;
            if (Parse(data, ref pos, 0) is not Dictionary<string, object> root
                || !root.TryGetValue("info", out var infoObj)
                || infoObj is not Dictionary<string, object> info)
                return null;

            string? name = info.TryGetValue("name", out var nameObj) && nameObj is byte[] nb && nb.Length > 0
                ? Encoding.UTF8.GetString(nb)
                : null;

            // Single-file torrent.
            if (info.TryGetValue("length", out var len) && len is long single)
                return new Meta(name, single);

            // Multi-file torrent: sum every file entry.
            if (info.TryGetValue("files", out var filesObj) && filesObj is List<object> files)
            {
                long total = 0;
                foreach (var f in files)
                {
                    if (f is Dictionary<string, object> d && d.TryGetValue("length", out var l) && l is long n)
                        total += n;
                }

                return new Meta(name, total);
            }
        }
        catch
        {
            /* unreadable or malformed — no size */
        }

        return null;
    }

    private static object? Parse(byte[] d, ref int p, int depth)
    {
        if (depth > MaxDepth || p >= d.Length)
            throw new FormatException();

        switch ((char)d[p])
        {
            case 'i':
            {
                p++;
                var start = p;
                while (d[p] != 'e')
                    p++;
                var value = long.Parse(Encoding.ASCII.GetString(d, start, p - start));
                p++;
                return value;
            }
            case 'l':
            {
                p++;
                var list = new List<object>();
                while (d[p] != 'e')
                    list.Add(Parse(d, ref p, depth + 1) ?? Array.Empty<byte>());
                p++;
                return list;
            }
            case 'd':
            {
                p++;
                var dict = new Dictionary<string, object>();
                while (d[p] != 'e')
                {
                    var key = Parse(d, ref p, depth + 1) as byte[] ?? throw new FormatException();
                    dict[Encoding.UTF8.GetString(key)] = Parse(d, ref p, depth + 1) ?? Array.Empty<byte>();
                }

                p++;
                return dict;
            }
            default:
            {
                var start = p;
                while (d[p] != ':')
                    p++;
                var length = int.Parse(Encoding.ASCII.GetString(d, start, p - start));
                p++;
                if (length < 0 || p + length > d.Length)
                    throw new FormatException();
                // Only dictionary keys are ever read back, so large binary values (piece hashes) are skipped, not copied.
                var bytes = length <= 256 ? d.AsSpan(p, length).ToArray() : Array.Empty<byte>();
                p += length;
                return bytes;
            }
        }
    }
}
