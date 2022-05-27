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
            => BenchmarkRunner.Run //<HandshakeBenchmark>
            (
                typeof(Program).Assembly,
#if DEBUG
                new DebugBuildConfig()
#else
                DefaultConfig.Instance
#endif
                    .AddDiagnoser(MemoryDiagnoser.Default)
                    //.AddDiagnoser(new EtwProfiler())
                , args
            );
    }
}