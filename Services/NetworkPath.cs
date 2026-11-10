using System.Diagnostics;

namespace pxdMediaManager.Services;

/// <summary>
/// Lets a folder setting point at a network share by URL (smb://host/share/folder) on Linux. A share has no fixed
/// path there: the desktop mounts it on demand under a per-session directory whose name changes between logins, so
/// the URL is what gets saved and it is turned into a real path each time it is needed.
/// </summary>
public static class NetworkPath
{
    private const string Scheme = "smb://";
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(15);
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static bool IsUrl(string? path) =>
        path is not null && path.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The local path for a folder setting. Ordinary paths come back unchanged; share URLs are mounted through the
    /// desktop (null if that fails). May block while a share connects, so keep it off the UI thread.
    /// </summary>
    public static string? ToLocal(string? path)
    {
        if (!IsUrl(path))
            return path;

        lock (Cache)
        {
            if (Cache.TryGetValue(path!, out var known) && Directory.Exists(known))
                return known;
        }

        var local = MountWithKioFuse(path!) ?? MountWithGvfs(path!);
        if (local is null || !Directory.Exists(local))
            return null;

        lock (Cache)
            Cache[path!] = local;
        return local;
    }

    /// <summary>True when the folder (path or share URL) is set and reachable right now. May block like <see cref="ToLocal"/>.</summary>
    public static bool Exists(string? path) =>
        !string.IsNullOrWhiteSpace(path) && ToLocal(path) is { } local && Directory.Exists(local);

    /// <summary>
    /// Converts a path from inside a desktop share mount (what the folder picker returns for a network location)
    /// back into its share URL, so the saved setting survives the next login. Other paths come back unchanged.
    /// </summary>
    public static string ToStored(string path)
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")?.TrimEnd('/');
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(runtimeDir) || !path.StartsWith(runtimeDir + "/", StringComparison.Ordinal))
            return path;

        var parts = path[(runtimeDir.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);

        // KDE: <runtime>/kio-fuse-XXXXXX/smb/user@host/share/folder
        if (parts.Length >= 4 && parts[0].StartsWith("kio-fuse-", StringComparison.Ordinal) && parts[1] == "smb")
            return Scheme + string.Join('/', parts[2..]);

        // GNOME and others: <runtime>/gvfs/smb-share:server=host,share=name,user=me/folder
        if (parts.Length >= 2 && parts[0] == "gvfs" && parts[1].StartsWith("smb-share:", StringComparison.Ordinal))
        {
            var fields = parts[1]["smb-share:".Length..].Split(',')
                .Select(f => f.Split('=', 2))
                .Where(f => f.Length == 2)
                .ToDictionary(f => f[0], f => f[1], StringComparer.OrdinalIgnoreCase);
            if (fields.TryGetValue("server", out var server) && fields.TryGetValue("share", out var share))
            {
                var user = fields.TryGetValue("user", out var u) ? u + "@" : "";
                return Scheme + user + string.Join('/', [server, share, .. parts[2..]]);
            }
        }

        return path;
    }

    /// <summary>KDE: kio-fuse mounts the URL and replies with the local path.</summary>
    private static string? MountWithKioFuse(string url)
    {
        // Reply looks like: ('/run/user/1000/kio-fuse-XXXXXX/smb/user@host/share',)
        var reply = Run(
            "gdbus",
            "call", "--session",
            "--dest", "org.kde.KIOFuse",
            "--object-path", "/org/kde/KIOFuse",
            "--method", "org.kde.KIOFuse.VFS.mountUrl",
            url)?.Trim();

        if (reply is null || reply.Length < 6 || !reply.StartsWith('(') || !reply.EndsWith(",)"))
            return null;

        var quote = reply[1];
        var end = reply.LastIndexOf(quote);
        if (quote is not ('\'' or '"') || end <= 2)
            return null;

        // gdbus prints the string as a GVariant literal, which backslash-escapes quotes and backslashes.
        return reply[2..end].Replace("\\" + quote, quote.ToString()).Replace(@"\\", @"\");
    }

    /// <summary>GNOME and other gvfs desktops: mount with gio, then find the share under the gvfs FUSE directory.</summary>
    private static string? MountWithGvfs(string url)
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtimeDir) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var segments = Uri.UnescapeDataString(uri.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        // Fails harmlessly when the share is already mounted.
        Run("gio", "mount", uri.AbsoluteUri);

        var gvfsRoot = Path.Combine(runtimeDir, "gvfs");
        if (!Directory.Exists(gvfsRoot))
            return null;

        foreach (var mount in Directory.GetDirectories(gvfsRoot, "smb-share:*"))
        {
            var fields = Path.GetFileName(mount)["smb-share:".Length..].Split(',');
            if (fields.Contains("server=" + uri.Host, StringComparer.OrdinalIgnoreCase)
                && fields.Contains("share=" + segments[0], StringComparer.OrdinalIgnoreCase))
                return Path.Combine([mount, .. segments[1..]]);
        }

        return null;
    }

    /// <summary>Runs a desktop helper and returns its output, or null if it is missing, fails, or takes too long.</summary>
    private static string? Run(string fileName, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in arguments)
                info.ArgumentList.Add(a);

            using var p = Process.Start(info);
            if (p is null)
                return null;

            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit((int)MountTimeout.TotalMilliseconds))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    /* already gone */
                }

                return null;
            }

            return p.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch
        {
            return null;
        }
    }
}
