using System.Diagnostics;
using System.Text;

namespace MyRoboticsInspector.Services;

/// <summary>Multi-corner overlay state for burn-in.</summary>
public record OverlayState(
    string? TopLeft = null,
    string? TopRight = null,
    string? BottomLeft = null,
    string? BottomRight = null);

/// <summary>
/// Records an RTSP stream to MP4 via a separate ffmpeg.exe subprocess, optionally with
/// burnt-in overlay text. Overlay is updated by writing to per-corner text files that
/// FFmpeg drawtext reloads on every frame.
///
/// LibVLC handles the live preview; this recorder pulls its own RTSP stream so the two
/// pipelines are decoupled (the camera doesn't care about extra subscribers).
/// </summary>
public class FfmpegRecorder : IAsyncDisposable
{
    private Process? _process;
    private string? _overlayDir;
    private bool _burnEnabled;

    public string? CurrentOutputPath { get; private set; }
    public bool IsRecording => _process is { HasExited: false };
    public string? LastError { get; private set; }

    public event EventHandler<string>? Error;
    public event EventHandler<string>? LogReceived;

    /// <summary>
    /// FFmpeg binary'sini doğrular. Verilen yol bulunamazsa platforma uygun yedek isimleri dener
    /// (Mac/Linux: "ffmpeg", Homebrew Apple Silicon: "/opt/homebrew/bin/ffmpeg", Intel Mac:
    /// "/usr/local/bin/ffmpeg"). Bulduğu çalışan yolu döner — yoksa null.
    /// </summary>
    public static string? ResolveFfmpeg(string preferredPath)
    {
        var candidates = new List<string> { preferredPath };

        if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
        {
            candidates.Add("ffmpeg");                          // PATH
            candidates.Add("/opt/homebrew/bin/ffmpeg");        // Apple Silicon Homebrew
            candidates.Add("/usr/local/bin/ffmpeg");           // Intel Mac Homebrew
            candidates.Add("/usr/bin/ffmpeg");                 // system
        }
        else if (!OperatingSystem.IsWindows())
        {
            candidates.Add("ffmpeg");                          // Linux/etc PATH
            candidates.Add("/usr/bin/ffmpeg");
            candidates.Add("/usr/local/bin/ffmpeg");
        }

        foreach (var c in candidates.Distinct())
        {
            if (TryProbe(c)) return c;
        }
        return null;
    }

    public static bool ProbeFfmpeg(string ffmpegPath) => ResolveFfmpeg(ffmpegPath) is not null;

    private static bool TryProbe(string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(path, "-version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (p is null) return false;
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public async Task StartAsync(
        string ffmpegPath,
        string rtspUrl,
        string outputPath,
        OverlayState overlay,
        bool burnOverlay,
        CancellationToken ct = default)
    {
        if (IsRecording) await StopAsync();

        var outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        _overlayDir = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Path.GetTempPath(),
            $".overlay_{Path.GetFileNameWithoutExtension(outputPath)}");
        Directory.CreateDirectory(_overlayDir);

        _burnEnabled = burnOverlay;
        WriteOverlayFiles(overlay);

        var args = BuildArgs(rtspUrl, outputPath, burnOverlay);

        var psi = new ProcessStartInfo(ffmpegPath, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        try
        {
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                LogReceived?.Invoke(this, e.Data);
                // FFmpeg writes everything to stderr including normal progress; capture last
                // few lines so we have something useful if it crashes.
                LastError = e.Data;
            };
            _process.Exited += (_, _) =>
            {
                if (_process?.ExitCode is int code && code != 0 && code != 255)
                    Error?.Invoke(this, $"FFmpeg çıktı kodu {code}: {LastError}");
            };
            _process.Start();
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();

            CurrentOutputPath = outputPath;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"FFmpeg başlatılamadı: {ex.Message}");
            _process?.Dispose();
            _process = null;
            throw;
        }

        await Task.CompletedTask;
    }

    public void UpdateOverlay(OverlayState overlay)
    {
        if (_overlayDir is null || !_burnEnabled) return;
        WriteOverlayFiles(overlay);
    }

    public async Task StopAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                // 'q' to ffmpeg's stdin = graceful stop (finalizes MOOV atom in MP4).
                try
                {
                    await _process.StandardInput.WriteAsync('q');
                    await _process.StandardInput.FlushAsync();
                }
                catch { /* pipe may be broken */ }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await _process.WaitForExitAsync(cts.Token); }
                catch { try { _process.Kill(true); } catch { } }
            }
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            CurrentOutputPath = null;
            TryCleanupOverlayDir();
        }
    }

    private void TryCleanupOverlayDir()
    {
        if (_overlayDir is null) return;
        try { Directory.Delete(_overlayDir, recursive: true); } catch { }
        _overlayDir = null;
    }

    private void WriteOverlayFiles(OverlayState overlay)
    {
        if (_overlayDir is null) return;
        WriteAtomic(Path.Combine(_overlayDir, "tl.txt"), overlay.TopLeft ?? " ");
        WriteAtomic(Path.Combine(_overlayDir, "tr.txt"), overlay.TopRight ?? " ");
        WriteAtomic(Path.Combine(_overlayDir, "bl.txt"), overlay.BottomLeft ?? " ");
        WriteAtomic(Path.Combine(_overlayDir, "br.txt"), overlay.BottomRight ?? " ");
    }

    private static void WriteAtomic(string path, string content)
    {
        // FFmpeg reloads every frame. Half-written file would flicker.
        // Write to .tmp, then atomic rename (File.Move with overwrite is atomic on same volume).
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best-effort */ }
    }

    private string BuildArgs(string rtspUrl, string outputPath, bool burnOverlay)
    {
        var sb = new StringBuilder();

        // No interactive prompts, overwrite output, TCP RTSP for reliability over wifi/poe.
        sb.Append("-y -hide_banner -loglevel warning ");
        sb.Append("-rtsp_transport tcp -fflags +genpts -stimeout 5000000 ");
        sb.Append("-i ").Append(QuoteForCli(rtspUrl)).Append(' ');

        if (burnOverlay)
        {
            sb.Append("-vf ").Append(QuoteForCli(BuildFilterChain())).Append(' ');
        }

        // x264 veryfast / crf 23 = good balance for inspection footage.
        // No audio for now (kanal videosunda gerekmiyor).
        sb.Append("-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -movflags +faststart -an ");
        sb.Append(QuoteForCli(outputPath));
        return sb.ToString();
    }

    private string BuildFilterChain()
    {
        var tl = EscapeForFilterFile(Path.Combine(_overlayDir!, "tl.txt"));
        var tr = EscapeForFilterFile(Path.Combine(_overlayDir!, "tr.txt"));
        var bl = EscapeForFilterFile(Path.Combine(_overlayDir!, "bl.txt"));
        var br = EscapeForFilterFile(Path.Combine(_overlayDir!, "br.txt"));

        var fontFile = ResolveFontFile();
        var fontPart = fontFile is null ? "" : $":fontfile='{EscapeForFilterFile(fontFile)}'";

        // expansion=none — treat textfile content as LITERAL.
        // Otherwise FFmpeg drawtext interprets % (expressions) and \ (escapes) in user text,
        // which silently breaks lines containing "87%" (battery), file paths, etc.
        const string common = "expansion=none:box=1:boxcolor=black@0.55:boxborderw=8";

        var tlF = $"drawtext=textfile='{tl}':reload=1{fontPart}:x=20:y=20:fontsize=20:fontcolor=white:{common}";
        var trF = $"drawtext=textfile='{tr}':reload=1{fontPart}:x=w-tw-20:y=20:fontsize=20:fontcolor=white:{common}";
        var blF = $"drawtext=textfile='{bl}':reload=1{fontPart}:x=20:y=h-th-20:fontsize=44:fontcolor=yellow:{common}";
        var brF = $"drawtext=textfile='{br}':reload=1{fontPart}:x=w-tw-20:y=h-th-20:fontsize=18:fontcolor=white:{common}";

        return string.Join(",", tlF, trF, blF, brF);
    }

    /// <summary>
    /// FFmpeg drawtext filter has its own escape rules for filenames inside single quotes:
    ///   backslash -> /  (Windows paths use forward slashes)
    ///   colon -> \:      (colon is a filter argument separator)
    /// </summary>
    private static string EscapeForFilterFile(string path)
        => path.Replace("\\", "/").Replace(":", @"\:");

    private static string QuoteForCli(string s)
    {
        // Process.Start with single-string CLI: wrap in double quotes, escape inner quotes.
        if (s.Contains('"')) s = s.Replace("\"", "\\\"");
        return $"\"{s}\"";
    }

    private static string? ResolveFontFile()
    {
        if (OperatingSystem.IsWindows())
        {
            var arial = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
            if (File.Exists(arial)) return arial;
        }
        else if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
        {
            // Mac sistem fontları — Helvetica her macOS'ta var, Arial çoğunda
            var candidates = new[]
            {
                "/System/Library/Fonts/Helvetica.ttc",
                "/Library/Fonts/Arial.ttf",
                "/System/Library/Fonts/Supplemental/Arial.ttf",
                "/System/Library/Fonts/Geneva.ttf",
            };
            foreach (var f in candidates)
                if (File.Exists(f)) return f;
        }
        // Linux: fontconfig'in default'una düş (return null → ffmpeg drawtext default font)
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }
}
