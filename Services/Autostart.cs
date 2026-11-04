using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace PaxMediaManager.Services;

public enum AutostartState
{
    Disabled,
    Enabled,
    /// <summary>Something starts a different copy of the app at login (e.g. the old version's scheduled task).</summary>
    Outdated,
}

public sealed record AutostartStatus(AutostartState State, string Message);

/// <summary>Checks and manages "start at login" per platform, always for the current user and without elevation.</summary>
public static class Autostart
{
    private const string AppName = "PaxMediaManager";
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyTaskName = "TorrentMonitor";   // the old app's scheduled task, which really is named that
    private const string OldRunValueName = "TorrentMonitor";  // and its login entry in the registry

    public static AutostartStatus GetStatus()
    {
        try
        {
            var command = LaunchCommand();
            if (command is null)
                return new AutostartStatus(AutostartState.Disabled, "Can't determine how this app was launched.");

            if (OperatingSystem.IsWindows())
                return WindowsStatus(command);
            if (OperatingSystem.IsMacOS())
                return FileStatus(MacPlistPath, command);
            return FileStatus(LinuxDesktopPath, command);
        }
        catch (Exception ex)
        {
            return new AutostartStatus(AutostartState.Disabled, "Could not check: " + ex.Message);
        }
    }

    public static void Enable()
    {
        var command = LaunchCommand() ?? throw new InvalidOperationException("Can't determine how this app was launched.");

        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(WindowsRunKey);
            key.SetValue(AppName, command);
            key.DeleteValue(OldRunValueName, throwOnMissingValue: false);
            RemoveStaleLegacyTask(command);
        }
        else if (OperatingSystem.IsMacOS())
        {
            WriteFile(MacPlistPath, MacPlist(command));
        }
        else
        {
            WriteFile(LinuxDesktopPath, LinuxDesktop(command));
        }
    }

    public static void Disable()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
            key?.DeleteValue(OldRunValueName, throwOnMissingValue: false);
            var command = LaunchCommand();
            if (command is not null && LegacyTaskXml() is { } xml && Mentions(xml, command))
                RunSchtasks($"/Delete /TN \"{LegacyTaskName}\" /F");
        }
        else
        {
            var path = OperatingSystem.IsMacOS() ? MacPlistPath : LinuxDesktopPath;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    // ---- Windows -------------------------------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static AutostartStatus WindowsStatus(string command)
    {
        string? oldRun;
        using (var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey))
        {
            // The app used to be called Torrent Monitor and registered itself under that name.
            oldRun = key?.GetValue(OldRunValueName) as string;
            if (key?.GetValue(AppName) is string existing)
            {
                if (!string.Equals(existing, command, StringComparison.OrdinalIgnoreCase))
                    return new AutostartStatus(AutostartState.Outdated, "A login entry exists but points to a different location.");
                if (oldRun is not null)
                    return new AutostartStatus(AutostartState.Outdated, "Set up, but the previous Torrent Monitor login entry would start a second copy.");

                // Our entry is right, but an old scheduled task would still launch a second (previous) copy at sign-in.
                return LegacyTaskXml() is { } leftover && !Mentions(leftover, command)
                    ? new AutostartStatus(AutostartState.Outdated, "Set up, but an old Task Scheduler entry still starts the previous version too.")
                    : new AutostartStatus(AutostartState.Enabled, "Starts automatically when you sign in.");
            }
        }

        if (oldRun is not null)
            return new AutostartStatus(AutostartState.Outdated, "The previous Torrent Monitor login entry is still set up. Switch it to PAX Media Manager?");

        if (LegacyTaskXml() is { } xml)
        {
            return Mentions(xml, command)
                ? new AutostartStatus(AutostartState.Enabled, "Starts at sign-in via Task Scheduler.")
                : new AutostartStatus(AutostartState.Outdated, "A Task Scheduler entry starts a previous version of the app.");
        }

        return new AutostartStatus(AutostartState.Disabled, "Not set to start when you sign in.");
    }

    /// <summary>The old app registered a scheduled task named "TorrentMonitor"; if it points elsewhere, replace it.</summary>
    private static void RemoveStaleLegacyTask(string command)
    {
        if (LegacyTaskXml() is not { } xml || Mentions(xml, command))
            return;

        var args = $"/Delete /TN \"{LegacyTaskName}\" /F";
        if (RunSchtasks(args).Code == 0)
            return;

        // Tasks created from an elevated prompt can't be removed by a normal process; ask Windows for approval (UAC).
        try
        {
            using var elevated = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            elevated?.WaitForExit(30_000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            /* UAC prompt declined */
        }

        if (LegacyTaskXml() is { } still && !Mentions(still, command))
            throw new InvalidOperationException(
                "Autostart is set, but the old Task Scheduler entry couldn't be removed (it needs administrator approval). Press the button again and approve the Windows prompt.");
    }

    private static string? LegacyTaskXml()
    {
        var (code, output) = RunSchtasks($"/Query /TN \"{LegacyTaskName}\" /XML");
        return code == 0 && output.Length > 0 ? output : null;
    }

    private static bool Mentions(string xml, string command) =>
        xml.Contains(command.Trim('"'), StringComparison.OrdinalIgnoreCase);

    private static (int Code, string Output) RunSchtasks(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        return (p.ExitCode, output);
    }

    // ---- macOS / Linux -------------------------------------------------------------------------------------------

    private static string MacPlistPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "com.torrentmonitor.app.plist");

    private static string LinuxDesktopPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "autostart",
            "torrent-monitor.desktop");

    private static string MacPlist(string command) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>com.torrentmonitor.app</string>
          <key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(command.Trim('"'))}</string></array>
          <key>RunAtLoad</key><true/>
        </dict>
        </plist>
        """;

    private static string LinuxDesktop(string command) =>
        $"[Desktop Entry]\nType=Application\nName=PAX Media Manager\nExec={command}\nX-GNOME-Autostart-enabled=true\nTerminal=false\n";

    private static AutostartStatus FileStatus(string path, string command)
    {
        if (!File.Exists(path))
            return new AutostartStatus(AutostartState.Disabled, "Not set to start when you sign in.");

        return File.ReadAllText(path).Contains(command.Trim('"'), StringComparison.Ordinal)
            ? new AutostartStatus(AutostartState.Enabled, "Starts automatically when you sign in.")
            : new AutostartStatus(AutostartState.Outdated, "A login entry exists but points to a different location.");
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ---- Shared --------------------------------------------------------------------------------------------------

    /// <summary>Quoted command line that relaunches this app (resolving mapped network drives to UNC on Windows).</summary>
    private static string? LaunchCommand()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return null;

        if (OperatingSystem.IsWindows())
            exe = ToUncIfMapped(exe);

        // Running via "dotnet PaxMediaManager.dll" (e.g. dotnet run): register the dll under dotnet instead.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Environment.GetCommandLineArgs().FirstOrDefault();
            return string.IsNullOrEmpty(dll) ? null : $"\"{exe}\" \"{dll}\"";
        }

        return $"\"{exe}\"";
    }

    // Mapped drives (like H:) may not be reconnected yet at sign-in; the UNC path always resolves.
    private static string ToUncIfMapped(string path)
    {
        if (path.Length < 3 || path[1] != ':')
            return path;

        var sb = new StringBuilder(512);
        var len = sb.Capacity;
        return WNetGetConnection(path[..2], sb, ref len) == 0
            ? sb.ToString().TrimEnd('\\') + path[2..]
            : path;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
