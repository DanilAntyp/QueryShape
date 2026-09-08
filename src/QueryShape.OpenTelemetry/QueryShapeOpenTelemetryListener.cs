using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace QueryShape.OpenTelemetry;

/// <summary>
/// Attaches QueryShape data to the spans that already exist. Never creates a competing trace unless
/// <see cref="QueryShapeOptions.CreateActivitiesWhenNoneExist"/> is on and there is no ambient activity at all.
/// </summary>
public sealed class QueryShapeOpenTelemetryListener : IQueryShapeListener
{
    /// <summary>Attribute names.</summary>
    public static class Attributes
    {
        /// <summary>Command fingerprint.</summary>
        public const string Fingerprint = "queryshape.fingerprint";

        /// <summary>Normalized SQL, truncated.</summary>
        public const string Shape = "queryshape.shape";

        /// <summary><c>Linq</c>, <c>Raw</c>, <c>SaveChanges</c>, <c>Other</c>.</summary>
        public const string Source = "queryshape.source";

        /// <summary>First user frame, when known.</summary>
        public const string CallSite = "queryshape.callsite";

        /// <summary>Comma-separated <c>TagWith</c> tags.</summary>
        public const string Tags = "queryshape.tags";

        /// <summary>Rows returned by the command, when known (events only).</summary>
        public const string Rows = "queryshape.rows";

        /// <summary>Command duration in milliseconds (events only).</summary>
        public const string DurationMs = "queryshape.duration_ms";

        /// <summary>Number of diagnoses for the scope.</summary>
        public const string DiagnosisCount = "queryshape.diagnosis_count";

        /// <summary>Highest severity among the scope's diagnoses.</summary>
        public const string MaxSeverity = "queryshape.max_severity";

        /// <summary>Number of commands in the scope.</summary>
        public const string QueryCount = "queryshape.query_count";

        /// <summary>Rule id on a diagnosis event.</summary>
        public const string RuleId = "queryshape.rule_id";

        /// <summary>Severity on a diagnosis event.</summary>
        public const string Severity = "queryshape.severity";

        /// <summary>Title on a diagnosis event.</summary>
        public const string Title = "queryshape.title";

        /// <summary>Fix summary on a diagnosis event.</summary>
        public const string FixSummary = "queryshape.fix.summary";

        /// <summary>Docs URL on a diagnosis event.</summary>
        public const string DocsUrl = "queryshape.docs_url";

        /// <summary>Event name for one captured command (when the ambient span is not a database span).</summary>
        public const string QueryEvent = "queryshape.query";

        /// <summary>Event name for one diagnosis.</summary>
        public const string DiagnosisEvent = "queryshape.diagnosis";
    }

    private static readonly ActivitySource s_source = new(QueryShapeOpenTelemetryOptions.SourceName);
    private static readonly Meter s_meter = new(QueryShapeOpenTelemetryOptions.SourceName);
    private static readonly Counter<long> s_queries = s_meter.CreateCounter<long>("queryshape.queries", unit: "{query}", description: "Database commands captured by QueryShape.");
    private static readonly Counter<long> s_diagnoses = s_meter.CreateCounter<long>("queryshape.diagnoses", unit: "{diagnosis}", description: "Diagnoses produced by QueryShape rules.");
    private static readonly Histogram<double> s_duration = s_meter.CreateHistogram<double>("queryshape.query.duration", unit: "ms", description: "Command duration as reported by EF Core.");

    private readonly QueryShapeOpenTelemetryOptions _options;
    private readonly ConcurrentDictionary<Guid, byte> _taggedAtStart = new();
    private int _taggedAtStartCount;

    /// <summary>Creates the listener.</summary>
    public QueryShapeOpenTelemetryListener(QueryShapeOpenTelemetryOptions? options = null)
    {
        _options = options ?? new QueryShapeOpenTelemetryOptions();
    }

    /// <summary>The <c>ActivitySource</c> used when <see cref="QueryShapeOptions.CreateActivitiesWhenNoneExist"/> is on. Add it to your tracer provider: <c>.AddSource("QueryShape")</c>.</summary>
    public static ActivitySource ActivitySource => s_source;

    /// <summary>The meter. Add it to your meter provider: <c>.AddMeter("QueryShape")</c>.</summary>
    public static Meter Meter => s_meter;

    /// <summary>
    /// Marks the database provider's span while it is still current. EF Core stops instrumentation spans (its <c>CommandExecuted</c> event) before
    /// the <c>Executed</c> interceptor runs, so this is the only reliable moment to tag them; see ADR-0007.
    /// </summary>
    public void OnCommandExecuting(CommandStart start, QueryShapeScope? scope)
    {
        ArgumentNullException.ThrowIfNull(start);

        var activity = Activity.Current;
        if (activity is null || !IsDatabaseSpan(activity))
        {
            return; // a request/parent span gets one queryshape.query event per command once the command has finished
        }

        activity.SetTag(Attributes.Fingerprint, start.Fingerprint);
        activity.SetTag(Attributes.Source, start.Source.ToString());
        if (activity.IsAllDataRequested)
        {
            activity.SetTag(Attributes.Shape, Truncate(start.Shape));
            if (start.CallSite is not null && start.CallSiteOrigin != CallSiteOrigin.Cached)
            {
                activity.SetTag(Attributes.CallSite, start.CallSite.ToString());
            }

            if (start.Tags.Count > 0)
            {
                activity.SetTag(Attributes.Tags, string.Join(",", start.Tags));
            }
        }

        if (Interlocked.Increment(ref _taggedAtStartCount) > 10_000)
        {
            _taggedAtStart.Clear();
            Interlocked.Exchange(ref _taggedAtStartCount, 0);
        }

        _taggedAtStart[start.CommandId] = 0;
    }

    /// <inheritdoc />
    public void OnCommandCaptured(CapturedCommand command, QueryShapeScope? scope)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_options.EmitMetrics)
        {
            var sourceTag = new KeyValuePair<string, object?>("source", command.Source.ToString());
            if (_options.IncludeFingerprintInMetrics)
            {
                s_queries.Add(1, sourceTag, new KeyValuePair<string, object?>("fingerprint", command.Fingerprint));
            }
            else
            {
                s_queries.Add(1, sourceTag);
            }

            s_duration.Record(command.Duration.TotalMilliseconds, sourceTag);
        }

        if (_taggedAtStart.TryRemove(command.CommandId, out _))
        {
            Interlocked.Decrement(ref _taggedAtStartCount);
            return; // the provider's span carries the tags already (set while it was current); no duplicate event on whatever span is current now
        }

        var activity = Activity.Current;
        if (activity is null)
        {
            var createWhenNone = scope?.Options.CreateActivitiesWhenNoneExist ?? QueryShapeOptions.Default.CreateActivitiesWhenNoneExist;
            if (!createWhenNone || !s_source.HasListeners())
            {
                return;
            }

            var own = s_source.StartActivity("queryshape.query", ActivityKind.Client, parentContext: default, startTime: command.StartTime);
            if (own is null)
            {
                return;
            }

            SetCommandTags(own, command, full: true);
            own.SetEndTime(command.StartTime.UtcDateTime + command.Duration);
            own.Stop();
            return;
        }

        if (!activity.IsAllDataRequested)
        {
            // Not sampled: only the cheap tags.
            activity.SetTag(Attributes.Fingerprint, command.Fingerprint);
            activity.SetTag(Attributes.Source, command.Source.ToString());
            return;
        }

        if (IsDatabaseSpan(activity) || !_options.EmitQueryEvents)
        {
            SetCommandTags(activity, command, full: true);
        }
        else
        {
            activity.AddEvent(new ActivityEvent(Attributes.QueryEvent, command.StartTime, CommandAttributes(command)));
        }
    }

    /// <inheritdoc />
    public void OnScopeCompleted(QueryShapeScope scope, IReadOnlyList<Diagnosis> diagnoses)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(diagnoses);

        if (_options.EmitMetrics)
        {
            foreach (var d in diagnoses)
            {
                s_diagnoses.Add(1, new KeyValuePair<string, object?>("rule_id", d.RuleId), new KeyValuePair<string, object?>("severity", d.Severity.ToString()));
            }
        }

        var activity = Activity.Current;
        var ownActivity = false;
        if (activity is null)
        {
            if (!scope.Options.CreateActivitiesWhenNoneExist || !s_source.HasListeners())
            {
                return;
            }

            activity = s_source.StartActivity("queryshape.scope " + (scope.Name ?? "scope"), ActivityKind.Internal, parentContext: default, startTime: scope.StartedAt);
            if (activity is null)
            {
                return;
            }

            ownActivity = true;
        }

        if (activity.IsAllDataRequested)
        {
            activity.SetTag(Attributes.QueryCount, scope.CommandCount);
            activity.SetTag(Attributes.DiagnosisCount, diagnoses.Count);
            if (diagnoses.Count > 0)
            {
                activity.SetTag(Attributes.MaxSeverity, diagnoses.Max(d => d.Severity).ToString());
            }

            foreach (var d in diagnoses)
            {
                var tags = new ActivityTagsCollection
                {
                    { Attributes.RuleId, d.RuleId },
                    { Attributes.Severity, d.Severity.ToString() },
                    { Attributes.Title, d.Title },
                };
                if (d.CallSite is not null)
                {
                    tags.Add(Attributes.CallSite, d.CallSite.ToString());
                }

                if (d.SuggestedFix is { } fix)
                {
                    tags.Add(Attributes.FixSummary, fix.Summary);
                    tags.Add(Attributes.DocsUrl, fix.DocsUrl);
                }

                activity.AddEvent(new ActivityEvent(Attributes.DiagnosisEvent, DateTimeOffset.UtcNow, tags));
            }
        }

        if (ownActivity)
        {
            activity.SetEndTime(DateTime.UtcNow);
            activity.Stop();
        }
    }

    private static bool IsDatabaseSpan(Activity activity)
    {
        if (activity.Kind == ActivityKind.Client)
        {
            return true;
        }

        foreach (var tag in activity.TagObjects)
        {
            if (tag.Key is "db.system" or "db.system.name")
            {
                return true;
            }
        }

        return false;
    }

    private void SetCommandTags(Activity activity, CapturedCommand command, bool full)
    {
        activity.SetTag(Attributes.Fingerprint, command.Fingerprint);
        activity.SetTag(Attributes.Source, command.Source.ToString());
        if (!full)
        {
            return;
        }

        activity.SetTag(Attributes.Shape, Truncate(command.Shape));
        if (command.CallSite is not null && command.CallSiteOrigin != CallSiteOrigin.Cached)
        {
            activity.SetTag(Attributes.CallSite, command.CallSite.ToString());
        }

        if (command.Tags.Count > 0)
        {
            activity.SetTag(Attributes.Tags, string.Join(",", command.Tags));
        }
    }

    private ActivityTagsCollection CommandAttributes(CapturedCommand command)
    {
        var tags = new ActivityTagsCollection
        {
            { Attributes.Fingerprint, command.Fingerprint },
            { Attributes.Shape, Truncate(command.Shape) },
            { Attributes.Source, command.Source.ToString() },
            { Attributes.DurationMs, Math.Round(command.Duration.TotalMilliseconds, 3) },
        };
        if (command.CallSite is not null && command.CallSiteOrigin != CallSiteOrigin.Cached)
        {
            // Sampled call sites are exported for the sampled command only; cached ones would put the tag on every command at full rate.
            tags.Add(Attributes.CallSite, command.CallSite.ToString());
        }

        if (command.Tags.Count > 0)
        {
            tags.Add(Attributes.Tags, string.Join(",", command.Tags));
        }

        if (command.RowsReturned is { } rows)
        {
            tags.Add(Attributes.Rows, rows);
        }

        return tags;
    }

    private string Truncate(string shape)
        => shape.Length <= _options.MaxShapeLength ? shape : shape[..Math.Max(0, _options.MaxShapeLength - 3)] + "...";

    internal static string Ms(TimeSpan t) => t.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
}
