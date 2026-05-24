# MyRoboticsInspector ↔ Robot Kartı MQTT Protokolü

**Versiyon:** v1.0 · **Tarih:** 22.05.2026 · **Hedef kitle:** Robot kartı firmware geliştiricileri

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

### 3.4 Komut yaşam döngüsü — press-and-hold

PC yazılımı **basılı tutarken sürekli aynı komutu yayınlamaz**; **bir defa** yayınlar ve robot çalışmaya devam eder. Bırakınca **Stop** yayınlanır.

```
PC: kullanıcı ▲ tuşuna bastı
PC → robot: {"cmd":"MoveForward","value":0.5}        ← bir kere
[robot ileri gidiyor...]
PC: kullanıcı tuşu bıraktı
PC → robot: {"cmd":"Stop"}                           ← bir kere
[robot duruyor]
```

**Robot firmware için kritik:** Komut tekrarı gelmezse de hareket etmeye devam et. PC bağlantısı kopsa ne olur? → **LWT** (bkz. §5).

### 3.5 Komut hızı

- Tipik tempo: kullanıcı etkileşimi başına 1 komut (debouncing yok)
- Worst case: dpad spam — saniyede ~10 komut. Firmware buna dayanıklı olmalı.
- Stale komut filtresi (opsiyonel, firmware kararı): `now() - cmd.ts > 500ms` ise reddet (ağ gecikmesi yüksek demektir).

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

Keep-Alive 15 sn = LWT en geç ~22 sn'de tetiklenir (1.5x kuralı). Daha hızlı tepki için 5 sn keep-alive da olabilir, ancak şebeke gürültüsü artar.

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
9. Robot kartını fişten çek → ~22 sn'de PC'de status `offline` görünmeli
10. PC kapanırsa → broker LWT ile robotun cmd topic'ine Stop yayınlamalı, robot durmalı (bu adım test edilemezse en azından firmware loglarından LWT mesajının alındığı doğrulanmalı)

---

## 14. Gamepad (Logitech F710) entegrasyonu — firmware'i etkilemez

PC tarafında **Logitech F710** gamepad'i XInput API üzerinden okunuyor. Bu sadece **PC tarafı input katmanı** — protokol açısından hiçbir fark yok: gamepad'ten gelen hareket de aynı `{prefix}/{robotId}/cmd` topic'ine aynı `{cmd,value,ts}` JSON formatında publish edilir.

Firmware için bilinmesi gereken tek davranış farkı: gamepad sürüş yaparken **komut hızı artabilir** (operatör stick'i sürekli sallarsa). Yukarıdaki §3.5'teki "saniyede ~10 komut" varsayımı korunur — PC tarafı 120ms throttle + hysteresis (0.10) ile spam'i frenler, ama yine de fare/parmakla DPad kullanımına kıyasla daha sık komut akar.

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
