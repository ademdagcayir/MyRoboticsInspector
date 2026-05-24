# MyRoboticsInspector — Sürüm Yayınlama Rehberi

**Hedef:** Yeni bir Windows sürümünü uçtan uca yayınlamak; mevcut kullanıcıların **uygulamada tek-tık** ile güncelleme alması.

Bu sistem **Velopack** üzerine kurulu. Velopack iki dosya üretir:
- **Setup.exe** — yeni kullanıcılar için ilk-kurulum installer'ı
- **\*-full / \*-delta nupkg + RELEASES** — mevcut kullanıcıların otomatik güncelleyebilmesi için sunucu manifesti

---

## 1. Hızlı yayın akışı (5 dakika)

Her yeni sürüm için:

```cmd
cd C:\Users\ademd\source\repos\MyRoboticsInspector\Tools
publish-windows.cmd 1.0.1
```

Sonra `..\Releases\` klasörünün **tüm içeriğini** sunucuya yükle:

```
https://myrobotics.com.tr/updates/inspector/
├── Setup.exe                              ← kullanıcılara yeni link verirsen bunu yolla
├── MyRoboticsInspector-1.0.1-full.nupkg   ← full paket (~150 MB)
├── MyRoboticsInspector-1.0.1-delta.nupkg  ← delta (önceki sürümden farkı, çok küçük)
└── RELEASES                               ← manifest, BU OLMAZSA istemci güncellemez!
```

**Çok önemli:** `RELEASES` dosyası **olmadan** istemci güncelleme görmez. Her seferinde yüklediğine emin ol.

---

## 2. Sürüm numaralandırma kuralları

`MyRoboticsInspector.csproj` içinde:

```xml
<ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
<ApplicationVersion>1</ApplicationVersion>
```

Bu değerleri **manuel değiştirme** — publish script'i `Version=1.0.1` parametresi ile override eder.

**Semantik sürüm öner:**
- **Major** (1.x.x → 2.x.x): kırılgan değişiklik (DB şeması, MQTT protokol uyumsuzluğu)
- **Minor** (1.0.x → 1.1.x): yeni özellik, geriye dönük uyumlu
- **Patch** (1.0.0 → 1.0.1): bug fix, küçük düzeltme

**Sürüm formatı:** `MAJOR.MINOR.PATCH` (üç parça, örn. `1.2.0`). Velopack dört parça da kabul eder (`1.2.0.42`) ama gerek yok.

---

## 3. İlk kurulum (yeni PC'de)

Kullanıcıya **`Setup.exe`** dosyasını gönder (veya download link). Çift tıkla, kurulum sessizce yapılır:

- Uygulama → `%LocalAppData%\MyRoboticsInspector\` altına kurulur
- Başlat menüsünde + masaüstünde kısayol oluşur
- Hiçbir UAC prompt'u, admin gerek yok (per-user install)

Sonradan **silmek** için: Ayarlar → Uygulamalar → MyRoboticsInspector → Kaldır.

**Önemli notlar:**
- İlk kurulumun ardından otomatik güncellemeler çalışır
- ZIP'i extract edip çalıştıran kullanıcılar **güncelleme alamaz** (UpdateService `NotInstalled` döner) — Setup.exe ile kurulması ŞART

---

## 4. Otomatik güncelleme akışı (kullanıcı tarafı)

1. Uygulama açılır → arka planda `Settings.UpdateServerUrl`'e HTTP GET atar (eğer `AutoCheckUpdates=true` ise)
2. Yeni sürüm varsa **Ayarlar sayfasının üst kısmında bildirim**: "🎉 Yeni sürüm: 1.0.1"
3. Kullanıcı "⬇ İndir ve Yeniden Başlat" tıklar
4. Sadece **delta paket** (genelde 1-10 MB) iner
5. Uygulama kapanır, yeni sürüm açılır

İlk açılışın yavaş olmasını istemiyorsan: `AutoCheckUpdates`'i kapatıp manuel "Şimdi Kontrol Et" akışına geç.

---

## 5. Sunucu kurulumu

Velopack **statik dosya sunucusu** kullanır — sıradan bir web server yeterli. Setup özel bir uygulama gerektirmez.

### Apache / nginx
Hiçbir özel yapılandırma gerekmez, sadece `RELEASES` ve `*.nupkg` dosyalarına public read erişimi olsun. **MIME** type için:

```nginx
# nginx — .nupkg ve RELEASES dosyalarına application/octet-stream gönder
location ~ \.(nupkg|RELEASES)$ {
    default_type application/octet-stream;
}
```

### GitHub Releases (alternatif, ücretsiz)
- Repository'de Release oluştur
- `Setup.exe`, `*-full.nupkg`, `*-delta.nupkg`, `RELEASES` dosyalarını asset olarak yükle
- UpdateServerUrl: `https://github.com/<org>/<repo>/releases/download/latest/`
- Veya custom GitHub API endpoint kullan

### CDN (CloudFlare / Fastly)
HTTP cache'i 5 dk'dan kısa tut, yoksa kullanıcı eski manifesti görür ve güncellemeyi göremez.

---

## 6. Code signing (önerilir, ileride)

Şu an Setup.exe ve .nupkg dosyaları **imzasız**. Windows SmartScreen ilk kurulumda "Unrecognized app" uyarısı verir. Production için:

```cmd
vpk pack ... --signParams="/v /fd sha256 /sha1 <thumbprint> /tr http://timestamp.digicert.com /td sha256"
```

Code-signing cert için: Sectigo / DigiCert / SSL.com (~$300/yıl). Veya **Microsoft Trusted Signing** ($10/ay) — yeni, ucuz seçenek.

---

## 7. Rollback (kötü sürüm yayınladıysan)

Velopack RELEASES dosyasındaki sırayı kullanır. Bozuk `1.0.1`'i geri almak için:

1. Sunucudan `MyRoboticsInspector-1.0.1-*.nupkg` dosyalarını sil
2. `RELEASES` dosyasını manuel düzenle, 1.0.1 satırlarını çıkar
3. İstemciler bir sonraki kontrolde 1.0.0'ı son sürüm olarak görür (zaten 1.0.0'ı kullananlar etkilenmez)

**Daha temiz:** `1.0.2` hot-fix yayınla, problemi düzelt, mevcut bozuk 1.0.1 kullanıcıları otomatik 1.0.2'ye yükselir.

---

## 8. Beta kanalı (opsiyonel)

Test grubunuza ayrı bir sunucu URL'i ver:

- Production: `https://myrobotics.com.tr/updates/inspector/`
- Beta:       `https://myrobotics.com.tr/updates/inspector-beta/`

Beta kullanıcılar Settings'te beta URL'i ayarlar. Aynı app, farklı manifest sunucusu.

---

## 9. Sorun giderme

### "NotInstalled" hatası
Kullanıcı zip'ten çalıştırıyor veya `dotnet run` ile dev modunda. Setup.exe ile kurması gerek.

### "Bağlantı zaman aşımı"
Sunucu erişilemiyor — UpdateServerUrl yanlış olabilir, firewall engelliyor olabilir, ya da sunucu down.

### Setup.exe SmartScreen uyarısı veriyor
Code signing eksik. "More info → Run anyway" ile devam edilir. Code signing sertifikası alın.

### Güncellemeden sonra uygulama açılmıyor
Velopack rollback'i otomatik tetikler — eski sürüm geri yüklenir. Log: `%LocalAppData%\MyRoboticsInspector\packages\SquirrelClowdTemp\`

### Çok büyük indirme
Delta paketleri kullanılmıyor olabilir. Bir önceki sürümün `*-full.nupkg` dosyası sunucuda olmalı ki delta hesaplanabilsin. Eski full paketleri silme — manifestte ilk birkaç sürümü tut.

---

## 10. CI/CD — GitHub Actions (aktif)

`.github/workflows/release-windows.yml` dosyası repo'ya eklenmiş durumda.

### Otomatik yayın (tag push)

```bash
git tag v1.0.1
git push origin v1.0.1
```

→ GitHub Actions otomatik olarak:
1. `dotnet publish` (self-contained, win-x64)
2. `vpk pack` (Setup.exe + full.nupkg + delta.nupkg + RELEASES)
3. GitHub Release oluşturur, dosyaları asset olarak ekler

Release URL'i: `https://github.com/ademdagcayir/MyRoboticsInspector/releases`

### Manuel tetikleme (Actions UI'dan)

GitHub → Actions → "Windows Release" → "Run workflow" → sürüm gir → Çalıştır.

### UpdateService: GitHub Releases kullanmak için

`AppSettings.UpdateServerUrl` değerini şöyle ayarla:

```
https://github.com/ademdagcayir/MyRoboticsInspector
```

UpdateService, URL `https://github.com/` ile başlıyorsa otomatik olarak `GithubSource` seçer
(public releases gerektirir). Private repo için kendi sunucunla `SimpleWebSource` kullanmaya devam et.

### Ek adım: kendi sunucuya da yükle (isteğe bağlı)

Eğer hem GitHub Release hem `myrobotics.com.tr/updates/inspector/` üzerinden güncelletmek istersen,
workflow'un sonuna ekle:

```yaml
- name: Upload to own server
  # scp / rsync / FTP / S3 ile Releases\ klasörünü sunucuya at
  run: |
    # Örnek: rsync -avz Releases/ user@myrobotics.com.tr:/var/www/updates/inspector/
```

---

## Hızlı kontrol listesi

İlk kez yayın için:
- [x] GitHub repo oluşturuldu (`ademdagcayir/MyRoboticsInspector`, private)
- [x] `.github/workflows/release-windows.yml` commit'lendi
- [ ] Sunucu hazır + HTTPS aktif (kendi sunucu kullanıyorsan)
- [ ] `Settings.UpdateServerUrl` default değeri kod içinde doğru
- [ ] Test PC'de Setup.exe ile kurulum + yeni sürüm güncelleme akışı denendi

Her sürüm için:
- [ ] `git tag vX.Y.Z && git push origin vX.Y.Z` ile GitHub Release tetikle
- [ ] Actions tamamlandı mı kontrol et: `gh run list --repo ademdagcayir/MyRoboticsInspector`
- [ ] Release dosyaları yüklendi mi: `gh release view vX.Y.Z --repo ademdagcayir/MyRoboticsInspector`
- [ ] Test PC'de "Şimdi Kontrol Et" → yeni sürüm görüldü → indirildi → çalıştı

Hızlı tek-satır kontrol:

```bash
gh release list --repo ademdagcayir/MyRoboticsInspector
```
