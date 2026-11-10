using System.Text;
using pxdStandard;

namespace pxdMediaManager.Services;

/// <summary>
/// "pxdMediaManager --self-test [folder]": checks that run without a window, a tray icon or the real settings. Every
/// file it makes is inside <c>folder</c> (default: a temp folder). Writes report.txt there; the exit code is 0 when
/// every check passed. Run it with PXDMM_DATA_DIR and PXD_SHARED_DIR unset or pointing at test folders.
/// </summary>
public static class SelfTest
{
    public static int Run(string folder)
    {
        folder = Path.GetFullPath(folder);
        var report = Path.Combine(folder, "report.txt");
        folder = Path.Combine(folder, "selftest-data"); // only this sub-folder is cleared and filled
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var lines = new List<string> { $"{AppChrome.AppName} {typeof(SelfTest).Assembly.GetName().Version} self-test, {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        var failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failed++;
            lines.Add($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
        }
        void Try(string name, Action test)
        {
            try { test(); }
            catch (Exception ex) { Check(name, false, ex.GetType().Name + ": " + ex.Message); }
        }

        // Settings folder of the former name → moved; a second run does nothing.
        Try("migration: former settings folder", () =>
        {
            var root = Path.Combine(folder, "appdata-former");
            var former = Path.Combine(root, pxdMigrate.FormerName(AppChrome.AppId));
            Directory.CreateDirectory(Path.Combine(former, "posters"));
            File.WriteAllText(Path.Combine(former, "settings.json"), "{\"TargetFolder\":\"x\"}");
            File.WriteAllText(Path.Combine(former, "posters", "p.jpg"), "poster");
            var first = Migration.DataFolder(root);
            var target = Path.Combine(root, AppChrome.AppId);
            Check("migration: former settings folder moved", first == "moved" && !Directory.Exists(former)
                && File.ReadAllText(Path.Combine(target, "settings.json")).Contains("\"x\"") && File.Exists(Path.Combine(target, "posters", "p.jpg")), first);
            File.WriteAllText(Path.Combine(target, "settings.json"), "{\"TargetFolder\":\"new\"}");
            var second = Migration.DataFolder(root);
            Check("migration: second run is a no-op", second == "none" && File.ReadAllText(Path.Combine(target, "settings.json")).Contains("\"new\""), second);
        });

        // Both folders there (e.g. the former app still installed and run again): the new one wins, nothing is touched.
        Try("migration: both folders", () =>
        {
            var root = Path.Combine(folder, "appdata-both");
            var former = Path.Combine(root, pxdMigrate.FormerName(AppChrome.AppId));
            var target = Path.Combine(root, AppChrome.AppId);
            Directory.CreateDirectory(former);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(former, "settings.json"), "old");
            File.WriteAllText(Path.Combine(target, "settings.json"), "new");
            var r = Migration.DataFolder(root);
            Check("migration: existing new folder is left alone", r == "none" && File.ReadAllText(Path.Combine(target, "settings.json")) == "new"
                && File.Exists(Path.Combine(former, "settings.json")), r);
        });

        // The very first name (Torrent Monitor) is still copied when nothing newer exists.
        Try("migration: Torrent Monitor folder", () =>
        {
            var root = Path.Combine(folder, "appdata-first");
            var first = Path.Combine(root, "TorrentMonitor");
            Directory.CreateDirectory(first);
            File.WriteAllText(Path.Combine(first, "settings.json"), "tm");
            var r = Migration.DataFolder(root);
            Check("migration: Torrent Monitor folder copied", r == "copied" && Directory.Exists(first)
                && File.ReadAllText(Path.Combine(root, AppChrome.AppId, "settings.json")) == "tm", r);
            Check("migration: Torrent Monitor second run is a no-op", Migration.DataFolder(root) == "none");
        });

        Try("migration: nothing to move", () => Check("migration: fresh install does nothing", Migration.DataFolder(Path.Combine(folder, "appdata-empty")) == "none"));

        // Linux/macOS autostart files that start the former program are rewritten once; others are left alone.
        Try("migration: autostart file", () =>
        {
            var desktop = Path.Combine(folder, "autostart", "torrent-monitor.desktop");
            Directory.CreateDirectory(Path.GetDirectoryName(desktop)!);
            var former = pxdMigrate.FormerName(AppChrome.AppId);
            File.WriteAllText(desktop, $"[Desktop Entry]\nType=Application\nExec=/home/u/.local/opt/{former}/{former}\n");
            const string command = "/home/u/.local/opt/pxdMediaManager/pxdMediaManager";
            var first = Autostart.RewriteFormerFile(desktop, command, mac: false);
            var text = File.ReadAllText(desktop);
            Check("migration: Linux autostart entry points at the new program", first && text.Contains("Exec=" + command) && text.Contains("Name=" + AppChrome.AppName)
                && !text.Contains(former));
            Check("migration: Linux autostart second run is a no-op", !Autostart.RewriteFormerFile(desktop, command, mac: false) && File.ReadAllText(desktop) == text);
            Check("migration: no autostart file, nothing created", !Autostart.RewriteFormerFile(Path.Combine(folder, "autostart", "none.desktop"), command, mac: false)
                && !File.Exists(Path.Combine(folder, "autostart", "none.desktop")));
        });

        // Run-entry arguments are kept (the registry itself is never touched by the self-test).
        Try("migration: run arguments", () =>
            Check("migration: Run entry arguments are kept", pxdMigrate.ArgsOf("\"C:\\x\\a.exe\" --show") == "--show" && pxdMigrate.ArgsOf("\"C:\\x\\a.exe\"") == ""));

        // The new names.
        Try("names", () =>
        {
            Check("names: app id / data folder / instance", AppChrome.AppId == "pxdMediaManager" && pxdMigrate.FormerName(AppChrome.AppId) != AppChrome.AppId
                && SingleInstance.Name.StartsWith("pxdMediaManager", StringComparison.Ordinal));
            Check("names: data folder override (PXDMM_DATA_DIR)", Environment.GetEnvironmentVariable("PXDMM_DATA_DIR") is not { Length: > 0 } d || AppPaths.DataDir == d,
                AppPaths.DataDir);
        });

        // The pxd Toolkit link, against a test copy of the shared folder.
        Try("toolkit link", () =>
        {
            var before = Environment.GetEnvironmentVariable("PXD_SHARED_DIR");
            Environment.SetEnvironmentVariable("PXD_SHARED_DIR", Path.Combine(folder, "shared"));
            try
            {
                var shown = pxdLink.TrayIconVisible(AppChrome.AppId);
                pxdLink.SetTrayIconVisible(AppChrome.AppId, false);
                var hidden = !pxdLink.TrayIconVisible(AppChrome.AppId);
                var json = File.ReadAllText(Path.Combine(folder, "shared", "toolkit.json"));
                Check("toolkit link: tray icon on by default, off when set", shown && hidden && json.Contains("\"pxdMediaManager\""));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PXD_SHARED_DIR", before);
            }
        });

        lines.Add(failed == 0 ? "ALL PASSED" : $"{failed} FAILED");
        File.WriteAllText(report, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
        return failed == 0 ? 0 : 1;
    }
}
