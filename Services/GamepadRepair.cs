using System.Diagnostics;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Logitech F710 (VID_046D &amp; PID_C21F) için Windows PnP onarımı.
///
/// <para><b>Kök neden (sahada doğrulandı):</b> Tekrarlanan USB tak/çıkar + D/X mod değişimi,
/// Windows'ta onlarca <i>hayalet</i> (CM_PROB_PHANTOM) F710 cihaz düğümü biriktirir → hayalet
/// XInput slotları (0:✓1:✓2:✓3:✓) ve enumerasyon karışıklığı. Ayrıca alıcının ana düğümü
/// zaman zaman <c>CM_PROB_FAILED_INSTALL</c> ile takılır → "bağlı görünür ama veri gelmez".</para>
///
/// <para><b>Onarım:</b> Hayalet düğümleri kaldırır, hatalı düğümü kaldırıp yeniden taratır
/// (temiz sürücü kurulumu), sağlam düğümleri disable/enable eder (USB tak/çıkar eşdeğeri).
/// pnputil/Disable-PnpDevice <b>yönetici</b> ister → işlem yükseltilmiş bir PowerShell ile yapılır.</para>
///
/// <para><b>Kullanım:</b> Önce <see cref="NeedsRepairAsync"/> (yetkisiz, hızlı) ile gerek var mı
/// bak; varsa <see cref="RunElevatedAsync"/> (UAC) çağır. Böylece her "Yeniden Bağla"da gereksiz
/// UAC çıkmaz — yalnızca gerçekten hayalet/hata varken yükseltme istenir.</para>
/// </summary>
public static class GamepadRepair
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    private const string Vid = "VID_046D&PID_C21F"; // Logitech Wireless Gamepad F710

    // Yetkisiz hızlı kontrol: hayalet (Unknown) / hatalı (Error) düğüm var mı, ya da hiç sağlam yok mu?
    private const string CheckScript = @"
$a = Get-PnpDevice | Where-Object { $_.InstanceId -match 'VID_046D&PID_C21F' }
$ph = @($a | Where-Object { $_.Status -eq 'Unknown' }).Count
$er = @($a | Where-Object { $_.Status -eq 'Error' }).Count
$ok = @($a | Where-Object { $_.Status -eq 'OK' }).Count
if ($ph -gt 0 -or $er -gt 0 -or $ok -eq 0) { 'NEEDS' } else { 'OK' }
";

    // Yükseltilmiş onarım: hayaletleri sil, hatalıyı kaldır+tara, sağlamı disable/enable et, sonucu dosyaya yaz.
    private const string RepairScript = @"
$ErrorActionPreference = 'SilentlyContinue'
$vp = 'VID_046D&PID_C21F'
$all = Get-PnpDevice | Where-Object { $_.InstanceId -match $vp }
# 1) Hayaletleri kaldir
foreach ($g in @($all | Where-Object { $_.Status -eq 'Unknown' })) {
  pnputil /remove-device ""$($g.InstanceId)"" *> $null
}
# 2) Hatali (FAILED_INSTALL) kaldir, saglamlari disable/enable (replug)
foreach ($d in @(Get-PnpDevice | Where-Object { $_.InstanceId -match $vp -and $_.Status -ne 'Unknown' })) {
  if ($d.Status -eq 'Error' -or $d.Problem -eq 'CM_PROB_FAILED_INSTALL') {
    pnputil /remove-device ""$($d.InstanceId)"" *> $null
  } else {
    Disable-PnpDevice -InstanceId $d.InstanceId -Confirm:$false *> $null
    Start-Sleep -Milliseconds 600
    Enable-PnpDevice  -InstanceId $d.InstanceId -Confirm:$false *> $null
  }
}
# 3) Yeniden tara (fiziksel cihaz temiz kurulsun)
pnputil /scan-devices *> $null
Start-Sleep -Seconds 2
# 4) Tarama sonrasi geri gelen inatci hatali dugumu son kez kaldir (tarama yok)
foreach ($d in @(Get-PnpDevice | Where-Object { $_.InstanceId -match $vp -and ($_.Status -eq 'Error' -or $_.Problem -eq 'CM_PROB_FAILED_INSTALL') })) {
  pnputil /remove-device ""$($d.InstanceId)"" *> $null
}
Start-Sleep -Milliseconds 500
$after = Get-PnpDevice | Where-Object { $_.InstanceId -match $vp }
$ok = @($after | Where-Object { $_.Status -eq 'OK' }).Count
$er = @($after | Where-Object { $_.Status -eq 'Error' }).Count
$ph = @($after | Where-Object { $_.Status -eq 'Unknown' }).Count
""Onarim: hayalet temizlendi, OK=$ok Hatali=$er Hayalet=$ph"" | Set-Content -Path (Join-Path $env:TEMP 'f710_onar_sonuc.txt') -Encoding UTF8
";

    /// <summary>Yetkisiz hızlı kontrol — hayalet/hatalı F710 düğümü var mı (yani onarım gerekli mi)?</summary>
    public static async Task<bool> NeedsRepairAsync()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{CheckScript.Replace("\r", " ").Replace("\n", " ").Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var outp = await p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await p.WaitForExitAsync(cts.Token);
            return outp.Contains("NEEDS", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Yükseltilmiş (yönetici) onarımı çalıştırır. UAC çıkar. Sonucu metin olarak döndürür.</summary>
    public static async Task<string> RunElevatedAsync()
    {
        if (!OperatingSystem.IsWindows()) return "Yalnızca Windows.";
        try
        {
            var dir = Path.GetTempPath();
            var psPath = Path.Combine(dir, "f710_onar.ps1");
            var resultPath = Path.Combine(dir, "f710_onar_sonuc.txt");
            try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { }
            await File.WriteAllTextAsync(psPath, RepairScript);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{psPath}\"",
                UseShellExecute = true,   // Verb=runas için şart
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);
            if (p is null) return "Onarım başlatılamadı.";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { return "Onarım zaman aşımına uğradı."; }

            try { if (File.Exists(resultPath)) return (await File.ReadAllTextAsync(resultPath)).Trim(); } catch { }
            return "Onarım tamamlandı.";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Onarım iptal — yönetici izni verilmedi.";
        }
        catch (Exception ex)
        {
            return $"Onarım hatası: {ex.Message}";
        }
    }
}
