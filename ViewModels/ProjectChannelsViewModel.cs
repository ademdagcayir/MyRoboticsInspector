using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

using FlowDirection = MyRoboticsInspector.Models.FlowDirection;

namespace MyRoboticsInspector.ViewModels;

/// <summary>
/// Tek bir kanal satırı — Inspection + ön-hesaplanmış UI alanları.
/// </summary>
public partial class ChannelListItem : ObservableObject
{
    public Inspection Inspection { get; }
    [ObservableProperty] private int defectCount;
    [ObservableProperty] private int criticalCount;
    [ObservableProperty] private int highCount;
    [ObservableProperty] private bool isSelected;

    public ChannelListItem(Inspection i) { Inspection = i; }

    /// <summary>Kanalın sıra numarası rozeti ("1", "2"…); henüz atanmadıysa "•".</summary>
    public string NumberBadge => Inspection.KanalNo?.ToString() ?? "•";

    public bool HasVideo => !string.IsNullOrWhiteSpace(Inspection.VideoPath)
                            && File.Exists(Inspection.VideoPath);

    public string ManholeFlow
    {
        get
        {
            var a = string.IsNullOrWhiteSpace(Inspection.EntryManhole) ? "—" : Inspection.EntryManhole;
            var b = string.IsNullOrWhiteSpace(Inspection.ExitManhole)  ? "—" : Inspection.ExitManhole;
            var arrow = Inspection.FlowDirection == FlowDirection.Upstream ? "←" : "→";
            return $"{a} {arrow} {b}";
        }
    }

    // ── Grid kolonları ──
    public string StreetText => string.IsNullOrWhiteSpace(Inspection.Street) ? "—" : Inspection.Street!;
    public string EntryText  => string.IsNullOrWhiteSpace(Inspection.EntryManhole) ? "—" : Inspection.EntryManhole!;
    public string ExitText   => string.IsNullOrWhiteSpace(Inspection.ExitManhole)  ? "—" : Inspection.ExitManhole!;
    public string MetersText => Inspection.DistanceMeters is double d && d > 0 ? $"{d:0.0} m" : "—";

    public string FlowLabel => Inspection.FlowDirection.GetLabel();
    public bool IsReverse => Inspection.FlowDirection == FlowDirection.Upstream;

    public Color StatusColor
    {
        get
        {
            if (CriticalCount > 0) return Color.FromArgb("#ff453a");
            if (HighCount > 0)     return Color.FromArgb("#ff9f0a");
            if (DefectCount > 0)   return Color.FromArgb("#30d158");
            return Color.FromArgb("#8e8e93");
        }
    }

    public string StatusLabel
    {
        get
        {
            if (CriticalCount > 0) return "KRİTİK";
            if (HighCount > 0)     return "DİKKAT";
            if (DefectCount > 0)   return "TAMAM";
            return "BOŞ";
        }
    }
}

[QueryProperty(nameof(ProjectId), "id")]
public partial class ProjectChannelsViewModel : BaseViewModel
{
    private readonly DatabaseService _db;
    private readonly LiveViewModel   _live;
    private readonly ReportService   _reports;

    public LiveViewModel Live => _live;

    public ObservableCollection<ChannelListItem> Channels { get; } = new();

    /// <summary>Seçili kanala ait çekilmiş fotoğraflar (yorumlu) — alt şeritte thumbnail.</summary>
    public ObservableCollection<Defect> ChannelPhotos { get; } = new();

    [ObservableProperty] private int    projectId;
    [ObservableProperty] private Job?   project;
    [ObservableProperty] private double totalMeters;
    [ObservableProperty] private int    totalChannels;

    [ObservableProperty] private bool            isPanelOpen    = false;
    [ObservableProperty] private ChannelListItem? selectedChannel;

    public string PanelToggleIcon    => IsPanelOpen ? "◀" : "▶";
    public string SummaryLabel       => $"{TotalChannels} kanal · {TotalMeters:0.0} m";
    public string RecordButtonText   => Live.IsRecording ? "●  Kayıt sürüyor" : "●  Kanal Başı";
    public string RecordButtonColor  => Live.IsRecording ? "#3a3a3c" : "#30d158";
    /// <summary>Kanal Başı yalnızca kayıt yokken aktif (durdurma artık Kanal Sonu ile).</summary>
    public bool   CanStartRecording  => !Live.IsRecording;
    public string SelectedChannelLabel
        => SelectedChannel is not null
            ? SelectedChannel.Inspection.DisplayName
            : "Kanal seçilmedi";

    /// <summary>Null-safe: SelectedChannel null iken compiled binding NPE atmaz.</summary>
    public string SelectedChannelFlow
        => SelectedChannel?.ManholeFlow ?? string.Empty;

    public ProjectChannelsViewModel(DatabaseService db, LiveViewModel live, ReportService reports)
    {
        _db   = db;
        _live = live;
        _reports = reports;
        Title = "Kanallar";

        // Kayıt durumu değişince buton metnini güncelle
        _live.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LiveViewModel.IsRecording))
            {
                OnPropertyChanged(nameof(RecordButtonText));
                OnPropertyChanged(nameof(RecordButtonColor));
                OnPropertyChanged(nameof(CanStartRecording));
            }
        };
    }

    partial void OnProjectIdChanged(int value)
    {
        if (value > 0) _ = LoadAsync();
    }

    partial void OnTotalChannelsChanged(int v)  => OnPropertyChanged(nameof(SummaryLabel));
    partial void OnTotalMetersChanged(double v)  => OnPropertyChanged(nameof(SummaryLabel));
    partial void OnIsPanelOpenChanged(bool v)    => OnPropertyChanged(nameof(PanelToggleIcon));
    partial void OnSelectedChannelChanged(ChannelListItem? v)
    {
        OnPropertyChanged(nameof(SelectedChannelLabel));
        OnPropertyChanged(nameof(SelectedChannelFlow));
        _ = LoadChannelPhotosAsync();
    }

    /// <summary>Seçili kanalın kayıtlı fotoğraflarını (yorumlu) DB'den yükle.</summary>
    private async Task LoadChannelPhotosAsync()
    {
        ChannelPhotos.Clear();
        if (SelectedChannel is null) return;
        var photos = await _db.GetDefectsAsync(SelectedChannel.Inspection.Id);
        foreach (var p in photos.OrderByDescending(p => p.CreatedAt))
            ChannelPhotos.Add(p);
    }

    public async Task LoadAsync()
    {
        if (IsBusy) return; // eşzamanlı yeniden yükleme yarışını önle
        try
        {
            IsBusy = true;
            var conn = await _db.GetConnectionAsync();
            Project = await conn.FindAsync<Job>(ProjectId);
            Title   = Project?.Title ?? "Kanallar";

            // Numarası olmayan (eski) kanalları geriye dönük 1'den numaralandır
            await _db.EnsureKanalNumbersAsync(ProjectId);

            var inspections = (await _db.GetInspectionsAsync(ProjectId))
                .OrderBy(i => i.KanalNo ?? int.MaxValue)
                .ThenBy(i => i.Id)
                .ToList();
            Channels.Clear();
            double total = 0;
            foreach (var i in inspections)
            {
                var defects = await _db.GetDefectsAsync(i.Id);
                var item = new ChannelListItem(i)
                {
                    DefectCount   = defects.Count,
                    HighCount     = defects.Count(d => d.Severity == DefectSeverity.High),
                    CriticalCount = defects.Count(d => d.Severity == DefectSeverity.Critical),
                };
                Channels.Add(item);
                total += i.DistanceMeters ?? 0;
            }
            TotalChannels = Channels.Count;
            TotalMeters   = total;
        }
        finally { IsBusy = false; }
    }

    // ── Panel toggle ──

    [RelayCommand]
    private void TogglePanel() => IsPanelOpen = !IsPanelOpen;

    // ── Kanal seçimi ──

    [RelayCommand]
    private void SelectChannel(ChannelListItem? item)
    {
        if (item is null) return;
        foreach (var ch in Channels) ch.IsSelected = false;
        item.IsSelected  = true;
        SelectedChannel  = item;
    }

    // ── Fotoğraf çekme + yorum ──

    [RelayCommand]
    private async Task CapturePhotoAsync()
    {
        if (SelectedChannel is null)
        {
            Live.StatusMessage = "⚠ Fotoğraf için önce bir kanal seçin";
            return;
        }

        // Kareyi 📷'ye basıldığı ANDA yakala (kullanıcı yorum yazarken görüntü ilerlese de doğru kare kalsın).
        // JPEG encode arka planda → UI thread takılmaz.
        var frame = await Task.Run(() => Live.Pipeline.Snapshot());
        if (frame is null)
        {
            Live.StatusMessage = "Görüntü alınamadı — yayın aktif değil";
            return;
        }

        // Yorum iste (iptal edilirse dosya hiç oluşturulmaz)
        var comment = await Shell.Current.DisplayPromptAsync(
            "Fotoğraf yorumu",
            "Bu görüntü için bir not / yorum girin:",
            accept: "Kaydet", cancel: "İptal",
            placeholder: "Örn. 12. metrede çatlak",
            maxLength: 500);

        if (comment is null) return; // iptal

        if (await PersistPhotoAsync(frame, comment))
            Live.StatusMessage = "Fotoğraf kaydedildi";
    }

    /// <summary>Yorum sormadan, sabit etiketle otomatik kare çeker (kanal başı / kanal sonu).</summary>
    private async Task CaptureAutoPhotoAsync(string label)
    {
        if (SelectedChannel is null) return;
        var frame = await Task.Run(() => Live.Pipeline.Snapshot()); // encode arka planda — UI/önizleme takılmaz
        if (frame is null) return;
        // Kanal başı/sonu için EN 13508-2 standart işaret kodları (referans rapor formatıyla aynı)
        var (code, desc) = label switch
        {
            "Kanal Başı" => ("BCD-XP", "Kanal Denetleme Başlangıcı"),
            "Kanal Sonu" => ("BCE-XP", "Kanal Denetleme Sonu"),
            _            => (null,     label)
        };
        await PersistPhotoAsync(frame, desc, code);
    }

    /// <summary>Verilen kareyi diske + DB'ye yazar, thumbnail şeridine ekler. Kayıt durumunu ETKİLEMEZ.</summary>
    private async Task<bool> PersistPhotoAsync(byte[] frame, string comment, string? iskiCode = null)
    {
        if (SelectedChannel is null) return false;
        try
        {
            var settings = await _db.GetSettingsAsync();
            var root = string.IsNullOrWhiteSpace(settings.StoragePath)
                ? FileSystem.AppDataDirectory
                : settings.StoragePath;
            var insp = SelectedChannel.Inspection;
            // Video zamanı (kayıt başından): dosya adı ve rapor bunu kullanır. Kayıt yoksa 00:00.
            var videoTime = Live.Pipeline.RecordingElapsed ?? TimeSpan.Zero;
            // {Proje}/{Mahalle}/{Sokak}/resim/{Kanal}_{Sokak}_{GB}_{ÇB}_{dk}_{sn}_{ms}.jpg
            var dir = StoragePaths.PhotosDir(root, Project?.Title, Project?.Neighborhood, insp.Street);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir,
                StoragePaths.PhotoFileName(insp.KanalNo, insp.Street,
                                           insp.EntryManhole, insp.ExitManhole,
                                           $"kanal_{insp.KanalNo ?? insp.Id}", videoTime));
            await File.WriteAllBytesAsync(path, frame);

            var photo = new Defect
            {
                InspectionId     = insp.Id,
                PhotoPath        = path,
                Description      = comment,
                IskiCode         = iskiCode,
                MainCode         = iskiCode?.Split('-')[0],
                DistanceMeters   = Live.Telemetry.DistanceMeters,
                VideoTimestampMs = (long)videoTime.TotalMilliseconds, // video zamanı (00:00 sayacı)
                Severity         = DefectSeverity.Info,
                CreatedAt        = DateTime.Now
            };
            await _db.SaveDefectAsync(photo);
            ChannelPhotos.Insert(0, photo); // en yeni üstte
            SelectedChannel.DefectCount = ChannelPhotos.Count;
            return true;
        }
        catch (Exception ex)
        {
            Live.StatusMessage = $"Fotoğraf kaydedilemedi: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    private async Task EditPhotoCommentAsync(Defect? photo)
    {
        if (photo is null) return;
        var updated = await Shell.Current.DisplayPromptAsync(
            "Yorumu düzenle",
            "Fotoğraf yorumu:",
            accept: "Kaydet", cancel: "İptal",
            initialValue: photo.Description ?? "",
            maxLength: 500);
        if (updated is null) return; // iptal

        photo.Description = updated;
        await _db.SaveDefectAsync(photo);

        // Defect ObservableObject değil — bağlı etiketi tazelemek için öğeyi yerinde değiştir
        var idx = ChannelPhotos.IndexOf(photo);
        if (idx >= 0)
        {
            ChannelPhotos.RemoveAt(idx);
            ChannelPhotos.Insert(idx, photo);
        }
        Live.StatusMessage = "Yorum güncellendi";
    }

    [RelayCommand]
    private async Task DeletePhotoAsync(Defect? photo)
    {
        if (photo is null) return;
        var confirmed = await Shell.Current.DisplayAlert(
            "Fotoğrafı sil",
            "Bu fotoğraf ve yorumu kalıcı olarak silinsin mi?",
            "Sil", "Vazgeç");
        if (!confirmed) return;

        await _db.DeleteDefectAsync(photo);
        if (!string.IsNullOrEmpty(photo.PhotoPath) && File.Exists(photo.PhotoPath))
        {
            try { File.Delete(photo.PhotoPath); } catch { }
        }
        ChannelPhotos.Remove(photo);
        if (SelectedChannel is not null) SelectedChannel.DefectCount = ChannelPhotos.Count;
        Live.StatusMessage = "Fotoğraf silindi";
    }

    /// <summary>Bir kanala ait tüm fotoğrafları (DB kayıtları + disk dosyaları) siler. Üzerine yeni kayıt yapılırken çağrılır.</summary>
    private async Task DeleteChannelPhotosAsync(int inspectionId)
    {
        try
        {
            var photos = await _db.GetDefectsAsync(inspectionId);
            foreach (var p in photos)
            {
                await _db.DeleteDefectAsync(p);
                if (!string.IsNullOrEmpty(p.PhotoPath) && File.Exists(p.PhotoPath))
                {
                    try { File.Delete(p.PhotoPath); } catch { }
                }
            }
            ChannelPhotos.Clear();
            if (SelectedChannel is not null) SelectedChannel.DefectCount = 0;
        }
        catch (Exception ex)
        {
            Live.StatusMessage = $"Eski fotoğraflar silinemedi: {ex.Message}";
        }
    }

    /// <summary>Bu kanala ait üretilmiş rapor PDF'i var mı?</summary>
    private async Task<bool> ChannelHasReportsAsync(Inspection insp)
    {
        try
        {
            var dir = await ChannelReportsDirAsync(insp);
            if (!Directory.Exists(dir)) return false;
            if (insp.KanalNo is int n)
                return Directory.EnumerateFiles(dir, $"_{n}_*rapor*.pdf").Any();
            return Directory.EnumerateFiles(dir, "*rapor*.pdf").Any();
        }
        catch { return false; }
    }

    /// <summary>Bu kanala ait tüm rapor PDF'lerini siler (üzerine yeni kayıt yapılırken).</summary>
    private async Task DeleteChannelReportsAsync(Inspection insp)
    {
        try
        {
            var dir = await ChannelReportsDirAsync(insp);
            if (!Directory.Exists(dir)) return;
            var pattern = insp.KanalNo is int n ? $"_{n}_*rapor*.pdf" : "*rapor*.pdf";
            foreach (var f in Directory.EnumerateFiles(dir, pattern).ToList())
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch (Exception ex)
        {
            Live.StatusMessage = $"Eski raporlar silinemedi: {ex.Message}";
        }
    }

    private async Task<string> ChannelReportsDirAsync(Inspection insp)
    {
        var settings = await _db.GetSettingsAsync();
        var root = string.IsNullOrWhiteSpace(settings.StoragePath)
            ? FileSystem.AppDataDirectory
            : settings.StoragePath;
        return StoragePaths.ReportsDir(root, Project?.Title, Project?.Neighborhood, insp.Street);
    }

    // ── Kayıt ──

    [RelayCommand]
    private async Task StartChannelRecordingAsync()
    {
        // Kayıt zaten sürüyorsa Kanal Başı bir şey yapmaz (durdurma Kanal Sonu ile)
        if (Live.IsRecording) return;
        if (SelectedChannel is null) { Live.StatusMessage = "⚠ Kayıt için bir kanal seçin"; return; }
        if (Project is null)         { Live.StatusMessage = "⚠ Proje yüklenemedi"; return; }

        // Önceki kayıt verisi varsa uyar — video dosyası ELLE SİLİNMİŞ olsa bile
        // fotoğraf ve/veya rapor kalmış olabilir; bu durumda da uyar ve hepsini temizle.
        var insp0 = SelectedChannel.Inspection;
        var existingPhotos = await _db.GetDefectsAsync(insp0.Id);
        bool hasReports = await ChannelHasReportsAsync(insp0);
        if (SelectedChannel.HasVideo || existingPhotos.Count > 0 || hasReports)
        {
            var channelName = insp0.ChannelCode ?? $"#{insp0.Id}";
            var confirmed = await Shell.Current.DisplayAlert(
                "Mevcut kayıt silinecek",
                $"\"{channelName}\" kanalının mevcut videosu, fotoğrafları ve raporları silinerek üzerine yeni kayıt yapılacak.\n\nDevam edilsin mi?",
                "Kayıt Başlat", "İptal");
            if (!confirmed) return;

            // Üzerine yazılacaksa eski fotoğrafları ve raporları da temizle (video kayıt başlarken silinir)
            await DeleteChannelPhotosAsync(insp0.Id);
            await DeleteChannelReportsAsync(insp0);
        }

        await Live.StartRecordingForChannelAsync(SelectedChannel.Inspection, Project);

        // Kısa gecikme sonra gerçekten başladı mı kontrol et
        await Task.Delay(1500);
        if (Live.IsFfmpegRecording)
        {
            Live.StatusMessage = $"● REC → {Path.GetFileName(SelectedChannel.Inspection.VideoPath ?? "")}";
            // Kanal başı otomatik fotoğrafı (kaydı durdurmaz)
            await CaptureAutoPhotoAsync("Kanal Başı");
        }
        else if (Live.IsRecording)
        {
            // FfmpegRecorder erken çıktı — ne yazıyor göster
            await Shell.Current.DisplayAlert("Kayıt Sorunu",
                $"FFmpeg bağlanamıyor olabilir.\n\nRTSP: {Live.RecordingRtspUrl}\nFfmpeg son hata: {Live.FfmpegLastError}\n\nAynı anda iki bağlantıyı desteklemiyor olabilir.",
                "Tamam");
        }
    }

    [RelayCommand]
    private void PlayVideo(ChannelListItem? item)
    {
        if (item is null || !item.HasVideo) return;
        try
        {
            Process.Start(new ProcessStartInfo(item.Inspection.VideoPath!)
            {
                UseShellExecute = true   // varsayılan oynatıcıda aç (Windows Media Player, VLC vs.)
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Video açılamadı: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task KanalSonuAsync()
    {
        // Rapor için kanal kimliğini seçim temizlenmeden önce sakla
        var finishedId = SelectedChannel?.Inspection.Id ?? 0;

        // 0. Kanal sonu otomatik fotoğrafı — kayıt DURDURULMADAN ÖNCE çek
        if (Live.IsRecording)
            await CaptureAutoPhotoAsync("Kanal Sonu");
        // 1. Kaydı durdur
        if (Live.IsRecording)
            await Live.StopChannelRecordingAsync();
        // 2. İncelemeyi kapat (mesafe, bitiş zamanı DB'ye yazılır)
        if (Live.IsInspecting)
            await Live.FinishInspectionCommand.ExecuteAsync(null);
        // 3. Seçimi temizle
        foreach (var ch in Channels) ch.IsSelected = false;
        SelectedChannel = null;
        // 4. Kanal listesini yenile (kusur sayısı, metre güncellensin)
        await LoadAsync();
        StatusMessage = "Kanal tamamlandı";

        // 5. Otomatik rapor (2 PDF) üret — rapor/ klasörüne
        if (finishedId > 0)
        {
            try
            {
                await _reports.GenerateChannelReportsAsync(finishedId);
                StatusMessage = "Kanal tamamlandı ✓  —  2 rapor oluşturuldu (rapor/)";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Kanal tamamlandı, rapor hatası: {ex.Message}";
            }
        }
    }

    // ── Kanal CRUD ──

    [RelayCommand]
    private async Task NewChannelAsync()
    {
        if (Project is null) return;
        await Shell.Current.GoToAsync($"channeledit?jobId={Project.Id}");
    }

    [RelayCommand]
    private async Task EditAsync(ChannelListItem? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"channeledit?id={item.Inspection.Id}");
    }

    /// <summary>Kanalın bulunduğu klasörü (sokak klasörü) Dosya Gezgini'nde açar.</summary>
    [RelayCommand]
    private async Task OpenChannelFolderAsync(ChannelListItem? item)
    {
        if (item is null || Project is null) return;
        try
        {
            var insp = item.Inspection;
            // Sokak klasörünü aç (içinde egim/rapor/resim/video alt klasörleri görünür)
            var settings = await _db.GetSettingsAsync();
            var root = string.IsNullOrWhiteSpace(settings.StoragePath)
                ? FileSystem.AppDataDirectory
                : settings.StoragePath;
            var dir = StoragePaths.StreetDir(root, Project.Title, Project.Neighborhood, insp.Street);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Klasör açılamadı: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenDetailAsync(ChannelListItem? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"inspectiondetail?id={item.Inspection.Id}");
    }

    /// <summary>Seçili kanal için iki raporu (klasik + EN 13508-2) rapor/ klasörüne üretir.</summary>
    [RelayCommand]
    private async Task GenerateReportsAsync(ChannelListItem? item)
    {
        item ??= SelectedChannel;
        if (item is null) { StatusMessage = "⚠ Rapor için bir kanal seçin"; return; }
        try
        {
            StatusMessage = "Raporlar oluşturuluyor…";
            var (classic, _) = await _reports.GenerateChannelReportsAsync(item.Inspection.Id);
            StatusMessage = "✓ 2 rapor oluşturuldu (rapor/ klasörü)";
            // rapor klasörünü aç
            var dir = Path.GetDirectoryName(classic);
            if (!string.IsNullOrEmpty(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rapor oluşturulamadı: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ChannelListItem? item)
    {
        if (item is null) return;
        var confirmed = await Shell.Current.DisplayAlert(
            "Kanalı sil",
            $"\"{item.Inspection.ChannelCode}\" silinsin mi? Bağlı tüm kusurlar ve video referansı kaybolur.",
            "Sil", "Vazgeç");
        if (!confirmed) return;

        var conn = await _db.GetConnectionAsync();
        await conn.DeleteAsync(item.Inspection);
        if (SelectedChannel == item) SelectedChannel = null;
        Channels.Remove(item);
        TotalChannels = Channels.Count;
        TotalMeters   = Channels.Sum(c => c.Inspection.DistanceMeters ?? 0);
        StatusMessage = "Kanal silindi";
    }

    [RelayCommand]
    private async Task OpenProjectFormAsync()
    {
        if (Project is null) return;
        await Shell.Current.GoToAsync($"projectform?id={Project.Id}");
    }

    [RelayCommand]
    private async Task BackToProjectsAsync()
    {
        await Shell.Current.GoToAsync("..");
    }
}
