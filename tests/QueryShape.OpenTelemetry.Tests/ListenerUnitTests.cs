using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using QueryShape.Core.Tests.TestModel;
using QueryShape.OpenTelemetry;

namespace QueryShape.OpenTelemetry.Tests;

public sealed class ListenerUnitTests : IDisposable
{
    private readonly SqliteShop _shop;

    public ListenerUnitTests()
    {
        _shop = new SqliteShop(configure: o => o.AddOpenTelemetry());
    }

    public void Dispose() => _shop.Dispose();

    [Fact]
    public async Task Database_style_span_gets_tags_not_events()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("test");

        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        using (var dbSpan = source.StartActivity("db", ActivityKind.Client))
        {
            dbSpan!.SetTag("db.system", "sqlite");
            await using var ctx = _shop.CreateContext();
            await ctx.Products.TagWith("lookup").CountAsync();

            dbSpan.GetTagItem("queryshape.fingerprint").Should().Be(scope.Commands[0].Fingerprint);
            dbSpan.GetTagItem("queryshape.source").Should().Be("Linq");
            dbSpan.GetTagItem("queryshape.shape").Should().Be("SELECT COUNT(*) FROM \"Products\" AS \"t0\"");
            dbSpan.GetTagItem("queryshape.tags").Should().Be("lookup");
            dbSpan.Events.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Provider_span_stopped_before_the_executed_interceptor_still_gets_the_tags_and_no_duplicate_event()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test-ef",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("test-ef");

        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        using var request = source.StartActivity("request", ActivityKind.Server);
        await using var ctx = _shop.CreateContext();
        using var instrumentation = new EfCoreInstrumentationSimulator(source, ctx);

        await ctx.Products.TagWith("lookup").CountAsync();

        var dbSpan = instrumentation.Spans.Should().ContainSingle().Subject;
        dbSpan.IsStopped.Should().BeTrue("the instrumentation stops its span on EF Core's CommandExecuted event, before QueryShape's Executed interceptor runs");
        dbSpan.GetTagItem("queryshape.fingerprint").Should().Be(scope.Commands.Single().Fingerprint);
        dbSpan.GetTagItem("queryshape.source").Should().Be("Linq");
        dbSpan.GetTagItem("queryshape.shape").Should().Be("SELECT COUNT(*) FROM \"Products\" AS \"t0\"");
        dbSpan.GetTagItem("queryshape.tags").Should().Be("lookup");
        ((string)dbSpan.GetTagItem("queryshape.callsite")!).Should().Contain("ListenerUnitTests.cs:");
        request!.Events.Should().BeEmpty("the command is on its own span; it must not be duplicated as a queryshape.query event on the request span");
        scope.Commands.Single().CallSiteOrigin.Should().Be(CallSiteOrigin.StackWalk, "the call site resolved at Executing is reused, not walked twice");
    }

    /// <summary>
    /// Does what OpenTelemetry.Instrumentation.EntityFrameworkCore does: one Client span per command, started on EF Core's CommandExecuting
    /// diagnostic event and stopped on CommandExecuted, which EF Core raises before it calls the Executed interceptor.
    /// </summary>
    private sealed class EfCoreInstrumentationSimulator : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly ActivitySource _source;
        private readonly DbContext _only;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly ConcurrentDictionary<Guid, Activity> _open = new();

        public EfCoreInstrumentationSimulator(ActivitySource source, DbContext only)
        {
            _source = source;
            _only = only;
            _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        }

        public List<Activity> Spans { get; } = [];

        void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
        {
            if (value.Name == "Microsoft.EntityFrameworkCore")
            {
                _subscriptions.Add(value.Subscribe(this));
            }
        }

        void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == RelationalEventId.CommandExecuting.Name && value.Value is CommandEventData starting && ReferenceEquals(starting.Context, _only))
            {
                var span = _source.StartActivity("db.command", ActivityKind.Client);
                if (span is null)
                {
                    return;
                }

                span.SetTag("db.system", "sqlite");
                _open[starting.CommandId] = span;
                Spans.Add(span);
            }
            else if (value.Key == RelationalEventId.CommandExecuted.Name && value.Value is CommandExecutedEventData executed && _open.TryRemove(executed.CommandId, out var span))
            {
                span.Stop();
            }
        }

        void IObserver<DiagnosticListener>.OnCompleted()
        {
        }

        void IObserver<DiagnosticListener>.OnError(Exception error)
        {
        }

        void IObserver<KeyValuePair<string, object?>>.OnCompleted()
        {
        }

        void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
        {
        }

        public void Dispose()
        {
            foreach (var s in _subscriptions)
            {
                s.Dispose();
            }
        }
    }

    [Fact]
    public async Task Cached_call_sites_from_sampling_are_not_exported()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test-sampling",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("test-sampling");

        var options = new QueryShapeOptions { CaptureCallSites = false, CallSiteSamplingInterval = 2 }.AddOpenTelemetry();
        using var scope = QueryShapeScope.Begin(options: options);
        using var span = source.StartActivity("request");
        await using var ctx = _shop.CreateContext(options);
        for (var i = 0; i < 3; i++)
        {
            var id = i;
            await ctx.Orders.Where(o => o.CustomerId == id && o.Total > -777).ToListAsync();
        }

        scope.Commands.Select(c => c.CallSiteOrigin).Should().Equal(CallSiteOrigin.Sampled, CallSiteOrigin.Cached, CallSiteOrigin.Sampled);
        var events = span!.Events.Where(e => e.Name == "queryshape.query").ToList();
        events.Should().HaveCount(3);
        events.Select(e => e.Tags.Any(t => t.Key == "queryshape.callsite")).Should().Equal(true, false, true);
    }

    [Fact]
    public async Task Unsampled_span_gets_only_cheap_tags()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test-unsampled",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("test-unsampled");

        using var scope = QueryShapeScope.Begin(options: _shop.Options);
        using var span = source.StartActivity("request");
        span!.IsAllDataRequested.Should().BeFalse();
        await using var ctx = _shop.CreateContext();
        await ctx.Products.CountAsync();

        span.GetTagItem("queryshape.fingerprint").Should().NotBeNull();
        span.GetTagItem("queryshape.shape").Should().BeNull();
        span.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task No_ambient_activity_creates_nothing_unless_opted_in()
    {
        var exported = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource("QueryShape").AddInMemoryExporter(exported).Build();

        Activity.Current.Should().BeNull();
        using (var scope = QueryShapeScope.Begin(options: _shop.Options))
        {
            await using var ctx = _shop.CreateContext();
            await ctx.Products.CountAsync();
        }

        provider.ForceFlush();
        exported.Should().BeEmpty("CreateActivitiesWhenNoneExist is off by default");

        var optIn = new QueryShapeOptions { CreateActivitiesWhenNoneExist = true }.AddOpenTelemetry();
        using (var scope = QueryShapeScope.Begin(options: optIn))
        {
            await using var ctx = _shop.CreateContext(optIn);
            await ctx.Products.ToListAsync();
        }

        provider.ForceFlush();
        exported.Select(a => a.DisplayName).Should().BeEquivalentTo("queryshape.query", "queryshape.scope scope");
        exported.Single(a => a.DisplayName == "queryshape.query").Kind.Should().Be(ActivityKind.Client);
        var scopeSpan = exported.Single(a => a.DisplayName.StartsWith("queryshape.scope"));
        scopeSpan.GetTagItem("queryshape.diagnosis_count").Should().Be(2, "QS004 unbounded + QS005 tracked read-only");
        scopeSpan.Events.Where(e => e.Name == "queryshape.diagnosis").Should().HaveCount(2);
    }

    [Fact]
    public async Task Metrics_are_emitted_for_queries_and_diagnoses()
    {
        var metrics = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder().AddMeter("QueryShape").AddInMemoryExporter(metrics).Build();

        using (var scope = QueryShapeScope.Begin(options: _shop.Options))
        {
            await using var ctx = _shop.CreateContext();
            await ctx.Products.ToListAsync();
            await ctx.Products.ToListAsync();
        }

        provider.ForceFlush();
        var names = metrics.Select(m => m.Name).ToList();
        names.Should().Contain(["queryshape.queries", "queryshape.diagnoses", "queryshape.query.duration"]);

        long Sum(Metric m)
        {
            long total = 0;
            foreach (ref readonly var p in m.GetMetricPoints())
            {
                total += p.GetSumLong();
            }

            return total;
        }

        Sum(metrics.Single(m => m.Name == "queryshape.queries")).Should().BeGreaterThanOrEqualTo(2);
        var diagnoses = metrics.Single(m => m.Name == "queryshape.diagnoses");
        var ruleIds = new List<string>();
        foreach (ref readonly var p in diagnoses.GetMetricPoints())
        {
            foreach (var tag in p.Tags)
            {
                if (tag.Key == "rule_id")
                {
                    ruleIds.Add((string)tag.Value!);
                }
            }
        }

        ruleIds.Should().Contain("QS004").And.Contain("QS008");
    }
}
