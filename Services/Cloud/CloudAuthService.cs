using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyRoboticsInspector.Services.Cloud;

/// <summary>Aktif bulut oturumu (Supabase Auth JWT).</summary>
public sealed class CloudSession
{
    public required string UserId { get; init; }
    public required string Email { get; init; }
    public required string AccessToken { get; set; }
    public required string RefreshToken { get; set; }
    public DateTime ExpiresAtUtc { get; set; }

    public bool IsExpiringSoon => DateTime.UtcNow > ExpiresAtUtc - TimeSpan.FromSeconds(90);
}

/// <summary>
/// Supabase Auth istemcisi — saf HttpClient, ek NuGet yok.
/// Çoklu hesap: refresh token YEREL PROFİL BAŞINA SecureStorage'da saklanır
/// (cloud_rt_{profileId}); profil değişince o profilin bulut hesabı devreye girer.
/// Tüm hatalar Türkçe mesajlı CloudAuthException olarak fırlatılır.
/// </summary>
public class CloudAuthService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _attachedProfileId = -1;

    public CloudSession? Session { get; private set; }
    public bool IsSignedIn => Session is not null;
    public event EventHandler? SessionChanged;

    /// <summary>
    /// Login ekranı kaldırıldığından yerel profil yok — bulut oturumu cihaz başına
    /// tek sabit kimlikle (0) saklanır. Login geri gelirse gerçek profil Id'si geçilir.
    /// </summary>
    public const int LocalProfileId = 0;

    private static string RtKey(int profileId) => $"cloud_rt_{profileId}";
    private static string EmailKey(int profileId) => $"cloud_email_{profileId}";

    // ── Oturum yaşam döngüsü ────────────────────────────────────────────────

    /// <summary>
    /// Yerel profil değişince çağrılır: o profilin kayıtlı refresh token'ı varsa
    /// sessizce oturum açar; yoksa oturumu kapatır. Ağ hatasında patlamaz.
    /// </summary>
    public async Task AttachProfileAsync(int profileId)
    {
        await _gate.WaitAsync();
        try
        {
            _attachedProfileId = profileId;
            Session = null;

            string? rt = null;
            try { rt = await SecureStorage.GetAsync(RtKey(profileId)); }
            catch { /* SecureStorage erişilemedi — oturumsuz devam */ }

            if (!string.IsNullOrEmpty(rt))
            {
                try
                {
                    Session = await RefreshCoreAsync(rt);
                    // KRİTİK: Supabase refresh token'ları tek-kullanımlık (rotate edilir).
                    // Dönen yeni token'ı geri yazmazsak SecureStorage'taki token tükenir →
                    // sonraki açılışta reddedilir → sessiz oturum kaybı. (GetValidAccessTokenAsync
                    // ve StoreAndActivateAsync zaten persist ediyor; bu yol da etmeli.)
                    await SecureStorage.SetAsync(RtKey(profileId), Session.RefreshToken);
                }
                catch { /* ağ yok / token geçersiz — kullanıcı tekrar giriş yapar */ }
            }
        }
        finally { _gate.Release(); }
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SignInAsync(string email, string password)
    {
        var session = await TokenRequestAsync(
            "grant_type=password",
            JsonSerializer.Serialize(new { email = email.Trim(), password }));
        await StoreAndActivateAsync(session);
    }

    public async Task SignUpAsync(string email, string password)
    {
        var body = JsonSerializer.Serialize(new { email = email.Trim(), password });
        using var req = NewRequest(HttpMethod.Post, "/auth/v1/signup", body);
        using var resp = await Http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new CloudAuthException(TranslateError(json));

        var parsed = JsonSerializer.Deserialize<AuthResponse>(json);
        // E-posta doğrulaması kapalıysa signup yanıtı doğrudan oturum içerir.
        if (parsed?.AccessToken is { Length: > 0 })
            await StoreAndActivateAsync(ToSession(parsed));
        else
            throw new CloudAuthException("Hesap oluşturuldu — e-postanıza gelen doğrulama bağlantısına tıklayıp tekrar giriş yapın.");
    }

    public async Task SignOutAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Session = null;
            if (_attachedProfileId >= 0)
            {
                SecureStorage.Remove(RtKey(_attachedProfileId));
                SecureStorage.Remove(EmailKey(_attachedProfileId));
            }
        }
        finally { _gate.Release(); }
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Geçerli (gerekirse yenilenmiş) access token döndürür; oturum yoksa null.</summary>
    public async Task<string?> GetValidAccessTokenAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Session is null) return null;
            if (Session.IsExpiringSoon)
            {
                try
                {
                    var fresh = await RefreshCoreAsync(Session.RefreshToken);
                    Session = fresh;
                    if (_attachedProfileId >= 0)
                        await SecureStorage.SetAsync(RtKey(_attachedProfileId), fresh.RefreshToken);
                }
                catch (CloudAuthException) { Session = null; return null; }
                catch { /* geçici ağ hatası — mevcut token'la dene */ }
            }
            return Session?.AccessToken;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Hassas işlem (bulut silme) öncesi parola kanıtı: mevcut hesabın e-postasıyla
    /// yeniden giriş dener. Başarılıysa true.
    /// </summary>
    public async Task<bool> ReauthenticateAsync(string password)
    {
        if (Session is null) return false;
        try
        {
            await TokenRequestAsync(
                "grant_type=password",
                JsonSerializer.Serialize(new { email = Session.Email, password }));
            return true;
        }
        catch (CloudAuthException) { return false; }
    }

    // ── İç işleyiş ──────────────────────────────────────────────────────────

    private async Task StoreAndActivateAsync(CloudSession session)
    {
        await _gate.WaitAsync();
        try
        {
            Session = session;
            if (_attachedProfileId >= 0)
            {
                await SecureStorage.SetAsync(RtKey(_attachedProfileId), session.RefreshToken);
                await SecureStorage.SetAsync(EmailKey(_attachedProfileId), session.Email);
            }
        }
        finally { _gate.Release(); }
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<CloudSession> RefreshCoreAsync(string refreshToken)
        => await TokenRequestAsync(
            "grant_type=refresh_token",
            JsonSerializer.Serialize(new { refresh_token = refreshToken }));

    private async Task<CloudSession> TokenRequestAsync(string grantQuery, string jsonBody)
    {
        using var req = NewRequest(HttpMethod.Post, $"/auth/v1/token?{grantQuery}", jsonBody);
        using var resp = await Http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new CloudAuthException(TranslateError(json));

        var parsed = JsonSerializer.Deserialize<AuthResponse>(json)
            ?? throw new CloudAuthException("Sunucu yanıtı çözümlenemedi.");
        if (string.IsNullOrEmpty(parsed.AccessToken) || parsed.User?.Id is null)
            throw new CloudAuthException("Sunucu yanıtında oturum bilgisi yok.");
        return ToSession(parsed);
    }

    private static CloudSession ToSession(AuthResponse r) => new()
    {
        UserId = r.User!.Id!,
        Email = r.User.Email ?? "",
        AccessToken = r.AccessToken!,
        RefreshToken = r.RefreshToken ?? "",
        ExpiresAtUtc = DateTime.UtcNow.AddSeconds(r.ExpiresIn > 0 ? r.ExpiresIn : 3600),
    };

    private static HttpRequestMessage NewRequest(HttpMethod method, string path, string? jsonBody)
    {
        var req = new HttpRequestMessage(method, CloudConfig.Url + path);
        req.Headers.Add("apikey", CloudConfig.AnonKey);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return req;
    }

    /// <summary>Supabase auth hata gövdesini kullanıcıya gösterilebilir Türkçeye çevirir.</summary>
    private static string TranslateError(string json)
    {
        string? code = null, msg = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error_code", out var c)) code = c.GetString();
            if (doc.RootElement.TryGetProperty("msg", out var m)) msg = m.GetString();
            else if (doc.RootElement.TryGetProperty("message", out var m2)) msg = m2.GetString();
            else if (doc.RootElement.TryGetProperty("error_description", out var m3)) msg = m3.GetString();
        }
        catch { /* gövde JSON değil */ }

        return (code ?? msg) switch
        {
            "invalid_credentials" => "E-posta veya parola hatalı.",
            "email_not_confirmed" => "E-posta doğrulanmamış — gelen kutunuzdaki bağlantıya tıklayın.",
            "user_already_exists" => "Bu e-posta ile zaten bir hesap var — giriş yapmayı deneyin.",
            "weak_password" => "Parola çok zayıf (en az 6 karakter).",
            "over_request_rate_limit" => "Çok fazla deneme — birkaç dakika bekleyin.",
            _ => string.IsNullOrWhiteSpace(msg) ? "Bulut sunucusuna ulaşılamadı." : msg!,
        };
    }

    private sealed class AuthResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("user")] public AuthUser? User { get; set; }
    }

    private sealed class AuthUser
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
    }
}

public class CloudAuthException : Exception
{
    public CloudAuthException(string message) : base(message) { }
}
