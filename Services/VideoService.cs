namespace MyRoboticsInspector.Services;

/// <summary>
/// Video playback service — stub implementation (LibVLC removed due to WinRT compatibility issues).
/// Provides placeholder implementations for API compatibility.
/// For actual video streaming, use external RTSP-to-HLS transcoding or HTML5 players.
/// </summary>
public class VideoService : IAsyncDisposable
{
    private string? _currentUrl;

    // Null placeholder for API compatibility with existing ViewModels
    public object? MediaPlayer => null;

    public bool IsPlaying => false;
    public bool IsRecording { get; private set; }
    public string? CurrentRecordingPath { get; private set; }

    public event EventHandler<string>? Error;

    /// <summary>Initialize video service (no-op with stub implementation).</summary>
    public void Initialize()
    {
        System.Diagnostics.Debug.WriteLine("VideoService: LibVLC initialization skipped (WinRT compatibility)");
    }

    /// <summary>Play a local video file (not implemented in stub).</summary>
    public void PlayFile(string filePath)
    {
        Error?.Invoke(this, "Video playback not available in this build");
    }

    public void Pause() { }

    public void Resume() { }

    public void Seek(long timeMs) { }

    /// <summary>Play an RTSP URL (not implemented in stub).</summary>
    public void Play(string url, int networkCachingMs = 200)
    {
        _currentUrl = url;
        Error?.Invoke(this, "RTSP playback not available. Use external RTSP-to-HLS transcoding or configure separate video service.");
    }

    public void Stop()
    {
        _currentUrl = null;
    }

    /// <summary>Recording not available (handled by FfmpegRecorder service instead).</summary>
    public void StartRecording(string url, string filePath, int networkCachingMs = 200)
    {
        IsRecording = true;
        CurrentRecordingPath = filePath;
    }

    public void StopRecording()
    {
        IsRecording = false;
        CurrentRecordingPath = null;
    }

    /// <summary>Take a snapshot (not implemented in stub).</summary>
    public string? TakeSnapshot(string outputDir = "")
    {
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await Task.CompletedTask;
    }
}
