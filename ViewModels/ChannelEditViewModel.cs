using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

// MAUI'nin de Microsoft.Maui.FlowDirection enum'ı var — alias ile namespace çakışmasını engelle
using FlowDirection = MyRoboticsInspector.Models.FlowDirection;

namespace MyRoboticsInspector.ViewModels;

/// <summary>Sağ panelde görünen proje kanalı satırı.</summary>
public class SiblingChannel
{
    public Inspection Channel { get; init; } = new();
    public bool HasVideo => !string.IsNullOrWhiteSpace(Channel.VideoPath) && File.Exists(Channel.VideoPath);
    public bool IsCurrent { get; init; }

    public string Label => string.IsNullOrWhiteSpace(Channel.ChannelCode)
        ? $"#{Channel.Id}"
        : Channel.ChannelCode;

    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (Channel.DistanceMeters > 0) parts.Add($"{Channel.DistanceMeters:0.0} m");
            if (Channel.StartedAt != default) parts.Add(Channel.StartedAt.ToString("dd.MM.yyyy"));
            return string.Join("  ·  ", parts);
        }
    }
}

[QueryProperty(nameof(ChannelId), "id")]
[QueryProperty(nameof(JobId), "jobId")]
public partial class ChannelEditViewModel : BaseViewModel
{
    private readonly DatabaseService _db;
    private readonly ReportService _report;

    public ObservableCollection<SiblingChannel> SiblingChannels { get; } = new();

    [ObservableProperty] private int channelId;
    [ObservableProperty] private int jobId;
    [ObservableProperty] private bool isNew = true;
    [ObservableProperty] private Inspection editingChannel = new();
    [ObservableProperty] private Job? parentProject;

    [ObservableProperty] private LabeledOption<FlowDirection>? selectedFlow;
    [ObservableProperty] private LabeledOption<ProjectType>? selectedProjectType;
    [ObservableProperty] private LabeledOption<PipeShape>? selectedPipeShape;
    [ObservableProperty] private LabeledOption<ViewStartLocation>? selectedViewStart;

    public List<LabeledOption<FlowDirection>> FlowOptions { get; } =
        Enum.GetValues<FlowDirection>()
            .Select(f => new LabeledOption<FlowDirection>(f, f.GetLabel()))
            .ToList();

    public List<LabeledOption<ProjectType>> ProjectTypeOptions { get; } =
        Enum.GetValues<ProjectType>()
            .Select(t => new LabeledOption<ProjectType>(t, t.GetLabel()))
            .ToList();

    public List<LabeledOption<PipeShape>> PipeShapeOptions { get; } =
        Enum.GetValues<PipeShape>()
            .Select(s => new LabeledOption<PipeShape>(s, s.GetLabel()))
            .ToList();

    public List<LabeledOption<ViewStartLocation>> ViewStartOptions { get; } =
        Enum.GetValues<ViewStartLocation>()
            .Select(v => new LabeledOption<ViewStartLocation>(v, v.GetLabel()))
            .ToList();

    public List<string> CleanedOptions { get; } = new() { "Evet", "Hayır", "Belirsiz" };

    [ObservableProperty] private string? selectedCleaned;

    public ChannelEditViewModel(DatabaseService db, ReportService report)
    {
        _db = db;
        _report = report;
        Title = "Kanal Düzenle";
    }

    partial void OnChannelIdChanged(int value)
    {
        if (value > 0) _ = LoadAsync();
    }

    partial void OnJobIdChanged(int value)
    {
        // Yeni kanal: jobId var ama channelId yok
        if (value > 0 && ChannelId == 0) _ = InitNewAsync(value);
    }

    public async Task LoadAsync()
    {
        var conn = await _db.GetConnectionAsync();
        var existing = await conn.FindAsync<Inspection>(ChannelId);
        if (existing is null) return;

        EditingChannel = existing;
        IsNew = false;
        ParentProject = await conn.FindAsync<Job>(existing.JobId);

        SelectedFlow = FlowOptions.First(f => f.Value == EditingChannel.FlowDirection);
        SelectedProjectType = ProjectTypeOptions.First(t => t.Value == EditingChannel.ProjectType);
        SelectedPipeShape = PipeShapeOptions.First(s => s.Value == EditingChannel.PipeShape);
        SelectedViewStart = ViewStartOptions.First(v => v.Value == EditingChannel.ViewStart);
        SelectedCleaned = EditingChannel.Cleaned switch
        {
            true => "Evet",
            false => "Hayır",
            _ => "Belirsiz"
        };

        Title = $"Kanal {EditingChannel.KanalNo}: {EditingChannel.ChannelCode ?? "—"}";
        await LoadSiblingsAsync(EditingChannel.JobId);
    }

    /// <summary>Yeni kanal oluştururken — proje ID'si biliniyor ama kanal ID'si yok.</summary>
    public async Task InitNewAsync(int forJobId)
    {
        var conn = await _db.GetConnectionAsync();
        ParentProject = await conn.FindAsync<Job>(forJobId);
        EditingChannel = new Inspection { JobId = forJobId };
        IsNew = true;
        ChannelId = 0;
        SelectedFlow        = FlowOptions[0];
        SelectedProjectType = ProjectTypeOptions[0];
        SelectedPipeShape   = PipeShapeOptions[0];
        SelectedViewStart   = ViewStartOptions[0];
        SelectedCleaned     = "Belirsiz";
        Title = "Yeni Kanal";
        await LoadSiblingsAsync(forJobId);
    }

    private async Task LoadSiblingsAsync(int jobId)
    {
        if (jobId <= 0) return;
        var channels = await _db.GetInspectionsAsync(jobId);
        SiblingChannels.Clear();
        foreach (var ch in channels.OrderBy(c => c.KanalNo).ThenBy(c => c.ChannelCode))
        {
            SiblingChannels.Add(new SiblingChannel
            {
                Channel = ch,
                IsCurrent = ch.Id == ChannelId
            });
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        EditingChannel.FlowDirection = SelectedFlow?.Value ?? FlowDirection.Downstream;
        EditingChannel.ProjectType   = SelectedProjectType?.Value ?? ProjectType.AtikSu;
        EditingChannel.PipeShape     = SelectedPipeShape?.Value ?? PipeShape.Dairesel;
        EditingChannel.ViewStart     = SelectedViewStart?.Value ?? ViewStartLocation.KanalBasi;
        EditingChannel.Cleaned = SelectedCleaned switch
        {
            "Evet"  => true,
            "Hayır" => false,
            _       => null
        };

        await _db.SaveInspectionAsync(EditingChannel);

        var savedJobId = EditingChannel.JobId;
        var wasNew     = ChannelId == 0;

        // Listeyi yenile (kaydedilen kanalı göster)
        await LoadSiblingsAsync(savedJobId);

        if (wasNew)
        {
            // Yeni kanal modunda: formu sıfırla — sayfada kal, bir sonraki kanalı girebilsin
            ChannelId = 0;
            await InitNewAsync(savedJobId);
            StatusMessage = $"Kanal kaydedildi ✓  —  yeni kanal girebilirsiniz";
        }
        else
        {
            // Düzenleme modunda: kapat
            StatusMessage = "Kanal güncellendi";
            await Shell.Current.GoToAsync("..");
        }
    }

    [RelayCommand]
    private async Task SaveAndCloseAsync()
    {
        // Her durumda kaydet ve kapat
        EditingChannel.FlowDirection = SelectedFlow?.Value ?? FlowDirection.Downstream;
        EditingChannel.ProjectType   = SelectedProjectType?.Value ?? ProjectType.AtikSu;
        EditingChannel.PipeShape     = SelectedPipeShape?.Value ?? PipeShape.Dairesel;
        EditingChannel.ViewStart     = SelectedViewStart?.Value ?? ViewStartLocation.KanalBasi;
        EditingChannel.Cleaned = SelectedCleaned switch
        {
            "Evet"  => true,
            "Hayır" => false,
            _       => null
        };
        await _db.SaveInspectionAsync(EditingChannel);
        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        await Shell.Current.GoToAsync("..");
    }

    // ── Sağ panel kanal aksiyonları ──

    [RelayCommand]
    private async Task EditSiblingAsync(SiblingChannel? item)
    {
        if (item is null) return;
        await Shell.Current.GoToAsync($"channeledit?id={item.Channel.Id}");
    }

    [RelayCommand]
    private void OpenSiblingVideo(SiblingChannel? item)
    {
        if (item is null || !item.HasVideo) return;
        try { Process.Start(new ProcessStartInfo(item.Channel.VideoPath!) { UseShellExecute = true }); }
        catch (Exception ex) { StatusMessage = $"Video açılamadı: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task GenerateSiblingReportAsync(SiblingChannel? item)
    {
        if (item is null) return;
        try
        {
            StatusMessage = "Rapor oluşturuluyor…";
            var dir = Path.GetDirectoryName(item.Channel.VideoPath)
                      ?? FileSystem.AppDataDirectory;
            var path = await _report.GenerateInspectionReportAsync(item.Channel.Id, dir);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusMessage = "Rapor açıldı";
        }
        catch (Exception ex) { StatusMessage = $"Rapor hatası: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var confirmed = await Shell.Current.DisplayAlert(
            "Kanalı sil",
            $"\"{EditingChannel.ChannelCode}\" silinsin mi? Bağlı kusurlar referansını kaybeder.",
            "Sil", "Vazgeç");
        if (!confirmed) return;

        var conn = await _db.GetConnectionAsync();
        await conn.DeleteAsync(EditingChannel);
        await Shell.Current.GoToAsync("..");
    }
}
