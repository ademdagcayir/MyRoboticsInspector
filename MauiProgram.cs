using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using SkiaSharp.Views.Maui.Controls.Hosting;
using MyRoboticsInspector.Pages;
using MyRoboticsInspector.Services;
using MyRoboticsInspector.ViewModels;
using QuestPDF.Infrastructure;

namespace MyRoboticsInspector;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if WINDOWS
        // Velopack bootstrap — kurulum/güncelleme CLI argümanlarını (--squirrel-*) yakalar
        // ve gerekirse erken çıkar. UI başlamadan önce çağrılmalı.
        Velopack.VelopackApp.Build().Run();
#endif

        QuestPDF.Settings.License = LicenseType.Community;

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if WINDOWS
        // ── Windows 11 native malzeme: Mica backdrop + temalı başlık çubuğu ──
        // Mica, masaüstü duvar kâğıdını yumuşatıp pencereye premium derinlik katar.
        // Desteklenmeyen ortamda (eski Win10 / RDP / GPU yok) sessizce klasik koyu temaya düşer.
        builder.ConfigureLifecycleEvents(events =>
        {
            events.AddWindows(windows => windows.OnWindowCreated(window =>
            {
                try
                {
                    window.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop
                    {
                        Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt
                    };

                    // Kök içeriği koyu temaya zorla → Mica koyu render olur, başlık çubuğu koyu görünür.
                    void ApplyDark()
                    {
                        if (window.Content is Microsoft.UI.Xaml.FrameworkElement fe)
                            fe.RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Dark;
                    }
                    ApplyDark();
                    window.Activated += (_, _) => ApplyDark();

                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                    var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
                    if (appWindow?.TitleBar is { } tb)
                    {
                        tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                        tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                        tb.ButtonForegroundColor = Microsoft.UI.Colors.White;
                        tb.ButtonInactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 142, 142, 147);
                        tb.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(38, 255, 255, 255);
                        tb.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                        tb.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(20, 255, 255, 255);
                        tb.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                    }
                }
                catch { /* Mica yoksa klasik koyu tema yeterli */ }
            }));
        });
#endif

        // Services (singletons — shared state across pages)
        builder.Services.AddSingleton<DatabaseService>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<VideoService>();
        builder.Services.AddSingleton<MqttRobotClient>();
        builder.Services.AddSingleton<IRobotProtocol>(sp => sp.GetRequiredService<MqttRobotClient>());
        builder.Services.AddSingleton<TelemetryService>();
        builder.Services.AddSingleton<ReportService>();
        builder.Services.AddSingleton<TelemetrySyncBuffer>();
        builder.Services.AddSingleton<SkiaOverlayRenderer>();
        builder.Services.AddSingleton<SyncedVideoPipeline>();
        builder.Services.AddSingleton<FfmpegOverlayRecorder>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<RobotDriveStreamer>();
        builder.Services.AddSingleton<IGamepadInput, XInputGamepadService>();
        builder.Services.AddSingleton<GamepadCommandMapper>();
        builder.Services.AddSingleton<UpdateService>();

        // ViewModels
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddSingleton<LiveViewModel>(); // singleton — kamera oturumu sayfalar arası korunur
        builder.Services.AddTransient<CustomersViewModel>();
        builder.Services.AddTransient<ProjectsViewModel>();
        builder.Services.AddTransient<ProjectFormViewModel>();
        builder.Services.AddTransient<ProjectChannelsViewModel>();
        builder.Services.AddTransient<ChannelEditViewModel>();
        builder.Services.AddTransient<InspectionsViewModel>();
        builder.Services.AddTransient<InspectionDetailViewModel>();
        builder.Services.AddTransient<InspectionReviewViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();

        // Shell + Pages
        builder.Services.AddTransient<AppShell>();
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddSingleton<MainPage>(); // singleton — OnAppearing guard çalışsın, kamera yeniden başlamasın
        builder.Services.AddTransient<CustomersPage>();
        builder.Services.AddTransient<ProjectsPage>();
        builder.Services.AddTransient<ProjectFormPage>();
        builder.Services.AddTransient<ProjectChannelsPage>();
        builder.Services.AddTransient<ChannelEditPage>();
        builder.Services.AddTransient<InspectionsPage>();
        builder.Services.AddTransient<InspectionDetailPage>();
        builder.Services.AddTransient<InspectionReviewPage>();
        builder.Services.AddTransient<SettingsPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
