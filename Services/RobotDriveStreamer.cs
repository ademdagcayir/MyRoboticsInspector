using MyRoboticsInspector.Models;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Tüm sürüş girdilerinin (gamepad, klavye, ekran dpad, test tezgahı) ortak çıkış katmanı.
///
/// <para><b>MyRoboticsFirmware gerçek protokolü (robot/yuruyus_ileri_geri.ino):</b>
/// Sürüş <b>bang-bang</b>'tir, oransal değil. Robot motoru ancak şu eşiklerde döner:</para>
/// <list type="bullet">
///   <item><c>forward_backward ≤ -90</c> → İLERİ  (negatif = ileri!)</item>
///   <item><c>forward_backward ≥ +90</c> → GERİ   (pozitif = geri!)</item>
///   <item><c>left_right ≤ -80</c> ve <c>forward_backward == 0</c> → SAĞ dönüş</item>
///   <item><c>left_right ≥ +80</c> ve <c>forward_backward == 0</c> → SOL dönüş</item>
/// </list>
/// <para>Yürüyüş ve dönüş <b>aynı anda olmaz</b> (dönüş için fb=0 şart). Bu yüzden burada
/// baskın eksen seçilir: |throttle| ≥ |steer| ise yürü, değilse dön. Ara değerler robotu
/// hareket ettirmediği için ±100 (tam) yayınlanır.</para>
///
/// <para><b>İç eksen sözleşmesi (sezgisel):</b> throttle&gt;0 = ileri, steer&gt;0 = sağ.
/// Firmware'e çevirirken işaret ters çevrilir (ileri→fb negatif, sağ→lr negatif).</para>
///
/// <para><b>Watchdog:</b> Robot ~500 ms sürüş komutu gelmezse motorları durdurur. Bu sınıf
/// aktif sürüşte son değeri 100 ms'de bir yeniden yayınlayarak akışı canlı tutar; sürüş
/// bırakılınca bir kez 0 yayınlar (anında durur), sonra sessizleşir.</para>
/// </summary>
public sealed class RobotDriveStreamer : IAsyncDisposable
{
    private readonly IRobotProtocol _robot;
    private static readonly TimeSpan StreamInterval = TimeSpan.FromMilliseconds(100);

    // Bang-bang eşikleri için stick eşiği (yarıdan fazla itince hareket)
    private const float ActivateThreshold = 0.5f;

    private readonly object _lock = new();
    private float _throttle;  // -1..+1  (ileri +, geri -)  → forward_backward'a TERS çevrilir
    private float _steer;     // -1..+1  (sağ +, sol -)     → left_right'a TERS çevrilir
    private bool _wasMoving;
    // Acil dur yarışına karşı: StopAsync her çağrıda artırır; StreamLoop tick başında okuduğu
    // değer yayın anında değişmişse (stop araya girmişse) bayat komutu YAYINLAMAZ.
    private int _stopGeneration;
    // StopAsync ile StreamLoop'un publish demetlerini serileştirir (brake'ten SONRA fb=±100 sızmasın).
    private readonly SemaphoreSlim _pubGate = new(1, 1);

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;
    public event EventHandler<string>? StreamError;

    /// <summary>UI/teşhis için en son firmware'e yayınlanan ham değerler.</summary>
    public int LastForwardBackward { get; private set; }
    public int LastLeftRight { get; private set; }

    public RobotDriveStreamer(IRobotProtocol robot)
    {
        _robot = robot;
        // UI thread'inde resolve edilse bile döngü thread-pool'da çalışmalı (XInputGamepadService kalıbı):
        // aksi halde 10 Hz watchdog beslemesi UI'nın boş olmasına bağlı kalır.
        _loopTask = Task.Run(() => StreamLoop(_cts.Token));
    }

    /// <summary>Oransal girdi (gamepad sağ stick): throttle = Y (ileri+), steer = X (sağ+). -1..+1.</summary>
    public void SetDrive(float throttle, float steer)
    {
        lock (_lock)
        {
            _throttle = Math.Clamp(throttle, -1f, 1f);
            _steer    = Math.Clamp(steer,    -1f, 1f);
        }
    }

    /// <summary>Diskret sürüş (ekran dpad / klavye / test). Firmware bang-bang olduğu için tam (±1) uygulanır.</summary>
    public void SetMove(RobotCommandType type, float value)
    {
        float mag = Math.Abs(value) < 0.01f ? 0f : 1f; // firmware ara hız yapamaz → tam ya da dur
        lock (_lock)
        {
            switch (type)
            {
                case RobotCommandType.MoveForward:  _throttle = +mag; _steer = 0; break;
                case RobotCommandType.MoveBackward: _throttle = -mag; _steer = 0; break;
                case RobotCommandType.TurnRight:    _steer = +mag; _throttle = 0; break;
                case RobotCommandType.TurnLeft:     _steer = -mag; _throttle = 0; break;
                default:                            _throttle = 0; _steer = 0; break;
            }
        }
    }

    /// <summary>İç eksen (throttle/steer) → firmware (fb/lr). Baskın eksen seçilir; işaret ters çevrilir.</summary>
    private static (int fb, int lr) ToFirmware(float throttle, float steer)
    {
        if (Math.Abs(throttle) >= Math.Abs(steer) && Math.Abs(throttle) >= ActivateThreshold)
            return (throttle > 0 ? -100 : 100, 0);          // ileri → fb negatif
        if (Math.Abs(steer) >= ActivateThreshold)
            return (0, steer > 0 ? -100 : 100);             // sağ → lr negatif, fb=0 (dönüş şartı)
        return (0, 0);
    }

    /// <summary>Sürüşü durdurur: durumu sıfırlar, anında forward_backward=0, left_right=0, brake=1 yayınlar.</summary>
    public async Task StopAsync()
    {
        lock (_lock)
        {
            _throttle = 0; _steer = 0; _wasMoving = false;
            _stopGeneration++; // StreamLoop'taki bekleyen tick'in bayat değeri yayınlamasını engeller
            LastForwardBackward = 0; LastLeftRight = 0;
        }
        if (!_robot.IsConnected) return;
        await _pubGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _robot.PublishRawAsync(FirmwareTopics.ForwardBackward, "0").ConfigureAwait(false);
            await _robot.PublishRawAsync(FirmwareTopics.LeftRight, "0").ConfigureAwait(false);
            await _robot.PublishRawAsync(FirmwareTopics.Brake, "1").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StreamError?.Invoke(this, $"Stop yayınlanamadı: {ex.Message}");
        }
        finally
        {
            _pubGate.Release();
        }
    }

    private async Task StreamLoop(CancellationToken ct)
    {
        var timer = new PeriodicTimer(StreamInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                float thr, str;
                int gen;
                lock (_lock) { thr = _throttle; str = _steer; gen = _stopGeneration; }

                var (fb, lr) = ToFirmware(thr, str);
                bool moving = fb != 0 || lr != 0;

                if (!_robot.IsConnected)
                {
                    lock (_lock) { _wasMoving = false; }
                    continue;
                }

                if (!moving)
                {
                    // Sürüş bırakıldı: bir kez 0 yay (anında dur), sonra sessiz (watchdog güvende).
                    bool publishZero;
                    lock (_lock)
                    {
                        publishZero = _wasMoving;
                        if (publishZero) { _wasMoving = false; LastForwardBackward = 0; LastLeftRight = 0; }
                    }
                    if (!publishZero) continue;

                    await _pubGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // Acil dur araya girdiyse zaten 0 + brake yayınlandı — tekrarlama.
                        lock (_lock) { if (gen != _stopGeneration) continue; }
                        try
                        {
                            await _robot.PublishRawAsync(FirmwareTopics.ForwardBackward, "0").ConfigureAwait(false);
                            await _robot.PublishRawAsync(FirmwareTopics.LeftRight, "0").ConfigureAwait(false);
                        }
                        catch { /* sonraki bırakışta yine denenir */ }
                    }
                    finally { _pubGate.Release(); }
                    continue;
                }

                await _pubGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // Acil dur (StopAsync) bu tick'in anlık görüntüsünden SONRA araya girdiyse
                    // bayat ±100 değerini brake'ten sonra yayınlamamak için tick atlanır.
                    lock (_lock)
                    {
                        if (gen != _stopGeneration) continue;
                        _wasMoving = true;
                        LastForwardBackward = fb; LastLeftRight = lr;
                    }
                    try
                    {
                        await _robot.PublishRawAsync(FirmwareTopics.ForwardBackward, fb.ToString()).ConfigureAwait(false);
                        await _robot.PublishRawAsync(FirmwareTopics.LeftRight, lr.ToString()).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Geçici yayın hatası — sonraki tick yeniden dener (akış sürekli)
                    }
                }
                finally { _pubGate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        finally { timer.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loopTask.ConfigureAwait(false); } catch { }
        try { await StopAsync().ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
