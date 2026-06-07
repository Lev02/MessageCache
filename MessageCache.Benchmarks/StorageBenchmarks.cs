using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MessageCache.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageCache.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[MinColumn, MaxColumn, MeanColumn, MedianColumn]
[ThreadingDiagnoser]
public class StorageBenchmarks
{
    private CacheStorage _storage = null!;
    private string[] _keys = null!;

    [Params(1000)]
    public int KeyCount;

    [Params(1, 4, 8)]
    public int ThreadCount;

    [GlobalSetup]
    public void Setup()
    {
        _storage = new CacheStorage(NullLogger<CacheStorage>.Instance);
        _keys = Enumerable.Range(0, KeyCount).Select(i => $"key:{i}").ToArray();

        foreach (var key in _keys)
            _storage.Set(key, "benchmark-value");
    }

    [GlobalCleanup]
    public void Cleanup() => _storage.Dispose();

    [Benchmark(Baseline = true)]
    public string? SingleThreadGet()
    {
        _storage.TryGet(_keys[0], out var v);
        return v;
    }

    [Benchmark]
    public void SingleThreadSet()
    {
        _storage.Set(_keys[0], "new-value");
    }

    [Benchmark]
    public void SingleThreadDelete()
    {
        _storage.Set(_keys[0], "temp");
        _storage.Delete(_keys[0]);
        _storage.Set(_keys[0], "benchmark-value"); // restore
    }

    [Benchmark]
    public void SetWithExpiry()
    {
        _storage.Set(_keys[0], "expiring-value", expirySeconds: 60);
    }

    [Benchmark]
    public int GetAllKeys()
    {
        return _storage.GetKeys("*").Length;
    }

    [Benchmark]
    public int ConcurrentReads()
    {
        int total = 0;
        Parallel.For(0, ThreadCount, _ =>
        {
            for (int i = 0; i < KeyCount / ThreadCount; i++)
            {
                if (_storage.TryGet(_keys[i % KeyCount], out string? _))
                    Interlocked.Increment(ref total);
            }
        });
        return total;
    }

    [Benchmark]
    public void ConcurrentMixedReadWrite()
    {
        // тут идет симуляции продакшена - 80% чтений и 20% записей
        Parallel.For(0, ThreadCount, t =>
        {
            for (int i = 0; i < 100; i++)
            {
                int idx = (t * 100 + i) % KeyCount;
                if (i % 5 == 0)
                    _storage.Set(_keys[idx], "updated");
                else
                    _storage.TryGet(_keys[idx], out string? _);
            }
        });
    }
}
