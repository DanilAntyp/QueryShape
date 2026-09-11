using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using Jellyfin.Database.Implementations;
using Jellyfin.Server.Integration.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using QueryShape;
using QueryShape.AspNetCore;
using QueryShape.OpenTelemetry;
using QueryShape.Reporting;
using Xunit;

/// <summary>
/// Drives Jellyfin's real HTTP API in memory, through its own integration-test host, with QueryShape attached the way a
/// consumer attaches it: capture on the context Jellyfin registered, a scope per request, and the OpenTelemetry listener.
/// The repository tests next door call the data layer directly; these go through routing, auth and the controllers.
/// </summary>
[Collection("JellyfinApi")]
public sealed class ApiTests(JellyfinApiFixture fixture)
{
    [Theory]
    [InlineData("/System/Info/Public")]
    [InlineData("/Users/Me")]
    [InlineData("/Items?limit=20&fields=ProviderIds,Overview&enableImages=true")]
    public async Task An_api_request_is_captured_as_one_scope(string path)
    {
        fixture.Recorder.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.AddAuthHeader(fixture.AccessToken);

        using var response = await fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // The middleware names the scope after the route template, which is what reports and telemetry group by.
        var scopes = fixture.Recorder.ToList();
        Assert.NotEmpty(scopes);
        Assert.All(scopes, s => Assert.StartsWith("GET /", s.Name));
        var scope = scopes[^1];
        Assert.True(response.Headers.Contains("X-QueryShape"), "the middleware adds its header to a real response");

        fixture.Output($"{path} -> scope \"{scope.Name}\", {scope.Commands} commands, header: {string.Join(string.Empty, response.Headers.GetValues("X-QueryShape"))}");
        foreach (var line in scope.Report.Queries)
        {
            fixture.Output($"  ×{line.Count} {line.Fingerprint} rows={line.RowsReturned?.ToString() ?? "-"} {line.CallSite ?? "call site unknown"}");
        }

        if (scope.Diagnoses.Count > 0)
        {
            fixture.Output(DiagnosisFormatter.Summarize(scope.Diagnoses, "  "));
        }
    }

    [Fact]
    public async Task A_library_request_reports_its_queries_and_their_findings()
    {
        fixture.Recorder.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Items?limit=20&fields=ProviderIds,Overview&enableImages=true&enableUserData=true");
        request.Headers.AddAuthHeader(fixture.AccessToken);
        using var response = await fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var scope = Assert.Single(fixture.Recorder);
        Assert.True(scope.Commands > 0, "the request queried the database");
        Assert.All(scope.Report.Queries, q => Assert.NotNull(q.CallSite));

        // Jellyfin's data layer is synchronous throughout; inside an async request every such call blocks a pooled thread (QS012).
        var blocking = scope.Diagnoses.Where(d => d.RuleId == "QS012").ToList();
        Assert.NotEmpty(blocking);
        Assert.All(blocking, d => Assert.Equal(Severity.Warning, d.Severity));
        Assert.All(blocking, d => Assert.NotNull(d.CallSite));

        // And the authentication path itself joins four of the user's collections into one statement.
        Assert.Contains(scope.Diagnoses, d => d.RuleId == "QS002" && d.CallSite!.Member.Contains("UserManager", StringComparison.Ordinal));

        fixture.Output(DiagnosisFormatter.Summarize(scope.Diagnoses, "  ") + "\n" + DiagnosisFormatter.Format(scope.Diagnoses, "  "));
    }

    [Fact]
    public async Task The_request_span_carries_the_queryshape_attributes_and_diagnosis_events()
    {
        fixture.Recorder.Clear();
        fixture.Spans.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Items?limit=20&fields=ProviderIds&enableImages=true&enableUserData=true");
        request.Headers.AddAuthHeader(fixture.AccessToken);
        using var response = await fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        fixture.TracerProvider.ForceFlush(2000);
        var spans = fixture.Spans.ToList();
        Assert.NotEmpty(spans);
        fixture.Output($"spans: {string.Join(", ", spans.Select(s => s.DisplayName))}");

        // The request span is the one every APM already shows, so the scope's verdict goes there.
        // TagObjects, not Tags: the counts are integers and Activity.Tags only exposes string values.
        var requestSpan = Assert.Single(spans, s => s.TagObjects.Any(t => t.Key == "queryshape.diagnosis_count"));
        Assert.Equal("Microsoft.AspNetCore.Hosting.HttpRequestIn", requestSpan.DisplayName);
        Assert.NotNull(requestSpan.TagObjects.Single(t => t.Key == "queryshape.max_severity").Value);
        Assert.NotEmpty(requestSpan.Events.Where(e => e.Name == "queryshape.diagnosis"));

        var diagnosis = requestSpan.Events.First(e => e.Name == "queryshape.diagnosis");
        Assert.Contains(diagnosis.Tags, t => t.Key == "queryshape.rule_id");
        Assert.Contains(diagnosis.Tags, t => t.Key == "queryshape.docs_url");

        // Microsoft.Data.Sqlite emits no database span, so each command is an event on the request span rather than
        // tags overwriting one another — ADR-0007, and the reason nothing here invents a competing trace.
        var queryEvents = requestSpan.Events.Where(e => e.Name == "queryshape.query").ToList();
        Assert.NotEmpty(queryEvents);
        Assert.Contains(queryEvents[0].Tags, t => t.Key == "queryshape.fingerprint");
        Assert.DoesNotContain(spans, s => s.TagObjects.Any(t => t.Key == "queryshape.fingerprint"));

        fixture.Output($"  request span: {string.Join(", ", requestSpan.TagObjects.Where(t => t.Key.StartsWith("queryshape.", StringComparison.Ordinal)).Select(t => t.Key + "=" + t.Value))}");
        fixture.Output($"  events: {queryEvents.Count} × queryshape.query, {requestSpan.Events.Count(e => e.Name == "queryshape.diagnosis")} × queryshape.diagnosis");
    }
}

/// <summary>One Jellyfin server for the class: booting it runs migrations and startup tasks, which is slow.</summary>
public sealed class JellyfinApiFixture : IAsyncLifetime
{
    private readonly QueryShapeJellyfinFactory _factory = new();

    public HttpClient Client { get; private set; } = null!;

    public string AccessToken { get; private set; } = string.Empty;

    public ScopeRecorder Recorder => _factory.Recorder;

    public List<System.Diagnostics.Activity> Spans => _factory.Spans;

    public TracerProvider TracerProvider => _factory.TracerProvider!;

    public async ValueTask InitializeAsync()
    {
        Client = _factory.CreateClient();
        AccessToken = await AuthHelper.CompleteStartupAsync(Client);
        Recorder.Clear();
    }

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Writes to whichever test is running: a collection fixture has no output helper of its own.</summary>
    public void Output(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
}

[CollectionDefinition("JellyfinApi")]
public sealed class JellyfinApiCollection : ICollectionFixture<JellyfinApiFixture>;

/// <summary>
/// Jellyfin's own test host plus the three lines a consumer writes: AddQueryShape, capture on the context Jellyfin
/// registered (EF Core 10's ConfigureDbContext reaches a registration you do not own), and UseQueryShape in the pipeline.
/// </summary>
public sealed class QueryShapeJellyfinFactory : JellyfinApplicationFactory
{
    public ScopeRecorder Recorder { get; } = new();

    public List<System.Diagnostics.Activity> Spans { get; } = [];

    public TracerProvider? TracerProvider { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var options = new QueryShapeOptions { CaptureCallSites = true, ReadSourceFiles = false, CallPathDepth = 3 };
        options.Listeners.Add(Recorder);
        options.AddOpenTelemetry();

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(options);
            services.ConfigureDbContext<JellyfinDbContext>(o => o.UseQueryShape(options));
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new UseQueryShapeFilter());
        });

        TracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("*")
            .AddInMemoryExporter(Spans)
            .Build();
    }

    protected override void Dispose(bool disposing)
    {
        TracerProvider?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Inserts <c>app.UseQueryShape()</c> into a pipeline defined by someone else's Startup.</summary>
    private sealed class UseQueryShapeFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.UseQueryShape(o => o.EmitResponseHeader = true);
                next(app);
            };
    }
}

/// <summary>Records every completed request scope so a test can read what the middleware saw.</summary>
public sealed class ScopeRecorder : IQueryShapeListener, IEnumerable<ScopeRecorder.Recorded>
{
    private readonly ConcurrentQueue<Recorded> _scopes = new();

    public void OnCommandCaptured(CapturedCommand command, QueryShapeScope? scope)
    {
        // Recording happens when the scope completes; nothing to do per command.
    }

    public void OnScopeCompleted(QueryShapeScope scope, IReadOnlyList<Diagnosis> diagnoses)
        => _scopes.Enqueue(new Recorded(scope.Name, scope.CommandCount, diagnoses, ScopeReport.FromScope(scope, diagnoses)));

    public void Clear() => _scopes.Clear();

    public System.Collections.Generic.IEnumerator<Recorded> GetEnumerator() => _scopes.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public sealed record Recorded(string? Name, int Commands, IReadOnlyList<Diagnosis> Diagnoses, ScopeReport Report);
}
