# MyRoboticsInspector ↔ Robot Kartı MQTT Protokolü

**Versiyon:** v1.1 · **Tarih:** 06.06.2026 · **Hedef kitle:** Robot kartı firmware geliştiricileri

> **v1.1 değişikliği:** Komut modeli "press-and-hold (bir kez gönder)" → **"streaming + watchdog"** olarak güncellendi. Robot firmware'i artık bir hareket-watchdog'u uygulamalı (§3.4). Telemetri, status, LWT şemaları değişmedi.

Bu doküman, kanal görüntüleme robotu ile PC yazılımı (MyRoboticsInspector) arasındaki MQTT haberleşmesinin kontratını tanımlar. Versiyon değişiklikleri **geriye dönük uyumluluğu kırmadan** alan ekleyebilir; mevcut alan adlarını veya semantiğini değiştirmek major versiyon artışı gerektirir.

---

## 1. Genel mimari

```
                    ┌───────────────────────┐
                    │   MQTT Broker         │
                    │   (Mosquitto 2.x)     │
                    │   broker_ip:1883      │
                    └─────────┬─────────────┘
                              │
              ┌───────────────┼────────────────┐
              │               │                │
   ┌──────────▼──────┐  ┌─────▼──────────┐  ┌──▼─────────────┐
   │ PC (Inspector)  │  │ Robot kartı    │  │ İsteğe bağlı:  │
   │ – komut publish │  │ – komut sub    │  │ kayıt/loglama  │
   │ – telemetry sub │  │ – telemetry pb │  │ – mqtt_sub vb. │
   │ – status sub    │  │ – status pb    │  │                │
   └─────────────────┘  └────────────────┘  └────────────────┘
```

**Broker:**
- TCP 1883 (TLS yok — kanal sahasında saha şebekesi izole varsayılır; gelecekte 8883 + TLS).
- Anonim auth (development) → username/password (prod).
- Tek robot için **tek broker** önerilir (kanalda izole ağ). Birden fazla robot olursa `robotId` ayırt eder.

**Roller:**
- **PC** = Subscriber: telemetry + status. Publisher: cmd.
- **Robot** = Subscriber: cmd. Publisher: telemetry + status.

---

## 2. Topic ağacı

Tüm topic'ler bir **prefix** ve **robotId** ile başlar — bu sayede aynı broker'da birden fazla robot izole edilebilir.

| Topic | Yön | QoS | Retain |
|---|---|---|---|
| `{prefix}/{robotId}/cmd` | PC → Robot | 1 | No |
| `{prefix}/{robotId}/telemetry` | Robot → PC | 0 | No |
| `{prefix}/{robotId}/status` | Robot → PC | 1 | **Yes** |
| `{prefix}/{robotId}/ack` *(opsiyonel)* | Robot → PC | 1 | No |
| `{prefix}/{robotId}/event` *(opsiyonel)* | Robot → PC | 1 | No |

**Varsayılan:** `prefix=myrobotics`, `robotId=robot1` → `myrobotics/robot1/cmd` vs.

**Not:** Üst seviye `prefix` kullanımı dilerseniz değiştirilebilir (örn. `myrobotics-test` development için, `myrobotics-prod` saha için).

---

## 3. Komutlar (PC → Robot)

### 3.1 Genel payload yapısı

Tüm komutlar `cmd` adı verilen tek bir topic'e gönderilir; ayrımı **JSON payload içindeki `cmd` alanı** yapar.

```json
{
  "cmd": "<CommandType>",
  "value": <opsiyonel sayı>,
  "payload": "<opsiyonel string>",
  "ts": <unix epoch ms — opsiyonel ama gönderilir>
}
```

- `cmd` (string, zorunlu) — komut tipi (aşağıdaki tablo)
- `value` (number, opsiyonel) — komuta göre yorumlanır
- `payload` (string, opsiyonel) — özel data (örn. custom command için)
- `ts` (number, opsiyonel) — PC'nin yayın anındaki UTC unix epoch (ms). Robot bunu kullanarak gecikme ölçebilir; firmware uygulamak isterse `now() - ts > 500ms` ise komutu reddedebilir (stale komut).

### 3.2 Komut türleri

| `cmd` | `value` semantiği | Robot davranışı |
|---|---|---|
| `Stop` | yok | Tüm motorları derhal durdur. **Acil durum durumunda da bu komut kullanılır.** |
| `MoveForward` | hız: 0.0 - 1.0 | Robot ileri sür (value: 1.0 = maks hız) |
| `MoveBackward` | hız: 0.0 - 1.0 | Robot geri sür |
| `TurnLeft` | hız: 0.0 - 1.0 | Sola dönüş (sabit pozisyonda veya yarıçap, firmware'e göre) |
| `TurnRight` | hız: 0.0 - 1.0 | Sağa dönüş |
| `LightOn` | yok | LED ışığı aç |
| `LightOff` | yok | LED ışığı kapat |
| `LightBrightness` | parlaklık: 0 - 100 | Parlaklığı set et (varsa) |
| `CameraPan` | açı (-180..+180 derece) | Pan motor varsa hareket et |
| `CameraTilt` | açı (-90..+90 derece) | Tilt motor varsa hareket et |
| `CameraZoom` | zoom: 1.0 - N | Optik/dijital zoom (varsa) |
| `Custom` | yok | `payload` alanı serbest format, üreticiye özel |

### 3.3 Komut örnekleri

**İleri 50% hızla:**
```json
{ "cmd": "MoveForward", "value": 0.5, "ts": 1716397245123 }
```

**Acil durdurma:**
```json
{ "cmd": "Stop", "ts": 1716397246000 }
```

**Işık aç:**
```json
{ "cmd": "LightOn", "ts": 1716397250000 }
```

**Sola 45° pan:**
```json
{ "cmd": "CameraPan", "value": -45.0 }
```

### 3.4 Komut yaşam döngüsü — streaming + watchdog (v1.1)

> **⚠️ v1.0 → v1.1 davranış değişikliği:** Eski "press-and-hold, bir kez gönder" modeli **kaldırıldı**. Artık PC, aktif hareket boyunca komutu **periyodik yeniden yayınlar (streaming)** ve robot firmware'i bir **watchdog** ile komut akışını izler. Bunun nedeni §3.4.1'de.

PC yazılımı, bir hareket komutu (Move/Turn) aktifken **son komutu sabit aralıkla (varsayılan 100 ms) yeniden yayınlar** — joystick sabit tutulsa bile. Hareket **Stop**'a dönünce streaming durur (boştayken trafik yok).

```
PC: kullanıcı sol stick'i ileri itti (ve basılı tutuyor)
PC → robot: {"cmd":"MoveForward","value":0.5}        ← her 100 ms tekrar
PC → robot: {"cmd":"MoveForward","value":0.5}
PC → robot: {"cmd":"MoveForward","value":0.5}        ← stick sabit olsa bile akış sürer
[robot ileri gidiyor...]
PC: kullanıcı stick'i bıraktı
PC → robot: {"cmd":"Stop"}                            ← bir kere, streaming durur
[robot duruyor — artık komut akmıyor]
```

#### 3.4.1 Neden streaming? (firmware için kritik gerekçe)

Eski event-based modelde robot, "komut hâlâ geçerli" ile "bağlantı koptu"yu **ayırt edemiyordu**. İki kötü seçenek vardı:
- **Watchdog yok →** veri/PC koparsa robot son komutla **sonsuza hareket eder** (örn. yürüyüş robotu duvara/insana çarpana dek devam — saha kazası riski).
- **Watchdog var ama event-based gönderim →** joystick sabit tutulurken yeni mesaj gelmediği için watchdog hareketi **yanlışlıkla keser** (sürüş 0.5 sn sonra tutukluk yapar).

Streaming bu ikilemi çözer: **akış varsa komut canlı, akış durduysa bağlantı koptu.**

#### 3.4.2 Robot firmware watchdog kuralı (ZORUNLU)

Firmware, **son geçerli hareket komutunun (Move/Turn) zaman damgasını** tutmalı ve her döngüde kontrol etmeli:

```c
// Son komuttan beri 500 ms boyunca yeni komut gelmediyse → güvenli dur.
// PC 100 ms aralıkla yayınlar; 500 ms = 5 kaçırılmış mesaj toleransı
// (200 m kablo + ağ jitter için yeterli marj).
if (millis() - sonHareketKomutu > 500) {
    motorlariDurdur();   // veya aktif fren — robot dinamiğine göre
}
```

- Watchdog **yalnızca hareket komutlarını** (Move/Turn) izler. `LightOn`, `CameraPan` gibi anlık komutlar streaming gerektirmez ve watchdog zaman damgasını **güncellememelidir** (yoksa ışık komutu sürüşü canlı tutar gibi yanlış davranır).
- `Stop` gelince watchdog'u sıfırla ve motorları durdur.
- Donanım watchdog'u (`avr/wdt.h`) ile birlikte kullanılması önerilir: yazılım kilitlenirse MCU kendini resetler.

> **Yedek katman — LWT:** Streaming asıl korumadır (~500 ms'de durur). PC çökerse veya broker'dan koparsa **LWT** (§5.3) de robotun `cmd` topic'ine `Stop` yayınlar. İkisi birbirini tamamlar; firmware ikisine de tepki vermeli.

### 3.5 Komut hızı

- **Aktif hareket sırasında:** sabit ~10 komut/sn (100 ms streaming). Stop'ta 0 komut/sn.
- Anlık komutlar (`LightOn`, `CameraPan` vb.) yalnızca kullanıcı eylemi başına yayınlanır.
- Firmware ~10 Hz hareket komutu akışına **rahat dayanmalı** (idempotent uygulama, kuyruğa alma yok — §15).
- Stale komut filtresi (opsiyonel, firmware kararı): `now() - cmd.ts > 500ms` ise reddet (ağ gecikmesi yüksek demektir). Streaming sayesinde bir sonraki tazeleme nasılsa gelir.

---

## 4. Telemetri (Robot → PC)

### 4.1 Payload yapısı

Tek bir topic — `telemetry` — periyodik olarak (önerilen: **2-5 Hz**) JSON yayınlar.

```json
{
  "distanceMeters": 12.45,
  "speed": 0.24,
  "tiltDegrees": 1.2,
  "pressureBar": 1.01,
  "temperatureC": 22.4,
  "humidityPercent": 64.3,
  "batteryPercent": 87.5,
  "gasAlarm": false,
  "waterAlarm": false,
  "lightOn": true,
  "activeMove": "Forward",
  "ts": 1716397250500
}
```

### 4.2 Alan tanımları

| Alan | Tip | Birim | Açıklama |
|---|---|---|---|
| `distanceMeters` | number? | metre | Robotun giriş bacasından kaç metre ileri olduğu. Encoder ya da winç çıktısı. |
| `speed` | number? | m/s | Anlık hız (ileri pozitif, geri negatif) |
| `tiltDegrees` | number? | derece | Yatay eksenden eğim (IMU). ±10° büyük cisim çarpışma sinyali |
| `pressureBar` | number? | bar | Boru içi hava/su basıncı (varsa) |
| `temperatureC` | number? | °C | Robot iç sıcaklığı |
| `humidityPercent` | number? | % | Robot iç nem |
| `batteryPercent` | number? | % | Pil seviyesi 0-100 |
| `gasAlarm` | bool? | – | Yanıcı gaz tespiti (CH4/H2S vb.) |
| `waterAlarm` | bool? | – | Sızıntı/su tespit sensörü |
| `lightOn` | bool? | – | Işık şu an açık mı (komut ack'i) |
| `activeMove` | string? | – | `"Stop"` / `"Forward"` / `"Backward"` / `"TurnLeft"` / `"TurnRight"` (komut state ack) |
| `ts` | number? | unix ms | Robot tarafı timestamp — saat senkronize değilse de PC drift'i izleyebilir |

**`?` = nullable.** Sensör yoksa veya değer geçersizse alanı **omit et** (JSON'da hiç olmasın) ya da `null` döndür. PC tarafı her ikisini de tolere eder. **Asla sahte sıfır gönderme** — UI "0" yazarsa operatör yanlış bilgi alır.

### 4.3 Yayın temposu

- **Önerilen:** 500 ms (2 Hz). Mesafe ve hareket bilgileri overlay'de canlı görünüyor.
- **Yüksek dinamik durumlar:** alarm anında veya hızlı hareket sırasında 250 ms (4 Hz).
- **Idle durumlar:** mesafe ve hareket sabitse 1000 ms (1 Hz).

PC tarafı buffer **etmez** — sadece son alınan değerleri overlay'de gösterir. 10 saniye telemetri gelmezse UI "Telemetri kayıp" uyarısı vermeli (henüz implementasyon yok, future).

### 4.4 Alarm semantiği

`gasAlarm` veya `waterAlarm` `true` ise:
- PC overlay'inde **kırmızı `⚠ GAZ` / cyan `⚠ SU`** yazısı
- FFmpeg burn-in'de aynı şekilde videoya gömülür
- (Future) UI'da bildirim/ses tetikleyebilir

Robot tarafı için: alarm tetiklendiği saniyede telemetri tempo artırılması iyi olur (250 ms).

### 4.5 LightOn handshake

PC `LightOn` komutu publish ettiğinde **buton "beklemede" durumuna geçer**. Robot ışığı açtığında bir sonraki telemetri yayınında `lightOn:true` döndürmeli. PC 3 saniye içinde `lightOn:true` görmezse "onaylanmadı" mesajı verir.

Aynı şekilde `LightOff` → telemetri `lightOn:false` döner.

### 4.6 ActiveMove handshake

`activeMove` her komut sonrası mevcut hareket durumunu yansıtır. Operatör DPad'i bıraktığında `activeMove: "Stop"` görmek için bu alan kritik.

---

## 5. Status topic + LWT (Last Will & Testament)

### 5.1 Online/offline mesajı

Robot broker'a bağlandığında **retain edilmiş** "online" mesajı yayınlar:

```
Topic: myrobotics/robot1/status
Payload: online
Retain: true
QoS: 1
```

### 5.2 LWT konfigürasyonu (robot tarafı)

Robot, broker'a CONNECT paketinde Last Will tanımlamalı:

```
Will Topic: myrobotics/robot1/status
Will Payload: offline
Will Retain: true
Will QoS: 1
```

Bu sayede robot **planlı veya plansız** broker'dan koparsa, broker otomatik `offline` mesajı yayınlar. PC bunu gördüğünde robotun çevrimdışı olduğunu anlar.

### 5.3 PC tarafı LWT (acil durdurma)

PC yazılımı da CONNECT'te LWT tanımlar:

```
Will Topic: myrobotics/robot1/cmd
Will Payload: {"cmd":"Stop","reason":"pc_disconnected"}
Will QoS: 1
```

**Bu kritik bir güvenlik özelliği:** PC çökerse, broker robotun `cmd` topic'ine Stop yayınlar. **Robot firmware bu mesajı görüp derhal durmalı.**

---

## 6. ACK topic (opsiyonel)

Eğer robot firmware her komut sonrası açıkça onay yayınlamak isterse:

```
Topic: myrobotics/robot1/ack
Payload:
{
  "cmd": "LightOn",
  "ok": true,
  "ts": 1716397250500,
  "error": null
}
```

Şu anki PC tarafı bu topic'i **dinlemiyor** — handshake telemetry üzerinden yapılıyor (§4.5). İleride hata kodları/açıklamaları gerekirse bu topic eklenir.

---

## 7. EVENT topic (opsiyonel — gelecek)

Beklenmedik durumlar için (motor takıntı, sensör arızası, yazılım reset) robot bu topic'e yayınlayabilir:

```
Topic: myrobotics/robot1/event
Payload:
{
  "severity": "warning" | "error" | "info",
  "code": "MOTOR_STALL",
  "message": "Sol tekerlek tıkanma tespit edildi",
  "ts": 1716397250500
}
```

PC tarafında UI'da bildirim olarak görünebilir (henüz implementasyon yok).

---

## 8. QoS ve mesaj garantileri

| Mesaj türü | QoS | Neden |
|---|---|---|
| Command (cmd) | **1** (at least once) | Önemli, kayıp olmaması gerekir, duplicate tolere edilebilir (idempotent) |
| Telemetry | **0** (at most once) | Yüksek frekans, kayıp tolere edilebilir, son değer önemli |
| Status (online/offline) | **1** | Kritik retain mesajı |
| LWT (Stop) | **1** | Kritik güvenlik mesajı |

**Idempotency:** `Stop` ve `LightOn/Off` doğal olarak idempotent. `MoveForward 0.5` da iki kez gelse robot zaten ileri gidiyor — fark etmez. Bu yüzden QoS 1'in duplicate'lerine kafa yorulmasına gerek yok.

---

## 9. Bağlantı parametreleri

PC ve robot için önerilen MQTT client ayarları:

| Parametre | Değer |
|---|---|
| Keep-Alive | 15 saniye |
| Clean Session | true |
| Auto-reconnect | true (5 sn backoff) |
| Client ID | benzersiz; örn. `robot-<mac>-<random6>` |

Keep-Alive 15 sn = LWT en geç ~22 sn'de tetiklenir (1.5x kuralı). **Sürüş güvenliği artık bu süreye bağlı değildir** — streaming watchdog'u (§3.4) bağlantı kopmasını ~500 ms'de yakalayıp robotu durdurur. LWT yalnızca yedek katmandır; bu yüzden 15 sn keep-alive yeterlidir, daha hızlı tepki için 5 sn de seçilebilir.

---

## 10. Güvenlik (mevcut durum + gelecek)

**v1 (current):**
- TLS yok
- Auth yok (anonim)
- Saha şebekesi izole varsayılır

**v2 hedefleri:**
- TLS 1.2+ (port 8883)
- Username/password auth (Mosquitto password file)
- Client cert auth (mTLS) — yüksek güvenlik tier
- Per-topic ACL (robot sadece kendi topic'lerine yazabilsin)

PC yazılımı zaten Settings'te `BrokerUsername` / `BrokerPassword` alanlarını destekliyor — robot firmware'i de aynı şekilde implementasyonu desteklemeli.

---

## 11. Sürüm değişiklik kuralları

- **Yeni alan ekleme** (telemetry'ye yeni sensör): minor version (v1.1, v1.2...). PC eski versiyonda yeni alanı görmez ama crash etmez.
- **Alan adı değiştirme** veya **anlam değiştirme**: major version (v2.0). Geriye uyumsuz, hem PC hem firmware aynı anda güncellenmeli.
- **Yeni komut**: minor. Robot bilmiyorsa görmezden gelir.

Mevcut versiyon her iki tarafta da log'da görünür: protocol_version = "v1.0".

---

## 12. Referans implementasyonlar

- **PC tarafı**: [`Services/MqttRobotClient.cs`](../Services/MqttRobotClient.cs) (MQTTnet 5.1)
- **PC tarafı telemetry parsing**: [`Services/TelemetryService.cs`](../Services/TelemetryService.cs) + [`Models/TelemetrySnapshot.cs`](../Models/TelemetrySnapshot.cs)
- **Simulator (firmware emülasyonu)**: [`Tools/RobotSimulator/Program.cs`](../Tools/RobotSimulator/Program.cs) — başlatma: `Tools/start-simulator.cmd`
- **Bu doküman bir contract**: firmware'i bu spec'e göre implement et, PC tarafı zaten uyumlu.

---

## 13. Hızlı test reçetesi

Firmware bu spec'e göre yazıldıktan sonra **PC ile entegrasyon testi**:

1. Mosquitto broker'ı çalıştır (`Tools/start-broker.cmd`)
2. Robot kartını broker'a bağla (host: PC IP, port 1883)
3. `mosquitto_sub -t "myrobotics/#" -v` (PC tarafında bir terminal) — tüm trafiği gör
4. Robot tarafı `online` retain'i yayınlamalı
5. Robot tarafı 2 Hz telemetri yayınlamalı
6. PC'den komut gönder: `mosquitto_pub -t "myrobotics/robot1/cmd" -m '{"cmd":"MoveForward","value":0.3}'`
7. Robot ileri gitmeli, `activeMove: "Forward"` döndürmeli
8. `mosquitto_pub -t "myrobotics/robot1/cmd" -m '{"cmd":"Stop"}'` → robot durmalı
9. **Watchdog testi (kritik):** `MoveForward` yayınla ama **tekrarlama**; robot ~500 ms içinde kendiliğinden durmalı (streaming akışı kesildi → watchdog devreye girdi). Ardından 100 ms aralıkla `MoveForward` akışı verirsen robot **kesintisiz** ilerlemeli.
10. Robot kartını fişten çek → ~22 sn'de PC'de status `offline` görünmeli
11. PC kapanırsa → (a) streaming akışı durduğu için robot ~500 ms'de watchdog ile durmalı, **ayrıca** (b) broker LWT ile robotun cmd topic'ine Stop yayınlamalı (iki bağımsız katman). Firmware loglarından LWT mesajının alındığı da doğrulanmalı.

---

## 14. Gamepad (Logitech F710) entegrasyonu — firmware'i etkilemez

PC tarafında **Logitech F710** gamepad'i XInput API üzerinden okunuyor. Bu sadece **PC tarafı input katmanı** — protokol açısından hiçbir fark yok: gamepad'ten gelen hareket de aynı `{prefix}/{robotId}/cmd` topic'ine aynı `{cmd,value,ts}` JSON formatında publish edilir.

Firmware için bilinmesi gereken davranış: gamepad ile sürüş sırasında PC, son hareket komutunu **100 ms aralıkla yeniden yayınlar** (§3.4 streaming). Yani sol stick ileri itilip sabit tutulduğunda dahi `MoveForward` akışı sürer — bu, robot watchdog'unun hareketi canlı görmesi için **tasarlanmış** davranıştır, spam değildir. Stick merkeze dönünce `Stop` yayınlanır ve akış durur. Anlık komutlar (CameraPan, ışık) yalnızca eylem başına gider, streaming'e dahil değildir.

**Mapping (PC tarafı kararı, robot bilmek zorunda değil):**

| F710 girdi | Yayınlanan komut |
|---|---|
| Sol stick | `MoveForward / MoveBackward / TurnLeft / TurnRight` (büyüklüğe göre `value`) |
| Sol stick merkez | `Stop` (anında, force-send) |
| Sağ stick | `CameraPan / CameraTilt` (varsa) |
| LT basılı | Hız çarpanı 0.3x (yavaş, hassas mod) |
| RT basılı | Hız çarpanı 1.0x (tam hız) |
| A | (UI eylemi: snapshot — komut yok) |
| **B** | **`Stop` + UI E-STOP** — robot derhal durmalı, LWT semantiği ile aynı |
| X | (UI eylemi: ışık aç/kapa → `LightOn` / `LightOff`) |
| Y | (UI eylemi: kusur işaretle — komut yok) |
| Start | (UI eylemi: kayıt aç/kapa — komut yok) |
| Back | (UI eylemi: inceleme başlat/bitir — komut yok) |
| DPad | Düşük hız (0.25) diskret hareket — stick alternatifi |

---

## 15. SSS (firmware ekibi için)

**S: Komut topic'inden gelen mesajları kuyruğa mı alalım, yoksa sadece son komutu mu uygulayalım?**
C: Komutlar idempotent, doğrudan uygulayın. Eski komutu unutun. Stop her zaman öncelikli.

**S: Telemetri JSON oluşturma yükü ne kadar?**
C: 200 byte civarı. 500ms'de bir bile saatte ~1.5 MB. Bandwidth sorun değil; CPU minimal.

**S: Tüm sensörlerimiz yok, telemetri yine de yayınlasın mı?**
C: Evet. Olmayan alanları JSON'dan çıkar (omit), `null` da olabilir. `distanceMeters` en kritik alan.

**S: Robot reset olunca state ne olur?**
C: Reset sonrası `Stop` durumunda başla, retain edilmiş `online` mesajını tekrar yayınla, `activeMove: "Stop"` ile telemetri başlat. `distanceMeters` encoder'a göre 0'dan başlamalı veya en son kalıcı değer.

**S: Iki PC aynı anda komut gönderebilir mi?**
C: Teknik olarak evet. Robot **son gelen komutu** uygulamalı. Operatör çakışmasını engellemek bir PC uygulama kararı (henüz yok). İhtiyaç olursa "yöneten PC" claim/release mekanizması v1.1'de eklenebilir.

**S: Hangi byte order, encoding?**
C: UTF-8 JSON, `application/json`. Sayılar JSON sayı tipi (number) — int ya da float aynı, parse edilirken otomatik tip belirlenir.

---

**İletişim:** Bu spec hakkında sorular veya değişiklik talepleri için MyRoboticsInspector projesi maintainer'larına ulaşın. Spec değişiklikleri commit log'da takip edilir.
