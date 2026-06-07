using Microsoft.Extensions.Logging;

namespace MessageCache.Core;

public sealed class CacheStorage : IDisposable
{
    private readonly Dictionary<string, CacheEntry> _store = new(StringComparer.Ordinal);
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Timer _expiryTimer;
    private readonly ILogger<CacheStorage> _logger;
    private bool _disposed;

    public CacheStatistics Statistics { get; } = new();
    
    public event Action<string>? KeyExpired;

    public CacheStorage(ILogger<CacheStorage> logger)
    {
        _logger = logger;
        _expiryTimer = new Timer(CleanupExpired, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public bool TryGet(string key, out string? value)
    {
        _lock.EnterReadLock();
        try
        {
            if (_store.TryGetValue(key, out var entry) && !entry.IsExpired)
            {
                value = entry.Value;
                Statistics.AddHit();
                return true;
            }

            value = null;
            Statistics.AddMiss();
            return false;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Set(string key, string value, int? expirySeconds = null)
    {
        long expiresAt = expirySeconds.HasValue
            ? DateTime.UtcNow.AddSeconds(expirySeconds.Value).Ticks
            : 0;

        _lock.EnterWriteLock();
        try
        {
            _store[key] = new CacheEntry(value, expiresAt);
            Statistics.AddSet();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool Delete(string key)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_store.Remove(key))
            {
                Statistics.AddDelete();
                return true;
            }

            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool Expire(string key, int seconds)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_store.TryGetValue(key, out var entry) && !entry.IsExpired)
            {
                _store[key] = new CacheEntry(entry.Value, DateTime.UtcNow.AddSeconds(seconds).Ticks);
                return true;
            }

            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public long GetTtl(string key)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_store.TryGetValue(key, out var entry)) return -2; // key not found
            if (entry.IsExpired) return -2;
            return entry.RemainingSeconds; // -1 = no expiry, >= 0 = seconds remaining
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public string[] GetKeys(string pattern = "*")
    {
        _lock.EnterReadLock();
        try
        {
            var now = DateTime.UtcNow.Ticks;
            return _store
                .Where(kv => (kv.Value.ExpiresAtTicks == 0 || kv.Value.ExpiresAtTicks > now)
                             && MatchesPattern(kv.Key, pattern))
                .Select(kv => kv.Key)
                .ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public int KeyCount()
    {
        _lock.EnterReadLock();
        try
        {
            var now = DateTime.UtcNow.Ticks;
            return _store.Count(kv => kv.Value.ExpiresAtTicks == 0 || kv.Value.ExpiresAtTicks > now);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private void CleanupExpired(object? state)
    {
        List<string>? expiredKeys = null;

        _lock.EnterWriteLock();
        try
        {
            var now = DateTime.UtcNow.Ticks;
            foreach (var kv in _store)
            {
                if (kv.Value.ExpiresAtTicks > 0 && kv.Value.ExpiresAtTicks < now)
                {
                    expiredKeys ??= new List<string>();
                    expiredKeys.Add(kv.Key);
                }
            }

            if (expiredKeys is not null)
            {
                foreach (var key in expiredKeys)
                    _store.Remove(key);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        if (expiredKeys is not null)
        {
            _logger.LogDebug($"Удалено {expiredKeys.Count} просроченных ключей");
            foreach (var key in expiredKeys)
                KeyExpired?.Invoke(key);
        }
    }

    private static bool MatchesPattern(string key, string pattern)
    {
        if (pattern == "*") return true;
        if (!pattern.Contains('*')) return key.Equals(pattern, StringComparison.Ordinal);

        var parts = pattern.Split('*');
        int pos = 0;

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            int idx = key.IndexOf(parts[i], pos, StringComparison.Ordinal);
            if (idx < 0) return false;
            if (i == 0 && idx != 0) return false;
            pos = idx + parts[i].Length;
        }

        if (!pattern.EndsWith('*') && pos != key.Length) return false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _expiryTimer.Dispose();
        _lock.Dispose();
    }
}
