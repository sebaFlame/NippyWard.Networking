using System;

using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Diagnostics.Windows;

namespace Benchmark
{
    public static class Program
    {
        public static void Main(string[] args)
            => BenchmarkRunner.Run //<TransferBenchmark>
            (
                typeof(Program).Assembly,
#if DEBUG
                new DebugInProcessConfig()
#else
                DefaultConfig.Instance
#endif
                    .AddDiagnoser(MemoryDiagnoser.Default)
                    //.AddDiagnoser(new EtwProfiler())
                , args
            );
    }
}