using MyRoboticsInspector.Models;

namespace MyRoboticsInspector.Services;

/// <summary>
/// F710 layout → RobotCommand çevrimi. Sürekli komut spam'i yapmaz; sadece "anlamlı değişiklik"te
/// IRobotProtocol.SendAsync() çağırır. Topic protokol şeması MQTT_PROTOKOL.md §3'e uyumlu.
///
/// Varsayılan mapping (F710, XInput mode):
///   Sol stick   : robot sürüş (Y = ileri/geri, |X| > |Y| iken X = sola/sağa dönüş)
///   Sağ stick   : kamera pan/tilt (varsa)
///   RT          : "yüksek hız" çarpanı (basılıyken stick magnitude'una 1.0x katsayı)
///   LT          : "yavaş hız" çarpanı (basılıyken 0.3x — hassas hareket)
///   A           : Snapshot
///   B           : Acil durdurma (Stop, hemen)
///   X           : Işık aç/kapa
///   Y           : Kusur ekle (modal açar)
///   Start       : Kayıt aç/kapa
///   Back        : İnceleme başlat/bitir
///   LB / RB     : (rezerve) zoom-/zoom+
///   DPad        : Yavaş diskret hareket (yan stick alternatifi)
/// </summary>
public class GamepadCommandMapper
{
    private readonly IGamepadInput _input;
    private readonly IRobotProtocol _robot;

    public event EventHandler<string>? StatusMessage;
    public event EventHandler? SnapshotRequested;
    public event EventHandler? EmergencyStopRequested;
    public event EventHandler? ToggleLightRequested;
    public event EventHandler? MarkDefectRequested;
    public event EventHandler? ToggleRecordingRequested;
    public event EventHandler? ToggleInspectionRequested;

    // Hareket throttle: aynı komutu sürekli göndermeyelim
    private RobotCommandType _lastMoveType = RobotCommandType.Stop;
    private float _lastMoveValue;
    private DateTime _lastMoveSentAt = DateTime.MinValue;
    private static readonly TimeSpan MinResendInterval = TimeSpan.FromMilliseconds(120);
    private const float MovementHysteresis = 0.10f; // hız 0.10'dan az değiştiyse yeniden yayma

    public GamepadCommandMapper(IGamepadInput input, IRobotProtocol robot)
    {
        _input = input;
        _robot = robot;
    }

    public void Attach()
    {
        _input.StateChanged   += OnStateChanged;
        _input.ButtonPressed  += OnButtonPressed;
    }

    public void Detach()
    {
        _input.StateChanged   -= OnStateChanged;
        _input.ButtonPressed  -= OnButtonPressed;

        // Emniyetli ayrılma — son hareket Stop olsun
        _ = TrySendMove(RobotCommandType.Stop, 0f, force: true);
    }

    private void OnButtonPressed(object? sender, GamepadButton b)
    {
        switch (b)
        {
            case GamepadButton.A:     SnapshotRequested?.Invoke(this, EventArgs.Empty); break;
            case GamepadButton.B:     EmergencyStopRequested?.Invoke(this, EventArgs.Empty);
                                      _ = TrySendMove(RobotCommandType.Stop, 0f, force: true); break;
            case GamepadButton.X:     ToggleLightRequested?.Invoke(this, EventArgs.Empty); break;
            case GamepadButton.Y:     MarkDefectRequested?.Invoke(this, EventArgs.Empty); break;
            case GamepadButton.Start: ToggleRecordingRequested?.Invoke(this, EventArgs.Empty); break;
            case GamepadButton.Back:  ToggleInspectionRequested?.Invoke(this, EventArgs.Empty); break;
        }
    }

    private void OnStateChanged(object? sender, GamepadState s)
    {
        // Tetikleyici çarpanları
        float speedScale = 1.0f;
        if (s.IsDown(GamepadButton.LT)) speedScale = 0.3f; // yavaş mod
        else if (s.IsDown(GamepadButton.RT)) speedScale = 1.0f; // tam hız
        else speedScale = 0.6f; // varsayılan: %60 (operatör için güvenli)

        // 1) DPad öncelikli (diskret düşük hız hareket)
        if (s.IsDown(GamepadButton.DPadUp))
        {
            _ = TrySendMove(RobotCommandType.MoveForward, 0.25f);
            return;
        }
        if (s.IsDown(GamepadButton.DPadDown))
        {
            _ = TrySendMove(RobotCommandType.MoveBackward, 0.25f);
            return;
        }
        if (s.IsDown(GamepadButton.DPadLeft))
        {
            _ = TrySendMove(RobotCommandType.TurnLeft, 0.25f);
            return;
        }
        if (s.IsDown(GamepadButton.DPadRight))
        {
            _ = TrySendMove(RobotCommandType.TurnRight, 0.25f);
            return;
        }

        // 2) Sol stick — tank stili: hangisi büyükse o eksen
        float lx = s.LeftStickX;
        float ly = s.LeftStickY;
        float ax = Math.Abs(lx), ay = Math.Abs(ly);

        if (ax < 0.05f && ay < 0.05f)
        {
            // Stick merkeze döndü → Stop (hatalı durmama için force)
            _ = TrySendMove(RobotCommandType.Stop, 0f);
        }
        else if (ay >= ax)
        {
            // Daha çok ileri/geri
            var type = ly > 0 ? RobotCommandType.MoveForward : RobotCommandType.MoveBackward;
            var val = Math.Clamp(ay * speedScale, 0f, 1f);
            _ = TrySendMove(type, val);
        }
        else
        {
            // Daha çok sola/sağa dönüş
            var type = lx > 0 ? RobotCommandType.TurnRight : RobotCommandType.TurnLeft;
            var val = Math.Clamp(ax * speedScale, 0f, 1f);
            _ = TrySendMove(type, val);
        }

        // 3) Sağ stick — kamera pan/tilt (sadece anlamlı değişim varsa yay)
        if (Math.Abs(s.RightStickX) > 0.5f)
        {
            // İleride: pan açısı entegrasyonu. Şimdilik yalnızca lateral hareket eşiğinde
            // CameraPan komutu olarak ham değer gönder.
            _ = _robot.SendAsync(new RobotCommand(RobotCommandType.CameraPan, s.RightStickX * 30f));
        }
        if (Math.Abs(s.RightStickY) > 0.5f)
        {
            _ = _robot.SendAsync(new RobotCommand(RobotCommandType.CameraTilt, s.RightStickY * 30f));
        }
    }

    /// <summary>
    /// Aynı komutu sürekli yaymamak için hysteresis + throttle uygula. Stop her zaman geçer.
    /// </summary>
    private async Task TrySendMove(RobotCommandType type, float value, bool force = false)
    {
        var now = DateTime.UtcNow;
        var typeChanged = type != _lastMoveType;
        var valueDelta = Math.Abs(value - _lastMoveValue);
        var stopRequested = type == RobotCommandType.Stop;
        var shouldSend = force
            || typeChanged
            || stopRequested && _lastMoveType != RobotCommandType.Stop
            || (valueDelta >= MovementHysteresis && (now - _lastMoveSentAt) >= MinResendInterval);

        if (!shouldSend) return;
        if (!_robot.IsConnected) return;

        try
        {
            await _robot.SendAsync(new RobotCommand(type, stopRequested ? null : value));
            _lastMoveType = type;
            _lastMoveValue = value;
            _lastMoveSentAt = now;
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"Joystick komut hatası: {ex.Message}");
        }
    }
}
