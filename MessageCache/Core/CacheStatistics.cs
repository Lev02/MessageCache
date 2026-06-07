namespace MessageCache.Core;

public sealed class CacheStatistics
{
    private long _hits;
    private long _misses;
    private long _sets;
    private long _deletes;
    private long _totalCommands;
    private long _activeConnections;

    public long Hits => Volatile.Read(ref _hits);
    public long Misses => Volatile.Read(ref _misses);
    public long Sets => Volatile.Read(ref _sets);
    public long Deletes => Volatile.Read(ref _deletes);
    public long TotalCommands => Volatile.Read(ref _totalCommands);
    public long ActiveConnections => Volatile.Read(ref _activeConnections);

    public void AddHit() => Interlocked.Increment(ref _hits);
    public void AddMiss() => Interlocked.Increment(ref _misses);
    public void AddSet() => Interlocked.Increment(ref _sets);
    public void AddDelete() => Interlocked.Increment(ref _deletes);
    public void AddCommand() => Interlocked.Increment(ref _totalCommands);
    public void AddConnection() => Interlocked.Increment(ref _activeConnections);
    public void RemoveConnection() => Interlocked.Decrement(ref _activeConnections);

    public override string ToString() =>
        $"{{\"hits\":{Hits},\"misses\":{Misses},\"sets\":{Sets}," +
        $"\"deletes\":{Deletes},\"totalCommands\":{TotalCommands}," +
        $"\"activeConnections\":{ActiveConnections}}}";
}
