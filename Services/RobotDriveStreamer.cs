using MyRoboticsInspector.Models;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Tüm sürüş girdilerinin (gamepad, klavye, ekran dpad butonları) ortak çıkış katmanı.
///
/// <para><b>Neden var?</b> Robot firmware'i bir watchdog ile komut akışını izler: son hareket
/// komutundan beri ~500 ms boyunca yeni mesaj gelmezse "bağlantı koptu" sayıp motorları durdurur
/// (bkz. docs/MQTT_PROTOKOL.md §3.4). Event-based gönderimde joystick/tuş sabit tutulurken yeni
/// mesaj üretilmez ve watchdog yürüyüşü yanlışlıkla keser. Bu sınıf, aktif bir hareket (Move/Turn)
/// sürerken son komutu sabit aralıkla (varsayılan 100 ms) yeniden yayınlayarak akışı "canlı" tutar.
/// Veri/PC koparsa akış durur → robot ~500 ms içinde güvenle durur.</para>
///
/// <para><b>Kullanım:</b> Girdi kaynakları yalnızca <see cref="SetMove"/> / <see cref="StopAsync"/>
/// çağırır; periyodik yayını bu sınıf üstlenir. Stop durumunda hiçbir şey yayınlanmaz (boşta trafik yok).
/// Anlık komutlar (ışık, kamera pan/tilt) streaming gerektirmez — onlar doğrudan IRobotProtocol
/// üzerinden gönderilmeli, bu sınıftan geçmemeli.</para>
/// </summary>
public sealed class RobotDriveStreamer : IAsyncDisposable
{
    private readonly IRobotProtocol _robot;
    private static readonly TimeSpan StreamInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _lock = new();
    private RobotCommandType _moveType = RobotCommandType.Stop;
    private float _moveValue;

    private readonly CancellationTokenSource _cts = new();

    /// <summary>Yayın hatası olduğunda (örn. geçici kopma) bilgi verir. UI loglayabilir.</summary>
    public event EventHandler<string>? StreamError;

    public RobotDriveStreamer(IRobotProtocol robot)
    {
        _robot = robot;
        _ = StreamLoop(_cts.Token);
    }

    /// <summary>
    /// Aktif hareketi günceller. Yalnızca yerel durumu set eder (ucuz, ağ I/O yok); gerçek yayını
    /// streaming döngüsü 100 ms'de bir yapar. İstediğin sıklıkta güvenle çağırabilirsin.
    /// </summary>
    public void SetMove(RobotCommandType type, float value)
    {
        lock (_lock)
        {
            _moveType = type;
            _moveValue = value;
        }
    }

    /// <summary>
    /// Sürüşü durdurur: streaming akışını keser ve anında bir <c>Stop</c> komutu yayınlar
    /// (watchdog'u beklemeden robotun hemen durması için).
    /// </summary>
    public async Task StopAsync()
    {
        lock (_lock)
        {
            _moveType = RobotCommandType.Stop;
            _moveValue = 0f;
        }

        if (!_robot.IsConnected) return;
        try
        {
            await _robot.SendAsync(new RobotCommand(RobotCommandType.Stop));
        }
        catch (Exception ex)
        {
            StreamError?.Invoke(this, $"Stop yayınlanamadı: {ex.Message}");
        }
    }

    private async Task StreamLoop(CancellationToken ct)
    {
        var timer = new PeriodicTimer(StreamInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                RobotCommandType type;
                float value;
                lock (_lock)
                {
                    type = _moveType;
                    value = _moveValue;
                }

                if (type == RobotCommandType.Stop) continue; // boşta sessiz
                if (!_robot.IsConnected) continue;

                try
                {
                    await _robot.SendAsync(new RobotCommand(type, value));
                }
                catch
                {
                    // Geçici yayın hatası — bir sonraki tick'te yeniden denenir (akış sürekli)
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose ile normal durdurma
        }
        finally
        {
            timer.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await StopAsync(); } catch { /* kapanışta yoksay */ }
        _cts.Dispose();
    }
}
