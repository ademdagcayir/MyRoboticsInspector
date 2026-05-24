using System.Runtime.InteropServices;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Windows native XInput poll loop. Çalıştığı tek slot: 0 (ilk gamepad).
/// F710'u XInput moduna (arkadaki anahtar "X") al; aksi DirectInput modunda görünmez.
///
/// 30 Hz polling, hardware'i bulunca event'ler raise eder. Diğer platformlarda
/// (Android/Mac) çağrılar emniyetle null/false döndürür ve hiçbir şey yapmaz.
/// </summary>
public class XInputGamepadService : IGamepadInput
{
    private const float StickDeadZone = 0.15f;      // sol/sağ stick: |val| < 0.15 = sıfır
    private const float TriggerThreshold = 0.12f;   // LT/RT bu eşiği geçince "basılı" sayılır

    private readonly System.Threading.Lock _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public bool IsConnected { get; private set; }
    public bool IsPolling { get; private set; }
    public GamepadState CurrentState { get; private set; } = GamepadState.Disconnected;

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<GamepadState>? StateChanged;
    public event EventHandler<GamepadButton>? ButtonPressed;
    public event EventHandler<GamepadButton>? ButtonReleased;

    public void StartPolling()
    {
        lock (_lock)
        {
            if (IsPolling) return;
            if (!OperatingSystem.IsWindows())
            {
                // XInput Windows-only; sessizce no-op.
                IsConnected = false;
                IsPolling = false;
                return;
            }
            _cts = new CancellationTokenSource();
            IsPolling = true;
            _loopTask = Task.Run(() => PollLoop(_cts.Token));
        }
    }

    public void StopPolling()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_lock)
        {
            cts = _cts; _cts = null;
            loop = _loopTask; _loopTask = null;
            IsPolling = false;
        }
        try { cts?.Cancel(); } catch { }
        try { loop?.Wait(500); } catch { }
        cts?.Dispose();
    }

    private async Task PollLoop(CancellationToken ct)
    {
        var prev = GamepadState.Disconnected;
        var period = TimeSpan.FromMilliseconds(33); // ~30 Hz
        using var timer = new PeriodicTimer(period);

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            GamepadState curr;
            try { curr = ReadController(0); }
            catch { curr = GamepadState.Disconnected; }

            if (curr.IsConnected != prev.IsConnected)
            {
                IsConnected = curr.IsConnected;
                ConnectionChanged?.Invoke(this, curr.IsConnected);
            }

            // Buton press/release edge detection
            var down = curr.ButtonsDown & ~prev.ButtonsDown; // newly pressed
            var up   = prev.ButtonsDown & ~curr.ButtonsDown; // newly released
            EmitButtonEdges(down, ButtonPressed);
            EmitButtonEdges(up,   ButtonReleased);

            if (HasMeaningfulChange(prev, curr))
            {
                CurrentState = curr;
                StateChanged?.Invoke(this, curr);
            }

            prev = curr;
        }
    }

    private static bool HasMeaningfulChange(GamepadState a, GamepadState b)
    {
        // Axis için 0.02 eşik altı değişikliği yutarız (gürültü)
        const float eps = 0.02f;
        return a.IsConnected != b.IsConnected
            || a.ButtonsDown != b.ButtonsDown
            || Math.Abs(a.LeftStickX - b.LeftStickX) > eps
            || Math.Abs(a.LeftStickY - b.LeftStickY) > eps
            || Math.Abs(a.RightStickX - b.RightStickX) > eps
            || Math.Abs(a.RightStickY - b.RightStickY) > eps
            || Math.Abs(a.LeftTrigger - b.LeftTrigger) > eps
            || Math.Abs(a.RightTrigger - b.RightTrigger) > eps;
    }

    private void EmitButtonEdges(GamepadButton mask, EventHandler<GamepadButton>? handler)
    {
        if (mask == GamepadButton.None || handler is null) return;
        foreach (GamepadButton b in Enum.GetValues<GamepadButton>())
        {
            if (b == GamepadButton.None) continue;
            if ((mask & b) == b)
            {
                try { handler(this, b); } catch { }
            }
        }
    }

    // ---- XInput P/Invoke ----

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState14(int dwUserIndex, ref XINPUT_STATE pState);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState91(int dwUserIndex, ref XINPUT_STATE pState);

    // XInput button bitfield (Microsoft headers)
    private const ushort XI_DPAD_UP        = 0x0001;
    private const ushort XI_DPAD_DOWN      = 0x0002;
    private const ushort XI_DPAD_LEFT      = 0x0004;
    private const ushort XI_DPAD_RIGHT     = 0x0008;
    private const ushort XI_START          = 0x0010;
    private const ushort XI_BACK           = 0x0020;
    private const ushort XI_LSTICK         = 0x0040;
    private const ushort XI_RSTICK         = 0x0080;
    private const ushort XI_LB             = 0x0100;
    private const ushort XI_RB             = 0x0200;
    private const ushort XI_A              = 0x1000;
    private const ushort XI_B              = 0x2000;
    private const ushort XI_X              = 0x4000;
    private const ushort XI_Y              = 0x8000;

    private static int TryXInput(int idx, ref XINPUT_STATE state)
    {
        try { return XInputGetState14(idx, ref state); }
        catch (DllNotFoundException)
        {
            try { return XInputGetState91(idx, ref state); }
            catch { return -1; }
        }
        catch { return -1; }
    }

    private static GamepadState ReadController(int idx)
    {
        var state = new XINPUT_STATE();
        var res = TryXInput(idx, ref state);
        const int ERROR_SUCCESS = 0;
        if (res != ERROR_SUCCESS) return GamepadState.Disconnected;

        var g = state.Gamepad;

        var buttons = GamepadButton.None;
        if ((g.wButtons & XI_DPAD_UP)    != 0) buttons |= GamepadButton.DPadUp;
        if ((g.wButtons & XI_DPAD_DOWN)  != 0) buttons |= GamepadButton.DPadDown;
        if ((g.wButtons & XI_DPAD_LEFT)  != 0) buttons |= GamepadButton.DPadLeft;
        if ((g.wButtons & XI_DPAD_RIGHT) != 0) buttons |= GamepadButton.DPadRight;
        if ((g.wButtons & XI_START)      != 0) buttons |= GamepadButton.Start;
        if ((g.wButtons & XI_BACK)       != 0) buttons |= GamepadButton.Back;
        if ((g.wButtons & XI_LSTICK)     != 0) buttons |= GamepadButton.LStick;
        if ((g.wButtons & XI_RSTICK)     != 0) buttons |= GamepadButton.RStick;
        if ((g.wButtons & XI_LB)         != 0) buttons |= GamepadButton.LB;
        if ((g.wButtons & XI_RB)         != 0) buttons |= GamepadButton.RB;
        if ((g.wButtons & XI_A)          != 0) buttons |= GamepadButton.A;
        if ((g.wButtons & XI_B)          != 0) buttons |= GamepadButton.B;
        if ((g.wButtons & XI_X)          != 0) buttons |= GamepadButton.X;
        if ((g.wButtons & XI_Y)          != 0) buttons |= GamepadButton.Y;

        var lx = Normalize(g.sThumbLX);
        var ly = Normalize(g.sThumbLY);
        var rx = Normalize(g.sThumbRX);
        var ry = Normalize(g.sThumbRY);
        var lt = g.bLeftTrigger  / 255f;
        var rt = g.bRightTrigger / 255f;

        if (lt > TriggerThreshold) buttons |= GamepadButton.LT;
        if (rt > TriggerThreshold) buttons |= GamepadButton.RT;

        return new GamepadState(true,
            ApplyDeadZone(lx), ApplyDeadZone(ly),
            ApplyDeadZone(rx), ApplyDeadZone(ry),
            lt, rt, buttons);
    }

    private static float Normalize(short v)
    {
        // -32768..32767 -> -1.0..+1.0
        return v < 0 ? v / 32768f : v / 32767f;
    }

    private static float ApplyDeadZone(float v)
    {
        if (Math.Abs(v) < StickDeadZone) return 0f;
        // Dead zone'dan sonra doğrusal rescale et — kenarda 1.0 kalsın
        var sign = Math.Sign(v);
        return sign * (Math.Abs(v) - StickDeadZone) / (1f - StickDeadZone);
    }

    public void Dispose()
    {
        StopPolling();
        GC.SuppressFinalize(this);
    }
}
