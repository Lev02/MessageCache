namespace MessageCache.Core;

public readonly struct CacheEntry(string value, long expiresAtTicks = 0)
{
    public readonly string Value = value;
    public readonly long ExpiresAtTicks = expiresAtTicks;

    public bool IsExpired =>
        ExpiresAtTicks > 0 && DateTime.UtcNow.Ticks > ExpiresAtTicks;

    public long RemainingSeconds =>
        ExpiresAtTicks == 0 ? -1 : Math.Max(0, (ExpiresAtTicks - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerSecond);
}
