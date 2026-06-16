using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

namespace MyRoboticsInspector.ViewModels;

/// <summary>One defect adapted for timeline display — knows its proportional X position.</summary>
public partial class DefectMarker : ObservableObject
{
    public Defect Defect { get; }
    [ObservableProperty] private double xProportion; // 0..1 across the scrubber

    public DefectMarker(Defect d, double xProp)
    {
        Defect = d;
        xProportion = xProp;
    }

    public Color Color => Defect.Severity switch
    {
        DefectSeverity.Critical => Color.FromArgb("#d33"),
        DefectSeverity.High     => Color.FromArgb("#e66"),
        DefectSeverity.Medium   => Color.FromArgb("#f93"),
        DefectSeverity.Low      => Color.FromArgb("#fc3"),
        _                       => Color.FromArgb("#aaa")
    };
}

[QueryProperty(nameof(InspectionId), "id")]
public partial class InspectionReviewViewModel : BaseViewModel
{
    private readonly VideoService _video;
    private readonly DatabaseService _db;

    public ObservableCollection<Defect> Defects { get; } = new();
    public ObservableCollection<DefectMarker> Markers { get; } = new();

    [ObservableProperty] private int inspectionId;
    [ObservableProperty] private Inspection? inspection;
    [ObservableProperty] private string? videoPath;
    [ObservableProperty] private bool hasVideo;
    [ObservableProperty] private bool isPlaying;
    [ObservableProperty] private long currentTimeMs;
    [ObservableProperty] private long totalLengthMs = 1; // never 0 to avoid divide-by-zero in markers
    [ObservableProperty] private string currentTimeDisplay = "00:00:00";
    [ObservableProperty] private string totalTimeDisplay = "00:00:00";
    [ObservableProperty] private Defect? selectedDefect;
    [ObservableProperty] private string videoFileName = "—";
    [ObservableProperty] private string videoFileSize = "";
    [ObservableProperty] private string videoFolderPath = "";

    public InspectionReviewViewModel(VideoService video, DatabaseService db)
    {
        _video = video;
        _db = db;
        Title = "İnceleme Önizleme";
    }

    partial void OnInspectionIdChanged(int value)
    {
        if (value > 0) SafeLoad();
    }

    /// <summary>Fire-and-forget LoadAsync'i güvenle çalıştırır — hata sessizce kaybolmaz.</summary>
    private async void SafeLoad()
    {
        try { await LoadAsync(); }
        catch (Exception ex)
        {
            StatusMessage = $"Yükleme hatası: {ex.Message}";
        }
    }

    public async Task LoadAsync()
    {
        var conn = await _db.GetConnectionAsync();
        Inspection = await conn.FindAsync<Inspection>(InspectionId);
        if (Inspection is null) return;

        Title = $"İnceleme #{InspectionId}";
        VideoPath = Inspection.VideoPath;
        HasVideo = !string.IsNullOrWhiteSpace(VideoPath) && File.Exists(VideoPath);

        if (HasVideo)
        {
            var fi = new FileInfo(VideoPath!);
            VideoFileName = fi.Name;
            VideoFolderPath = fi.DirectoryName ?? "";
            var mb = fi.Length / 1_048_576.0;
            VideoFileSize = mb >= 1 ? $"{mb:0.0} MB" : $"{fi.Length / 1024.0:0} KB";
            StatusMessage = $"{VideoFileName}  ·  {VideoFileSize}";
        }
        else if (!string.IsNullOrWhiteSpace(VideoPath))
        {
            VideoFileName = Path.GetFileName(VideoPath) ?? "—";
            VideoFolderPath = Path.GetDirectoryName(VideoPath) ?? "";
            StatusMessage = "Kayıt dosyası bulunamadı";
        }
        else
        {
            VideoFileName = "—";
            StatusMessage = "Bu inceleme için kayıt yok";
        }

        var defects = await _db.GetDefectsAsync(InspectionId);
        Defects.Clear();
        foreach (var d in defects) Defects.Add(d);

        // Gerçek video süresi DB'de saklanmıyor — inceleme başlangıç/bitiş zamanından
        // türet; yoksa en geç kusur zamanına normalize et ki işaretçiler şeride
        // orantılı dağılsın (eskiden hepsi 1 ms'e bölünüp en sağa yığılıyordu).
        long durationMs = 0;
        if (Inspection.FinishedAt is DateTime finished && finished > Inspection.StartedAt)
            durationMs = (long)(finished - Inspection.StartedAt).TotalMilliseconds;
        var maxDefectMs = Defects.Count > 0 ? Defects.Max(d => d.VideoTimestampMs) : 0;
        TotalLengthMs = Math.Max(1, Math.Max(durationMs, maxDefectMs));
        TotalTimeDisplay = TimeSpan.FromMilliseconds(TotalLengthMs).ToString(@"hh\:mm\:ss");

        RebuildMarkers();
    }

    private void RebuildMarkers()
    {
        Markers.Clear();
        if (TotalLengthMs <= 0) return;
        foreach (var d in Defects)
        {
            var x = Math.Clamp((double)d.VideoTimestampMs / TotalLengthMs, 0.0, 1.0);
            Markers.Add(new DefectMarker(d, x));
        }
    }

    /// <summary>Videoyu sistem varsayılan oynatıcısında aç (VLC, Windows Media Player vb.).</summary>
    [RelayCommand(CanExecute = nameof(HasVideo))]
    private void OpenVideo()
    {
        if (string.IsNullOrWhiteSpace(VideoPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(VideoPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Oynatıcı açılamadı: {ex.Message}";
        }
    }

    /// <summary>Kayıt dosyasının bulunduğu klasörü Dosya Gezgini'nde aç.</summary>
    [RelayCommand(CanExecute = nameof(HasVideo))]
    private void OpenFolder()
    {
        if (string.IsNullOrWhiteSpace(VideoPath)) return;
        try
        {
            // Windows Explorer'da dosyayı seç ve klasörü göster
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{VideoPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Klasör açılamadı: {ex.Message}";
        }
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        // Dış oynatıcıya yönlendir
        if (HasVideo) OpenVideo();
    }

    [RelayCommand]
    private void SeekTo(object? value)
    {
        if (value is Defect d) { SelectedDefect = d; }
        else if (value is DefectMarker m) { SelectedDefect = m.Defect; }
    }

    [RelayCommand]
    private void SkipBackward() { /* dış oynatıcıda kontrol edilir */ }

    [RelayCommand]
    private void SkipForward() { /* dış oynatıcıda kontrol edilir */ }

    [RelayCommand]
    private async Task DeleteDefectAsync(Defect? defect)
    {
        if (defect is null) return;
        await _db.DeleteDefectAsync(defect);
        Defects.Remove(defect);
        RebuildMarkers();
    }

    partial void OnHasVideoChanged(bool value)
    {
        OpenVideoCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    public void Cleanup()
    {
        _video.Stop();
    }
}
