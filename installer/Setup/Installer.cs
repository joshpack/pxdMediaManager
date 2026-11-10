using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;
using pxdStandard;

namespace pxdMediaManager.Setup;

/// <summary>
/// Installs pxd Media Manager for this Windows user: the app folder (from the payload inside this program) into
/// %LOCALAPPDATA%\Programs\pxd Media Manager, Start menu (and optionally desktop) shortcuts, and an Apps &amp; features
/// entry whose Uninstall runs uninstall.cmd in that folder. Per user, so no administrator rights; self-contained, so
/// the PC needs no .NET. Settings (%APPDATA%\pxdMediaManager) are never touched: they survive updates and uninstalls.
/// </summary>
public static class Installer
{
    public const string AppId = "pxdMediaManager";
    public const string AppName = "pxd Media Manager";
    private const string ExeName = AppId + ".exe";
    private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
    private const string UninstallKey = UninstallRoot + AppId;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // ---- Migration only: what the setup program of the app's former name registered (see pxdMigrate). ----
    private static string FormerId => pxdMigrate.FormerName(AppId);
    private static string FormerExeName => FormerId + ".exe";
    private static string FormerUninstallKey => UninstallRoot + FormerId;
    /// <summary>The former display name: shortcut names and the default install folder used it.</summary>
    private static string FormerAppName => pxdMigrate.FormerPrefix.ToUpperInvariant() + " Media Manager";
    private static string FormerDefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", FormerAppName);
    private static string FormerStartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), FormerAppName + ".lnk");
    private static string FormerDesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), FormerAppName + ".lnk");

    public static string Version =>
        typeof(Installer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    /// <summary>Where it's installed already, and which version (from its Apps &amp; features entry).</summary>
    public static (string? Folder, string? Version) Installed
    {
        get
        {
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                var folder = key?.GetValue("InstallLocation") as string;
                if (folder is { Length: > 0 } && File.Exists(Path.Combine(folder, ExeName)))
                    return (folder, key?.GetValue("DisplayVersion") as string);
            }

            // Migration: a copy installed under the former name is updated where it is, except that its default
            // folder (named after the former name) becomes the new default folder.
            var former = FormerInstall();
            return former.Folder is null ? (null, null) : (NewFolderFor(former.Folder), former.Version);
        }
    }

    /// <summary>Migration only: the install the former setup program registered, if it's still there.</summary>
    private static (string? Folder, string? Version) FormerInstall()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(FormerUninstallKey);
            var folder = key?.GetValue("InstallLocation") as string;
            return folder is { Length: > 0 } && File.Exists(Path.Combine(folder, FormerExeName))
                ? (folder, key?.GetValue("DisplayVersion") as string)
                : (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Migration only: the former default install folder maps to the new default folder; any other folder stays.</summary>
    private static string NewFolderFor(string formerFolder) =>
        SameFolder(formerFolder, FormerDefaultFolder) ? DefaultFolder : formerFolder;

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool HasPayload => typeof(Installer).Assembly.GetManifestResourceInfo("payload.zip") is not null;

    private static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    private static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    public static async Task<string> InstallAsync(string folder, bool desktop, Action<string, double> progress)
    {
        folder = Path.GetFullPath(folder);
        var exe = Path.Combine(folder, ExeName);
        // Migration: an install made under the former name, and whether it had a desktop shortcut (kept if so).
        var formerFolder = FormerInstall().Folder;
        desktop |= File.Exists(FormerDesktopShortcut);
        await UnpackAsync(folder, progress);
        if (formerFolder is not null && !SameFolder(formerFolder, folder))
            await CloseRunningAsync(formerFolder);

        progress("Adding shortcuts…", 1);
        CreateShortcut(StartMenuShortcut, exe, folder);
        if (desktop) CreateShortcut(DesktopShortcut, exe, folder);
        else TryDelete(DesktopShortcut);

        progress("Registering…", 1);
        File.WriteAllText(Path.Combine(folder, "uninstall.cmd"), UninstallScript(folder), new UTF8Encoding(false));
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "pxd");
            key.SetValue("DisplayIcon", exe + ",0");
            key.SetValue("InstallLocation", folder);
            key.SetValue("UninstallString", $"cmd.exe /c \"\"{Path.Combine(folder, "uninstall.cmd")}\"\"");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(FolderSize(folder) / 1024), RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
        MigrateFormer(formerFolder, exe);
        // A "start at login" entry from an earlier copy keeps pointing at the right program (with its arguments).
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            if (run?.GetValue(AppId) is string current)
            {
                var args = pxdMigrate.ArgsOf(current);
                run.SetValue(AppId, $"\"{exe}\"" + (args.Length > 0 ? " " + args : ""));
            }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // refresh icons
        progress("Done", 1);
        return exe;
    }

    /// <summary>The app's files into the folder (a running copy from there is closed first), and nothing else.</summary>
    public static async Task UnpackAsync(string folder, Action<string, double> progress)
    {
        folder = Path.GetFullPath(folder);
        if (!HasPayload) throw new InvalidOperationException("This setup program was built without pxd Media Manager inside it.");

        progress("Closing pxd Media Manager if it's running…", 0);
        await CloseRunningAsync(folder);

        // Unpacked beside the old copy, then swapped in: a half-finished install never replaces a working one.
        var fresh = folder + ".new";
        var old = folder + ".old";
        TryDeleteFolder(fresh);
        TryDeleteFolder(old);
        await Task.Run(() =>
        {
            using var zip = new ZipArchive(typeof(Installer).Assembly.GetManifestResourceStream("payload.zip")!, ZipArchiveMode.Read);
            var total = zip.Entries.Sum(e => e.Length);
            long done = 0;
            foreach (var entry in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(fresh, entry.FullName));
                // nothing in the payload may land outside the install folder
                if (!target.StartsWith(Path.GetFullPath(fresh) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                done += entry.Length;
                progress("Copying files…", total == 0 ? 1 : (double)done / total);
            }
        });
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        if (Directory.Exists(folder)) Directory.Move(folder, old);
        Directory.Move(fresh, folder);
        TryDeleteFolder(old);
    }

    /// <summary>
    /// What Apps &amp; features runs to uninstall: closes the app, removes its shortcuts, entries and folder. Settings and
    /// the library's caches in %APPDATA%\pxdMediaManager stay, in case it's installed again.
    /// </summary>
    private static string UninstallScript(string folder) => $"""
        @echo off
        rem pxd Media Manager: uninstall (made by its setup program). Settings in %APPDATA%\pxdMediaManager are kept.
        taskkill /f /fi "IMAGENAME eq {ExeName}" /fi "USERNAME eq %USERNAME%" >nul 2>&1
        del "{StartMenuShortcut}" >nul 2>&1
        del "{DesktopShortcut}" >nul 2>&1
        reg delete "HKCU\{UninstallKey}" /f >nul 2>&1
        reg delete "HKCU\{RunKey}" /v pxdMediaManager /f >nul 2>&1
        cd /d "%TEMP%"
        start "" /min cmd /c "ping 127.0.0.1 -n 3 >nul & rmdir /s /q "{folder}""
        """;

    /// <summary>
    /// Migration only, after the new copy is installed and registered: removes what the former name's setup program
    /// registered (its Apps &amp; features entry, its Start menu and desktop shortcuts and, when the app now lives in a
    /// different folder, the former folder) and moves its "start at login" entry to the new name and program. Safe to
    /// repeat: once those are gone there is nothing left to do.
    /// </summary>
    private static void MigrateFormer(string? formerFolder, string exe)
    {
        TryDelete(FormerStartMenuShortcut);
        TryDelete(FormerDesktopShortcut);
        try { Registry.CurrentUser.DeleteSubKeyTree(FormerUninstallKey, throwOnMissingSubKey: false); } catch { }
        pxdMigrate.RunEntry(FormerId, AppId, exe);
        if (formerFolder is not null && !SameFolder(formerFolder, Path.GetDirectoryName(exe)!))
            TryDeleteFolder(formerFolder);
    }

    private static async Task CloseRunningAsync(string folder)
    {
        // Migration: a copy of the former name's program in the folder is closed too.
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)).Concat(Process.GetProcessesByName(FormerId)))
        {
            try
            {
                if (p.MainModule?.FileName is not { } file || !file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                p.CloseMainWindow();
                await Task.Run(() => p.WaitForExit(8000));
                if (!p.HasExited) p.Kill();
                await Task.Run(() => p.WaitForExit(5000));
            }
            catch { } // not ours to close (another user, access denied)
        }
    }

    private static long FolderSize(string folder) =>
        new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static void TryDeleteFolder(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

    // ---- Shortcuts (IShellLink) ----

    private static void CreateShortcut(string path, string target, string workingFolder)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(workingFolder);
        link.SetIconLocation(target, 0);
        link.SetDescription("Torrents, your media library and a built-in video and picture viewer");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ((IPersistFile)link).Save(path, true);
        Marshal.ReleaseComObject(link);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
