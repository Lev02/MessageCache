using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using MessageCache.Processing;
using MessageCache.Protocol;
using Microsoft.Extensions.Logging;

namespace MessageCache.Network;

/// <summary>
/// Управляет клиентом. В это входит:
/// 1. Чтение команд через System.IO.Pipelines и из обработка/перенаправление.
/// 2. Ответы клиенту.
/// 3. Перенапрвляет запросы на подписки (команда WATCH) в SubscriptionManager
/// </summary>
public sealed class ClientConnection(
    Socket socket,
    CommandProcessor processor,
    SubscriptionManager subscriptionManager,
    bool requireAuth,
    ILogger logger)

    : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // Активные подписки на ключи key -> (subscriptionId, watchTask cts)
    private readonly Dictionary<string, (Guid SubId, CancellationTokenSource Cts)> _watches = new();

    private bool _isAuthenticated = !requireAuth;

    public async Task RunAsync(CancellationToken serverToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, _cts.Token);
        var ct = linked.Token;

        processor.Statistics.AddConnection();
        logger.LogDebug("Клиент подключен: {SocketRemoteEndPoint}", socket.RemoteEndPoint);

        var stream = new NetworkStream(socket, ownsSocket: false);
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(
            pool: MemoryPool<byte>.Shared,
            bufferSize: 4096,
            minimumReadSize: 256,
            leaveOpen: true));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadResult result;
                try
                {
                    result = await reader.ReadAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var buffer = result.Buffer;

                while (TryReadLine(ref buffer, out var lineSeq))
                {
                    await ProcessLineAsync(lineSeq, ct);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted || result.IsCanceled) break;
            }
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // клиент внезапно отключился, это не ошибка
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка обработки клиента {EndPoint}", socket.RemoteEndPoint);
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (reader.TryReadTo(out line, (byte)'\n', advancePastDelimiter: true))
        {
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        line = default;
        return false;
    }

    private ValueTask ProcessLineAsync(ReadOnlySequence<byte> lineSeq, CancellationToken ct)
    {
        processor.Statistics.AddCommand();

        if (lineSeq.IsSingleSegment)
        {
            return DispatchSpanAsync(lineSeq.FirstSpan, ct);
        }

        int len = (int)lineSeq.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            lineSeq.CopyTo(rented);
            return DispatchSpanAsync(rented.AsSpan(0, len), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private ValueTask DispatchSpanAsync(ReadOnlySpan<byte> span, CancellationToken ct)
    {
        //обрезка \r\n
        while (!span.IsEmpty && (span[^1] == '\r' || span[^1] == '\n'))
            span = span[..^1];

        if (span.IsEmpty) return ValueTask.CompletedTask;

        if (!CommandParser.TryParse(span, out var cmd))
            return SendBytesAsync(BuildError("ERR unknown command"), ct);
        
        if (!_isAuthenticated && cmd.Type != CommandType.Auth)
            return SendRawAsync(ResponseWriter.NoAuthBytes, ct);

        // Нужно достать из cmd все значения перед ExecuteAsync, так как ExecuteAsync это асинхронный метод, а cmd это ref struct
        var type = cmd.Type;
        string? key = cmd.KeySpan.IsEmpty ? null : Encoding.UTF8.GetString(cmd.KeySpan);
        string? value = cmd.ValueSpan.IsEmpty ? null : Encoding.UTF8.GetString(cmd.ValueSpan);
        string? arg3 = cmd.Arg3Span.IsEmpty ? null : Encoding.UTF8.GetString(cmd.Arg3Span);
        int expiry = cmd.ExpirySeconds;

        return ExecuteAsync(type, key, value, arg3, expiry, ct);
    }

    private async ValueTask ExecuteAsync(
        CommandType type, string? key, string? value, string? arg3, int expiry, CancellationToken ct)
    {
        switch (type)
        {
            case CommandType.Ping:
                await SendRawAsync(ResponseWriter.PongBytes, ct);
                break;

            case CommandType.Auth:
                if (processor.CheckPassword(arg3))
                {
                    _isAuthenticated = true;
                    await SendRawAsync(ResponseWriter.OkBytes, ct);
                }
                else
                {
                    await SendRawAsync(ResponseWriter.InvalidAuthBytes, ct);
                }
                break;

            case CommandType.Set:
                if (key is null || value is null) { await SendBytesAsync(BuildError("ERR wrong number of arguments"), ct); break; }
                processor.Set(key, value, expiry > 0 ? expiry : null);
                await SendRawAsync(ResponseWriter.OkBytes, ct);
                break;

            case CommandType.Get:
                if (key is null) { await SendBytesAsync(BuildError("ERR wrong number of arguments"), ct); break; }
                if (processor.TryGet(key, out var retrieved))
                    await SendBytesAsync(BuildBulkString(retrieved!), ct);
                else
                    await SendRawAsync(ResponseWriter.NilBytes, ct);
                break;

            case CommandType.Delete:
                if (key is null) { await SendBytesAsync(BuildError("ERR wrong number of arguments"), ct); break; }
                processor.Delete(key);
                await SendRawAsync(ResponseWriter.OkBytes, ct);
                break;

            case CommandType.Expire:
                if (key is null) { await SendBytesAsync(BuildError("ERR wrong number of arguments"), ct); break; }
                bool expired = processor.Expire(key, expiry);
                await SendBytesAsync(BuildInteger(expired ? 1 : 0), ct);
                break;

            case CommandType.Ttl:
                if (key is null) { await SendBytesAsync(BuildError("ERR wrong number of arguments"), ct); break; }
                long ttl = processor.GetTtl(key);
                await SendBytesAsync(BuildInteger(ttl), ct);
                break;

            case CommandType.Keys:
                string pattern = arg3 ?? "*";
                string[] keys = processor.GetKeys(pattern);
                await SendBytesAsync(BuildArray(keys), ct);
                break;

            case CommandType.Watch:
                if (key is null) { await SendBytesAsync(BuildError("ERR wrong number of arguments"), ct); break; }
                await HandleWatchAsync(key, ct);
                break;

            case CommandType.Unwatch:
                if (key is null) { await SendBytesAsync(BuildError("ERR wrong number of arguments"), ct); break; }
                HandleUnwatch(key);
                await SendRawAsync(ResponseWriter.OkBytes, ct);
                break;

            case CommandType.Stats:
                var stats = processor.GetStats();
                await SendBytesAsync(BuildSimpleString(stats), ct);
                break;

            default:
                await SendBytesAsync(BuildError("ERR unknown command"), ct);
                break;
        }
    }

    private async ValueTask HandleWatchAsync(string key, CancellationToken ct)
    {
        if (_watches.ContainsKey(key))
        {
            await SendRawAsync(ResponseWriter.OkBytes, ct);
            return;
        }

        var (subId, reader) = subscriptionManager.Subscribe(key);
        var watchCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _watches[key] = (subId, watchCts);

        _ = PushNotificationsAsync(key, reader, watchCts.Token);

        await SendRawAsync(ResponseWriter.OkBytes, ct);
    }

    private void HandleUnwatch(string key)
    {
        if (!_watches.TryGetValue(key, out var entry)) return;
        entry.Cts.Cancel();
        subscriptionManager.Unsubscribe(key, entry.SubId);
        _watches.Remove(key);
    }

    private async Task PushNotificationsAsync(string key, System.Threading.Channels.ChannelReader<NotificationMessage> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in reader.ReadAllAsync(ct))
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(512 + msg.Key.Length * 2 + (msg.Value?.Length ?? 0) * 2);
                try
                {
                    int len = ResponseWriter.WriteNotify(rented, msg.Key, msg.Value);
                    await SendRawAsync(rented.AsMemory(0, len), ct);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsConnectionError(ex)) { }
    }

    // ── Response builders (ArrayPool-based) ──────────────────────────────────

    private static byte[] BuildError(string message)
    {
        int size = 3 + Encoding.UTF8.GetByteCount(message);
        byte[] buf = ArrayPool<byte>.Shared.Rent(size);
        int len = ResponseWriter.WriteError(buf, message);
        // Return a trimmed copy; caller must return to pool — but since BuildXxx is used inline,
        // we use a small heap alloc here for correctness. Hot path uses SendRawAsync with static arrays.
        var result = buf.AsSpan(0, len).ToArray();
        ArrayPool<byte>.Shared.Return(buf);
        return result;
    }

    private static byte[] BuildBulkString(string value)
    {
        int size = 32 + Encoding.UTF8.GetByteCount(value);
        byte[] buf = ArrayPool<byte>.Shared.Rent(size);
        int len = ResponseWriter.WriteBulkString(buf, value);
        var result = buf.AsSpan(0, len).ToArray();
        ArrayPool<byte>.Shared.Return(buf);
        return result;
    }

    private static byte[] BuildInteger(long value)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(32);
        int len = ResponseWriter.WriteInteger(buf, value);
        var result = buf.AsSpan(0, len).ToArray();
        ArrayPool<byte>.Shared.Return(buf);
        return result;
    }

    private static byte[] BuildSimpleString(string message)
    {
        int size = 4 + Encoding.UTF8.GetByteCount(message);
        byte[] buf = ArrayPool<byte>.Shared.Rent(size);
        int len = ResponseWriter.WriteSimpleString(buf, message);
        var result = buf.AsSpan(0, len).ToArray();
        ArrayPool<byte>.Shared.Return(buf);
        return result;
    }

    private static byte[] BuildArray(string[] items)
    {
        int size = ResponseWriter.EstimateArraySize(items);
        byte[] buf = ArrayPool<byte>.Shared.Rent(size);
        int len = ResponseWriter.WriteArray(buf, items);
        var result = buf.AsSpan(0, len).ToArray();
        ArrayPool<byte>.Shared.Return(buf);
        return result;
    }

    // ── Low-level send ────────────────────────────────────────────────────────

    /// <summary>Send pre-built bytes without additional copying.</summary>
    private async ValueTask SendRawAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            await socket.SendAsync(data, SocketFlags.None, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Send a byte array built by one of the Build* helpers.</summary>
    private ValueTask SendBytesAsync(byte[] data, CancellationToken ct) =>
        SendRawAsync(data.AsMemory(), ct);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        foreach (var (key, entry) in _watches)
        {
            await entry.Cts.CancelAsync();
            subscriptionManager.Unsubscribe(key, entry.SubId);
        }

        _watches.Clear();
        processor.Statistics.RemoveConnection();

        try { socket.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
        socket.Dispose();
        _sendGate.Dispose();
        _cts.Dispose();

        logger.LogDebug("Client disconnected");
        await ValueTask.CompletedTask;
    }

    private static bool IsConnectionError(Exception ex) =>
        ex is SocketException or IOException or ObjectDisposedException;
}
