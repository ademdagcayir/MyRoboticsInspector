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

    // Kamera kafası son gönderilen değerler (tekrar yaymayı önlemek için)
    private int _lastHead180 = int.MinValue;
    private int _lastHead360 = int.MinValue;

    private void OnStateChanged(object? sender, GamepadState s)
    {
        // NOT: Firmware sürüşü bang-bang (eşik ≥90/80) — ara hız yok, tetikleyici çarpanı anlamsız.
        // Streamer baskın ekseni seçip ±100'e çevirir; işaret tersini de o uygular.

        // 1) DPad öncelikli — diskret tam sürüş (firmware ara hız yapamaz → tam ya da dur)
        if (s.IsDown(GamepadButton.DPadUp))         _drive.SetMove(RobotCommandType.MoveForward,  1f);
        else if (s.IsDown(GamepadButton.DPadDown))  _drive.SetMove(RobotCommandType.MoveBackward, 1f);
        else if (s.IsDown(GamepadButton.DPadLeft))  _drive.SetMove(RobotCommandType.TurnLeft,     1f);
        else if (s.IsDown(GamepadButton.DPadRight)) _drive.SetMove(RobotCommandType.TurnRight,    1f);
        else
        {
            // 2) SAĞ stick — sürüş: Y = ileri/geri (yukarı = ileri), X = sağ/sol.
            //    Streamer |throttle| ≥ 0.5 / baskın eksen kuralıyla ±100'e çevirir.
            float thr = Math.Abs(s.RightStickY) < 0.06f ? 0f : s.RightStickY;
            float str = Math.Abs(s.RightStickX) < 0.06f ? 0f : s.RightStickX;
            _drive.SetDrive(thr, str);
        }

        // 3) SOL stick — kamera kafası (anlık): Y = 180 tilt, X = 360 dönüş.
        //    Firmware eşiği |değer| ≥ 35 → ölü bölge 0.35 (altı zaten hareket etmez).
        if (_robot.IsConnected)
        {
            int tilt = Math.Abs(s.LeftStickY) < 0.35f ? 0 : (int)Math.Round(s.LeftStickY * 100f);
            int rot  = Math.Abs(s.LeftStickX) < 0.35f ? 0 : (int)Math.Round(s.LeftStickX * 100f);
            if (tilt != _lastHead180) { _ = _robot.PublishRawAsync(FirmwareTopics.Head180UpDown, tilt.ToString()); _lastHead180 = tilt; }
            if (rot  != _lastHead360) { _ = _robot.PublishRawAsync(FirmwareTopics.Head360CwCcw,  rot.ToString());  _lastHead360 = rot; }
        }
    }
}
