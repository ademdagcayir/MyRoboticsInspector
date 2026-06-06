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

    public LiveViewModel Live => _live;

    public ObservableCollection<ChannelListItem> Channels { get; } = new();

    [ObservableProperty] private int    projectId;
    [ObservableProperty] private Job?   project;
    [ObservableProperty] private double totalMeters;
    [ObservableProperty] private int    totalChannels;

    [ObservableProperty] private bool            isPanelOpen    = true;
    [ObservableProperty] private ChannelListItem? selectedChannel;

    public string PanelToggleIcon    => IsPanelOpen ? "◀" : "▶";
    public string SummaryLabel       => $"{TotalChannels} kanal · {TotalMeters:0.0} m";
    public string RecordButtonText   => Live.IsRecording ? "■  Durdur" : "●  Kayıt Başlat";
    public string RecordButtonColor  => Live.IsRecording ? "#ff453a" : "#30d158";
    public string SelectedChannelLabel
        => SelectedChannel is not null
            ? (SelectedChannel.Inspection.ChannelCode ?? $"#{SelectedChannel.Inspection.Id}")
            : "Kanal seçilmedi";

    /// <summary>Null-safe: SelectedChannel null iken compiled binding NPE atmaz.</summary>
    public string SelectedChannelFlow
        => SelectedChannel?.ManholeFlow ?? string.Empty;

    public ProjectChannelsViewModel(DatabaseService db, LiveViewModel live)
    {
        _db   = db;
        _live = live;
        Title = "Kanallar";

        // Kayıt durumu değişince buton metnini güncelle
        _live.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LiveViewModel.IsRecording))
            {
                OnPropertyChanged(nameof(RecordButtonText));
                OnPropertyChanged(nameof(RecordButtonColor));
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

            var inspections = await _db.GetInspectionsAsync(ProjectId);
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

    // ── Kayıt ──

    [RelayCommand]
    private async Task StartChannelRecordingAsync()
    {
        if (Live.IsRecording)
        {
            await Live.StopChannelRecordingAsync();
            await LoadAsync(); // ▶ butonu güncellensin
            return;
        }
        if (SelectedChannel is null) { Live.StatusMessage = "⚠ Kayıt için bir kanal seçin"; return; }
        if (Project is null)         { Live.StatusMessage = "⚠ Proje yüklenemedi"; return; }

        // Kanalın zaten kaydedilmiş bir videosu varsa uyar
        if (SelectedChannel.HasVideo)
        {
            var channelName = SelectedChannel.Inspection.ChannelCode
                              ?? $"#{SelectedChannel.Inspection.Id}";
            var confirmed = await Shell.Current.DisplayAlert(
                "Mevcut video silinecek",
                $"\"{channelName}\" kanalının mevcut videosu silinerek üzerine yeni kayıt yapılacak.\n\nDevam edilsin mi?",
                "Kayıt Başlat", "İptal");
            if (!confirmed) return;
        }

        await Live.StartRecordingForChannelAsync(SelectedChannel.Inspection, Project);

        // Kısa gecikme sonra gerçekten başladı mı kontrol et
        await Task.Delay(1500);
        if (Live.IsFfmpegRecording)
        {
            Live.StatusMessage = $"● REC → {Path.GetFileName(SelectedChannel.Inspection.VideoPath ?? "")}";
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

    [RelayCommand]
    private async Task OpenDetailAsync(ChannelListItem? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"inspectiondetail?id={item.Inspection.Id}");
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
