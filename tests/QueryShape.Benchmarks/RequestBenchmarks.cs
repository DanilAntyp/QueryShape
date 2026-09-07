using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace QueryShape.Benchmarks;

/// <summary>
/// End-to-end HTTP requests through the sample app (in-process TestServer, in-memory SQLite): the number that matters for the
/// "&lt; 3% p99" budget. QueryShape on = interceptor + middleware + rules at scope end; off = the Enabled kill switch.
/// </summary>
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 5, iterationCount: 30)]
[MemoryDiagnoser]
public class RequestBenchmarks
{
    private WebApplicationFactory<Program> _off = null!;
    private WebApplicationFactory<Program> _on = null!;
    private HttpClient _offClient = null!;
    private HttpClient _onClient = null!;

    [Params("/good/n-plus-one", "/bad/n-plus-one")]
    public string Path { get; set; } = "/good/n-plus-one";

    [GlobalSetup]
    public void Setup()
    {
        _off = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureServices(s => s.AddQueryShape(o => o.Enabled = false));
        });
        _on = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureServices(s => s.AddQueryShape(o => o.CaptureCallSites = false));
        });
        _offClient = _off.CreateClient();
        _onClient = _on.CreateClient();
        _offClient.GetAsync(new Uri(Path, UriKind.Relative)).GetAwaiter().GetResult().EnsureSuccessStatusCode();
        _onClient.GetAsync(new Uri(Path, UriKind.Relative)).GetAwaiter().GetResult().EnsureSuccessStatusCode();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _offClient.Dispose();
        _onClient.Dispose();
        _off.Dispose();
        _on.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task<long> QueryShape_Off()
    {
        using var response = await _offClient.GetAsync(new Uri(Path, UriKind.Relative));
        return response.Content.Headers.ContentLength ?? 0;
    }

    [Benchmark]
    public async Task<long> QueryShape_On()
    {
        using var response = await _onClient.GetAsync(new Uri(Path, UriKind.Relative));
        return response.Content.Headers.ContentLength ?? 0;
    }
}
