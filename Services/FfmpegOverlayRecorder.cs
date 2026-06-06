using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MyRoboticsInspector.Services;

/// <summary>
/// TEK-GEÇİŞ drawtext kayıt servisi (4K /101, tam kalite, gerçek-zamanlı).
///
/// Tek ffmpeg: RTSP /101 → drawtext overlay (statik alanlar + reload'lu DİNAMİK metre/saat) → H.264 → MP4.
/// Decode/encode TEK kez yapılır; ham 4K app'e dönmez → eski Skia çift-transcode çökmesi yapısal imkânsız.
/// Canlı önizleme AYRI /102 akışından beslendiği için kayıt CANLI'yı DONDURMAZ.
///
/// Senkron: meter.txt'ye <see cref="TelemetrySyncBuffer"/>.MetersAt(now − recOffset) ATOMİK yazılır (10Hz);
/// drawtext reload=1 her karede okur. recOffset, /101 boru hattı gecikmesini telafi eder (Ayarlar'dan kalibre).
/// Dayanıklılık: fragmented-MP4 (+frag_keyframe+empty_moov) → app çökse/Kill edilse bile dosya oynatılır.
/// Encoder: çalışma-anı probe nvenc→qsv→libx264 (ilk başarılı, oturum boyu cache).
/// </summary>
public sealed class FfmpegOverlayRecorder : IAsyncDisposable
{
    private readonly TelemetrySyncBuffer _sync;

    private Process? _proc;
    private string? _sessionDir;
    private string? _meterPath;
    private string? _nowPath;
    private System.Threading.Timer? _writer;
    private double _recOffsetSeconds = 0.25;

    public string? OutputPath { get; private set; }
    public bool IsRecording => _proc is { HasExited: false };
    public string? LastError { get; private set; }
    public event EventHandler<string>? Error;

    public FfmpegOverlayRecorder(TelemetrySyncBuffer sync) => _sync = sync;

    // ── Encoder argümanları (plan §2b) ──
    private const string NvencArgs  = "-c:v h264_nvenc -preset p4 -rc vbr -cq 23 -b:v 0 -pix_fmt yuv420p";
    private const string QsvArgs    = "-c:v h264_qsv -global_quality 22 -look_ahead 0";
    private const string X264Args   = "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p";

    private static string FontsDir() => Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    private static (string regular, string bold) FontFiles(string? family) =>
        (family ?? "Arial").Trim().ToLowerInvariant() switch
        {
            "segoe ui"        => ("segoeui.ttf", "segoeuib.ttf"),
            "tahoma"          => ("tahoma.ttf",  "tahomabd.ttf"),
            "verdana"         => ("verdana.ttf", "verdanab.ttf"),
            "calibri"         => ("calibri.ttf", "calibrib.ttf"),
            "times new roman" => ("times.ttf",   "timesbd.ttf"),
            "consolas"        => ("consola.ttf", "consolab.ttf"),
            "courier new"     => ("cour.ttf",    "courbd.ttf"),
            "impact"          => ("impact.ttf",  "impact.ttf"),
            _                 => ("arial.ttf",   "arialbd.ttf"),
        };

    public async Task<bool> StartAsync(
        string ffmpegPath, string rtspUrl, string outputPath,
        VideoOverlayModel model, string? fontFamily, double fontScale,
        double recOffsetSeconds, string? encoderPref, int videoHeight = 2160)
    {
        if (IsRecording) await StopAsync();
        _recOffsetSeconds = recOffsetSeconds;

        var outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        _sessionDir = Path.Combine(Path.GetTempPath(), $"mri_ovl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sessionDir);
        _meterPath = Path.Combine(_sessionDir, "meter.txt");
        _nowPath   = Path.Combine(_sessionDir, "now.txt");

        // Statik alanlar (bir kez)
        WriteAtomic(Path.Combine(_sessionDir, "company.txt"),  model.CompanyName ?? "");
        WriteAtomic(Path.Combine(_sessionDir, "project.txt"),  model.ProjectName ?? "");
        WriteAtomic(Path.Combine(_sessionDir, "location.txt"), model.Location ?? "");
        var baca = new List<string>();
        if (!string.IsNullOrWhiteSpace(model.ManholeIn))  baca.Add($"Giriş: {model.ManholeIn}");
        if (!string.IsNullOrWhiteSpace(model.ManholeOut)) baca.Add($"Çıkış: {model.ManholeOut}");
        WriteAtomic(Path.Combine(_sessionDir, "manhole.txt"), string.Join("    ", baca));
        WriteAtomic(Path.Combine(_sessionDir, "flow.txt"),
            string.IsNullOrWhiteSpace(model.FlowText) ? "" : $"Akış yönü: {model.FlowText}");
        // Dinamik (timer yazar)
        WriteAtomic(_meterPath, "");
        WriteAtomic(_nowPath, DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));

        // Filtre script — komut-satırı escape cehennemini bypass eder
        int h = videoHeight > 0 ? videoHeight : 2160;
        double s = Math.Clamp(fontScale <= 0 ? 1.0 : fontScale, 0.4, 3.0);
        var (reg, bold) = FontFiles(fontFamily);
        var filterPath = Path.Combine(_sessionDir, "overlay.filter");
        File.WriteAllText(filterPath, BuildFilterScript(h, s, reg, bold), new UTF8Encoding(false));

        // Encoder seç (probe + cache)
        var enc = await ResolveEncoderAsync(ffmpegPath, encoderPref ?? "auto");

        var args = BuildArgs(rtspUrl, outputPath, filterPath, enc);

        var psi = new ProcessStartInfo(ffmpegPath, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = FontsDir(),
        };
        try
        {
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                LastError = e.Data;
                if (e.Data.Contains("Connection refused") || e.Data.Contains("No route") ||
                    e.Data.Contains("Could not") || e.Data.Contains("Invalid data"))
                    Error?.Invoke(this, MaskCreds(e.Data));
            };
            _proc.Start();
            ChildProcessReaper.Register(_proc);
            _proc.BeginErrorReadLine();
            _proc.BeginOutputReadLine();
            OutputPath = outputPath;

            _writer = new System.Threading.Timer(_ => Tick(), null, 0, 100); // 10 Hz
            return true;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Kayıt başlatılamadı: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    public async Task StopAsync()
    {
        _writer?.Dispose(); _writer = null;
        if (_proc is not null)
        {
            try
            {
                if (!_proc.HasExited)
                {
                    try { await _proc.StandardInput.WriteAsync('q'); await _proc.StandardInput.FlushAsync(); } catch { }
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    try { await _proc.WaitForExitAsync(cts.Token); } catch { try { _proc.Kill(true); } catch { } }
                }
            }
            catch { }
        }
        Cleanup();
    }

    // ── Filtre script (setpts + drawtext zinciri + format) ──
    private string BuildFilterScript(int h, double s, string regFont, string boldFont)
    {
        int fSmall = Math.Max(12, (int)(h * 0.030 * 0.80 * s));
        int fBody  = Math.Max(14, (int)(h * 0.030 * s));
        int fTitle = Math.Max(18, (int)(h * 0.030 * 1.20 * s));
        int fMeter = Math.Max(28, (int)(h * 0.085 * s));
        int pad    = Math.Max(8,  (int)(h * 0.022));
        int border = Math.Max(4,  (int)(fBody * 0.25));

        var fb = Esc(Path.Combine(FontsDir(), boldFont));
        var fr = Esc(Path.Combine(FontsDir(), regFont));
        string Tf(string f) => Esc(Path.Combine(_sessionDir!, f));
        string Box() => $"box=1:boxcolor=black@0.55:boxborderw={border}";

        var c = new List<string>();
        // Üst-sol stack (numeric y)
        int y = pad;
        c.Add($"drawtext=textfile='{Tf("company.txt")}':reload=0:expansion=none:fontfile='{fr}':x={pad}:y={y}:fontsize={fSmall}:fontcolor=0xCCCCCC:{Box()}"); y += (int)(fSmall * 1.7);
        c.Add($"drawtext=textfile='{Tf("project.txt")}':reload=0:expansion=none:fontfile='{fb}':x={pad}:y={y}:fontsize={fTitle}:fontcolor=0xFFD60A:{Box()}"); y += (int)(fTitle * 1.6);
        c.Add($"drawtext=textfile='{Tf("location.txt")}':reload=0:expansion=none:fontfile='{fr}':x={pad}:y={y}:fontsize={fBody}:fontcolor=white:{Box()}"); y += (int)(fBody * 1.7);
        c.Add($"drawtext=textfile='{Tf("manhole.txt")}':reload=0:expansion=none:fontfile='{fr}':x={pad}:y={y}:fontsize={fBody}:fontcolor=white:{Box()}"); y += (int)(fBody * 1.7);
        c.Add($"drawtext=textfile='{Tf("flow.txt")}':reload=0:expansion=none:fontfile='{fr}':x={pad}:y={y}:fontsize={fBody}:fontcolor=0x64D2FF:{Box()}");
        // Üst-sağ: saat + REC
        c.Add($"drawtext=textfile='{Tf("now.txt")}':reload=1:expansion=none:fontfile='{fr}':x=w-tw-{pad}:y={pad}:fontsize={fBody}:fontcolor=white:{Box()}");
        c.Add($"drawtext=text='● REC':expansion=none:fontfile='{fb}':x=w-tw-{pad}:y={pad + (int)(fBody * 1.9)}:fontsize={fBody}:fontcolor=0xFF3B30:{Box()}");
        // Alt-sol: büyük metre
        c.Add($"drawtext=textfile='{Tf("meter.txt")}':reload=1:expansion=none:fontfile='{fb}':x={pad}:y=h-{fMeter + pad}:fontsize={fMeter}:fontcolor=0xFFD60A:{Box()}");

        // setpts BAŞTA (MP4 0'dan başlasın), format SONDA (encoder uyumu)
        return "setpts=PTS-STARTPTS," + string.Join(",", c) + ",format=yuv420p";
    }

    private static string BuildArgs(string rtspUrl, string outputPath, string filterScriptPath, string encArgs)
    {
        var sb = new StringBuilder();
        sb.Append("-y -hide_banner -loglevel warning ");
        sb.Append("-rtsp_transport tcp -thread_queue_size 1024 -fflags +genpts ");
        sb.Append("-i ").Append(Q(rtspUrl)).Append(' ');
        sb.Append("-an ");
        sb.Append("-filter_script:v ").Append(Q(filterScriptPath)).Append(' ');
        sb.Append(encArgs).Append(' ');
        sb.Append("-movflags +frag_keyframe+empty_moov+default_base_moof ");
        sb.Append(Q(outputPath));
        return sb.ToString();
    }

    // ── Encoder probe (cache) ──
    private static readonly object _encLock = new();
    private static string? _encCacheKey, _encCacheArgs;

    private static async Task<string> ResolveEncoderAsync(string ffmpegPath, string pref)
    {
        lock (_encLock) { if (_encCacheKey == pref && _encCacheArgs is not null) return _encCacheArgs; }

        var order = pref switch
        {
            "nvenc"   => new[] { ("h264_nvenc", NvencArgs) },
            "qsv"     => new[] { ("h264_qsv", QsvArgs) },
            "libx264" => new[] { ("libx264", X264Args) },
            _         => new[] { ("h264_nvenc", NvencArgs), ("h264_qsv", QsvArgs), ("libx264", X264Args) },
        };
        string chosen = X264Args; // son çare
        foreach (var (name, eargs) in order)
        {
            if (await TestEncoderAsync(ffmpegPath, eargs)) { chosen = eargs; Debug.WriteLine($"[recorder] encoder = {name}"); break; }
            Debug.WriteLine($"[recorder] encoder {name} probe FAIL");
        }
        lock (_encLock) { _encCacheKey = pref; _encCacheArgs = chosen; }
        return chosen;
    }

    private static async Task<bool> TestEncoderAsync(string ffmpegPath, string encArgs)
    {
        try
        {
            var args = $"-hide_banner -loglevel error -f lavfi -i testsrc=size=320x240:rate=25 -frames:v 25 {encArgs} -f mp4 -y NUL";
            using var p = Process.Start(new ProcessStartInfo(ffmpegPath, args)
            { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true });
            if (p is null) return false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await p.WaitForExitAsync(cts.Token); } catch { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── Senkron yazıcı (10Hz) ──
    private void Tick()
    {
        try
        {
            var m = _sync.MetersAt(_sync.NowSeconds - _recOffsetSeconds);
            if (m is double v && _meterPath is not null)             // BOŞ değer → son metreyi koru (flicker yok)
                WriteAtomic(_meterPath, v.ToString("0.00", CultureInfo.InvariantCulture) + " m");
            if (_nowPath is not null)
                WriteAtomic(_nowPath, DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));
        }
        catch { }
    }

    private static void WriteAtomic(string path, string content)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false)); // BOM yok
            File.Move(tmp, path, overwrite: true);                    // NTFS atomik rename
        }
        catch { }
    }

    // drawtext filtergraph yol kaçışı: \ → /, : → \:
    private static string Esc(string p) => p.Replace("\\", "/").Replace(":", "\\:");
    private static string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
    private static string MaskCreds(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty :
        System.Text.RegularExpressions.Regex.Replace(s, @"://[^/@\s:]+:[^/@\s]+@", "://***@");

    private void Cleanup()
    {
        try { _proc?.Dispose(); } catch { }
        _proc = null; OutputPath = null;
        _writer?.Dispose(); _writer = null;
        if (_sessionDir is not null)
        {
            try { Directory.Delete(_sessionDir, recursive: true); } catch { }
            _sessionDir = null; _meterPath = null; _nowPath = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }
}
