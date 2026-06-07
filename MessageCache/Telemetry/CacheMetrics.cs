using System.Diagnostics;
using System.Diagnostics.Metrics;
using MessageCache.Core;

namespace MessageCache.Telemetry;

/// <summary>
/// Метрики приложения, которые совместимы с OpenTelemetry
/// </summary>
public sealed class CacheMetrics : IDisposable
{
    public const string MeterName = "MessageCache";

    private readonly Meter _meter;
    private readonly Counter<long> _setsCounter;
    private readonly Counter<long> _hitsCounter;
    private readonly Counter<long> _missesCounter;
    private readonly Counter<long> _deletesCounter;
    private readonly Counter<long> _expiredCounter;

    public CacheMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName, "1.0.0");

        _setsCounter = _meter.CreateCounter<long>(
            "messagecache.sets.total", "operations", "Total SET operations");

        _hitsCounter = _meter.CreateCounter<long>(
            "messagecache.hits.total", "operations", "Total cache hits");

        _missesCounter = _meter.CreateCounter<long>(
            "messagecache.misses.total", "operations", "Total cache misses");

        _deletesCounter = _meter.CreateCounter<long>(
            "messagecache.deletes.total", "operations", "Total DELETE operations");

        _expiredCounter = _meter.CreateCounter<long>(
            "messagecache.expired.total", "keys", "Total keys evicted by TTL");
    }

    public void RecordSet() => _setsCounter.Add(1);
    public void RecordHit() => _hitsCounter.Add(1);
    public void RecordMiss() => _missesCounter.Add(1);
    public void RecordDelete() => _deletesCounter.Add(1);
    public void RecordKeyExpired() => _expiredCounter.Add(1);

    public void Dispose() => _meter.Dispose();
}
