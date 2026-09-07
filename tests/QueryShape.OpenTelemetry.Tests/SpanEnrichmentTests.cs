using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using QueryShape.OpenTelemetry;
using QueryShape.SampleApp.Tests;

namespace QueryShape.OpenTelemetry.Tests;

/// <summary>Spans exported for sample-app requests must carry queryshape.* tags and queryshape.diagnosis events with exact attributes.</summary>
public sealed class SpanEnrichmentTests : IDisposable
{
    private readonly List<Activity> _exported = [];
    private readonly TracerProvider _tracerProvider;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SpanEnrichmentTests()
    {
        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("Microsoft.AspNetCore")        // request spans, built into ASP.NET Core 8+
            .AddSource(QueryShapeOpenTelemetryOptions.SourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddInMemoryExporter(_exported)
            .Build();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureServices(services =>
            {
                services.AddQueryShape(o => o.CaptureCallSites = true);
                services.AddQueryShapeOpenTelemetry();
            });
        });
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _tracerProvider.Dispose();
    }

    private async Task<Activity> RequestSpanAsync(string path)
    {
        var response = await _client.GetAsync(new Uri(path, UriKind.Relative));
        response.EnsureSuccessStatusCode();

        // The hosting span stops after the response is handed to the client, so poll briefly for the export.
        // The built-in hosting span (ActivitySource "Microsoft.AspNetCore") carries few tags of its own; ours mark it.
        List<Activity> candidates = [];
        for (var attempt = 0; attempt < 50 && candidates.Count == 0; attempt++)
        {
            _tracerProvider.ForceFlush();
            lock (_exported)
            {
                candidates = _exported.Where(a => a.GetTagItem(QueryShapeOpenTelemetryListener.Attributes.QueryCount) is not null).ToList();
            }

            if (candidates.Count == 0)
            {
                await Task.Delay(50);
            }
        }

        candidates.Should().NotBeEmpty($"the request span for {path} should have been exported; exported: {string.Join(", ", _exported.Select(a => a.DisplayName))}");
        return candidates[^1];
    }

    [RuntimeMatchedFact]
    public async Task N_plus_one_request_span_has_diagnosis_events_and_query_events()
    {
        var span = await RequestSpanAsync("/bad/n-plus-one");

        span.GetTagItem(QueryShapeOpenTelemetryListener.Attributes.QueryCount).Should().Be(41);
        span.GetTagItem(QueryShapeOpenTelemetryListener.Attributes.DiagnosisCount).Should().Be(4, "QS001, QS004 and QS005 for both the Customer and the Order query");
        span.GetTagItem(QueryShapeOpenTelemetryListener.Attributes.MaxSeverity).Should().Be("Error");

        var events = span.Events.ToList();
        events.Count(e => e.Name == "queryshape.query").Should().Be(41, "SQLite emits no DB spans, so each command becomes an event on the request span");
        var query = events.First(e => e.Name == "queryshape.query");
        var attrs = query.Tags.ToDictionary(t => t.Key, t => t.Value);
        attrs.Keys.Should().Contain(["queryshape.fingerprint", "queryshape.shape", "queryshape.source", "queryshape.duration_ms", "queryshape.callsite"]);
        attrs["queryshape.source"].Should().Be("Linq");
        ((string)attrs["queryshape.shape"]!).Should().StartWith("SELECT \"t0\".\"Id\", \"t0\".\"Country\", \"t0\".\"Name\" FROM \"Customers\" AS \"t0\"");
        ((string)attrs["queryshape.callsite"]!).Should().StartWith("BadEndpoints.cs:");

        var diagnoses = events.Where(e => e.Name == "queryshape.diagnosis").ToList();
        diagnoses.Should().HaveCount(4);
        var n1 = diagnoses.Select(e => e.Tags.ToDictionary(t => t.Key, t => t.Value)).Single(d => (string)d["queryshape.rule_id"]! == "QS001");
        n1["queryshape.severity"].Should().Be("Error");
        ((string)n1["queryshape.title"]!).Should().StartWith("N+1 query: Order by CustomerId executed 40 times");
        ((string)n1["queryshape.callsite"]!).Should().StartWith("BadEndpoints.cs:");
        ((string)n1["queryshape.fix.summary"]!).Should().StartWith("Add .Include(c => c.Orders) to the Customer query");
        n1["queryshape.docs_url"].Should().Be("https://github.com/queryshape/QueryShape/blob/main/docs/rules/QS001.md");
    }

    [RuntimeMatchedTheory]
    [InlineData("/bad/client-evaluation", "QS003")]
    [InlineData("/bad/unbounded", "QS004")]
    [InlineData("/bad/duplicate-query", "QS008")]
    public async Task Each_bad_endpoint_span_carries_its_rule(string path, string ruleId)
    {
        var span = await RequestSpanAsync(path);
        var ruleIds = span.Events.Where(e => e.Name == "queryshape.diagnosis").Select(e => (string)e.Tags.Single(t => t.Key == "queryshape.rule_id").Value!).ToList();
        ruleIds.Should().Contain(ruleId);
        ((int)span.GetTagItem("queryshape.diagnosis_count")!).Should().Be(ruleIds.Count);
    }

    [RuntimeMatchedFact]
    public async Task Clean_request_has_zero_diagnoses_and_no_max_severity()
    {
        var span = await RequestSpanAsync("/good/duplicate-query");
        span.GetTagItem("queryshape.diagnosis_count").Should().Be(0);
        span.GetTagItem("queryshape.max_severity").Should().BeNull();
        span.Events.Should().Contain(e => e.Name == "queryshape.query");
    }
}
