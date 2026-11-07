using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using PaxStandard;

namespace PaxMediaManager.Services;

/// <summary>
/// One copy of the app per user. Starting the exe again (from a shortcut, or from PAX Toolkit) asks the running copy to
/// open its window, which matters most when its tray icon is hidden.
/// </summary>
public static class SingleInstance
{
    /// <summary>
    /// The instance name. A different data folder (PAXMM_DATA_DIR, used by tests) is a separate instance, so a test copy
    /// never talks to, or is blocked by, the copy that is really in use.
    /// </summary>
    public static string Name
    {
        get
        {
            var name = "PaxMediaManager";
            if (Environment.GetEnvironmentVariable("PAXMM_DATA_DIR") is { Length: > 0 } dir)
                name += "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dir).ToLowerInvariant())))[..12];
            return name;
        }
    }

    private static string PipeName => Name + ".show." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..8];

    /// <summary>Listens (on a background thread) for later starts of the exe; <paramref name="show"/> is called for each one.</summary>
    public static void Listen(Action show)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    server.ReadByte();
                    show();
                }
                catch
                {
                    Thread.Sleep(500);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Single instance",
        };
        thread.Start();
    }

    /// <summary>Asks the running copy to open its window. False if it didn't answer.</summary>
    public static bool SignalRunning(int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            client.WriteByte(1);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// The tray icon's visibility as PAX Toolkit and the app's own Settings set it (%APPDATA%\PAX\toolkit.json →
/// apps.PaxMediaManager.trayIcon). Calls <c>apply</c> with the current state at start and whenever the file changes.
/// </summary>
public sealed class TrayLink : IDisposable
{
    private readonly Action<bool> _apply;
    private readonly IDisposable? _watch;

    /// <param name="apply">Called with true (show) or false (hide); after start-up it is called on a worker thread.</param>
    public TrayLink(Action<bool> apply)
    {
        _apply = apply;
        apply(Visible);
        try
        {
            _watch = PaxLink.Watch(() => _apply(Visible));
        }
        catch
        {
            /* no shared folder: the icon just stays as it is */
        }
    }

    public static bool Visible => PaxLink.TrayIconVisible(AppChrome.AppId);

    /// <summary>Saves the choice for PAX Toolkit too; the file watcher then applies it.</summary>
    public void Set(bool visible)
    {
        PaxLink.SetTrayIconVisible(AppChrome.AppId, visible);
        _apply(visible);
    }

    public void Dispose() => _watch?.Dispose();
}
