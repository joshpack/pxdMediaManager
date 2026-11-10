using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

namespace pxdMediaManager.Viewer;

/// <summary>
/// Thin wrapper over LibVLC that renders into a <see cref="WriteableBitmap"/> through software video callbacks, so the
/// video is an ordinary Avalonia image (controls can sit on top of it, and it behaves the same on every OS).
/// All events are raised on the UI thread.
/// </summary>
public sealed class VideoPlayer : IDisposable
{
    private static bool _coreReady;

    private static LibVLC? _sharedVlc;

    private readonly LibVLC _vlc;
    private readonly MediaPlayer _player;
    private readonly SemaphoreSlim _frameGate = new(1, 1);

    // Callback delegates must stay referenced for as long as LibVLC can call them.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    private IntPtr _buffer;
    private int _width, _height, _pitch, _alignedLines;
    private WriteableBitmap? _bitmap;
    private int _framePending;
    private bool _disposed;

    /// <summary>The one LibVLC engine all windows share (also used to read file specs without playing).</summary>
    public static LibVLC Engine()
    {
        if (!_coreReady)
        {
            Core.Initialize();
            _coreReady = true;
        }

        return _sharedVlc ??= new LibVLC("--no-osd", "--no-video-title-show", "--no-snapshot-preview");
    }

    public VideoPlayer()
    {
        if (!_coreReady)
        {
            Core.Initialize();
            _coreReady = true;
        }

        // The engine is heavy to start, so every window shares one; only the MediaPlayer is per window.
        _vlc = _sharedVlc ??= new LibVLC("--no-osd", "--no-video-title-show", "--no-snapshot-preview");
        _player = new MediaPlayer(_vlc);

        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _unlockCb = OnUnlock;
        _displayCb = OnDisplay;
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);

        _player.EndReached += (_, _) => Post(() => Ended?.Invoke());
        _player.EncounteredError += (_, _) => Post(() => Error?.Invoke("This file couldn't be played."));
        _player.Playing += (_, _) => Post(() => StateChanged?.Invoke());
        _player.Paused += (_, _) => Post(() => StateChanged?.Invoke());
        _player.Stopped += (_, _) => Post(() => StateChanged?.Invoke());
    }

    /// <summary>A new frame bitmap exists (first frame, or the video size changed); assign it to an Image.</summary>
    public event Action<WriteableBitmap>? BitmapChanged;

    /// <summary>The bitmap's pixels changed; the Image needs to repaint.</summary>
    public event Action? FrameReady;

    public event Action? Ended;
    public event Action<string>? Error;
    public event Action? StateChanged;

    public bool IsPlaying => !_disposed && _player.IsPlaying;
    public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(0, _player.Time));
    public TimeSpan Length => TimeSpan.FromMilliseconds(Math.Max(0, _player.Length));

    public int Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 150);
    }

    public bool Muted
    {
        get => _player.Mute;
        set => _player.Mute = value;
    }

    public void Open(string path)
    {
        using var media = new Media(_vlc, path, FromType.FromPath);
        _player.Play(media);
    }

    public void TogglePause()
    {
        if (_player.State == VLCState.Ended || _player.State == VLCState.Stopped)
            _player.Play();
        else
            _player.Pause();
    }

    public void Pause() => _player.SetPause(true);

    public void SeekTo(TimeSpan position) => _player.Time = (long)Math.Max(0, position.TotalMilliseconds);

    public void SeekBy(TimeSpan delta) => SeekTo(Position + delta);

    public void SetRate(float rate) => _player.SetRate(rate);

    // ---- Tracks (audio languages and subtitles) -------------------------------------------------------------------

    public IReadOnlyList<(int Id, string Name)> AudioTracks() =>
        (_player.AudioTrackDescription ?? Array.Empty<TrackDescription>()).Select(t => (t.Id, t.Name ?? $"Track {t.Id}")).ToList();

    public IReadOnlyList<(int Id, string Name)> Subtitles() =>
        (_player.SpuDescription ?? Array.Empty<TrackDescription>()).Select(t => (t.Id, t.Name ?? $"Track {t.Id}")).ToList();

    public int CurrentAudio => _player.AudioTrack;
    public int CurrentSubtitle => _player.Spu;
    public void SetAudio(int id) => _player.SetAudioTrack(id);
    public void SetSubtitle(int id) => _player.SetSpu(id);

    // ---- Frame plumbing (runs on LibVLC's threads) -----------------------------------------------------------------

    private void OnCleanup(ref IntPtr opaque)
    {
        // The buffer lives as long as the player; it is freed in Dispose.
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // 32-bit BGRA, which is exactly what Avalonia's Bgra8888 bitmaps hold.
        Marshal.Copy(Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);

        _width = (int)width;
        _height = (int)height;
        _pitch = (_width * 4 + 31) / 32 * 32;
        _alignedLines = (_height + 31) / 32 * 32;
        pitches = (uint)_pitch;
        lines = (uint)_alignedLines;

        _frameGate.Wait();
        try
        {
            if (_buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal(_pitch * _alignedLines);
        }
        finally
        {
            _frameGate.Release();
        }

        // The bitmap is recreated on the UI thread when the next frame arrives.
        _bitmap = null;
        return 1;
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        _frameGate.Wait(); // held until OnUnlock so the UI never copies a half-written frame
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) => _frameGate.Release();

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        // Skip frames the UI hasn't caught up with instead of queueing them.
        if (Interlocked.Exchange(ref _framePending, 1) == 0)
            Dispatcher.UIThread.Post(CopyFrame, DispatcherPriority.Render);
    }

    private void CopyFrame()
    {
        try
        {
            if (_disposed || _buffer == IntPtr.Zero || _width <= 0 || _height <= 0)
                return;

            var created = false;
            if (_bitmap is null || _bitmap.PixelSize.Width != _width || _bitmap.PixelSize.Height != _height)
            {
                _bitmap = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                created = true;
            }

            _frameGate.Wait();
            try
            {
                using var fb = _bitmap.Lock();
                var rowBytes = Math.Min(_width * 4, fb.RowBytes);
                unsafe
                {
                    for (var y = 0; y < _height; y++)
                        Buffer.MemoryCopy(
                            (byte*)_buffer + (long)y * _pitch,
                            (byte*)fb.Address + (long)y * fb.RowBytes,
                            fb.RowBytes,
                            rowBytes);
                }
            }
            finally
            {
                _frameGate.Release();
            }

            if (created)
                BitmapChanged?.Invoke(_bitmap);
            FrameReady?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _framePending, 0);
        }
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Stopping from the UI thread can deadlock with a frame callback waiting on it, so do it elsewhere.
        _ = Task.Run(() =>
        {
            try
            {
                _player.Stop();
                _player.Dispose();
            }
            catch
            {
                /* shutting down anyway */
            }

            if (_buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_buffer);
        });
    }
}
