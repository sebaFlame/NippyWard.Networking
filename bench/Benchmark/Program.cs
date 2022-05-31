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
            => BenchmarkRunner.Run
            (
                typeof(Program).Assembly,
#if DEBUG
                new DebugBuildConfig()
#else
                DefaultConfig.Instance
#endif
                //uncomment do start event tracing    
                //.AddDiagnoser(new EtwProfiler())
                , args
            );
    }
}