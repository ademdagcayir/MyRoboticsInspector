using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

using FlowDirection = MyRoboticsInspector.Models.FlowDirection;

namespace MyRoboticsInspector.ViewModels;

/// <summary>
/// Tek bir kanal satırı — Inspection + ön-hesaplanmış UI alanları (defect counts, status renk).
/// </summary>
public partial class ChannelListItem : ObservableObject
{
    public Inspection Inspection { get; }
    [ObservableProperty] private int defectCount;
    [ObservableProperty] private int criticalCount;
    [ObservableProperty] private int highCount;

    public ChannelListItem(Inspection i) { Inspection = i; }

    public bool HasVideo => !string.IsNullOrWhiteSpace(Inspection.VideoPath)
                            && File.Exists(Inspection.VideoPath);

    /// <summary>"YK49A → YK3697A" gibi mini diyagram metni.</summary>
    public string ManholeFlow
    {
        get
        {
            var a = string.IsNullOrWhiteSpace(Inspection.EntryManhole) ? "—" : Inspection.EntryManhole;
            var b = string.IsNullOrWhiteSpace(Inspection.ExitManhole) ? "—" : Inspection.ExitManhole;
            var arrow = Inspection.FlowDirection == FlowDirection.Upstream ? "←" : "→";
            return $"{a}  {arrow}  {b}";
        }
    }

    public string FlowLabel => Inspection.FlowDirection.GetLabel();
    public bool IsReverse => Inspection.FlowDirection == FlowDirection.Upstream;

    /// <summary>
    /// Status pill rengi:
    /// - Critical varsa kırmızı (legacy "kırmızı")
    /// - High varsa turuncu (legacy "turuncu" — sorunlu kanal)
    /// - Hiçbiri yoksa yeşil (legacy "yeşil" — temiz/OK)
    /// - Hiç defect yoksa nötr (gri — beklemede)
    /// </summary>
    public Color StatusColor
    {
        get
        {
            if (CriticalCount > 0) return Color.FromArgb("#ff453a");
            if (HighCount > 0) return Color.FromArgb("#ff9f0a");
            if (DefectCount > 0) return Color.FromArgb("#30d158");  // var ama Low/Medium
            return Color.FromArgb("#8e8e93");                        // hiç defect yok / beklemede
        }
    }

    public string StatusLabel
    {
        get
        {
            if (CriticalCount > 0) return "KRİTİK";
            if (HighCount > 0) return "DİKKAT";
            if (DefectCount > 0) return "TAMAM";
            return "BOŞ";
        }
    }
}

[QueryProperty(nameof(ProjectId), "id")]
public partial class ProjectChannelsViewModel : BaseViewModel
{
    private readonly DatabaseService _db;

    public ObservableCollection<ChannelListItem> Channels { get; } = new();

    [ObservableProperty] private int projectId;
    [ObservableProperty] private Job? project;
    [ObservableProperty] private double totalMeters;
    [ObservableProperty] private int totalChannels;

    public string SummaryLabel => $"{TotalChannels} kanal · toplam {TotalMeters:0.0} m";

    public ProjectChannelsViewModel(DatabaseService db)
    {
        _db = db;
        Title = "Kanallar";
    }

    partial void OnProjectIdChanged(int value)
    {
        if (value > 0) _ = LoadAsync();
    }

    partial void OnTotalChannelsChanged(int v) => OnPropertyChanged(nameof(SummaryLabel));
    partial void OnTotalMetersChanged(double v) => OnPropertyChanged(nameof(SummaryLabel));

    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            var conn = await _db.GetConnectionAsync();
            Project = await conn.FindAsync<Job>(ProjectId);
            Title = Project?.Title ?? "Kanallar";

            var inspections = await _db.GetInspectionsAsync(ProjectId);
            Channels.Clear();
            double total = 0;
            foreach (var i in inspections)
            {
                var defects = await _db.GetDefectsAsync(i.Id);
                var item = new ChannelListItem(i)
                {
                    DefectCount = defects.Count,
                    HighCount = defects.Count(d => d.Severity == DefectSeverity.High),
                    CriticalCount = defects.Count(d => d.Severity == DefectSeverity.Critical),
                };
                Channels.Add(item);
                total += i.DistanceMeters ?? 0;
            }
            TotalChannels = Channels.Count;
            TotalMeters = total;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task NewChannelAsync()
    {
        if (Project is null) return;
        // Yeni boş Inspection oluştur, edit sayfasına gönder
        var fresh = new Inspection
        {
            JobId = Project.Id,
            StartedAt = DateTime.Now,
            ChannelCode = $"K-{DateTime.Now:yyyyMMddHHmm}",
        };
        await _db.SaveInspectionAsync(fresh);
        await Shell.Current.GoToAsync($"channeledit?id={fresh.Id}");
    }

    [RelayCommand]
    private async Task EditAsync(ChannelListItem? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"channeledit?id={item.Inspection.Id}");
    }

    [RelayCommand]
    private async Task PlayVideoAsync(ChannelListItem? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"inspectionreview?id={item.Inspection.Id}");
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
        Channels.Remove(item);
        TotalChannels = Channels.Count;
        TotalMeters = Channels.Sum(c => c.Inspection.DistanceMeters ?? 0);
        StatusMessage = "Kanal silindi";
    }

    [RelayCommand]
    private async Task BackToProjectsAsync()
    {
        await Shell.Current.GoToAsync("..");
    }
}
