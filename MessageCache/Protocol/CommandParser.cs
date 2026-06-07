using System.Buffers.Text;

namespace MessageCache.Protocol;

public static class CommandParser
{
    public static bool TryParse(ReadOnlySpan<byte> line, out ParsedCommand command)
    {
        command = default;

        // обрезка \r\n и whitespace
        while (!line.IsEmpty && (line[^1] == '\r' || line[^1] == '\n' || line[^1] == ' '))
            line = line[..^1];

        if (line.IsEmpty) return false;

        // ищем слово-действие
        int spaceIdx = line.IndexOf((byte)' ');
        ReadOnlySpan<byte> verb = spaceIdx < 0 ? line : line[..spaceIdx];
        ReadOnlySpan<byte> rest = spaceIdx < 0 ? default : line[(spaceIdx + 1)..];

        // быстрый перевод слов в uppercase
        Span<byte> verbUpper = stackalloc byte[verb.Length];
        for (int i = 0; i < verb.Length; i++)
            verbUpper[i] = (byte)(verb[i] & 0xDF);

        if (verbUpper.SequenceEqual("PING"u8))
        {
            command.Type = CommandType.Ping;
            return true;
        }

        if (verbUpper.SequenceEqual("STATS"u8))
        {
            command.Type = CommandType.Stats;
            return true;
        }

        if (verbUpper.SequenceEqual("KEYS"u8))
        {
            command.Type = CommandType.Keys;
            command.Arg3Span = rest.IsEmpty ? "*"u8 : rest;
            return true;
        }

        if (rest.IsEmpty) return false; // все команды ниже требуют аргумент, поэтому возвращаем false

        if (verbUpper.SequenceEqual("GET"u8))
        {
            command.Type = CommandType.Get;
            command.KeySpan = rest;
            return true;
        }

        if (verbUpper.SequenceEqual("DELETE"u8))
        {
            command.Type = CommandType.Delete;
            command.KeySpan = rest;
            return true;
        }

        if (verbUpper.SequenceEqual("TTL"u8))
        {
            command.Type = CommandType.Ttl;
            command.KeySpan = rest;
            return true;
        }

        if (verbUpper.SequenceEqual("WATCH"u8))
        {
            command.Type = CommandType.Watch;
            command.KeySpan = rest;
            return true;
        }

        if (verbUpper.SequenceEqual("UNWATCH"u8))
        {
            command.Type = CommandType.Unwatch;
            command.KeySpan = rest;
            return true;
        }

        if (verbUpper.SequenceEqual("AUTH"u8))
        {
            command.Type = CommandType.Auth;
            command.Arg3Span = rest;
            return true;
        }

        if (verbUpper.SequenceEqual("EXPIRE"u8))
        {
            int sp = rest.IndexOf((byte)' ');
            if (sp < 0) return false;
            command.Type = CommandType.Expire;
            command.KeySpan = rest[..sp];
            if (!Utf8Parser.TryParse(rest[(sp + 1)..], out int secs, out _)) return false;
            command.ExpirySeconds = secs;
            return true;
        }

        if (verbUpper.SequenceEqual("SET"u8))
        {
            int sp = rest.IndexOf((byte)' ');
            if (sp < 0) return false;

            command.Type = CommandType.Set;
            command.KeySpan = rest[..sp];
            ReadOnlySpan<byte> valueAndRest = rest[(sp + 1)..];

            // ищем суффикс " EX ", который отвечает за TTL ключа
            int exPos = FindExSuffix(valueAndRest);
            if (exPos >= 0)
            {
                command.ValueSpan = valueAndRest[..exPos];
                ReadOnlySpan<byte> expirySpan = valueAndRest[(exPos + 4)..]; // skip " EX "
                if (Utf8Parser.TryParse(expirySpan, out int expiry, out _))
                    command.ExpirySeconds = expiry;
            }
            else
            {
                command.ValueSpan = valueAndRest;
            }

            return true;
        }

        return false;
    }

    private static int FindExSuffix(ReadOnlySpan<byte> span)
    {
        // поиск паттерна " EX " или " ex "
        for (int i = 0; i <= span.Length - 4; i++)
        {
            if (span[i] == ' '
                && (span[i + 1] == 'E' || span[i + 1] == 'e')
                && (span[i + 2] == 'X' || span[i + 2] == 'x')
                && span[i + 3] == ' ')
            {
                return i;
            }
        }

        return -1;
    }
}
