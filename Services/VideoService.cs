using LibVLCSharp.Shared;

// NOT: Apple platformlarında (iOS/macCatalyst) `MediaPlayer` adında global bir namespace var
// (Apple'ın MPMediaPlayer framework'ü). LibVLCSharp.Shared.MediaPlayer'ı tam-nitelikli adıyla
// referans etmek namespace çakışmasını engeller. Daha fazla bilgi: Mac Catalyst Apple bindings.

namespace MyRoboticsInspector.Services;

/// <summary>
/// Owns a single LibVLC instance and exposes a MediaPlayer that XAML VideoView can bind to.
/// Handles RTSP playback, snapshots, and writing the same stream to disk via LibVLC's :sout chain.
/// </summary>
public class VideoService : IAsyncDisposable
{
    private LibVLC? _libVlc;
    private Media? _currentMedia;

    public LibVLCSharp.Shared.MediaPlayer? MediaPlayer { get; private set; }

    public bool IsPlaying => MediaPlayer?.IsPlaying == true;
    public bool IsRecording { get; private set; }
    public string? CurrentRecordingPath { get; private set; }

    public event EventHandler<string>? Error;

    private static bool _coreInitialized;

    private LibVLC EnsureLibVLC()
    {
        if (!_coreInitialized)
        {
            Core.Initialize();
            _coreInitialized = true;
        }
        _libVlc ??= new LibVLC("--no-osd", "--quiet");
        MediaPlayer ??= new LibVLCSharp.Shared.MediaPlayer(_libVlc) { EnableHardwareDecoding = true };
        return _libVlc;
    }

    /// <summary>Open a local video file for playback (review mode for recorded inspections).</summary>
    public void PlayFile(string filePath)
    {
        try
        {
            var lib = EnsureLibVLC();
            Stop();
            _currentMedia = new Media(lib, filePath, FromType.FromPath);
            MediaPlayer!.Media = _currentMedia;
            MediaPlayer.Play();
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Dosya açılamadı: {ex.Message}");
        }
    }

    public void Pause() => MediaPlayer?.Pause();
    public void Resume() { if (MediaPlayer?.CanPause == true) MediaPlayer.Play(); }
    public void Seek(long timeMs)
    {
        if (MediaPlayer?.IsSeekable == true)
        {
            try { MediaPlayer.Time = timeMs; } catch { }
        }
    }

    /// <summary>Start playing an RTSP/HTTP URL. <paramref name="networkCachingMs"/> trades latency for stability.</summary>
    public void Play(string url, int networkCachingMs = 200)
    {
        try
        {
            var libVlc = EnsureLibVLC();
            Stop();

            _currentMedia = new Media(libVlc, url, FromType.FromLocation);
            _currentMedia.AddOption($":network-caching={networkCachingMs}");
            _currentMedia.AddOption(":rtsp-tcp");

            MediaPlayer!.Media = _currentMedia;
            MediaPlayer.Play();
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Video başlatılamadı: {ex.Message}");
        }
    }

    public void Stop()
    {
        try
        {
            if (IsRecording) StopRecording();
            MediaPlayer?.Stop();
            _currentMedia?.Dispose();
            _currentMedia = null;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Video durdurulamadı: {ex.Message}");
        }
    }

    /// <summary>
    /// Start a parallel recording of the current source to <paramref name="filePath"/> (MP4).
    /// Restarts playback with a :sout chain that duplicates the stream to the file and the display.
    /// </summary>
    public void StartRecording(string url, string filePath, int networkCachingMs = 200)
    {
        try
        {
            var libVlc = EnsureLibVLC();
            Stop();

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _currentMedia = new Media(libVlc, url, FromType.FromLocation);
            _currentMedia.AddOption($":network-caching={networkCachingMs}");
            _currentMedia.AddOption(":rtsp-tcp");
            var escaped = filePath.Replace("\\", "/");
            _currentMedia.AddOption($":sout=#duplicate{{dst=display,dst=std{{access=file,mux=mp4,dst='{escaped}'}}}}");
            _currentMedia.AddOption(":sout-keep");

            MediaPlayer!.Media = _currentMedia;
            MediaPlayer.Play();

            IsRecording = true;
            CurrentRecordingPath = filePath;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Kayıt başlatılamadı: {ex.Message}");
        }
    }

    public void StopRecording()
    {
        if (!IsRecording) return;
        IsRecording = false;
        CurrentRecordingPath = null;
        MediaPlayer?.Stop();
    }

    /// <summary>Capture the current frame to a PNG file. Returns the saved path on success, null if not playing.</summary>
    public string? TakeSnapshot(string targetDirectory)
    {
        if (MediaPlayer is null || !IsPlaying) return null;
        try
        {
            Directory.CreateDirectory(targetDirectory);
            var path = Path.Combine(targetDirectory, $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            // num=0 means: snapshot all video tracks (we only have one). width=0/height=0 = original size.
            return MediaPlayer.TakeSnapshot(0, path, 0, 0) ? path : null;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Snapshot alınamadı: {ex.Message}");
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Stop();
            MediaPlayer?.Dispose();
            _libVlc?.Dispose();
        }
        catch { }
        MediaPlayer = null;
        _libVlc = null;
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
