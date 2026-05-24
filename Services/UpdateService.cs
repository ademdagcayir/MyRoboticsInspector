#if WINDOWS
using Velopack;
using Velopack.Sources;
#endif

namespace MyRoboticsInspector.Services;

/// <summary>
/// Velopack tabanlı otomatik güncelleme — sadece Windows'ta aktif.
/// Diğer platformlarda IsSupported=false ve tüm metodlar no-op.
///
/// Akış:
///   1) Kullanıcı "Güncellemeleri Kontrol Et" der → CheckAsync HTTP üzerinden RELEASES manifesti çeker
///   2) Yeni sürüm varsa UpdateInfo döner, UI "X.Y.Z yeni" gösterir
///   3) Kullanıcı "İndir ve Yükle" der → ApplyAndRestartAsync delta paketi çeker + uygular + restart
/// </summary>
public class UpdateService
{
    public bool IsSupported =>
#if WINDOWS
        true;
#else
        false;
#endif

    public string CurrentVersion => AppInfo.Current.VersionString;

    /// <summary>
    /// Sunucuda güncelleme var mı kontrol et. Yoksa null, varsa yeni sürüm versiyon dizesi döner.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(string updateUrl)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(updateUrl))
            return UpdateCheckResult.NotSupported;

#if WINDOWS
        try
        {
            var mgr = new UpdateManager(new SimpleWebSource(updateUrl));
            if (!mgr.IsInstalled)
                return UpdateCheckResult.NotInstalled;

            var info = await mgr.CheckForUpdatesAsync();
            if (info is null)
                return UpdateCheckResult.UpToDate;

            return UpdateCheckResult.UpdateAvailable(info.TargetFullRelease.Version.ToString());
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Error(ex.Message);
        }
#else
        await Task.CompletedTask;
        return UpdateCheckResult.NotSupported;
#endif
    }

    /// <summary>
    /// Güncellemeyi indir + uygula + uygulamayı yeni sürümle yeniden başlat.
    /// Bu metoddan dönüldüğünde uygulama zaten kapanıyor.
    /// </summary>
    public async Task<UpdateCheckResult> ApplyAndRestartAsync(string updateUrl, IProgress<int>? progress = null)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(updateUrl))
            return UpdateCheckResult.NotSupported;

#if WINDOWS
        try
        {
            var mgr = new UpdateManager(new SimpleWebSource(updateUrl));
            if (!mgr.IsInstalled)
                return UpdateCheckResult.NotInstalled;

            var info = await mgr.CheckForUpdatesAsync();
            if (info is null)
                return UpdateCheckResult.UpToDate;

            await mgr.DownloadUpdatesAsync(info, progress is null ? null : new Action<int>(progress.Report));
            mgr.ApplyUpdatesAndRestart(info);
            // Yukarıdaki çağrı normalde sürüm uygulanmadan dönmez, ama defansif olarak:
            return UpdateCheckResult.UpdateAvailable(info.TargetFullRelease.Version.ToString());
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Error(ex.Message);
        }
#else
        await Task.CompletedTask;
        return UpdateCheckResult.NotSupported;
#endif
    }
}

/// <summary>Güncelleme akışının üç olası dönüşü (+ hata).</summary>
public readonly record struct UpdateCheckResult(UpdateState State, string? NewVersion, string? ErrorMessage)
{
    public static UpdateCheckResult UpToDate            => new(UpdateState.UpToDate, null, null);
    public static UpdateCheckResult NotInstalled        => new(UpdateState.NotInstalled, null, null);
    public static UpdateCheckResult NotSupported        => new(UpdateState.NotSupported, null, null);
    public static UpdateCheckResult UpdateAvailable(string v) => new(UpdateState.UpdateAvailable, v, null);
    public static UpdateCheckResult Error(string msg)   => new(UpdateState.Error, null, msg);
}

public enum UpdateState
{
    UpToDate,
    UpdateAvailable,
    NotInstalled,   // dev build veya zip-extract — Velopack metadata yok
    NotSupported,   // Windows dışı platform
    Error,
}
