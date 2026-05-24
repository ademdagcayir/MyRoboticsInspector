using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

// NOT: Apple platformlarında global `MediaPlayer` namespace'i var — tam-nitelikli ad kullan.

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
    private IDispatcherTimer? _pollTimer;

    public LibVLCSharp.Shared.MediaPlayer? MediaPlayer => _video.MediaPlayer;

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

    public InspectionReviewViewModel(VideoService video, DatabaseService db)
    {
        _video = video;
        _db = db;
        Title = "İnceleme Önizleme";
    }

    partial void OnInspectionIdChanged(int value)
    {
        if (value > 0) _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        var conn = await _db.GetConnectionAsync();
        Inspection = await conn.FindAsync<Inspection>(InspectionId);
        if (Inspection is null) return;

        Title = $"İnceleme #{InspectionId} - Önizleme";
        VideoPath = Inspection.VideoPath;
        HasVideo = !string.IsNullOrWhiteSpace(VideoPath) && File.Exists(VideoPath);

        var defects = await _db.GetDefectsAsync(InspectionId);
        Defects.Clear();
        foreach (var d in defects) Defects.Add(d);

        if (HasVideo)
        {
            _video.PlayFile(VideoPath!);
            OnPropertyChanged(nameof(MediaPlayer));
            IsPlaying = true;
            StartPolling();
        }
        else
        {
            StatusMessage = "Bu inceleme için kayıt yok";
        }
    }

    private void StartPolling()
    {
        if (_pollTimer is not null) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _pollTimer = dispatcher.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(250);
        _pollTimer.Tick += (_, _) =>
        {
            if (MediaPlayer is null) return;
            CurrentTimeMs = MediaPlayer.Time;
            var len = MediaPlayer.Length;
            if (len > 0 && len != TotalLengthMs)
            {
                TotalLengthMs = len;
                TotalTimeDisplay = TimeSpan.FromMilliseconds(len).ToString(@"hh\:mm\:ss");
                RebuildMarkers();
            }
            CurrentTimeDisplay = TimeSpan.FromMilliseconds(CurrentTimeMs).ToString(@"hh\:mm\:ss");
            IsPlaying = MediaPlayer.IsPlaying;
        };
        _pollTimer.Start();
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

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (MediaPlayer is null) return;
        if (MediaPlayer.IsPlaying) { _video.Pause(); IsPlaying = false; }
        else { _video.Resume(); IsPlaying = true; }
    }

    [RelayCommand]
    private void SeekTo(object? value)
    {
        if (MediaPlayer is null || !MediaPlayer.IsSeekable) return;
        if (value is long ms) { _video.Seek(ms); }
        else if (value is double dms) { _video.Seek((long)dms); }
        else if (value is Defect d) { _video.Seek(d.VideoTimestampMs); SelectedDefect = d; }
        else if (value is DefectMarker m) { _video.Seek(m.Defect.VideoTimestampMs); SelectedDefect = m.Defect; }
    }

    [RelayCommand]
    private void SkipBackward()
    {
        if (MediaPlayer is null) return;
        _video.Seek(Math.Max(0, MediaPlayer.Time - 5000));
    }

    [RelayCommand]
    private void SkipForward()
    {
        if (MediaPlayer is null) return;
        _video.Seek(Math.Min(TotalLengthMs, MediaPlayer.Time + 5000));
    }

    [RelayCommand]
    private async Task DeleteDefectAsync(Defect? defect)
    {
        if (defect is null) return;
        await _db.DeleteDefectAsync(defect);
        Defects.Remove(defect);
        RebuildMarkers();
    }

    public void Cleanup()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        _video.Stop();
    }
}
