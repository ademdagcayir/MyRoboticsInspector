# MyRoboticsInspector

**My Robotics** kanal görüntüleme robotu için modern Windows + Android yazılımı.

> Deneyimsoft Kanal Takip Sistemi'nin yerine geçen, iOS/macOS estetikli, tam özellikli yerli çözüm.

---

## Özellikler

| Kategori | Detay |
|----------|-------|
| 📹 Canlı Video | RTSP/HTTP akışı (Hikvision + marka-bağımsız), FFmpeg overlay burn-in |
| 🤖 Robot Kontrolü | MQTT (Mosquitto broker), F710 gamepad + klavye/DPad, E-STOP |
| 📋 İnceleme | EN 13508-2:2003+A1:2011 Türkçe kanal muayene standartları |
| 📸 Kusur İşaretleme | İSKİ kodları, anlık snapshot, metre + zaman damgası |
| 📄 PDF Rapor | QuestPDF — foto grid, kusur listesi, müşteri + proje bilgileri |
| 🗄️ Veri | SQLite — müşteri, iş/proje, kanal, kusur kayıtları |
| ☁️ Yedekleme | OneDrive otomatik senkron (Windows) |
| 🔄 Otomatik Güncelleme | Velopack — per-user install, delta güncelleme |

---

## Kurulum (kullanıcı)

1. [Releases](../../releases/latest) sayfasından **Setup.exe** indir
2. Çalıştır → `%LocalAppData%\MyRoboticsInspector\` altına kurulur
3. UAC/admin gerekmiyor; başlat menüsünde kısayol oluşur
4. Sonraki güncellemeler Ayarlar → "Şimdi Kontrol Et" ile tek tıkla gelir

---

## Geliştirme

### Gereksinimler

- Visual Studio 2022 17.8+ veya Rider 2024+
- .NET 9 SDK
- MAUI workload: `dotnet workload install maui-windows`
- FFmpeg 8.x (`ffmpeg.exe` PATH'te veya Settings'te yol belirt)
- Mosquitto MQTT broker (lokal test için `Tools\mosquitto-dev.conf`)

### Derleme

```bash
# Windows (geliştirme)
dotnet build -f net9.0-windows10.0.19041.0

# Android
dotnet build -f net9.0-android

# Mac Catalyst
dotnet build -f net9.0-maccatalyst
```

### Robot Simülatörü

MQTT broker olmadan test için:

```bash
cd Tools\RobotSimulator
dotnet run
```

Simülatör telemetri gönderir, komutlara yanıt verir ve 90 saniyelik döngüde gaz/su alarmları üretir.

---

## Yayın (Windows .exe)

```cmd
cd Tools
publish-windows.cmd 1.0.1
```

`Releases\` klasöründeki tüm dosyaları sunucuya yükle:
```
https://myrobotics.com.tr/updates/inspector/
```

Detaylar: [`docs/RELEASE.md`](docs/RELEASE.md)

---

## Stack

- **.NET 9 MAUI** — Windows (birincil), Android, Mac Catalyst, iOS
- **LibVLCSharp.MAUI 3.9.7.1** — RTSP video akışı + kayıt
- **MQTTnet 5.1.0** — robot MQTT haberleşmesi
- **FFmpeg 8.x** — video overlay burn-in (metre/süre/OSD)
- **QuestPDF 2026.5** — PDF rapor
- **sqlite-net-pcl** — yerel veri
- **CommunityToolkit.Mvvm** — MVVM altyapısı
- **Velopack** — otomatik güncelleme

---

## Dokümanlar

| Dosya | İçerik |
|-------|--------|
| [`docs/MQTT_PROTOKOL.md`](docs/MQTT_PROTOKOL.md) | Robot firmware MQTT kontratı (v1.0) |
| [`docs/SAHA_TEST_PLANI.md`](docs/SAHA_TEST_PLANI.md) | Saha QA checklist (60+ adım) |
| [`docs/RELEASE.md`](docs/RELEASE.md) | Sürüm yayınlama rehberi |

---

## Lisans

Tescilli yazılım — My Robotics © 2026. Tüm hakları saklıdır.
