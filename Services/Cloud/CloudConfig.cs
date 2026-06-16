namespace MyRoboticsInspector.Services.Cloud;

/// <summary>
/// Supabase bulut yedek yapılandırması. ChequeApp ile aynı Supabase projesi kullanılır;
/// izolasyon 'inspector_' önekli tablolar + ayrı 'inspector-files' bucket ile sağlanır.
/// Anon key public-by-design'dır (RLS koruması sunucudadır) — gizli anahtar DEĞİLDİR.
/// Kurulum: supabase/inspector_cloud.sql dosyasını Supabase SQL Editor'da bir kez çalıştır.
/// </summary>
public static class CloudConfig
{
    // MyRoboticsInspector'a AİT AYRI Supabase projesi (ChequeApp'ten tamamen bağımsız).
    public const string Url = "https://sroxaqucqhvwswkivsyw.supabase.co";

    // Publishable key (yeni Supabase API key formatı, eski "anon" karşılığı). apikey
    // header'ında kullanılır; public-by-design — gerçek koruma sunucudaki RLS'tedir.
    public const string AnonKey = "sb_publishable_I2ky5SUsiGDhj62Ue0Asfg_Hw6C_J58";

    public const string Bucket = "inspector-files";
}
