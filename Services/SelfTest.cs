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

        // Updating before start (pxdUpdate): never during a self-test or from a bin\ build.
        Try("update: skipped", () =>
        {
            Check("update: skipped for --self-test", !pxdUpdate.BeforeStart(Program.UpdateRepo, Program.UpdateAsset, ["--self-test", folder]));
            var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
            if (dir.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
                Check("update: skipped for a bin\\ build", !pxdUpdate.BeforeStart(Program.UpdateRepo, Program.UpdateAsset, []));
        });

        // A made-up newer release on a local server, applied to a portable copy and to a copy installed by the setup
        // program (both temp folders): the same zip updates both, and the setup's own files are kept.
        Try("update: fake release", () => UpdateFromFakeRelease(Path.Combine(folder, "update"), Check));

        lines.Add(failed == 0 ? "ALL PASSED" : $"{failed} FAILED");
        File.WriteAllText(report, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
        return failed == 0 ? 0 : 1;
    }

    private static void UpdateFromFakeRelease(string root, Action<string, bool, string> check)
    {
        const string exe = AppChrome.AppId + ".exe";
        var pkg = Path.Combine(root, "pkg", AppChrome.AppId);   // the portable zip has one top folder
        Directory.CreateDirectory(Path.Combine(pkg, "libvlc", "win-x64"));
        File.WriteAllText(Path.Combine(pkg, exe), "new exe");
        File.WriteAllText(Path.Combine(pkg, "app.dll"), "new dll");
        File.WriteAllText(Path.Combine(pkg, "libvlc", "win-x64", "libvlc.dll"), "new vlc");
        var zipName = $"{AppChrome.AppId}-99.0.0-win-x64-portable.zip";
        var zip = Path.Combine(root, zipName);
        System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(root, "pkg"), zip);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();

        string Copy(string name, bool setup)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(dir, "libvlc", "win-x64"));
            File.WriteAllText(Path.Combine(dir, exe), "old exe");
            File.WriteAllText(Path.Combine(dir, "app.dll"), "old dll");
            File.WriteAllText(Path.Combine(dir, "libvlc", "win-x64", "libvlc.dll"), "old vlc");
            if (setup)
                File.WriteAllText(Path.Combine(dir, "uninstall.cmd"), "setup's uninstaller");
            return dir;
        }

        var portable = Copy("portable", setup: false);
        var installed = Copy("installed", setup: true);

        var port = Random.Shared.Next(41000, 49000);
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                byte[] body = ctx.Request.Url!.AbsolutePath switch
                {
                    "/repos/" + Program.UpdateRepo + "/releases/latest" => Encoding.UTF8.GetBytes(
                        $$"""{"tag_name":"v99.0.0","assets":[{"name":"{{AppChrome.AppId}}-99.0.0-win-x64-setup.exe","browser_download_url":"{{prefix}}setup"},{"name":"{{zipName}}","browser_download_url":"{{prefix}}zip"},{"name":"{{zipName}}.sha256","browser_download_url":"{{prefix}}sum"},{"name":"{{AppChrome.AppId}}-99.0.0-linux-x64.tar.gz","browser_download_url":"{{prefix}}tar"}]}"""),
                    "/zip" => File.ReadAllBytes(zip),
                    "/sum" => Encoding.ASCII.GetBytes(hash + "  " + zipName),
                    _ => [],
                };
                ctx.Response.StatusCode = body.Length == 0 ? 404 : 200;
                try { ctx.Response.OutputStream.Write(body); ctx.Response.Close(); } catch { /* client gone */ }
            }
        });

        var before = Environment.GetEnvironmentVariable("PXD_UPDATE_API");
        Environment.SetEnvironmentVariable("PXD_UPDATE_API", prefix.TrimEnd('/'));
        try
        {
            var plan = pxdUpdate.FindUpdateAsync(Program.UpdateRepo, Program.UpdateAsset, pxdUpdate.ThisVersion()).GetAwaiter().GetResult();
            check("update: the portable zip is picked (not the setup or Linux file)", plan is { } p && p.Zip.EndsWith("/zip") && p.Sum is not null, plan?.Zip ?? "none");
            check("update: no update when this copy is current",
                pxdUpdate.FindUpdateAsync(Program.UpdateRepo, Program.UpdateAsset, new Version(99, 0, 0)).GetAwaiter().GetResult() is null, "");
            foreach (var (name, dir) in new[] { ("portable copy", portable), ("setup-installed copy", installed) })
            {
                var ok = pxdUpdate.ApplyAsync(plan!.Value.Zip, plan.Value.Sum, dir, Path.Combine(dir, exe), [], start: false).GetAwaiter().GetResult();
                pxdUpdate.CleanUp(dir);
                check($"update: {name} updated in place", ok && File.ReadAllText(Path.Combine(dir, exe)) == "new exe"
                    && File.ReadAllText(Path.Combine(dir, "app.dll")) == "new dll"
                    && File.ReadAllText(Path.Combine(dir, "libvlc", "win-x64", "libvlc.dll")) == "new vlc"
                    && !Directory.EnumerateFiles(dir, "*.pxdold", SearchOption.AllDirectories).Any(), "");
            }

            check("update: the setup's uninstall.cmd is kept", File.ReadAllText(Path.Combine(installed, "uninstall.cmd")) == "setup's uninstaller", "");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PXD_UPDATE_API", before);
            listener.Stop();
        }
    }
}
