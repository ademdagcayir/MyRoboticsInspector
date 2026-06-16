using System.ComponentModel;
using MyRoboticsInspector.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace MyRoboticsInspector.Pages;

public partial class ProjectChannelsPage : ContentPage
{
    private readonly ProjectChannelsViewModel _vm;
    private CancellationTokenSource? _blinkCts;

    public ProjectChannelsPage(ProjectChannelsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
        ChannelCanvas.PaintSurface += OnPaintSurface;
        // DİKKAT: Singleton Live.PropertyChanged aboneliği constructor'da KURULMAZ
        // (transient sayfa hiç GC edilemezdi) — OnAppearing/OnDisappearing'de kurulup sökülür.
    }

    private void OnLivePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(_vm.Live.IsRecording))
            Dispatcher.Dispatch(UpdateRecBlink);
    }

    // Kayıt sürerken kırmızı noktayı yanıp söndür; bitince tam görünür bırak.
    // Her çağrı önceki döngü neslini iptal eder (CTS) — hızlı durdur/başlat'ta
    // eski döngünün canlanıp çift animasyon çalıştırması mümkün değildir.
    // Tümü UI thread'inde çalışır (Dispatcher.Dispatch) — kilit gerekmez.
    private async void UpdateRecBlink()
    {
        _blinkCts?.Cancel();
        _blinkCts = null;
        if (!_vm.Live.IsRecording)
        {
            RecDot.Opacity = 1;
            return;
        }
        var cts = _blinkCts = new CancellationTokenSource();
        while (!cts.IsCancellationRequested && _vm.Live.IsRecording)
        {
            await RecDot.FadeTo(0.15, 450, Easing.SinInOut);
            await RecDot.FadeTo(1.0, 450, Easing.SinInOut);
        }
        if (ReferenceEquals(_blinkCts, cts)) _blinkCts = null;
        RecDot.Opacity = 1;
    }

    private void OnPipelineFrameReady(object? sender, EventArgs e)
        => Dispatcher.Dispatch(() => ChannelCanvas.InvalidateSurface());

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x0D, 0x0F, 0x14));

        var pipe = _vm.Live.Pipeline;
        int fw = pipe.FrameWidth, fh = pipe.FrameHeight;
        if (fw <= 0 || fh <= 0) return;

        var info = e.Info;
        float scale = Math.Min((float)info.Width / fw, (float)info.Height / fh);
        float dw = fw * scale, dh = fh * scale;
        float dx = (info.Width - dw) / 2f, dy = (info.Height - dh) / 2f;
        pipe.DrawCurrent(canvas, new SKRect(dx, dy, dx + dw, dy + dh));
    }

    // Her görünürlükte: pipeline'a abone ol + kanal listesini DB'den TAZELE.
    // Böylece başka sayfada (ör. kanal ekleme) eklenen kanallar geri dönünce listede görünür.
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _vm.Live.Pipeline.FrameReady -= OnPipelineFrameReady; // çift aboneliği önle
        _vm.Live.Pipeline.FrameReady += OnPipelineFrameReady;
        _vm.Live.PropertyChanged -= OnLivePropertyChanged;    // çift aboneliği önle
        _vm.Live.PropertyChanged += OnLivePropertyChanged;
        _vm.Activate(); // VM'in Live aboneliği + buton durumu tazeleme
        UpdateRecBlink(); // sayfa görünmezken değişen kayıt durumuna göre blink'i senkronla

        // Kayıt sürerken listeyi yeniden kurma (seçimi/kaydı bozmasın); aksi hâlde her zaman tazele.
        if (_vm.ProjectId > 0 && !_vm.Live.IsRecording)
            await _vm.LoadAsync();

        // Stream henüz başlamadıysa başlat (LoadSettings çağırma — overlay kanal bağlamını bozmasın).
        if (!_vm.Live.IsStreaming)
            await _vm.Live.StartStreamCommand.ExecuteAsync(null);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Live.Pipeline.FrameReady -= OnPipelineFrameReady;
        _vm.Live.PropertyChanged -= OnLivePropertyChanged; // singleton'a asılı kalma — sayfa GC edilebilsin
        _vm.Deactivate();
        // Görünmeyen sayfada animasyon döngüsü sürmesin
        _blinkCts?.Cancel();
        _blinkCts = null;
        RecDot.Opacity = 1;
    }
}
