using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
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
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Services (singletons — shared state across pages)
        builder.Services.AddSingleton<DatabaseService>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<VideoService>();
        builder.Services.AddSingleton<MqttRobotClient>();
        builder.Services.AddSingleton<IRobotProtocol>(sp => sp.GetRequiredService<MqttRobotClient>());
        builder.Services.AddSingleton<TelemetryService>();
        builder.Services.AddSingleton<ReportService>();
        builder.Services.AddSingleton<FfmpegRecorder>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<IGamepadInput, XInputGamepadService>();
        builder.Services.AddSingleton<GamepadCommandMapper>();
        builder.Services.AddSingleton<UpdateService>();

        // ViewModels
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<LiveViewModel>();
        builder.Services.AddTransient<CustomersViewModel>();
        builder.Services.AddTransient<ProjectsViewModel>();
        builder.Services.AddTransient<ProjectFormViewModel>();
        builder.Services.AddTransient<ProjectChannelsViewModel>();
        builder.Services.AddTransient<ChannelEditViewModel>();
        builder.Services.AddTransient<InspectionsViewModel>();
        builder.Services.AddTransient<InspectionDetailViewModel>();
        builder.Services.AddTransient<InspectionReviewViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();

        // Pages
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<MainPage>();
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
