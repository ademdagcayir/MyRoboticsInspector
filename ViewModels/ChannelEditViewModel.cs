using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

// MAUI'nin de Microsoft.Maui.FlowDirection enum'ı var — alias ile namespace çakışmasını engelle
using FlowDirection = MyRoboticsInspector.Models.FlowDirection;

namespace MyRoboticsInspector.ViewModels;

[QueryProperty(nameof(ChannelId), "id")]
public partial class ChannelEditViewModel : BaseViewModel
{
    private readonly DatabaseService _db;

    [ObservableProperty] private int channelId;
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

    public ChannelEditViewModel(DatabaseService db)
    {
        _db = db;
        Title = "Kanal Düzenle";
    }

    partial void OnChannelIdChanged(int value)
    {
        if (value > 0) _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        var conn = await _db.GetConnectionAsync();
        var existing = await conn.FindAsync<Inspection>(ChannelId);
        if (existing is null) return;

        EditingChannel = existing;
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
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        EditingChannel.FlowDirection = SelectedFlow?.Value ?? FlowDirection.Downstream;
        EditingChannel.ProjectType = SelectedProjectType?.Value ?? ProjectType.AtikSu;
        EditingChannel.PipeShape = SelectedPipeShape?.Value ?? PipeShape.Dairesel;
        EditingChannel.ViewStart = SelectedViewStart?.Value ?? ViewStartLocation.KanalBasi;
        EditingChannel.Cleaned = SelectedCleaned switch
        {
            "Evet" => true,
            "Hayır" => false,
            _ => null
        };

        await _db.SaveInspectionAsync(EditingChannel);
        StatusMessage = "Kanal kaydedildi";
        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task SaveAndCloseAsync() => await SaveAsync();

    [RelayCommand]
    private async Task CancelAsync()
    {
        await Shell.Current.GoToAsync("..");
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
