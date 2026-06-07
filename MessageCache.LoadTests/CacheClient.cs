using System.Buffers;
using System.Net.Sockets;
using System.Text;

namespace MessageCache.LoadTests;

/// <summary>
/// TCP-клиент для использования в тестовых сценариях
/// </summary>
public sealed class CacheClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly byte[] _receiveBuffer = ArrayPool<byte>.Shared.Rent(4096);

    public CacheClient(string host, int port)
    {
        _tcp = new TcpClient();
        _tcp.NoDelay = true;
        _tcp.Connect(host, port);
        _stream = _tcp.GetStream();
    }

    private string? Send(string command)
    {
        var bytes = Encoding.UTF8.GetBytes(command + "\r\n");
        _stream.Write(bytes, 0, bytes.Length);

        int read = _stream.Read(_receiveBuffer, 0, _receiveBuffer.Length);
        if (read <= 0) return null;

        return Encoding.UTF8.GetString(_receiveBuffer, 0, read).TrimEnd('\r', '\n');
    }

    public string? Set(string key, string value, int? exSeconds = null)
    {
        var cmd = exSeconds.HasValue ? $"SET {key} {value} EX {exSeconds}" : $"SET {key} {value}";
        return Send(cmd);
    }

    public string? Get(string key) => Send($"GET {key}");

    public string? Ping() => Send("PING");

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_receiveBuffer);
        _stream.Dispose();
        _tcp.Dispose();
    }
}
