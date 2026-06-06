using MyRoboticsInspector.Models;
using MyRoboticsInspector.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace MyRoboticsInspector;

public partial class MainPage : ContentPage
{
    private readonly LiveViewModel _vm;
    private bool _hasInitialized = false;

    public MainPage(LiveViewModel vm)
    {
        try
        {
            InitializeComponent();
            BindingContext = _vm = vm;

            // SKCanvasView ↔ senkron pipeline: yeni composite kare gelince yeniden çiz
            VideoCanvas.PaintSurface += OnPaintSurface;
            _vm.Pipeline.FrameReady += OnPipelineFrameReady;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MainPage constructor error: {ex}");
        }
    }

    private void OnPipelineFrameReady(object? sender, EventArgs e)
        => Dispatcher.Dispatch(() => VideoCanvas.InvalidateSurface());

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x0D, 0x0F, 0x14)); // video void zemini

        int fw = _vm.Pipeline.FrameWidth;
        int fh = _vm.Pipeline.FrameHeight;
        if (fw <= 0 || fh <= 0) return;

        // Aspect-fit: composite kareyi (ana akış çözünürlüğü) ekrana sığacak şekilde küçült
        var info = e.Info;
        float scale = Math.Min((float)info.Width / fw, (float)info.Height / fh);
        float dw = fw * scale, dh = fh * scale;
        float dx = (info.Width - dw) / 2f, dy = (info.Height - dh) / 2f;
        _vm.Pipeline.DrawCurrent(canvas, new SKRect(dx, dy, dx + dw, dy + dh));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_hasInitialized)
            return;

        _hasInitialized = true;

        try
        {
            // Delay loading to ensure UI is fully ready
            await Task.Delay(300);

            if (_vm != null)
            {
                await _vm.LoadSettingsAsync();

                // Initialize video service (without MediaElement for now)
                _vm.VideoService?.Initialize();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MainPage.OnAppearing error: {ex}");
            try
            {
                await DisplayAlert("Hata", $"Ayarlar yükleme hatası: {ex.Message}", "Tamam");
            }
            catch { }
        }
    }

#if WINDOWS
    // ===== KLAVYE SÜRÜŞÜ (yalnızca Windows) =====
    // WASD / ok tuşları robotu sürer. Tuş basıldığında LiveViewModel.KeyboardDrive() aktif hareketi
    // RobotDriveStreamer'a set eder (streamer 100 ms'de bir yeniden yayınlar → robot watchdog'u canlı
    // görür); tuş bırakıldığında KeyboardStopAsync() Stop yayınlar. Gamepad/dpad ile aynı streamer'ı
    // paylaşır, yani tek watchdog kaynağı. Çoklu basışı doğru ele almak için basılı tuş seti tutulur.
    private readonly HashSet<Windows.System.VirtualKey> _pressedKeys = new();

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe)
        {
            fe.KeyDown -= OnPlatformKeyDown;
            fe.KeyUp   -= OnPlatformKeyUp;
            fe.KeyDown += OnPlatformKeyDown;
            fe.KeyUp   += OnPlatformKeyUp;
            fe.IsTabStop = true; // sayfa kök öğesi klavye odağı alabilsin
            // Otomatik odak: kullanıcı tıklamadan da W/A/S/D ile sürebilsin
            void GrabFocus() { try { fe.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); } catch { } }
            fe.Loaded += (_, _) => GrabFocus();
            fe.PointerPressed += (_, _) => GrabFocus();
            GrabFocus();
        }
    }

    private void OnPlatformKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (MapDriveKey(e.Key) is null) return;
        // Auto-repeat (WasKeyDown) zararsız — SetMove idempotent; yine de seti güncel tutmak yeterli.
        _pressedKeys.Add(e.Key);
        UpdateKeyboardDrive();
        e.Handled = true;
    }

    private void OnPlatformKeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_pressedKeys.Remove(e.Key))
        {
            UpdateKeyboardDrive();
            e.Handled = true;
        }
    }

    /// <summary>Basılı tuş setine göre tek bir aktif sürüş komutu uygular; set boşsa durur.</summary>
    private void UpdateKeyboardDrive()
    {
        if (_vm is null) return;
        foreach (var k in _pressedKeys)
        {
            if (MapDriveKey(k) is RobotCommandType t)
            {
                _vm.KeyboardDrive(t);
                return;
            }
        }
        _ = _vm.KeyboardStopAsync();
    }

    private static RobotCommandType? MapDriveKey(Windows.System.VirtualKey k) => k switch
    {
        Windows.System.VirtualKey.W or Windows.System.VirtualKey.Up    => RobotCommandType.MoveForward,
        Windows.System.VirtualKey.S or Windows.System.VirtualKey.Down  => RobotCommandType.MoveBackward,
        Windows.System.VirtualKey.A or Windows.System.VirtualKey.Left  => RobotCommandType.TurnLeft,
        Windows.System.VirtualKey.D or Windows.System.VirtualKey.Right => RobotCommandType.TurnRight,
        _ => null
    };
#endif
}
