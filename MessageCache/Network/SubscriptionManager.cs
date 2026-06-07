using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MessageCache.Network;

public sealed class SubscriptionManager
{
    // key -> { subscriptionId -> channel }
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<NotificationMessage>>>
        _subscriptions = new(StringComparer.Ordinal);
    
    public (Guid SubscriptionId, ChannelReader<NotificationMessage> Reader) Subscribe(string key)
    {
        var channel = Channel.CreateBounded<NotificationMessage>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        var id = Guid.NewGuid();
        var keyMap = _subscriptions.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, Channel<NotificationMessage>>());
        keyMap[id] = channel;
        return (id, channel.Reader);
    }
    
    public void Unsubscribe(string key, Guid subscriptionId)
    {
        if (_subscriptions.TryGetValue(key, out var keyMap)
            && keyMap.TryRemove(subscriptionId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
    
    public void Notify(string key, string? value)
    {
        if (!_subscriptions.TryGetValue(key, out var keyMap)) return;

        var message = new NotificationMessage(key, value);
        foreach (var (_, channel) in keyMap)
            channel.Writer.TryWrite(message);
    }
}
