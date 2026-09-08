using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace QueryShape.Benchmarks;

/// <summary>
/// The number CLAUDE.md section 9 asks for: p99 request latency under concurrent load, QueryShape on vs off, through the sample app
/// (in-process TestServer, in-memory SQLite). Not a BenchmarkDotNet job: BenchmarkDotNet measures one call at a time.
/// Run: <c>dotnet run -c Release --project tests/QueryShape.Benchmarks -f net10.0 -- --load [workers] [requestsPerWorker]</c>.
/// </summary>
public static class LoadMeasurement
{
    public static async Task RunAsync(int workers, int requestsPerWorker, TextWriter output)
    {
        output.WriteLine($"Load: {workers} concurrent workers x {requestsPerWorker} requests per worker per endpoint, in-process TestServer, SQLite file (WAL, pooled connections).");
        output.WriteLine("Each off/on pair is measured twice and the second pass is reported, so JIT and cache warm-up do not favour whichever mode runs second.");
        output.WriteLine();
        output.WriteLine("| Endpoint | Mode | p50 (µs) | p95 (µs) | p99 (µs) | p99 ratio |");
        output.WriteLine("|---|---|---:|---:|---:|---:|");

        foreach (var path in new[] { "/good/n-plus-one", "/bad/n-plus-one" })
        {
            Percentiles off = default!, on = default!;
            for (var pass = 0; pass < 2; pass++)
            {
                off = await MeasureAsync(path, enabled: false, workers, requestsPerWorker);
                on = await MeasureAsync(path, enabled: true, workers, requestsPerWorker);
            }

            output.WriteLine($"| `{path}` | off | {Row(off)} | 1.00 |");
            output.WriteLine($"| `{path}` | on | {Row(on)} | {(on.P99 / off.P99).ToString("0.00", CultureInfo.InvariantCulture)} |");
        }
    }

    private static string Row(Percentiles p)
        => string.Join(" | ", new[] { p.P50, p.P95, p.P99 }.Select(v => v.ToString("N0", CultureInfo.InvariantCulture)));

    private sealed record Percentiles(double P50, double P95, double P99);

    private static async Task<Percentiles> MeasureAsync(string path, bool enabled, int workers, int requestsPerWorker)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseContentRoot(AppContext.BaseDirectory); // the sample app needs no content files; the default content root is relative to the working directory
            b.UseEnvironment("Production");
            b.ConfigureLogging(l => l.ClearProviders());   // request logging would dominate the measurement and the output
            b.ConfigureServices(s => s.AddQueryShape(o =>
            {
                o.Enabled = enabled;
                o.CaptureCallSites = false;
            }));
        });
        using var client = factory.CreateClient();
        var uri = new Uri(path, UriKind.Relative);

        // Warm-up: JIT, EF Core compiled-query cache, SQLite page cache.
        for (var i = 0; i < 200; i++)
        {
            using var warm = await client.GetAsync(uri);
            warm.EnsureSuccessStatusCode();
        }

        var latencies = new double[workers * requestsPerWorker];
        var tasks = Enumerable.Range(0, workers).Select(async w =>
        {
            var offset = w * requestsPerWorker;
            for (var i = 0; i < requestsPerWorker; i++)
            {
                var start = Stopwatch.GetTimestamp();
                using var response = await client.GetAsync(uri);
                latencies[offset + i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000.0;
                response.EnsureSuccessStatusCode();
            }
        });
        await Task.WhenAll(tasks);

        Array.Sort(latencies);
        return new Percentiles(Percentile(latencies, 50), Percentile(latencies, 95), Percentile(latencies, 99));
    }

    private static double Percentile(double[] sorted, int percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
