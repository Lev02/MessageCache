using BenchmarkDotNet.Running;
using MessageCache.Benchmarks;

Console.WriteLine("MessageCache Benchmarks");
Console.WriteLine("========================");
Console.WriteLine();

BenchmarkSwitcher
    .FromAssembly(typeof(ParserBenchmarks).Assembly)
    .Run(args);
