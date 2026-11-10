using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Eden.Protocol.Tests.Benchmarks;

internal static class Program
{
    private static void Main(string[] args) =>
        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, DefaultConfig.Instance.WithArtifactsPath("artifacts/library-benchmarks"));
}
