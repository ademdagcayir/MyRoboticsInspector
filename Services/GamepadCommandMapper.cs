using MyRoboticsInspector.Models;

namespace MyRoboticsInspector.Services;

/// <summary>
/// F710 layout → sürüş/aksiyon çevrimi. Sürüş komutlarını doğrudan yaymaz; <see cref="RobotDriveStreamer"/>
/// üzerinden aktif hareketi günceller — streaming/watchdog akışını streamer üstlenir (bkz.
/// RobotDriveStreamer ve docs/MQTT_PROTOKOL.md §3.4). Topic protokol şeması MQTT_PROTOKOL.md §3'e uyumlu.
///
/// Varsayılan mapping (F710, XInput mode):
///   Sol stick   : robot sürüş (Y = ileri/geri, |X| > |Y| iken X = sola/sağa dönüş)
///   Sağ stick   : kamera pan/tilt (varsa) — anlık komut, streaming dışı
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
    private readonly RobotDriveStreamer _drive;

    public event EventHandler<string>? StatusMessage;
    public event EventHandler? SnapshotRequested;
    public event EventHandler? EmergencyStopRequested;
    public event EventHandler? ToggleLightRequested;
    public event EventHandler? MarkDefectRequested;
    public event EventHandler? ToggleRecordingRequested;
    public event EventHandler? ToggleInspectionRequested;

    public GamepadCommandMapper(IGamepadInput input, IRobotProtocol robot, RobotDriveStreamer drive)
    {
        _input = input;
        _robot = robot;
        _drive = drive;
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

        // Emniyetli ayrılma — sürüşü durdur
        _ = _drive.StopAsync();
    }

    private void OnButtonPressed(object? sender, GamepadButton b)
    {
        switch (b)
        {
            case GamepadButton.A:     SnapshotRequested?.Invoke(this, EventArgs.Empty); break;
            case GamepadButton.B:     EmergencyStopRequested?.Invoke(this, EventArgs.Empty);
                                      _ = _drive.StopAsync(); break;
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
        if (s.IsDown(GamepadButton.DPadUp))    { _drive.SetMove(RobotCommandType.MoveForward,  0.25f); return; }
        if (s.IsDown(GamepadButton.DPadDown))  { _drive.SetMove(RobotCommandType.MoveBackward, 0.25f); return; }
        if (s.IsDown(GamepadButton.DPadLeft))  { _drive.SetMove(RobotCommandType.TurnLeft,     0.25f); return; }
        if (s.IsDown(GamepadButton.DPadRight)) { _drive.SetMove(RobotCommandType.TurnRight,    0.25f); return; }

        // 2) Sol stick — tank stili: hangisi büyükse o eksen
        float lx = s.LeftStickX;
        float ly = s.LeftStickY;
        float ax = Math.Abs(lx), ay = Math.Abs(ly);

        if (ax < 0.05f && ay < 0.05f)
        {
            // Stick merkeze döndü → Stop
            _ = _drive.StopAsync();
        }
        else if (ay >= ax)
        {
            // Daha çok ileri/geri
            var type = ly > 0 ? RobotCommandType.MoveForward : RobotCommandType.MoveBackward;
            var val = Math.Clamp(ay * speedScale, 0f, 1f);
            _drive.SetMove(type, val);
        }
        else
        {
            // Daha çok sola/sağa dönüş
            var type = lx > 0 ? RobotCommandType.TurnRight : RobotCommandType.TurnLeft;
            var val = Math.Clamp(ax * speedScale, 0f, 1f);
            _drive.SetMove(type, val);
        }

        // 3) Sağ stick — kamera pan/tilt (anlık komut, streaming dışı; sadece anlamlı değişimde yay)
        if (_robot.IsConnected)
        {
            if (Math.Abs(s.RightStickX) > 0.5f)
                _ = _robot.SendAsync(new RobotCommand(RobotCommandType.CameraPan, s.RightStickX * 30f));
            if (Math.Abs(s.RightStickY) > 0.5f)
                _ = _robot.SendAsync(new RobotCommand(RobotCommandType.CameraTilt, s.RightStickY * 30f));
        }
    }
}
