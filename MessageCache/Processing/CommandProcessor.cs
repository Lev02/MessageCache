using MessageCache.Core;
using MessageCache.Network;
using MessageCache.Telemetry;

namespace MessageCache.Processing;

/// <summary>
/// Делает запись в CacheStorage, оформляет подписки через SubscriptionManager, пишет метрики в CacheMetrics
/// </summary>
public sealed class CommandProcessor
{
    private readonly CacheStorage _storage;
    private readonly SubscriptionManager _subscriptionManager;
    private readonly CacheMetrics _metrics;
    private readonly string _password;

    public CacheStatistics Statistics => _storage.Statistics;

    public CommandProcessor(
        CacheStorage storage,
        SubscriptionManager subscriptionManager,
        CacheMetrics metrics,
        string password = "")
    {
        _storage = storage;
        _subscriptionManager = subscriptionManager;
        _metrics = metrics;
        _password = password;

        _storage.KeyExpired += key =>
        {
            _subscriptionManager.Notify(key, null);
            _metrics.RecordKeyExpired();
        };
    }

    public bool CheckPassword(string? provided) =>
        string.IsNullOrEmpty(_password) || provided == _password;

    public void Set(string key, string value, int? expirySeconds = null)
    {
        _storage.Set(key, value, expirySeconds);
        _subscriptionManager.Notify(key, value);
        _metrics.RecordSet();
    }

    public bool TryGet(string key, out string? value)
    {
        bool found = _storage.TryGet(key, out value);
        if (found) _metrics.RecordHit();
        else _metrics.RecordMiss();
        return found;
    }

    public void Delete(string key)
    {
        bool deleted = _storage.Delete(key);
        if (deleted)
        {
            _subscriptionManager.Notify(key, null);
            _metrics.RecordDelete();
        }
    }

    public bool Expire(string key, int seconds) => _storage.Expire(key, seconds);

    public long GetTtl(string key) => _storage.GetTtl(key);

    public string[] GetKeys(string pattern = "*") => _storage.GetKeys(pattern);

    public string GetStats()
    {
        var stats = _storage.Statistics;
        int keyCount = _storage.KeyCount();
        return $"{{\"hits\":{stats.Hits},\"misses\":{stats.Misses}," +
               $"\"sets\":{stats.Sets},\"deletes\":{stats.Deletes}," +
               $"\"totalCommands\":{stats.TotalCommands}," +
               $"\"activeConnections\":{stats.ActiveConnections}," +
               $"\"keys\":{keyCount}}}";
    }
}
