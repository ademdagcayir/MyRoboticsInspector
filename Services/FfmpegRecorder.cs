using System.Diagnostics;

namespace MyRoboticsInspector.Services;

/// <summary>
/// FFmpeg ikili dosyasını platforma uygun konumlarda bulan statik yardımcı.
///
/// NOT: Kayıt/önizleme artık <see cref="SyncedVideoPipeline"/> tarafından yapılıyor
/// (tek decode → SkiaSharp composite → encode). Eskiden bu sınıf RTSP→drawtext→MP4
/// kaydı da yapıyordu; o makine kaldırıldı, yalnızca ffmpeg konum çözümü kaldı.
/// </summary>
public static class FfmpegRecorder
{
    /// <summary>
    /// Verilen yolu doğrular; bulunamazsa platforma uygun yedek konumları dener.
    /// Çalışan ilk yolu döner, yoksa null.
    /// </summary>
    public static string? ResolveFfmpeg(string preferredPath)
    {
        var candidates = new List<string> { preferredPath };

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe")); // WinGet
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "ffmpeg.exe")); // Scoop
            candidates.Add(@"C:\ProgramData\chocolatey\bin\ffmpeg.exe"); // Chocolatey
            candidates.Add(@"C:\Program Files\ffmpeg\bin\ffmpeg.exe");
            candidates.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
        }
        else if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
        {
            candidates.Add("ffmpeg");
            candidates.Add("/opt/homebrew/bin/ffmpeg");
            candidates.Add("/usr/local/bin/ffmpeg");
            candidates.Add("/usr/bin/ffmpeg");
        }
        else
        {
            candidates.Add("ffmpeg");
            candidates.Add("/usr/bin/ffmpeg");
            candidates.Add("/usr/local/bin/ffmpeg");
        }

        foreach (var c in candidates.Distinct())
            if (TryProbe(c)) return c;
        return null;
    }

    /// <summary>ffmpeg bulunabiliyor mu (true/false).</summary>
    public static bool ProbeFfmpeg(string ffmpegPath) => ResolveFfmpeg(ffmpegPath) is not null;

    private static bool TryProbe(string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(path, "-version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (p is null) return false;
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
