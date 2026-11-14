using BenchmarkDotNet.Running;

namespace DataBridge.Benchmarks;

internal static class Program
{
    // Select benchmarks with --filter, for example: --filter "*KeyMerge*".
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
