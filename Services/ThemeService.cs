namespace MyRoboticsInspector.Services;

/// <summary>
/// Uygulama temasını (Sistem / Açık / Koyu) yönetir.
/// Tercih Preferences'ta saklanır (açılışta anında okunur). Renk anahtarları
/// ColorsDark.xaml / ColorsLight.xaml içindedir; tema değişince doğru palet
/// merged-dictionary olarak yüklenir ve kök sayfa yeniden kurulur (StaticResource'lar yeniden çözülür).
/// </summary>
public static class ThemeService
{
    public const string System = "system";
    public const string Light  = "light";
    public const string Dark   = "dark";

    private const string PrefKey = "app_theme";

    /// <summary>Kayıtlı tema tercihi (varsayılan: koyu).</summary>
    public static string CurrentPref => Preferences.Get(PrefKey, Dark);

    public static void Persist(string pref) => Preferences.Set(PrefKey, pref);

    /// <summary>Açılışta: UserAppTheme + paleti uygula (kök henüz kurulmadığı için yeniden kurma yok).</summary>
    public static void ApplyInitial()
    {
        var pref = CurrentPref;
        SetUserAppTheme(pref);
        // App.xaml varsayılan olarak ColorsDark yükler — yalnızca açık tema gerekiyorsa değiştir.
        if (IsLight(pref)) SwapPalette(true);
    }

    /// <summary>Çalışma anında tema değiştir: tercihi sakla, paleti değiştir, kök sayfayı yeniden kur.</summary>
    public static void Apply(string pref)
    {
        Persist(pref);
        SetUserAppTheme(pref);
        SwapPalette(IsLight(pref));
        (Application.Current as App)?.RecreateRoot();
    }

    private static void SetUserAppTheme(string pref)
    {
        if (Application.Current is null) return;
        Application.Current.UserAppTheme = pref switch
        {
            Light => AppTheme.Light,
            Dark  => AppTheme.Dark,
            _     => AppTheme.Unspecified
        };
    }

    private static bool IsLight(string pref) => pref switch
    {
        Light => true,
        Dark  => false,
        _     => Application.Current?.PlatformAppTheme == AppTheme.Light
    };

    /// <summary>
    /// Yalnızca renk paleti sözlüğünü değiştirir. Theme.xaml renkleri DynamicResource ile
    /// tükettiği için stil/fırça/gradyanlar canlı güncellenir; sayfalardaki StaticResource
    /// renkler ise kök sayfa yeniden kurulunca (Apply) yeniden çözülür.
    /// </summary>
    private static void SwapPalette(bool light)
    {
        var app = Application.Current;
        if (app is null) return;

        var dicts = app.Resources.MergedDictionaries;
        try
        {
            var toRemove = dicts.Where(d => d is Resources.Styles.ColorsDark
                                         || d is Resources.Styles.ColorsLight).ToList();
            foreach (var d in toRemove) dicts.Remove(d);

            dicts.Add(light
                ? (ResourceDictionary)new Resources.Styles.ColorsLight()
                : new Resources.Styles.ColorsDark());
        }
        catch
        {
            // Palet değişimi başarısızsa uygulama mevcut temayla çalışmaya devam eder.
        }
    }
}
