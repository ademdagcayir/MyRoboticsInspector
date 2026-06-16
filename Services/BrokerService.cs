using System.Diagnostics;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Yerel Mosquitto broker'ını uygulama içinden başlatır/durdurur — ayrı start-broker.cmd
/// çalıştırmaya gerek kalmadan. mosquitto.exe'yi dev config (1883, anonim, tüm arayüzler) ile
/// çocuk işlem olarak yönetir. Config çalışma anında AppData'ya yazılır (deploy'da Tools klasörüne
/// bağımlı olmamak için). Windows-only; mosquitto bulunamazsa no-op + uyarı.
/// </summary>
public class BrokerService : IAsyncDisposable
{
    private Process? _proc;

    // Start/Stop'u serileştirir — paralel çağrılar iki süreç başlatmasın / yarışmasın
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>true = çalışıyor, false = durdu.</summary>
    public event EventHandler<bool>? StatusChanged;
    /// <summary>mosquitto stdout/stderr satırları (verbose log).</summary>
    public event EventHandler<string>? Output;

    private static readonly string[] MosquittoPaths =
    {
        // 1) Uygulamayla GÖMÜLÜ gelen broker (exe yanındaki 'mosquitto\' klasörü) — öncelikli.
        //    Böylece ayrı Mosquitto kurulumu gerekmez; tek installer her şeyi getirir.
        Path.Combine(AppContext.BaseDirectory, "mosquitto", "mosquitto.exe"),
        // 2) Sisteme ayrıca kurulmuş Mosquitto (geri dönüş).
        @"C:\Program Files\mosquitto\mosquitto.exe",
        @"C:\Program Files (x86)\mosquitto\mosquitto.exe",
    };

    public static string? FindMosquitto() => MosquittoPaths.FirstOrDefault(File.Exists);

    /// <summary>Broker'ı başlatır. Zaten çalışıyorsa true döner. Bulunamazsa/başlatılamazsa false.</summary>
    public async Task<bool> StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning)
            {
                StatusChanged?.Invoke(this, true); // UI'daki bayat "durdu" durumu kendini onarsın
                return true;
            }

            var exe = FindMosquitto();
            if (exe is null)
            {
                Output?.Invoke(this, "Mosquitto bulunamadı: C:\\Program Files\\mosquitto\\mosquitto.exe");
                Output?.Invoke(this, "Kurmak için: winget install -e --id EclipseFoundation.Mosquitto");
                return false;
            }

            // Dev config'i AppData'ya yaz (start-broker.cmd'deki mosquitto-dev.conf ile aynı içerik).
            var confPath = Path.Combine(FileSystem.AppDataDirectory, "mosquitto-dev.conf");
            try { await File.WriteAllTextAsync(confPath, ConfigContent()); }
            catch (Exception ex) { Output?.Invoke(this, $"Config yazılamadı: {ex.Message}"); return false; }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"-c \"{confPath}\" -v",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = FileSystem.AppDataDirectory,
            };

            // Eski (çıkmış) süreç nesnesini bırak — handle sızıntısı olmasın
            _proc?.Dispose();
            _proc = null;

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(this, e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Output?.Invoke(this, e.Data); };
            // Yalnızca GÜNCEL sürecin çıkışı durumu değiştirsin — hızlı durdur+başlat'ta eski
            // sürecin gecikmiş Exited event'i yeni broker'ı "durdu" gösteremesin.
            proc.Exited += (_, _) => { if (ReferenceEquals(proc, _proc)) StatusChanged?.Invoke(this, false); };
            _proc = proc;

            try
            {
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                StatusChanged?.Invoke(this, true);
                return true;
            }
            catch (Exception ex)
            {
                Output?.Invoke(this, $"Broker başlatılamadı: {ex.Message}");
                _proc = null;
                proc.Dispose();
                return false;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // Önce _proc'u boşalt — eski sürecin Exited event'i (ReferenceEquals kontrolü sayesinde)
            // artık durumu değiştiremez.
            var old = _proc;
            _proc = null;
            if (old is not null)
            {
                try
                {
                    if (!old.HasExited)
                    {
                        old.Kill(entireProcessTree: true);
                        // Port bırakılsın — hemen ardından gelen Start bind hatası yemesin
                        using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await old.WaitForExitAsync(killCts.Token);
                    }
                }
                catch { /* zaten kapanmış / zaman aşımı olabilir */ }
                finally { old.Dispose(); }
            }
            StatusChanged?.Invoke(this, false);
        }
        finally { _gate.Release(); }
    }

    private static string ConfigContent() => string.Join("\n", new[]
    {
        "# MyRoboticsInspector dev broker (uygulama içinden yönetilir)",
        "listener 1883 0.0.0.0",
        "allow_anonymous true",
        "persistence true",
        "persistence_location ./",
        "log_dest stdout",
        "log_type all",
        "connection_messages true",
    });

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }
}
