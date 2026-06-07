using System.Collections.Concurrent;

namespace MessageCache.LoadTests;

/// <summary>
/// Thread-safe pool of reusable CacheClient connections.
/// Eliminates TCP port exhaustion caused by creating a new connection per request.
/// Each Rent() call takes a connection from the pool (or creates one if empty).
/// Each Return() puts it back for the next caller.
/// </summary>
public sealed class ConnectionPool : IDisposable
{
    private readonly ConcurrentBag<CacheClient> _pool = new();
    private readonly string _host;
    private readonly int _port;
    private bool _disposed;

    public int InitialSize { get; }

    public ConnectionPool(string host, int port, int initialSize)
    {
        _host = host;
        _port = port;
        InitialSize = initialSize;

        for (int i = 0; i < initialSize; i++)
            _pool.Add(new CacheClient(host, port));
    }

    /// <summary>
    /// Take a connection from the pool.
    /// If pool is empty, creates a temporary connection on the fly.
    /// </summary>
    public CacheClient Rent()
    {
        return _pool.TryTake(out var client) ? client : new CacheClient(_host, _port);
    }

    /// <summary>
    /// Return a healthy connection back to the pool for reuse.
    /// Pass discard: true if the connection errored and should not be reused.
    /// </summary>
    public void Return(CacheClient client, bool discard = false)
    {
        if (discard)
            client.Dispose();
        else
            _pool.Add(client);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_pool.TryTake(out var client))
            client.Dispose();
    }
}
