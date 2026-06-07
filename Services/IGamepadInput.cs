namespace MyRoboticsInspector.Services;

/// <summary>
/// Logitech F710 (XInput modunda) ya da Xbox-uyumlu herhangi bir gamepad'in soyutlaması.
/// Implementasyonlar: <see cref="XInputGamepadService"/> (Windows). Android için ileride
/// AndroidGamepadService eklenebilir; arayüz aynı kalır.
/// </summary>
public interface IGamepadInput : IDisposable
{
    /// <summary>Şu an aktif (slot 0) bir gamepad bağlı mı (XInput slot SUCCESS).</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gerçek bağlantı durumu. XInput'ta SUCCESS yalnızca "slot bağlı" demektir, "veri akıyor"
    /// demek DEĞİLDİR — F710 dongle'ı takılıyken kumanda uyusa bile SUCCESS döner (hepsi 0).
    /// dwPacketNumber watchdog'u ile gerçek durum: Disconnected / Live / Stale (alıcı bağlı,
    /// kumanda uyanmıyor). Varsayılan: IsConnected'e göre türetilmiş (Live/Disconnected).
    /// </summary>
    GamepadLink Link => IsConnected ? GamepadLink.Live : GamepadLink.Disconnected;

    /// <summary>Link durumu değişince bildirim (Live ↔ Stale ↔ Disconnected).</summary>
    event EventHandler<GamepadLink>? LinkChanged;

    /// <summary>Poll loop çalışıyor mu (StartPolling/StopPolling ile kontrol).</summary>
    bool IsPolling { get; }

    GamepadState CurrentState { get; }

    /// <summary>Teşhis: XInput slot bağlantı özeti (UI'da gösterilir). Varsayılan boş.</summary>
    string SlotSummary => "";

    event EventHandler<bool>? ConnectionChanged;

    /// <summary>Tüm state değişikliği için (axis dahil) tek bildirim.</summary>
    event EventHandler<GamepadState>? StateChanged;

    /// <summary>Her poll tick'te ham state — UI debug göstergesi için, filtrelenmemiş.</summary>
    event EventHandler<GamepadState>? RawTick;

    /// <summary>Tek seferlik buton press tetikleyici (kenar tetikleme: down sırasında).</summary>
    event EventHandler<GamepadButton>? ButtonPressed;

    /// <summary>Buton release (yukarı kalkma).</summary>
    event EventHandler<GamepadButton>? ButtonReleased;

    void StartPolling();
    void StopPolling();
}

/// <summary>
/// Gamepad gerçek bağlantı durumu (dwPacketNumber watchdog ile belirlenir).
/// </summary>
public enum GamepadLink
{
    /// <summary>Slot bağlı değil (XInput ERROR_DEVICE_NOT_CONNECTED).</summary>
    Disconnected,
    /// <summary>Bağlı ve veri akıyor (paket ilerliyor / ham girdi var).</summary>
    Live,
    /// <summary>Alıcı bağlı ama kumanda uyuyor/ölü — paket donmuş + veri tam nötr. "Bir tuşa basın".</summary>
    Stale,
}

[Flags]
public enum GamepadButton
{
    None       = 0,
    DPadUp     = 1 << 0,
    DPadDown   = 1 << 1,
    DPadLeft   = 1 << 2,
    DPadRight  = 1 << 3,
    Start      = 1 << 4,
    Back       = 1 << 5,
    LStick     = 1 << 6,   // sol stick basılı
    RStick     = 1 << 7,   // sağ stick basılı
    LB         = 1 << 8,
    RB         = 1 << 9,
    A          = 1 << 12,
    B          = 1 << 13,
    X          = 1 << 14,
    Y          = 1 << 15,
    // Sentetik: tetikleyici eşik geçince
    LT         = 1 << 16,
    RT         = 1 << 17,
}

/// <summary>
/// Tek bir poll anındaki gamepad durumu. Tüm axis değerleri -1.0..+1.0 normalize edilmiş;
/// tetikleyiciler 0.0..1.0. Stick'lerde dead zone uygulanmıştır.
/// </summary>
public readonly record struct GamepadState(
    bool IsConnected,
    float LeftStickX,
    float LeftStickY,
    float RightStickX,
    float RightStickY,
    float LeftTrigger,
    float RightTrigger,
    GamepadButton ButtonsDown)
{
    public static GamepadState Disconnected => new(false, 0, 0, 0, 0, 0, 0, GamepadButton.None);

    public bool IsDown(GamepadButton b) => (ButtonsDown & b) == b;
}
