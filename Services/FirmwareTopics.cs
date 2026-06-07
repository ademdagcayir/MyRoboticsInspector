namespace MyRoboticsInspector.Services;

/// <summary>
/// MyRoboticsFirmware (Arduino Mega + Ethernet/MQTT) ile BİREBİR uyumlu MQTT topic'leri.
/// Firmware "bare" topic + düz tamsayı payload (atoi) kullanır — ÖN EK YOK.
///
/// <para><b>Robot abone:</b> forward_backward, left_right, 180_up_down, 360_CW_CCW, brake, led,
/// panel/180, panel/360, robot/diag.  <b>Robot yayın:</b> accl_x (eğim, ham ADC, Δ&gt;10),
/// pressure (ham, Δ&gt;30), voltage (ham, Δ&gt;10), robot/log.</para>
/// <para><b>Pano abone:</b> forward_backward, gerisarma, metre_sifir, voltage, pano/diag.
/// <b>Pano yayın:</b> metre (encoder/200, Δ&gt;30), ayrıca fiziksel ileri/geri butonuyla
/// forward_backward (-100/+100), pano/log.</para>
///
/// <para><b>SÜRÜŞ — bang-bang + TERS işaret (yuruyus_ileri_geri.ino):</b>
/// forward_backward ≤ -90 → İLERİ, ≥ +90 → GERİ. left_right ≤ -80 → SAĞ, ≥ +80 → SOL,
/// ve dönüş için forward_backward=0 ŞART (yürü+dön aynı anda olmaz). brake=1 motorları kilitler.
/// Watchdog: yalnızca forward_backward/left_right/brake besler; ~500 ms gelmezse motor durur.</para>
/// <para><b>IŞIK:</b> Mevcut firmware joystick/led'i KULLANMIYOR — ışık A2 basınç≥350 ile otomatik.
/// KAMERA KAFA: 180_up_down / 360_CW_CCW eşiği |değer| ≥ 35.</para>
/// </summary>
public static class FirmwareTopics
{
    // ── Robot — sürüş (değer -100..+100) ──
    public const string ForwardBackward = "joystick/forward_backward"; // Sağ Y: ileri(+)/geri(-)
    public const string LeftRight        = "joystick/left_right";       // Sağ X: sağ(+)/sol(-)
    public const string Brake            = "joystick/brake";            // fren (0/1 veya 0..100)

    // ── Robot — kamera kafası ──
    public const string Head180UpDown    = "joystick/180_up_down";      // Sol Y: tilt
    public const string Head360CwCcw      = "joystick/360_CW_CCW";       // Sol X: dönüş

    // ── Robot — ışık ──
    public const string Led               = "joystick/led";              // 0/1

    // ── Pano — makara ──
    public const string Gerisarma         = "joystick/gerisarma";        // tambur geri sarma
    public const string MetreSifir        = "metre_sifir";               // metre sıfırla

    // ── Teşhis istek ──
    public const string RobotDiag         = "robot/diag";
    public const string PanoDiag          = "pano/diag";

    // ── Telemetri (robot/pano → PC) ──
    public const string AcclX             = "accl_x";    // eğim, ham ADC 0..1023
    public const string Pressure          = "pressure";  // basınç, ham ADC
    public const string Voltage           = "voltage";   // voltaj, ham ADC
    public const string Metre             = "metre";     // metre (gerçek, encoder/200)
    public const string RobotLog          = "robot/log";
    public const string PanoLog           = "pano/log";

    // ── Heartbeat (canlılık) — cihaz her ~1 sn yayınlar (uptime sn), retained değil ──
    public const string RobotAlive        = "robot/alive";
    public const string PanoAlive         = "pano/alive";

    /// <summary>PC'nin abone olması gereken telemetri topic'leri.</summary>
    public static readonly string[] TelemetrySubscriptions =
        { AcclX, Pressure, Voltage, Metre };
}
