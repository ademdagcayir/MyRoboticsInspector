using System.Diagnostics;

namespace MyRoboticsInspector.Services;

/// <summary>
/// Telemetri (özellikle metre) örneklerini ortak bir monotonik saate (Stopwatch saniye)
/// damgalayıp zaman-indeksli bir ring buffer'da tutar. Video kareleri kendi PTS'lerinden
/// türetilen yakalama-zamanıyla bu buffer'dan sorgulanır → kare ile metre senkron olur.
///
/// Neden gerekli: MQTT telemetrisi ile RTSP videosu farklı gecikmelerle gelir. "En son
/// değeri en son kareye bas" yaklaşımı 6. metrenin karesine 6.3 yazılmasına yol açar.
/// Burada her kare, KENDİ yakalama anına en yakın metre örneğini (lineer interpolasyonla)
/// alır → "6. metrede 6. metrenin görüntüsü".
///
/// Tüm zamanlar saniyedir ve aynı <see cref="NowSeconds"/> saatine göredir.
/// </summary>
public sealed class TelemetrySyncBuffer
{
    private readonly record struct Sample(double T, double Meters, bool HasMeters);

    private readonly object _lock = new();
    private readonly Sample[] _ring;
    private int _head;          // bir sonraki yazılacak indeks
    private int _count;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <param name="capacity">Tutulacak örnek sayısı. 25 Hz telemetride 600 ≈ 24 sn geçmiş.</param>
    public TelemetrySyncBuffer(int capacity = 600)
    {
        _ring = new Sample[Math.Max(8, capacity)];
    }

    /// <summary>Ortak monotonik saat (saniye). Hem telemetri hem video tarafı bunu kullanır.</summary>
    public double NowSeconds => _clock.Elapsed.TotalSeconds;

    /// <summary>Yeni bir metre örneği ekler (MQTT telemetrisi geldiğinde). Şimdiki saatle damgalanır.</summary>
    public void AddMeters(double meters) => Add(meters, hasMeters: true);

    /// <summary>Metre bilgisi olmayan ama zaman çizelgesini taze tutan örnek (opsiyonel).</summary>
    public void Touch() => Add(0, hasMeters: false);

    private void Add(double meters, bool hasMeters)
    {
        lock (_lock)
        {
            _ring[_head] = new Sample(NowSeconds, meters, hasMeters);
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }
    }

    /// <summary>
    /// Verilen yakalama-zamanına (saniye) en uygun metre değerini döner.
    /// İki örnek arasında lineer interpolasyon yapar; aralık dışındaysa en yakın uca tutunur.
    /// Hiç metre örneği yoksa null.
    /// </summary>
    public double? MetersAt(double tSeconds)
    {
        lock (_lock)
        {
            if (_count == 0) return null;

            // Ring'i kronolojik gez: en eski → en yeni
            int firstIdx = (_head - _count + _ring.Length) % _ring.Length;

            Sample? prev = null;
            double? lastMetersBefore = null;
            double lastTBefore = 0;

            for (int i = 0; i < _count; i++)
            {
                var s = _ring[(firstIdx + i) % _ring.Length];
                if (!s.HasMeters) continue;

                if (s.T <= tSeconds)
                {
                    lastMetersBefore = s.Meters;
                    lastTBefore = s.T;
                    prev = s;
                }
                else
                {
                    // s, tSeconds'tan sonraki ilk metre örneği
                    if (lastMetersBefore is double mPrev)
                    {
                        double span = s.T - lastTBefore;
                        if (span <= 1e-6) return s.Meters;
                        double f = (tSeconds - lastTBefore) / span;
                        f = Math.Clamp(f, 0.0, 1.0);
                        return mPrev + (s.Meters - mPrev) * f;
                    }
                    // tSeconds tüm örneklerden eski → en eski metre
                    return s.Meters;
                }
            }

            // tSeconds tüm örneklerden yeni → en son bilinen metre
            return lastMetersBefore;
        }
    }

    public void Clear()
    {
        lock (_lock) { _head = 0; _count = 0; }
    }
}
