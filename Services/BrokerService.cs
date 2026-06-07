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

    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>true = çalışıyor, false = durdu.</summary>
    public event EventHandler<bool>? StatusChanged;
    /// <summary>mosquitto stdout/stderr satırları (verbose log).</summary>
    public event EventHandler<string>? Output;

    private static readonly string[] MosquittoPaths =
    {
        @"C:\Program Files\mosquitto\mosquitto.exe",
        @"C:\Program Files (x86)\mosquitto\mosquitto.exe",
    };

    public static string? FindMosquitto() => MosquittoPaths.FirstOrDefault(File.Exists);

    /// <summary>Broker'ı başlatır. Zaten çalışıyorsa true döner. Bulunamazsa/başlatılamazsa false.</summary>
    public async Task<bool> StartAsync()
    {
        if (IsRunning) return true;

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

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(this, e.Data); };
        _proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Output?.Invoke(this, e.Data); };
        _proc.Exited += (_, _) => StatusChanged?.Invoke(this, false);

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
            return false;
        }
    }

    public Task StopAsync()
    {
        try
        {
            if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true);
        }
        catch { /* zaten kapanmış olabilir */ }
        _proc = null;
        StatusChanged?.Invoke(this, false);
        return Task.CompletedTask;
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
