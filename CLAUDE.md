# MyRoboticsInspector — Proje Rehberi (CLAUDE.md)

> Bu dosyanın amacı: tüm kodu taramadan projeyi hızlıca anlamak. Yeni bir oturuma
> başlarken ÖNCE bunu oku. UI tamamen **Türkçe**. Birincil hedef **Windows (paketsiz)**.

## 1. Proje nedir?
Kanal/atıksu **CCTV görüntüleme robotu** kontrol ve raporlama uygulaması (.NET 9 MAUI).
Operatör; RTSP kameralı bir tekerlekli robotu joystick'le sürer, canlı videoyu izler,
fotoğraf/video kaydeder, kusur işaretler, kanal başına PDF rapor üretir. Robot ve
pano (kablo makarası) **MQTT** üzerinden konuşur; veri overlay olarak videoya gömülür.

- **Donanım**: Robot (Arduino Mega + W5500 Ethernet), Pano (Arduino Mega), Hikvision RTSP kamera, Logitech F710 joystick (XInput).
- **Firmware repo (AYRI)**: https://github.com/ademdagcayir/MyRoboticsFirmware  (`robot/robot_mega_moodemsiz`, `pano/pano_mega_modemsiz`)
- **Uygulama repo**: https://github.com/ademdagcayir/MyRoboticsInspector

## 2. Teknoloji yığını
- **.NET 9 MAUI**, hedef `net9.0-windows10.0.19041.0` (Android/Mac kodda var ama Windows birincil).
- **CommunityToolkit.Mvvm** — `[ObservableProperty]`, `[RelayCommand]`, `partial void OnXChanged`.
- **MQTTnet** (broker istemcisi), **SQLite** (`sqlite-net`), **SkiaSharp** (canlı overlay), **QuestPDF** (rapor), **ClosedXML** (Excel), **Velopack** (otomatik güncelleme), **LibVLCSharp** (VideoService — eski yol).
- **ffmpeg** (harici exe) — kayıt + canlı decode.

## 3. Mimari — ekranlar
- **LoginPage** — sinematik operatör seçimi (altın aksanlı cam panel, giriş animasyonu). Yerel profil/PIN; **PIN'siz son profil sessizce otomatik girer** (App.BuildRootPage → `last_profile_autologin` Preferences). Çıkış/profil değiştirme AppShell flyout footer'ında.
- **MainPage** ("Kontrol Paneli", eski adı "Canlı Görüntü") — kalkış-öncesi kontrol paneli: kamera, çevre birimleri (Broker/Robot/Pano/Joystick online), MQTT/cihaz konsolları, joystick teşhisi, manuel sürüş test tezgahı. `Görünüm` dropdown ile büyük alan: Çevre Birimleri / Kamera / MQTT Log / Robot-Pano Konsolu.
- **ProjectsPage → ProjectChannelsPage** — asıl saha ekranı: kanal listesi + canlı kamera + Kanal Başı/Sonu + fotoğraflar. Kanal Sonu → otomatik PDF rapor.
- **ChannelEditPage** — kanal formu (İSKİ/EN 13508-2 alanları).
- **InspectionsPage / InspectionDetailPage / InspectionReviewPage** — geçmiş kayıtlar.
- **SettingsPage** — RTSP, MQTT broker, proje, depolama, ffmpeg, overlay font, **sensör kalibrasyonu**, yedekleme (OneDrive + **Bulut Yedek**), güncelleme.

## 3b. BULUT YEDEK (Supabase) — add-only + PIN korumalı silme
- **Amaç**: video/resim/rapor/veri otomatik buluta; **silme yalnız özel izinle** (sunucu doğrulamalı PIN). Çoklu hesap: her yerel profil kendi bulut hesabına bağlanır.
- **Altyapı**: ChequeApp ile AYNI Supabase projesi (`vmjqfqbfcwjiyzhzxnlr`), `inspector_` önekli tablolar + `inspector-files` bucket — tam izole. Saf `HttpClient`, ek NuGet yok.
- **KURULUM (bir kez)**: `supabase/inspector_cloud.sql` → Supabase SQL Editor'da çalıştır. Çalıştırılmazsa uygulama bozulmaz; senkron "Bulut tabloları kurulu değil" hatası gösterir.
- `Services/Cloud/CloudConfig.cs` — URL + anon key (public-by-design) + bucket adı.
- `Services/Cloud/CloudAuthService.cs` — Supabase Auth (kayıt/giriş/refresh/re-auth). Refresh token **profil başına** SecureStorage'da (`cloud_rt_{profileId}`); profil değişince o profilin hesabı devreye girer.
- `Services/Cloud/CloudBackupService.cs` — outbox deseni: tarayıcı StorageRoot'taki (projeler/snapshots/inspections/reports) .mp4/.jpg/.png/.pdf dosyalarını `cloud_file_state` SQLite kuyruğuna ekler (kod yolundan bağımsız — HER dosyayı yakalar); işçi sırayla Storage'a stream upload (`{uid}/{deviceId}/{relPath}`); SQLite satırları jsonb anlık görüntü olarak `inspector_records`'a upsert; günde bir tam `.db3` kopyası (`conn.BackupAsync`). 60 sn'den taze dosyalar atlanır (yazımı bitmemiş olabilir). 409 = zaten yüklü. AppSettings YÜKLENMEZ (kamera/broker şifresi içerir), Profile.Pin payload'dan çıkarılır.
- **Silme koruması (sunucuda zorlanır)**: tablolarda + storage'da DELETE politikası `inspector_delete_unlocked()` ister → yalnız `inspector_unlock_delete(pin)` RPC'siyle **5 dakikalık pencere** açılır; PIN bcrypt hash sunucuda (`inspector_settings`), cihazda tutulmaz. UPDATE politikası yok = dosyalar immutable.
- **UI**: Ayarlar → "☁️ BULUT YEDEK" kartı (giriş/kayıt, anahtarlar, kuyruk durumu, son dosyalar + 🔒🗑 silme, PIN yönetimi). AppShell flyout footer'da canlı bulut rozeti.
- **Çoklu cihaz/operatör/robot ayrımı (TEK FİRMA HESABI modeli):** Tüm tabletler tek firma hesabıyla giriş yapar (tenant_id). Ayrım 3 katmanda: (1) **cihaz** = kalıcı `device_id` (Preferences GUID) + `inspector_devices` envanteri (device_name = Ayarlar'daki "Cihaz Adı", boşsa MachineName); storage yolu `{uid}/{deviceId}/projeler/...`. (2) **operatör + robot** = her kanal kaydı (Inspection) KAYIT ANINDA `OperatorName`/`RobotId`/`DeviceId` ile damgalanır (LiveViewModel.StartRecordingForChannelAsync; Ayarlar'daki OperatorName + RobotId + CloudBackupService.DeviceId'den dondurulur — sonra ayar değişse de korunur). Bu alanlar Inspection payload'ında jsonb olarak buluta gider → ofiste `payload->>'OperatorName'` / `payload->>'RobotId'` ile sorgulanır (ek SQL kolonu gerekmedi). Login ekranı KALDIRILDI (kullanıcı tercihi: sürtünmesiz açılış) — operatör = Ayarlar'daki aktif "Operatör Adı".
- PostgREST upsert dersi (ChequeApp'ten): `?on_conflict=<unique kolonlar>` parametresi ŞART.

## 4. MQTT FIRMWARE PROTOKOLÜ (KRİTİK — koddan doğrulandı)
Firmware **bare topic + düz tamsayı payload (atoi)** kullanır — ÖN EK YOK. Sabitler: `Services/FirmwareTopics.cs`.
Broker firmware'de **192.168.1.130:1883** (uygulamanın Broker Host'u buna ayarlı OLMALI).
IP'ler: broker `.130`, robot `.131`, pano `.132`.

**Sürüş — BANG-BANG + TERS İŞARET** (`robot/.../yuruyus_ileri_geri.ino`):
- `joystick/forward_backward` ≤ **−90** → İLERİ (negatif!), ≥ **+90** → GERİ (pozitif!)
- `joystick/left_right` ≤ **−80** → SAĞ, ≥ **+80** → SOL — **ve dönüş için fb=0 ŞART** (aynı anda yürü+dön YOK)
- Ara değerler (40, 60…) motoru döndürmez. `joystick/brake`=1 fren.
- Firmware v1.2.0+ : motor PWM **soft-start rampası** (kalkış akımı düşük), ileri/geri/sağ/sol.

**Kamera kafa**: `joystick/180_up_down` (tilt), `joystick/360_CW_CCW` (dönüş), eşik |değer|≥35.
**Işık**: `joystick/led` — NOT: mevcut firmware bunu kullanmıyor (ışık A2 basınca göre otomatik).
**Telemetri (robot→PC, ham ADC)**: `accl_x` (eğim), `pressure`, `voltage`. Yalnız değer ESİK kadar değişince yayınlanır.
**Pano**: `metre` yayınlar (encoder/200), `joystick/gerisarma`+`metre_sifir` dinler. Fiziksel ileri/geri butonu da `forward_backward` (−100/+100) yayınlar.
**Heartbeat (v1.2.0+)**: robot `robot/alive`, pano `pano/alive` her 1 sn. PC bunları canlılık için dinler (konsola yazmaz).
**Teşhis**: PC `robot/diag`/`pano/diag` yayınlar → cihaz `robot/log`/`pano/log`'a tam durum raporu döker.
**Sürüm**: cihaz `robot/version`/`pano/version` (retained) yayınlar.

Uygulama eşleme: `Services/MqttRobotClient.cs` (SendAsync), `RobotDriveStreamer.cs` (throttle/steer→fb/lr, 100ms stream, watchdog besler), `GamepadCommandMapper.cs` (sağ stick=sürüş, sol stick=kafa).

## 5. Telemetri + Kalibrasyon
- `Services/TelemetryService.cs` — MQTT ham int → observable. Ham (`TiltRaw`,`PressureRaw`) + kalibre (`TiltDegrees`,`PressurePercent`,`PressureFill`,`PressureBarColor`).
- `Services/Calibration.cs` — Eğim 3-nokta parçalı-lineer (0°/−45°/+45° ham ADC); Basınç min/max → %0..100; renk ≥60 yeşil / 40–60 turuncu / <40 kırmızı.
- Kalibrasyon değerleri `AppSettings` (TiltRaw0Deg, TiltRawMinus45, TiltRawPlus45, PressureRawMin, PressureRawMax). Ayarlar'da "📍 Yakala" anlık ham değeri alır. `ApplyCalibration(settings)` LiveViewModel (yükleme) + SettingsViewModel (kaydetme) tarafından çağrılır.

## 6. Video pipeline (canlı=kayıt SENKRON)
İki AYRI ffmpeg yolu:
- **Canlı önizleme**: `Services/SyncedVideoPipeline.cs` — RTSP **/102** sub-stream → ffmpeg decode (MJPEG+PTS) → her kare SkiaSharp ile overlay gömülür (`SkiaOverlayRenderer.cs`) → `SKCanvasView`. Metre `TelemetrySyncBuffer` (zaman-indeksli interpolasyon) ile kareye senkron; **metre ramp yumuşatma** (üstel ease, zıplama yok).
- **Kayıt**: `Services/FfmpegOverlayRecorder.cs` — RTSP **/101** 4K → tek-geçiş ffmpeg `drawtext` (textfile `reload=1`). Metre/saat/eğim text dosyaları 10Hz yazılır. Metre ramp burada da var.
- **Overlay içeriği**: sol-üst proje/baca/akış, sağ-üst saat (video zamanı 00:00'dan), sol-alt **metre**, sağ-alt **eğim (kalibre °)**. `SkiaOverlayRenderer` + `FfmpegOverlayRecorder.BuildFilterScript` İKİSİ de güncellenmeli.
- **Basınç barı**: video overlay'e GÖMÜLMEZ; stream'in altında UI elemanı (MainPage + ProjectChannelsPage), `Live.Telemetry.PressureFill/PressureBarColor/PressurePercent`.

## 7. Joystick (Logitech F710 / XInput) — KRONİK SORUN ve çözümü
- `Services/XInputGamepadService.cs` — XInput P/Invoke, 4 slot tarar, girdi üreten slotu kilitler. **dwPacketNumber WATCHDOG**: SUCCESS=slot bağlı demek, VERİ akıyor demek DEĞİL. Paket donmuş+nötr ise `GamepadLink.Stale`. Boştaki canlı kumanda BAĞLI kalır.
- **Kök sorun**: F710 dongle Windows'ta biriken **hayalet (CM_PROB_PHANTOM)** PnP düğümleri + sürücü **FAILED_INSTALL** → "bağlı ama veri yok". Tak/çıkar/D-X dansı geçici çözer.
- **Çözüm**: `Services/GamepadRepair.cs` — hayalet düğümleri siler + hatalı cihazı yeniden kurar (`pnputil`/Disable-Enable, **runas/UAC**). "Yeniden Bağla" gerekirse arka planda çalıştırır. Masaüstünde elle `F710_Onar.cmd` da var.
- F710 VID/PID: `046D:C21F` (X mode). Veri gelmiyorsa çoğu zaman kumanda↔dongle RF eşleşmesi ölü → fiziksel re-pair (dongle çıkar, pil çıkar, dongle tak, 15 sn içinde pil tak).

## 8. Build / çalıştır / yayınla
**Build (Windows TFM):**
```
dotnet build MyRoboticsInspector.csproj -t:Build -p:TargetFramework=net9.0-windows10.0.19041.0 -nologo -v:q
```
Hata filtresi: `... 2>&1 | Select-String ": error"`.
- **MSB3021/3026/3027 (apphost.exe kilidi) BENİGN** — uygulama açık demektir; gerçek hata `error CS`/`error XC`/`XFC`.
- İkon değişmiyorsa: uygulamayı kapat → `obj` sil → tam rebuild (Resizetizer önbelleğe alır). `appicon.svg` arka plan + `appiconfg.svg` foreground + csproj `MauiIcon Color`.

**Sürüm yayınlama (Velopack + GitHub Actions):**
1. `MyRoboticsInspector.csproj` → `ApplicationDisplayVersion` (örn 1.1.6) + `ApplicationVersion` (artır).
2. Commit (mesaj sonu: `Co-Authored-By: Claude ...`). Push.
3. `git tag vX.Y.Z` + `git push origin vX.Y.Z` → `.github/workflows/release-windows.yml` (tag `v*` tetikli, `permissions: contents: write`) Setup.exe + nupkg + RELEASES üretir.
4. İzle: `gh run watch <id> --repo ademdagcayir/MyRoboticsInspector --exit-status`.
5. Mevcut PC'ler açılışta sessiz auto-update (App.xaml.cs `TryStartupAutoUpdateAsync`) + relaunch.
- **YAYIN KURALI**: Kullanıcı açıkça "yayınla" demeden commit/tag/release YAPMA (kullanıcı tercihi). Kod yazıp build doğrula, yayını bekle.
- Çok satırlı git commit mesajı: `git commit -F <dosya>` (gömülü tırnaklar native arg parse'ı bozar).

## 9. Önemli dosyalar haritası
- `Services/FirmwareTopics.cs` — MQTT topic sabitleri (firmware ile birebir).
- `Services/MqttRobotClient.cs` — MQTT istemci + komut eşleme + trafik logu.
- `Services/TelemetryService.cs` / `Calibration.cs` — telemetri + kalibrasyon.
- `Services/SyncedVideoPipeline.cs` / `SkiaOverlayRenderer.cs` — canlı önizleme + overlay.
- `Services/FfmpegOverlayRecorder.cs` — kayıt (drawtext).
- `Services/TelemetrySyncBuffer.cs` — metre↔kare zaman senkron.
- `Services/XInputGamepadService.cs` / `GamepadCommandMapper.cs` / `RobotDriveStreamer.cs` / `GamepadRepair.cs` — joystick.
- `Services/BrokerService.cs` — yerel Mosquitto başlat/durdur.
- `Services/StoragePaths.cs` — projeler/{Proje}/{Mahalle}/{Sokak}/{egim,rapor,resim,video}.
- `Services/ChannelReportRenderer.cs` — QuestPDF "Görüntüleme Raporu" + "TSE EN 13508-2".
- `ViewModels/LiveViewModel.cs` — EN BÜYÜK VM (kamera, joystick, MQTT, konsol, telemetri overlay push).
- `ViewModels/ProjectChannelsViewModel.cs` — kanal akışı + otomatik rapor (`Live` ile LiveViewModel'e erişir).
- `Models/AppSettings.cs` — tüm ayarlar (SQLite `app_settings`, tek satır).
- `MauiProgram.cs` — DI (TelemetryService/SyncedVideoPipeline/MqttRobotClient **singleton**).

## 9b. Testler
- `Tests/MyRoboticsInspector.LogicTests` — xunit, **net9.0** (MAUI değil); saf mantık dosyaları kaynak-link ile derlenir (Calibration, StoragePaths, TelemetrySyncBuffer). Koş: `dotnet test Tests/MyRoboticsInspector.LogicTests`.
- Ana csproj `DefaultItemExcludes` içinde `Tests\**` OLMALI — yoksa MAUI, test obj dosyalarını derleyip "Yinelenen AssemblyAttribute" hatası verir.

## 10. Gotcha / dersler
- **sqlite-net OrderBy'da `??` desteklemez** → `NotSupportedException`. `OrderByDescending(p => p.LastLoginAt ?? p.CreatedAt)` çalışmaz; listeyi çekip bellekte sırala. (Login geri açılınca ortaya çıkan gizli çökme buydu.)
- **WinUI stowed exception (0xc000027b)**: async void event handler'dan kaçan exception AppDomain handler'ına DÜŞMEZ, crash.log'a yazılmaz, uygulama sessiz ölür. `Platforms/Windows/App.xaml.cs` içinde `this.UnhandledException` logger'ı var — çökme teşhisinde İLK oraya bak.
- Duman testi: `Start-Process` ile exe'yi başlat, 15 sn bekle, süreç ayakta mı + crash.log değişti mi kontrol et.
- ffmpeg `drawtext reload=1`: textfile'ı **rename ile değiştirme** (Windows'ta okuma anında −13 Permission denied → ffmpeg ölür). `WriteShared` ile yerinde paylaşımlı yaz.
- Konsol/loglar: yüksek hızlı akışta **CollectionView titrer**. Çözüm: tek-metin terminal (tampon + ~200ms toplu Text güncelleme), CollectionView değil.
- Kamera kimlik bilgileri (admin / IP 192.168.1.64) — logda/raporda AÇMA.
- MAUI Color: `Microsoft.Maui.Graphics.Color/Colors`.
- Yeniden görüntü alınca eski video+foto+rapor silinmeli (kanal yeniden kaydında).
