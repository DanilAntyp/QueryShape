using BenchmarkDotNet.Running;

namespace QueryShape.Benchmarks;

/// <summary>Entry point (an explicit class so the sample app's global <c>Program</c> stays unambiguous).</summary>
public static class Entry
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Entry).Assembly).Run(args);
}
