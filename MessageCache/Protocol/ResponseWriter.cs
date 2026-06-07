using System.Buffers.Text;
using System.Text;

namespace MessageCache.Protocol;

/// <summary>
/// Формирует ответы клиентам по протоколу
/// </summary>
public static class ResponseWriter
{
    // небольшая оптимизация - создаём некоторые виды ответов заранее
    public static readonly byte[] OkBytes = "+OK\r\n"u8.ToArray();
    public static readonly byte[] PongBytes = "+PONG\r\n"u8.ToArray();
    public static readonly byte[] NilBytes = "$NIL\r\n"u8.ToArray();
    public static readonly byte[] NoAuthBytes = "-ERR NOAUTH Authentication required\r\n"u8.ToArray();
    public static readonly byte[] InvalidAuthBytes = "-ERR invalid password\r\n"u8.ToArray();

    public static int WriteError(Span<byte> buffer, string message)
    {
        int pos = 0;
        buffer[pos++] = (byte)'-';
        pos += Encoding.UTF8.GetBytes(message, buffer[pos..]);
        buffer[pos++] = (byte)'\r';
        buffer[pos++] = (byte)'\n';
        return pos;
    }

    public static int WriteBulkString(Span<byte> buffer, string value)
    {
        int pos = 0;
        buffer[pos++] = (byte)'$';
        int valueLen = Encoding.UTF8.GetByteCount(value);
        Utf8Formatter.TryFormat(valueLen, buffer[pos..], out int numWritten);
        pos += numWritten;
        buffer[pos++] = (byte)'\r';
        buffer[pos++] = (byte)'\n';
        pos += Encoding.UTF8.GetBytes(value, buffer[pos..]);
        buffer[pos++] = (byte)'\r';
        buffer[pos++] = (byte)'\n';
        return pos;
    }

    public static int WriteInteger(Span<byte> buffer, long value)
    {
        int pos = 0;
        buffer[pos++] = (byte)':';
        Utf8Formatter.TryFormat(value, buffer[pos..], out int written);
        pos += written;
        buffer[pos++] = (byte)'\r';
        buffer[pos++] = (byte)'\n';
        return pos;
    }

    public static int WriteSimpleString(Span<byte> buffer, string message)
    {
        int pos = 0;
        buffer[pos++] = (byte)'+';
        pos += Encoding.UTF8.GetBytes(message, buffer[pos..]);
        buffer[pos++] = (byte)'\r';
        buffer[pos++] = (byte)'\n';
        return pos;
    }

    public static int WriteArray(Span<byte> buffer, string[] items)
    {
        int pos = 0;
        buffer[pos++] = (byte)'*';
        Utf8Formatter.TryFormat(items.Length, buffer[pos..], out int numWritten);
        pos += numWritten;
        buffer[pos++] = (byte)'\r';
        buffer[pos++] = (byte)'\n';

        foreach (var item in items)
        {
            pos += WriteBulkString(buffer[pos..], item);
        }

        return pos;
    }

    public static int WriteNotify(Span<byte> buffer, string key, string? value)
    {
        int pos = 0;
        "!NOTIFY "u8.CopyTo(buffer[pos..]);
        pos += 8;
        pos += Encoding.UTF8.GetBytes(key, buffer[pos..]);
        buffer[pos++] = (byte)' ';
        if (value is not null)
            pos += Encoding.UTF8.GetBytes(value, buffer[pos..]);
        else
            "(deleted)"u8.CopyTo(buffer[pos..]);
        pos += value is null ? 9 : 0;
        buffer[pos++] = (byte)'\r';
        buffer[pos++] = (byte)'\n';
        return pos;
    }

    public static int EstimateArraySize(string[] items)
    {
        int size = 16;
        foreach (var item in items)
            size += 16 + Encoding.UTF8.GetByteCount(item);
        return size;
    }
}
