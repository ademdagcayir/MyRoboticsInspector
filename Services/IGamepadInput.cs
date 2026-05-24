namespace MyRoboticsInspector.Services;

/// <summary>
/// Logitech F710 (XInput modunda) ya da Xbox-uyumlu herhangi bir gamepad'in soyutlaması.
/// Implementasyonlar: <see cref="XInputGamepadService"/> (Windows). Android için ileride
/// AndroidGamepadService eklenebilir; arayüz aynı kalır.
/// </summary>
public interface IGamepadInput : IDisposable
{
    /// <summary>Şu an aktif (slot 0) bir gamepad bağlı mı.</summary>
    bool IsConnected { get; }

    /// <summary>Poll loop çalışıyor mu (StartPolling/StopPolling ile kontrol).</summary>
    bool IsPolling { get; }

    GamepadState CurrentState { get; }

    event EventHandler<bool>? ConnectionChanged;

    /// <summary>Tüm state değişikliği için (axis dahil) tek bildirim.</summary>
    event EventHandler<GamepadState>? StateChanged;

    /// <summary>Tek seferlik buton press tetikleyici (kenar tetikleme: down sırasında).</summary>
    event EventHandler<GamepadButton>? ButtonPressed;

    /// <summary>Buton release (yukarı kalkma).</summary>
    event EventHandler<GamepadButton>? ButtonReleased;

    void StartPolling();
    void StopPolling();
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
