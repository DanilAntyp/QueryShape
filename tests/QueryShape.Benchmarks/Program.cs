using System.Globalization;
using BenchmarkDotNet.Running;

namespace QueryShape.Benchmarks;

/// <summary>Entry point (an explicit class so the sample app's global <c>Program</c> stays unambiguous).</summary>
public static class Entry
{
    public static async Task<int> Main(string[] args)
    {
        // --load [workers] [requestsPerWorker]: p99 under concurrency (CLAUDE.md section 9); everything else goes to BenchmarkDotNet.
        if (args.Length > 0 && args[0] == "--load")
        {
            var workers = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 8;
            var requests = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 2000;
            await LoadMeasurement.RunAsync(workers, requests, Console.Out);
            return 0;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Entry).Assembly).Run(args);
        return 0;
    }
}
