# MyRoboticsInspector — Saha Pilot Test Planı

**Sürüm:** 1.0 · **Tarih:** 22.05.2026 · **Hedef:** İlk gerçek robot + Hikvision kamera entegrasyonundan önce uygulamanın tüm uçlarını doğrulamak.

Bu plan **sıralı** çalıştırılmak için tasarlandı: bir bölüm önceki bölümlerin "Geçti" işaretlenmesini varsayar. Bir adım kaldıysa, **alttaki adımlara geçmeden hatayı çöz** veya not düş — çünkü sonraki bölümler kırılmış olabilir.

## Saha çıkışı öncesi — kontrol listesi

- [ ] **Windows PC** (Win 10 1809+ / Win 11) hazır, internet ya da en az 4G hotspot var
- [ ] **.NET 9 SDK** + **MAUI workload** yüklü (`dotnet workload list` çıktısında `maui-windows`)
- [ ] **FFmpeg 8.x** PATH'te (`where ffmpeg` → bulundu) — overlay burn-in için
- [ ] **Mosquitto 2.x** kurulu (`"C:\Program Files\mosquitto\mosquitto.exe" --help` çalışıyor)
- [ ] **Tools/start-broker.cmd** çalıştırılabilir — broker başlıyor, port 1883'te dinliyor
- [ ] **Tools/start-simulator.cmd** çalıştırılabilir — simulator broker'a bağlanıyor, telemetri publish ediyor
- [ ] **Hikvision kamera** IP'si biliniyor + admin şifresi elde var
- [ ] **Robot kartı IP'si veya broker IP'si** biliniyor
- [ ] **OneDrive masaüstü istemcisi** yüklü ve oturum açık (yedekleme testi için)
- [ ] Test sahada en az **1 müşteri** tanımlamak için bilgiler hazır (ad, telefon, adres)

---

## A — İlk açılış & profil sistemi

### A1 · İlk açılışta profil yoksa otomatik form
- **Hazırlık:** SQLite DB'yi sıfırla (`%LocalAppData%\Packages\...\LocalState\myroboticsinspector.db3` sil) veya yeni cihazda dene.
- **Adım:** Uygulamayı başlat.
- **Beklenen:** Login sayfası **doğrudan "Yeni Profil" formunda** açılır. "İptal" butonu görünmez.
- **Sonuç:** ☐ Geçti ☐ Kaldı · Not: ____________

### A2 · Profil oluştur (PIN'siz)
- **Adım:** Ad: "Adem Dagcayir", E-posta: boş, PIN: boş. "Oluştur" tıkla.
- **Beklenen:** AppShell açılır, ana sayfa görünür. Login sayfası kapanır.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### A3 · Profil oluştur (PIN'li)
- **Hazırlık:** İlgili önceki testlerde önceden DB temizle.
- **Adım:** PIN: "1234" girip oluştur. Çıkış yap (Settings'ten). Login sayfasında profili seç, yanlış PIN gir.
- **Beklenen:** "PIN hatalı" mesajı. Doğru PIN ile giriş başarılı.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### A4 · PIN doğrulama kuralları
- **Adım:** Yeni profil, PIN'e "12" (2 basamak) ya da "abc" gir → Oluştur.
- **Beklenen:** "PIN 4-6 rakamdan oluşmalı veya boş bırakılmalı" mesajı.
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## B — Settings konfigürasyonu

### B1 · Settings ekranına git, varsayılanları gör
- **Adım:** Flyout → "Ayarlar"
- **Beklenen:** RTSP URL = `rtsp://admin:password@192.168.1.64:554/Streaming/Channels/101` (Hikvision varsayılan), BrokerHost = `192.168.1.10`, BrokerPort = 1883, TopicPrefix = `myrobotics`, RobotId = `robot1`, FfmpegPath = `ffmpeg.exe`, BurnOverlayInRecording = true.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### B2 · Proje + konum bilgisi gir
- **Adım:** ProjectName = "Atakent Yağmursuyu", Neighborhood = "Atakent", Street = "234. Sokak", OperatorName = (profil adı), CompanyName = "My Robotics". Kaydet.
- **Beklenen:** "Ayarlar kaydedildi" mesajı.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### B3 · OneDrive otomatik tespit (Windows)
- **Adım:** Yedekleme bölümünde "OneDrive klasörüne yedekle" butonu görülüyor.
- **Beklenen:** OneDrive yolu otomatik tespit edildi mi (ör. `C:\Users\<user>\OneDrive\MyRoboticsInspector\`). "OneDrive aktif" durum etiketi.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### B4 · OneDrive yönlendirmesi
- **Adım:** "OneDrive klasörüne yedekle" tıkla, StoragePath alanını gözle.
- **Beklenen:** StoragePath = `<OneDrive>\MyRoboticsInspector` olarak değişir. Kaydet.
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## C — MQTT broker bağlantı doğrulama

### C1 · Broker'sız test
- **Hazırlık:** Mosquitto kapalı.
- **Adım:** Settings → "Bağlantıyı Test Et" tıkla.
- **Beklenen:** 3 sn sonra `✗ Bağlantı zaman aşımı (192.168.1.10:1883)` veya `✗ Connection refused` (host'a göre).
- **Sonuç:** ☐ Geçti ☐ Kaldı

### C2 · Broker var ama yayıncı yok
- **Hazırlık:** `Tools/start-broker.cmd` başlat. Simulator KAPALI. Settings'te BrokerHost = `localhost`.
- **Adım:** "Bağlantıyı Test Et" tıkla.
- **Beklenen:** `⚠ Bağlandı, ama telemetri yok. Robot/simulator çalışıyor mu? Topic: myrobotics/robot1/telemetry`
- **Sonuç:** ☐ Geçti ☐ Kaldı

### C3 · Tam pipeline
- **Hazırlık:** Broker + `Tools/start-simulator.cmd` çalışıyor.
- **Adım:** "Bağlantıyı Test Et" tıkla.
- **Beklenen:** `✓ Bağlandı, 2 sn'de N telemetri mesajı. Örnek: {"distanceMeters":0,...}` (N ≥ 3).
- **Sonuç:** ☐ Geçti ☐ Kaldı

### C4 · Canlı sayfadan bağlan
- **Adım:** Canlı sayfa → "Bağlan" tıkla (Robot bölümü).
- **Beklenen:** Status "Broker'a bağlandı", durum etiketi `● Bağlı • Stop`.
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## D — Canlı RTSP video

### D1 · Geçerli RTSP
- **Hazırlık:** Gerçek Hikvision kamera ağda. Settings'te RTSP URL doğru girilmiş.
- **Adım:** Canlı sayfa → "Başlat" (yayın).
- **Beklenen:** 1-3 sn içinde kamera görüntüsü gelir. Overlay XAML katmanı görünür (proje/mahalle/sokak sol-üst, tarih/saat sağ-üst). Mesafe 0 m (telemetri Stop'ta).
- **Sonuç:** ☐ Geçti ☐ Kaldı

### D2 · RTSP düşmesi (kabloyu çek)
- **Hazırlık:** Yayın açık.
- **Adım:** Kamera ethernet kablosunu fiziksel olarak çek (veya kamerayı kapat).
- **Beklenen:** Video donar, sonunda boş kare. Uygulama çökmez.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### D3 · Yanlış RTSP URL
- **Adım:** Settings'te URL'i `rtsp://localhost:1/x` yap. Kaydet. Canlı'da "Başlat".
- **Beklenen:** Birkaç saniye sonra Status: "Video başlatılamadı: ..." veya boş ekran kalır.
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## E — Robot kontrolü

### E1 · DPad basılı tut + bırak
- **Hazırlık:** Broker + simulator çalışıyor. Canlı sayfada "Bağlan" yapılmış.
- **Adım:** ▲ butonuna **basılı tut** 2 saniye → bırak.
- **Beklenen:** Simulator konsolunda `CMD <- MoveForward` ve `CMD <- Stop` mesajları. Status etiketi `● Bağlı • Forward` → `● Bağlı • Stop`. Mesafe artar (örn. 0 → 0.3 m), sonra durur.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### E2 · Pointer kaçışı = Stop (mouse)
- **Adım:** ▲'a fareyle bas, basılı tutarken **fareyi butondan dışarı kaydır**.
- **Beklenen:** `PointerExited` → Stop yayınlanır. Mesafe durur.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### E3 · Tüm yönler
- **Adım:** Sırayla ▲ ◀ ▶ ▼ basılı tut.
- **Beklenen:** Simulator log'da Forward, TurnLeft, TurnRight, Backward görünür; her birinden sonra Stop.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### E4 · Acil durdurma
- **Hazırlık:** ▲'a basılı tutuyorken **acil durum simülasyonu**: parmağını ekranda bırak, başka eliyle E-STOP butonuna bas.
- **Adım:** Üstteki büyük kırmızı "■ ACİL DURDURMA"ya tıkla.
- **Beklenen:** Status: `■ ACİL DURDURMA gönderildi`. Simulator log'da Stop. ActiveMove → Stop.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### E5 · Işık handshake
- **Adım:** "💡 Işık Aç" tıkla.
- **Beklenen:** Buton anında "Işık ON gönderildi, onay bekleniyor..." Status'u. ActivityIndicator dönüyor. ~0.5 sn sonra `lightOn:true` telemetrisi gelir → buton "💡 Işık Kapat"a döner, Status "Işık ON onaylandı".
- **Sonuç:** ☐ Geçti ☐ Kaldı

### E6 · Işık handshake timeout (simulator durdurulmuşken)
- **Hazırlık:** Simulator'ü kapat. Broker hâlâ açık. Bağlantı kesilmemiş.
- **Adım:** "💡 Işık Aç" tıkla.
- **Beklenen:** Komut gönderilir ama ack gelmez. 3 sn sonra Status: "Işık komutu onaylanmadı (timeout)". Spinner durur.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### E7 · LWT (Last Will & Testament)
- **Hazırlık:** Uygulama broker'a bağlı, ▲'a basılı tut.
- **Adım:** Uygulamayı **zorla kapat** (Task Manager → End Task).
- **Beklenen:** `mosquitto_sub -t myrobotics/robot1/cmd` ile dinleyen biri ~10-15 sn içinde `{"cmd":"Stop","reason":"pc_disconnected"}` görür (broker LWT'yi yayınladı).
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## F — İnceleme yaşam döngüsü

### F1 · Müşteri tanımla
- **Adım:** Flyout → "Müşteriler" → "+ Yeni" → ad/telefon/adres gir → Kaydet.
- **Beklenen:** Listede müşteri görünür.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### F2 · İnceleme başlat (müşterisiz hata yolu)
- **Adım:** Canlı sayfada müşteri **seçmeden** "▶ İncelemeyi Başlat" butonu durumunu gözle.
- **Beklenen:** Buton **disabled** (CanStartInspection false).
- **Sonuç:** ☐ Geçti ☐ Kaldı

### F3 · İnceleme başlat
- **Adım:** Müşteri seç → "▶ İncelemeyi Başlat".
- **Beklenen:** Buton "■ İncelemeyi Bitir"e dönüşür. "⚠ Kusur Ekle (0)" görünür. Defect mini-listesi paneli açılır (boş). Overlay'de "🔍 İNCELEME AKTİF" görünür.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### F4 · İnceleme aktifken müşteri picker disabled
- **Adım:** İnceleme aktifken Müşteri picker'a tıkla.
- **Beklenen:** Picker disabled, değiştirilemez.
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## G — Kayıt + overlay burn-in (FFmpeg)

### G1 · Kayıt başlat (FFmpeg)
- **Hazırlık:** Yayın açık, inceleme aktif, FFmpeg PATH'te.
- **Adım:** "● Kayda Başla" tıkla.
- **Beklenen:** Status: "Kayıt (overlay gömülü): kayit_YYYYMMDD_HHMMSS.mp4". Overlay'de "● REC" kırmızı görünür. FFmpeg.exe Task Manager'da koşuyor.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### G2 · Kayıt dosyası inceleme klasöründe
- **Adım:** Windows Explorer'da `<StoragePath>\inspections\<inspectionId>\` klasörünü aç.
- **Beklenen:** `kayit_*.mp4` ve gizli `.overlay_*` klasörü var. `.overlay_*` içinde `tl.txt`, `tr.txt`, `bl.txt`, `br.txt` mevcut, her saniye güncelleniyor.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### G3 · Kaydı durdur ve oynatma kontrolü
- **Adım:** "■ Kaydı Durdur".
- **Beklenen:** Status: "Kayıt durduruldu". FFmpeg.exe kapanır. MP4 dosyası bir media player ile oynatılabilir (VLC/Windows Media). **Overlay metinleri videoya gömülü** (proje/mahalle/sokak sol-üst, mesafe sol-alt vs).
- **Sonuç:** ☐ Geçti ☐ Kaldı

### G4 · FFmpeg yokken fallback
- **Hazırlık:** Settings'te FfmpegPath = `nope.exe` yap. Kaydet.
- **Adım:** Kayıt başlat.
- **Beklenen:** Status: "FFmpeg bulunamadı, overlay'siz LibVLC kaydı kullanılıyor". Kayıt yine başlar (ama overlay gömülmez). Settings'i geri al.
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## H — Kusur işaretleme

### H1 · Kusur ekle (snapshot otomatik)
- **Hazırlık:** İnceleme aktif, yayın açık. Telemetri akıyor (örn. mesafe 12.5m).
- **Adım:** "⚠ Kusur Ekle" tıkla.
- **Beklenen:** Modal açılır. Snapshot otomatik çekilir, modalda önizleme görünür. Mesafe alanı **12.5** dolu (telemetriden). Video Zamanı (ms) alanı **read-only**, MediaPlayer.Time değeri dolu.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### H2 · Kusur kaydet
- **Adım:** Şiddet: "High", Tür: "Çatlak", Açıklama: "Boyuna 40cm çatlak". Kaydet.
- **Beklenen:** Modal kapanır. Sağ panelde defect mini-listesinde yeni kart en üstte. "⚠ Kusur Ekle (1)" sayacı. Status: "Kusur eklendi (1)".
- **Sonuç:** ☐ Geçti ☐ Kaldı

### H3 · Kusur önizleme
- **Adım:** Mini listede karta tıkla.
- **Beklenen:** Büyük önizleme overlay'i açılır. Snapshot büyük, şiddet/mesafe/video zamanı + tür + açıklama görünür. Sil/Kapat butonları.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### H4 · Kusuru sil
- **Adım:** Önizlemede "🗑 Sil" veya mini liste satırında 🗑 tıkla.
- **Beklenen:** Mini listeden kalkar. Sayaç azalır. Status: "Kusur silindi".
- **Sonuç:** ☐ Geçti ☐ Kaldı

### H5 · Kusur iptal (snapshot temizliği)
- **Adım:** "⚠ Kusur Ekle" → modal açılır → "İptal".
- **Beklenen:** Modal kapanır. Snapshot dosyası **silinmiş** olmalı (Explorer'da `.../defects/` kontrol et). Pano kirlenmiş kalmasın.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### H6 · 3+ kusur ekle
- **Adım:** En az 3 farklı kusur ekle (farklı şiddet/tür/mesafe).
- **Beklenen:** Hepsi mini listede görünür. Newest-first sıra.
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## I — İnceleme bitirme + drill-down

### I1 · İnceleme bitir
- **Adım:** "■ İncelemeyi Bitir" tıkla.
- **Beklenen:** Status: "İnceleme tamamlandı (N kusur)". Mini liste paneli gizlenir. Kayıt da otomatik durdu (FFmpeg kapandı).
- **Sonuç:** ☐ Geçti ☐ Kaldı

### I2 · İncelemeler listesi
- **Adım:** Flyout → "İncelemeler".
- **Beklenen:** Az önce bitirilen inceleme listede en üstte. Tarih, mesafe (telemetriden snapshot edilmiş son değer).
- **Sonuç:** ☐ Geçti ☐ Kaldı

### I3 · Detaya git
- **Adım:** Karta tıkla veya "Detay" butonu.
- **Beklenen:** InspectionDetailPage açılır. Müşteri/iş bilgileri, kusur listesi (foto thumbnail'lı), "📄 PDF Rapor Üret" + "🎬 Videoyu İzle" butonları.
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## J — Kayıt review modu (scrubber + markers)

### J1 · Videoyu izle
- **Adım:** Detay sayfasında "🎬 Videoyu İzle" tıkla.
- **Beklenen:** Review sayfası açılır. MP4 oynamaya başlar. Video uzunluğu öğrenilince scrubber'ın üstünde **defect tick marker'lar** renkli olarak (Critical kırmızı, High pembe, vs) görünür.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### J2 · Scrubber sürükle
- **Adım:** Scrubber'ı orta noktaya sürükle.
- **Beklenen:** Video o zamana atlar. CurrentTimeDisplay güncellenir.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### J3 · Defect marker'a tıkla (timeline)
- **Adım:** Bir defect marker'ına (renkli dikey çizgi) tıkla.
- **Beklenen:** Video o kusurun zamanına atlar. Sağdaki kusur listesinde aynı kusur seçili olur.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### J4 · Defect listeden seek
- **Adım:** Sağ paneldeki kusurlardan birine tıkla.
- **Beklenen:** Video o zamana atlar.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### J5 · Play/Pause/5sn skip
- **Adım:** ⏸/▶ butonu, ⏪ 5s ve 5s ⏩ butonları test et.
- **Beklenen:** Beklendiği gibi davranır.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### J6 · Videoyu olmayan inceleme
- **Hazırlık:** Hiç kayıt yapılmamış bir inceleme yarat (kayıt başlatmadan bitir).
- **Adım:** Detay → "Videoyu İzle".
- **Beklenen:** "(Kayıt bulunamadı)" mesajı. Hata yok.
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## K — PDF rapor

### K1 · PDF üret
- **Adım:** Detay → "📄 PDF Rapor Üret".
- **Beklenen:** PDF üretilir. Varsayılan PDF görüntüleyici (Edge / Adobe) açılır. Sayfa 1: müşteri/iş kartları + bulgu tablosu. Sayfa 2+: kusur kartları **fotoğraflarla**.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### K2 · Rapor dosyası nerede
- **Adım:** `<StoragePath>\reports\` aç.
- **Beklenen:** `Inceleme_<id>_YYYYMMDD_HHmmss.pdf`.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### K3 · Türkçe karakter
- **Adım:** PDF'i aç ve "Mahalle" / "Şiddet" / "Çatlak" / "Açıklama" gibi Türkçe karakterleri kontrol et.
- **Beklenen:** Doğru renderleniyor (ş, ç, ı, ğ, ö, ü).
- **Sonuç:** ☐ Geçti ☐ Kaldı

### K4 · Fotoğraf eksik kusur
- **Adım:** Snapshot dosyası elle silinmiş bir kusur olan bir incelemeye PDF yap.
- **Beklenen:** "(görsel okunamadı)" veya "(görsel yok)" placeholder. Crash yok.
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## L — Yedekleme (OneDrive)

### L1 · OneDrive klasörüne yazma
- **Hazırlık:** Settings'te "OneDrive klasörüne yedekle" tıklı olsun, StoragePath = `<OneDrive>\MyRoboticsInspector`.
- **Adım:** Yeni inceleme yap, kayıt + kusur ekle, bitir.
- **Beklenen:** `<OneDrive>\MyRoboticsInspector\inspections\<id>\` klasörü oluşur, içinde `kayit_*.mp4` ve `defects\snapshot_*.png`.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L2 · OneDrive senkron
- **Adım:** Explorer'da inceleme klasörüne sağ tık → "OneDrive durumunu görüntüle" (veya OneDrive ikonuna bak).
- **Beklenen:** Yeşil tik (✓) → buluta yüklendi. Mavi daireler aktif yükleniyor, kırmızı X = sorun var.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L3 · İnceleme detayda yedek rozeti
- **Adım:** Detaya git.
- **Beklenen:** "Yedek: ✓ OneDrive" rozeti (veya benzer durum etiketi).
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L4 · OneDrive offline
- **Hazırlık:** OneDrive istemcisini durdur (sistem tepsisinden Çıkış).
- **Adım:** Yeni kayıt + kusur yap.
- **Beklenen:** Uygulama yazmaya devam eder (yerel disk), ama Explorer'da dosyalar mavi yükleme ikonuyla kalır. OneDrive başlayınca senkron edilir.
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## L2 — Joystick (Logitech F710)

### L2.1 · Gamepad bağlanmadan başlat
- **Hazırlık:** F710 dongle bağlı değil. Settings → "Joystick otomatik aç" ON.
- **Adım:** Uygulama başlat → canlı sayfa.
- **Beklenen:** Status: "Joystick girdisi açıldı..." mesajı. Joystick rozeti **"🎮 Joystick: Aktif (bekleniyor)"**. Crash yok.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L2.2 · Dongle bağla, F710 XInput modunda
- **Hazırlık:** Önceki test devam ediyor (joystick aktif, beklemede).
- **Adım:** F710 dongle'ı USB'ye tak, gamepad'in **arka anahtarı 'X' konumunda**, üzerindeki LED yanıyor.
- **Beklenen:** ~1 sn içinde rozet **"🎮 Joystick: Aktif"** olur. Konsolda/log'da bağlantı olayı.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L2.3 · Sol stick sürüş — ileri, geri, sola, sağa
- **Hazırlık:** Broker + simulator çalışıyor, app broker'a bağlı, joystick aktif.
- **Adım:** Sol stick'i sırayla yukarı, aşağı, sola, sağa it. Her seferinde 1-2 saniye tut, sonra merkeze bırak.
- **Beklenen:** Simulator log'unda `MoveForward 0.6`, `MoveBackward 0.6`, `TurnLeft 0.6`, `TurnRight 0.6` ve aralarında `Stop` mesajları. activeMove telemetrisi her hareket sırasında değişir.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L2.4 · LT yavaş mod
- **Adım:** LT'yi basılı tutarken sol stick'i tam yukarı it.
- **Beklenen:** `MoveForward 0.3` (LT çarpanı 0.3x). Simulator'de mesafe artışı yavaş.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L2.5 · RT tam hız
- **Adım:** RT'yi basılı tutarken sol stick'i tam yukarı it.
- **Beklenen:** `MoveForward 1.0` (RT çarpanı 1.0x).
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L2.6 · B = Acil durdurma
- **Hazırlık:** Robot Forward'da ilerliyor.
- **Adım:** B butonuna bas.
- **Beklenen:** Anında Stop publish edilir. Status: "■ ACİL DURDURMA gönderildi". activeMove → Stop.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L2.7 · X = Işık aç/kapa
- **Adım:** X butonuna bas.
- **Beklenen:** LightOn/Off handshake (telemetry.lightOn değişir). UI'da ışık butonu state'i güncellenir.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L2.8 · A = Snapshot
- **Hazırlık:** Yayın açık.
- **Adım:** A butonuna bas.
- **Beklenen:** Snapshot çekilir, Status: "Görüntü kaydedildi: snapshot_*.png".
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L2.9 · Y = Kusur ekle modal
- **Hazırlık:** İnceleme aktif, yayın açık.
- **Adım:** Y butonuna bas.
- **Beklenen:** Kusur ekleme modal'ı açılır (DPad UI'daki butona basmışsın gibi).
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L2.10 · Start = Kayıt
- **Adım:** Start butonuna bas.
- **Beklenen:** Kayıt başlar/durur (FFmpeg overlay'lı). Overlay'de "● REC" görünür.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L2.11 · Back = İnceleme başlat/bitir
- **Hazırlık:** Müşteri seçili.
- **Adım:** Back butonuna bas.
- **Beklenen:** İnceleme başlar. Tekrar bas → biter.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L2.12 · Dongle'ı çek (sıcak kopma)
- **Adım:** Joystick aktifken USB dongle'ı çek.
- **Beklenen:** Birkaç saniye içinde rozet **"🎮 Joystick: Aktif (bekleniyor)"**. App crash yok. Tekrar tak → tekrar "Aktif".
- **Sonuç:** ☐ Geçti ☐ Kaldı

### L2.13 · DirectInput modu hatası
- **Hazırlık:** F710 arka anahtarı 'D' (DirectInput) konumuna al.
- **Beklenen:** XInput görmez. Rozet "Aktif (bekleniyor)" kalır. Kullanıcı 'X' moduna alınca tanır.
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## M — Negatif senaryolar (robustness)

### M1 · Broker çökmesi
- **Hazırlık:** Broker çalışıyor, app bağlı, yayın açık.
- **Adım:** Broker'ı kapat (Task Manager).
- **Beklenen:** Status etiketi `○ Bağlı değil`'e döner. Video etkilenmez. Yeniden bağlanmak için "Bağlan" tıklamak yeterli.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### M2 · Disk dolu
- **Adım:** Test imkanın varsa hedef disk neredeyse doluyken kayıt başlat.
- **Beklenen:** FFmpeg dosyaya yazamaz, exit code !=0; Status: "FFmpeg çıktı kodu N: ..." mesajı.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### M3 · Uygulama beklenmedik kapanma
- **Hazırlık:** Aktif inceleme + kayıt sırasında.
- **Adım:** Task Manager → End Task.
- **Beklenen:** MP4 dosyası hatalı footer'la kalabilir (FFmpeg graceful shutdown yapamadı). Uygulama tekrar açıldığında inceleme listesinde mevcut, VideoPath set, ama Review modunda son saniyeler eksik olabilir.
- **Sonuç:** ☐ Geçti ☐ Kaldı

### M4 · Aynı anda iki RTSP istemcisi
- **Adım:** Uygulamada yayın açık. Aynı zamanda VLC ile aynı RTSP URL'ini aç.
- **Beklenen:** Hikvision genelde birden fazla istemciye servis verir. İki istemci de görüntü alır. (Kamera modeline göre limit olabilir).
- **Sonuç:** ☐ Geçti ☐ Kaldı

---

## N — Performans gözlemleri

Şu metrikleri kaydet (her test koşusu için):

| Metrik | Beklenen | Ölçülen |
|---|---|---|
| RTSP video başlama gecikmesi | < 3 sn | ____ sn |
| MQTT bağlantı süresi | < 1 sn | ____ sn |
| Telemetri overlay update gecikmesi | < 1 sn (görsel) | ____ sn |
| FFmpeg CPU kullanımı (kayıt sırasında) | < %15 (1080p H.264 veryfast) | ____ % |
| 30 dk inceleme MP4 boyutu | ~250-400 MB | ____ MB |
| Snapshot dosya boyutu | < 500 KB | ____ KB |
| PDF rapor boyutu (10 kusurlu) | < 5 MB | ____ MB |

---

## O — Bug raporu şablonu

Her geçmeyen test için bu formatla rapor:

```
Test ID: ____
Adım: ____
Beklenen: ____
Gerçekleşen: ____
Reproduce: 1. ___  2. ___  3. ___
Sıklık: Her zaman / Bazen / Bir kez
Önem: Kritik / Yüksek / Orta / Düşük
Loglar / ekran görüntüsü: ____
Notlar: ____
```

---

## Test öncesi son hatırlatma

- **Yedek al**: SQLite DB dosyasını test öncesi yedekle. Bozulursa geri yüklersin.
- **Tarih/saat doğru**: Cihazda tarih yanlışsa overlay'de yanlış tarih damgalanır, kayıtlar karışır.
- **Disk alanı**: 1 saat 1080p kayıt ~500MB. Test süresi × 500MB hesap et.
- **Log topla**: `%LocalAppData%\Packages\<app>\LocalCache\Logs\` (eğer varsa) ya da Visual Studio Output penceresi.

Bu plan tamamlandığında uygulama saha kullanımına hazır demektir. Geçmeyen testler [docs/MQTT_PROTOKOL.md](MQTT_PROTOKOL.md) güncellemeleri gerektiriyor olabilir — protokol versiyonunu da yükselt.
