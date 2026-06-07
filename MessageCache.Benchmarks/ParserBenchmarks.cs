using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MessageCache.Protocol;

namespace MessageCache.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
public class ParserBenchmarks
{
    private byte[] _setCommand = null!;
    private byte[] _setWithExpiry = null!;
    private byte[] _getCommand = null!;
    private byte[] _deleteCommand = null!;
    private byte[] _pingCommand = null!;
    private byte[] _keysCommand = null!;
    
    [GlobalSetup]
    public void Setup()
    {
        _setCommand = Encoding.UTF8.GetBytes("SET mykey hello-world\r\n");
        _setWithExpiry = Encoding.UTF8.GetBytes("SET session:abc token123 EX 3600\r\n");
        _getCommand = Encoding.UTF8.GetBytes("GET mykey\r\n");
        _deleteCommand = Encoding.UTF8.GetBytes("DELETE mykey\r\n");
        _pingCommand = Encoding.UTF8.GetBytes("PING\r\n");
        _keysCommand = Encoding.UTF8.GetBytes("KEYS user:*\r\n");
    }

    [Benchmark(Baseline = true)]
    public bool ParsePing()
    {
        return CommandParser.TryParse(_pingCommand, out _);
    }

    [Benchmark]
    public bool ParseGet()
    {
        return CommandParser.TryParse(_getCommand, out _);
    }

    [Benchmark]
    public bool ParseSet()
    {
        return CommandParser.TryParse(_setCommand, out _);
    }

    [Benchmark]
    public bool ParseSetWithExpiry()
    {
        return CommandParser.TryParse(_setWithExpiry, out _);
    }

    [Benchmark]
    public bool ParseDelete()
    {
        return CommandParser.TryParse(_deleteCommand, out _);
    }

    [Benchmark]
    public bool ParseKeys()
    {
        return CommandParser.TryParse(_keysCommand, out _);
    }

    [Benchmark]
    public int ParseMixedBatch()
    {
        int count = 0;
        for (int i = 0; i < 1000; i++)
        {
            var cmd = (i % 4) switch
            {
                0 => _setCommand.AsSpan(),
                1 => _getCommand.AsSpan(),
                2 => _deleteCommand.AsSpan(),
                _ => _pingCommand.AsSpan(),
            };
            if (CommandParser.TryParse(cmd, out _)) 
                count++;
        }
        return count;
    }
}
