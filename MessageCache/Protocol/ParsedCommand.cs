namespace MessageCache.Protocol;

public ref struct ParsedCommand
{
    public CommandType Type;
    public ReadOnlySpan<byte> KeySpan;
    public ReadOnlySpan<byte> ValueSpan;
    public ReadOnlySpan<byte> Arg3Span; // это либо паттерн для команды KEYS, либо пароль для команды AUTH
    public int ExpirySeconds;
}
