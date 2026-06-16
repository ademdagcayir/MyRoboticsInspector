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
        // NOT: try/catch YOK — XAML/DI hatası yutulursa sayfa yarım kurulur (boş ekran) ve iz kalmaz.
        // İstisna yukarı taşınınca App.xaml.cs'teki UnhandledException hook'u crash.log'a yazar.
        InitializeComponent();
        BindingContext = _vm = vm;

        // SKCanvasView ↔ senkron pipeline: yeni composite kare gelince yeniden çiz
        VideoCanvas.PaintSurface += OnPaintSurface;
        _vm.Pipeline.FrameReady += OnPipelineFrameReady;
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
    private Microsoft.Maui.Controls.Window? _hookedWindow;

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe)
        {
            fe.KeyDown    -= OnPlatformKeyDown;
            fe.KeyUp      -= OnPlatformKeyUp;
            fe.LostFocus  -= OnPlatformLostFocus;
            fe.KeyDown    += OnPlatformKeyDown;
            fe.KeyUp      += OnPlatformKeyUp;
            // Pencere içi odak kaybında (dialog/popup, başka kontrole tıklama) güvenli dur
            fe.LostFocus  += OnPlatformLostFocus;
            fe.IsTabStop = true; // sayfa kök öğesi klavye odağı alabilsin
            // Otomatik odak: kullanıcı tıklamadan da W/A/S/D ile sürebilsin.
            // Metin kutusu odaktayken odağı ÇALMA — kullanıcı yazı yazıyor olabilir.
            void GrabFocus() { try { fe.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); } catch { } }
            fe.Loaded += (_, _) => { GrabFocus(); HookWindowSafetyStop(); };
            fe.PointerPressed += (_, _) => { if (!IsTextInputFocused(fe)) GrabFocus(); };
            GrabFocus();
            HookWindowSafetyStop();
        }
    }

    /// <summary>
    /// Pencere aktivasyon kaybında (Alt-Tab vb.) güvenli dur. WinUI'de pencere arka plana düşünce
    /// odaklı öğe LostFocus ALMAZ ve KeyUp gelmez → _pressedKeys dolu kalır → streamer robotu
    /// süresiz sürmeye devam ederdi. MAUI Window.Deactivated bu durumu yakalar.
    /// </summary>
    private void HookWindowSafetyStop()
    {
        var win = Window;
        if (win is null || ReferenceEquals(win, _hookedWindow)) return;
        if (_hookedWindow is not null)
        {
            _hookedWindow.Deactivated -= OnWindowDeactivated;
            _hookedWindow.Destroying  -= OnWindowDeactivated;
        }
        _hookedWindow = win;
        win.Deactivated += OnWindowDeactivated;
        win.Destroying  += OnWindowDeactivated;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => SafetyStopDrive();

    private void OnPlatformLostFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => SafetyStopDrive();

    /// <summary>Basılı tuş setini temizler ve sürüşü durdurur (odak/aktivasyon kaybı emniyeti).</summary>
    private void SafetyStopDrive()
    {
        if (_pressedKeys.Count == 0) return; // sürüş yoksa gereksiz Stop yayınlama
        _pressedKeys.Clear();
        if (_vm is not null) _ = _vm.KeyboardStopAsync();
    }

    /// <summary>Sayfadan ayrılırken (uygulama içi navigasyon) sürüşü güvenli durdur.</summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SafetyStopDrive();
    }

    /// <summary>Tuş olayının kaynağı bir metin giriş kontrolü mü? (Entry/Editor → WinUI TextBox)</summary>
    private static bool IsTextInput(object? src) => src is Microsoft.UI.Xaml.Controls.TextBox
        or Microsoft.UI.Xaml.Controls.PasswordBox
        or Microsoft.UI.Xaml.Controls.RichEditBox
        or Microsoft.UI.Xaml.Controls.AutoSuggestBox;

    private static bool IsTextInputFocused(Microsoft.UI.Xaml.FrameworkElement fe)
    {
        try
        {
            return fe.XamlRoot is not null
                && IsTextInput(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(fe.XamlRoot));
        }
        catch { return false; }
    }

    private void OnPlatformKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (MapDriveKey(e.Key) is null) return;
        if (IsTextInput(e.OriginalSource))
        {
            // Metin kutusuna yazılıyor (KeyDown köke kabarcıklanır): sürüş komutu ÜRETME.
            // Takılı kalmış basılı tuş varsa bırak (güvenli dur).
            if (_pressedKeys.Count > 0) { _pressedKeys.Clear(); UpdateKeyboardDrive(); }
            return; // e.Handled set ETME — karakter metin kutusuna normal girsin
        }
        // Auto-repeat (WasKeyDown) zararsız — SetMove idempotent; yine de seti güncel tutmak yeterli.
        _pressedKeys.Add(e.Key);
        UpdateKeyboardDrive();
        e.Handled = true;
    }

    private void OnPlatformKeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Her zaman setten çıkar (odak metin kutusunda olsa bile) — yoksa tuş takılı kalır,
        // robot durmaz. Removal yalnızca robotu durdurur; güvenli yan.
        if (_pressedKeys.Remove(e.Key))
        {
            UpdateKeyboardDrive();
            if (!IsTextInput(e.OriginalSource)) e.Handled = true;
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
