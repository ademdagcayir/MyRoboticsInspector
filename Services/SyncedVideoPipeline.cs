using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Birleşik senkron video pipeline'ı (modern yaklaşım):
///   RTSP (/101 ana akış) ──► ffmpeg decode (MJPEG + per-frame PTS/showinfo)
///        └─► her kare: PTS'ten app-saatine eşle → TelemetrySyncBuffer'dan
///             o yakalama anına ait metreyi çek → SkiaSharp ile overlay'i göm
///                  ├─► CurrentFrame (canlı SKCanvasView, ekranda küçültülür)
///                  └─► ffmpeg encode (stdin MJPEG → H.264 MP4) [kayıt sırasında]
///
/// Tek RTSP bağlantısı; canlı görüntü ile kayıt PİKSEL-AYNI ve metre↔kare SENKRON.
/// Senkron: video PTS jitter'siz/drift'siz; tek sabit <see cref="OffsetSeconds"/>
/// (video boru hattı gecikmesi) operatörce ince ayarlanır.
/// </summary>
public sealed class SyncedVideoPipeline : IAsyncDisposable
{
    private readonly TelemetrySyncBuffer _sync;
    private readonly SkiaOverlayRenderer _renderer;

    private Process? _decode;
    private Process? _encode;
    private CancellationTokenSource? _cts;
    private Task? _reader;

    private readonly object _frameLock = new();
    private SKBitmap? _current;          // en son composite edilmiş kare (UI okur)

    private readonly object _ptsLock = new();
    private readonly Queue<double> _ptsQueue = new();
    private double _pts0 = double.NaN;
    private double _appAtFirst;

    private int _w, _h;
    private volatile bool _recording;

    // Kayıt: okuyucu thread'inden AYRI sabit-hızlı örnekleyici. _current'i fps hızında örnekler;
    // okuyucu/canlı görüntü encoder'a hiç dokunmaz → donma yok, süre = gerçek süre.
    private CancellationTokenSource? _recCts;
    private Task? _recSampler;

    /// <summary>Video boru hattı gecikme telafisi (saniye). Metre = buffer.At(kareZamanı − bu).</summary>
    public double OffsetSeconds { get; set; } = 0.25;

    /// <summary>Önizleme overlay'inde REC rozeti göster (kayıt FfmpegOverlayRecorder'da olduğu için VM kontrol eder).</summary>
    public bool ShowRecBadge { get; set; }

    /// <summary>Kayıt başlangıç anı — overlay'deki video zaman sayacı (00:00'dan) bundan hesaplanır. Kayıt yokken null.</summary>
    public DateTime? RecordingStartedAt { get; set; }

    /// <summary>Kayıt başından bu yana geçen süre (video zamanı). Kayıt yoksa null.</summary>
    public TimeSpan? RecordingElapsed
        => RecordingStartedAt is DateTime t ? DateTime.Now - t : (TimeSpan?)null;

    /// <summary>Overlay verisi. Statik alanları VM doldurur; Meters her karede senkron seçilir.</summary>
    public VideoOverlayModel Overlay { get; } = new();

    /// <summary>Gömülen overlay yazılarının font ailesi + boyut çarpanını ayarlar (anlık etkili).</summary>
    public void ConfigureOverlayFont(string? family, double scale)
    {
        _renderer.FontFamily = string.IsNullOrWhiteSpace(family) ? "Arial" : family!;
        _renderer.FontScale = scale;
    }

    public bool IsRunning => _decode is { HasExited: false };
    public bool IsRecording => _recording;
    public int FrameWidth => _w;
    public int FrameHeight => _h;

    public event EventHandler? FrameReady;          // yeni composite kare hazır (UI invalidate)
    public event EventHandler<string>? Error;

    /// <summary>Son hata mesajı (teşhis için — ProjectChannelsViewModel kullanır).</summary>
    public string? LastError { get; private set; }
    private void RaiseError(string message)
    {
        LastError = message;
        var h = Error; h?.Invoke(this, message);
    }

    public SyncedVideoPipeline(TelemetrySyncBuffer sync, SkiaOverlayRenderer renderer)
    {
        _sync = sync;
        _renderer = renderer;
    }

    // ──────────────────────────────────────────────────────────────
    //  Başlat / Durdur
    // ──────────────────────────────────────────────────────────────

    public void Start(string ffmpegPath, string rtspUrl, int fps = 15)
    {
        Stop();
        _cts = new CancellationTokenSource();
        lock (_ptsLock) { _ptsQueue.Clear(); _pts0 = double.NaN; }

        // ÖNİZLEME yolu — düşük gecikme. Kullanıcı bunu SUB akış /102 (≈640x360) ile besler,
        // o yüzden downscale GEREKMEZ. Kayıt AYRI bir process (FfmpegOverlayRecorder, /101 4K
        // drawtext) tarafından yapılır → bu pipeline encoder'a hiç dokunmaz, canlı DONMAZ.
        var args =
            $"-rtsp_transport tcp -fflags nobuffer -flags low_delay " +
            $"-probesize 200000 -analyzeduration 500000 " +
            $"-i \"{rtspUrl}\" -an " +
            $"-vf showinfo,fps={fps} " +
            $"-f mjpeg -q:v 3 pipe:1";

        var psi = new ProcessStartInfo(ffmpegPath, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            _decode = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _decode.ErrorDataReceived += OnDecodeStderr;
            _decode.Start();
            ChildProcessReaper.Register(_decode);
            _decode.BeginErrorReadLine();
            _reader = ReadFramesAsync(_decode.StandardOutput.BaseStream, _cts.Token);
        }
        catch (Exception ex)
        {
            RaiseError($"Decode başlatılamadı: {ex.Message}");
            _decode?.Dispose();
            _decode = null;
        }
    }

    public void Stop()
    {
        StopRecording();
        _cts?.Cancel();
        KillProc(ref _decode);
        lock (_frameLock) { _current?.Dispose(); _current = null; }
    }

    // ──────────────────────────────────────────────────────────────
    //  Kayıt (encode ffmpeg — stdin MJPEG → H.264)
    // ──────────────────────────────────────────────────────────────

    public bool StartRecording(string ffmpegPath, string outputPath, int fps = 15)
    {
        if (_recording) return true;
        if (_w == 0 || _h == 0) { RaiseError("Kayıt: kare boyutu henüz bilinmiyor"); return false; }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Süre eşitliği: örnekleyici (RecSamplerLoop) tam {fps} kare/sn yazar; girişi de
        // {fps} CFR kabul et → video süresi = gerçek kayıt süresi. (wallclock yerine sabit
        // hız: gerçek app'in bursty yazımında wallclock süreyi şişiriyordu — örnekleyici kesin.)
        var args =
            $"-y -hide_banner -loglevel warning " +
            $"-f mjpeg -framerate {fps} -i pipe:0 " +
            $"-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p " +
            $"-r {fps} -movflags +faststart -an " +
            $"\"{outputPath}\"";

        var psi = new ProcessStartInfo(ffmpegPath, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            _encode = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _encode.ErrorDataReceived += (_, e) => { if (e.Data is not null) Debug.WriteLine($"[encode] {e.Data}"); };
            _encode.Start();
            ChildProcessReaper.Register(_encode);
            _encode.BeginErrorReadLine();

            // Sabit-hızlı örnekleyici ayrı thread (okuyucu/canlı görüntüden bağımsız).
            _recCts = new CancellationTokenSource();
            var enc = _encode;
            var token = _recCts.Token;
            _recSampler = Task.Run(() => RecSamplerLoop(enc, fps, token));

            _recording = true;
            return true;
        }
        catch (Exception ex)
        {
            RaiseError($"Kayıt başlatılamadı: {ex.Message}");
            _encode?.Dispose();
            _encode = null;
            return false;
        }
    }

    public void StopRecording()
    {
        _recording = false;

        // Örnekleyiciyi durdur (kalan kareleri yazıp çıkar).
        _recCts?.Cancel();
        try { _recSampler?.Wait(4000); } catch { }
        _recCts?.Dispose();
        _recCts = null;
        _recSampler = null;

        if (_encode is null) return;
        try
        {
            if (!_encode.HasExited)
            {
                try { _encode.StandardInput.Close(); } catch { }   // writer bitti → stdin'i kapat (MOOV finalize)
                if (!_encode.WaitForExit(5000)) { try { _encode.Kill(true); } catch { } }
            }
        }
        catch { }
        finally { _encode.Dispose(); _encode = null; }
    }

    /// <summary>
    /// Sabit-hızlı kayıt örnekleyici: gerçek zamanda tam {fps} kare/sn olacak şekilde en son
    /// composite kareyi (_current) örnekler, JPEG'e çevirip encode-ffmpeg'e yazar.
    /// "yazılması gereken kare = geçen_süre × fps" → video süresi = gerçek kayıt süresi (kesin).
    /// Yeni kare gelmese (donma) son kare çoğaltılır → donma gerçek süresiyle kaydedilir.
    /// Okuyucu thread'i bu döngüye dokunmaz → canlı görüntü asla bloklanmaz.
    /// </summary>
    private void RecSamplerLoop(Process encode, int fps, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long written = 0;
        try
        {
            while (!ct.IsCancellationRequested && !encode.HasExited)
            {
                long target = (long)(sw.Elapsed.TotalSeconds * fps);
                while (written < target && !ct.IsCancellationRequested && !encode.HasExited)
                {
                    SKBitmap? snap;
                    lock (_frameLock) { snap = _current?.Copy(); }
                    if (snap is null) { break; } // henüz kare yok
                    try
                    {
                        using var img = SKImage.FromBitmap(snap);
                        using var jpeg = img.Encode(SKEncodedImageFormat.Jpeg, 90);
                        if (jpeg is not null)
                        {
                            var s = encode.StandardInput.BaseStream;
                            var arr = jpeg.ToArray();
                            s.Write(arr, 0, arr.Length);
                            s.Flush();
                        }
                    }
                    catch (Exception ex) { Debug.WriteLine($"[rec-sampler] {ex.Message}"); }
                    finally { snap.Dispose(); }
                    written++;
                }
                Thread.Sleep(5);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[rec-sampler loop] {ex.Message}"); }
    }

    /// <summary>En son composite kareyi (overlay dahil) JPEG olarak döner — snapshot için.</summary>
    /// <remarks>
    /// KRİTİK: JPEG encode (4K'da ~150ms) kilit DIŞINDA yapılır. Kilit yalnızca hızlı bir
    /// kopya (memcpy) için tutulur. Aksi hâlde encode boyunca <see cref="_frameLock"/> tutulur,
    /// okuyucu thread'i (ComposeFrame) bloklanır, decode-ffmpeg'in stdout pipe'ı dolar ve
    /// CANLI GÖRÜNTÜ DONAR. Bu, "resim çekince kayıt/önizleme dondu" hatasının köküydü.
    /// </remarks>
    public byte[]? Snapshot()
    {
        SKBitmap? copy;
        lock (_frameLock)
        {
            if (_current is null) return null;
            copy = _current.Copy();   // hızlı: sadece bellek kopyası, encode YOK
        }
        if (copy is null) return null;
        try
        {
            using var img = SKImage.FromBitmap(copy);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 92);
            return data?.ToArray();
        }
        finally { copy.Dispose(); }
    }

    /// <summary>UI thread'inde çağrılır: en son kareyi verilen canvas'a (hedef boyuta) çizer.</summary>
    public void DrawCurrent(SKCanvas canvas, SKRect dst)
    {
        lock (_frameLock)
        {
            if (_current is null) return;
            canvas.DrawBitmap(_current, dst);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  stderr (showinfo PTS) ayrıştırma
    // ──────────────────────────────────────────────────────────────

    private static readonly Regex PtsRx = new(@"pts_time:([0-9.]+)", RegexOptions.Compiled);
    private static readonly Regex SizeRx = new(@"\bs:(\d+)x(\d+)", RegexOptions.Compiled);

    private void OnDecodeStderr(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        var line = e.Data;

        if (_w == 0)
        {
            var sm = SizeRx.Match(line);
            if (sm.Success) { _w = int.Parse(sm.Groups[1].Value); _h = int.Parse(sm.Groups[2].Value); }
        }

        var pm = PtsRx.Match(line);
        if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pts))
        {
            lock (_ptsLock)
            {
                _ptsQueue.Enqueue(pts);
                if (_ptsQueue.Count > 300) _ptsQueue.Dequeue(); // taşma koruması
            }
        }

        if (line.Contains("Connection refused") || line.Contains("No route to host") ||
            line.Contains("Could not") || line.Contains("Server returned"))
            RaiseError(line);
    }

    private double NextPtsOrNow()
    {
        lock (_ptsLock)
        {
            if (_ptsQueue.Count > 0) return _ptsQueue.Dequeue();
        }
        return double.NaN;
    }

    // ──────────────────────────────────────────────────────────────
    //  Kare okuma + composite
    // ──────────────────────────────────────────────────────────────

    private async Task ReadFramesAsync(Stream stream, CancellationToken ct)
    {
        var readBuf = new byte[65536];
        var accum = new MemoryStream(2 * 1024 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(readBuf.AsMemory(), ct);
                if (read == 0) break;
                accum.Write(readBuf, 0, read);

                int consumed = ProcessJpegs(accum.GetBuffer(), (int)accum.Length);
                if (consumed > 0)
                {
                    int remaining = (int)accum.Length - consumed;
                    if (remaining > 0)
                        Buffer.BlockCopy(accum.GetBuffer(), consumed, accum.GetBuffer(), 0, remaining);
                    accum.SetLength(remaining);
                    accum.Position = remaining;
                }
                if (accum.Length > 8 * 1024 * 1024) { accum.SetLength(0); accum.Position = 0; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) RaiseError($"Okuma hatası: {ex.Message}"); }
    }

    private int ProcessJpegs(byte[] data, int len)
    {
        int consumed = 0, pos = 0;
        while (pos < len - 1)
        {
            int soi = Find(data, pos, len, 0xFF, 0xD8);
            if (soi < 0) break;
            int eoi = Find(data, soi + 2, len, 0xFF, 0xD9);
            if (eoi < 0) break;
            int end = eoi + 2;

            ComposeFrame(data, soi, end - soi);
            consumed = end; pos = end;
        }
        return consumed;
    }

    private void ComposeFrame(byte[] data, int offset, int length)
    {
        double pts = NextPtsOrNow();

        // PTS → app-saati eşlemesi (jitter'siz). İlk karede çapayı kur.
        double captureAppTime;
        if (!double.IsNaN(pts))
        {
            if (double.IsNaN(_pts0)) { _pts0 = pts; _appAtFirst = _sync.NowSeconds; }
            double appEquivalent = _appAtFirst + (pts - _pts0);
            captureAppTime = appEquivalent - OffsetSeconds;
        }
        else
        {
            captureAppTime = _sync.NowSeconds - OffsetSeconds;
        }

        SKBitmap? bmp;
        try
        {
            using var skData = SKData.CreateCopy(new ReadOnlySpan<byte>(data, offset, length).ToArray());
            bmp = SKBitmap.Decode(skData);
        }
        catch { return; }
        if (bmp is null) return;

        if (_w == 0) { _w = bmp.Width; _h = bmp.Height; }

        // Overlay: senkron metre + statik alanlar + video zaman sayacı (duvar saati DEĞİL)
        Overlay.Meters = _sync.MetersAt(captureAppTime);
        Overlay.NowText = FormatVideoClock(RecordingElapsed);
        Overlay.IsRecording = ShowRecBadge;

        try
        {
            using (var canvas = new SKCanvas(bmp))
            {
                _renderer.DrawOverlay(canvas, bmp.Width, bmp.Height, Overlay);
                canvas.Flush();
            }
        }
        catch { }

        // NOT: Kayıt encode'u burada YAPILMAZ. Ayrı RecSamplerLoop, _current'i sabit hızda
        // örnekleyip encode-ffmpeg'e yazar → okuyucu thread'i encoder'a hiç dokunmaz, canlı
        // görüntü encoder yavaşlasa bile DONMAZ.

        // UI'ya yeni kareyi ver
        SKBitmap? old;
        lock (_frameLock) { old = _current; _current = bmp; }
        old?.Dispose();
        FrameReady?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Video zaman sayacı: 00:00 formatı (60 dk üstünde sa:dk:sn). Kayıt yoksa 00:00.</summary>
    public static string FormatVideoClock(TimeSpan? elapsed)
    {
        var t = elapsed ?? TimeSpan.Zero;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    private static int Find(byte[] d, int from, int len, byte a, byte b)
    {
        for (int i = from; i < len - 1; i++) if (d[i] == a && d[i + 1] == b) return i;
        return -1;
    }

    private static void KillProc(ref Process? p)
    {
        if (p is null) return;
        try { p.EnableRaisingEvents = false; if (!p.HasExited) p.Kill(true); } catch { }
        try { p.Dispose(); } catch { }
        p = null;
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_reader is not null) { try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
