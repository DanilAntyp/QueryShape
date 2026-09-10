using Microsoft.Extensions.Logging;
using QueryShape.Rules;

namespace QueryShape;

/// <summary>All knobs. One instance configures both capture (on the interceptor) and analysis (on scopes).</summary>
public sealed class QueryShapeOptions
{
    /// <summary>Process-wide defaults used when no options are supplied. Mutating this instance affects every scope begun without explicit options.</summary>
    public static QueryShapeOptions Default { get; } = new();

    /// <summary>Master switch. When <c>false</c> the interceptor and middleware do nothing beyond a boolean check. Default on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Same shape executed at least this many times in one scope with varying parameters is an N+1 (QS001). Default 5.</summary>
    public int NPlusOneThreshold { get; set; } = 5;

    /// <summary>A query returning more rows than this is unbounded (QS004). Default 1000.</summary>
    public int UnboundedRowThreshold { get; set; } = 1000;

    /// <summary>QS004 reports a query with no filter and no limit at Info instead of Warning when it returned fewer rows than this: small lookup tables are loaded whole on purpose. Default 20.</summary>
    public int UnboundedMinimumRows { get; set; } = 20;

    /// <summary><c>Contains</c> over a collection with more elements than this is flagged (QS007). Default 500.</summary>
    public int ContainsCollectionThreshold { get; set; } = 500;

    /// <summary>A single-query include result with at least this many rows per distinct root entity is a Cartesian explosion (QS002). Default 10.</summary>
    public int CartesianExplosionFactor { get; set; } = 10;

    /// <summary>QS002/QS006 ignore include results smaller than this many rows: tiny result sets multiply harmlessly. Default 50.</summary>
    public int CartesianMinimumRows { get; set; } = 50;

    /// <summary>After this many commands a scope stops recording and reports <c>QS_OVERFLOW</c>. Default 10 000.</summary>
    public int MaxCommandsPerScope { get; set; } = 10_000;

    /// <summary>Commands captured outside any scope are kept in a ring buffer of this size. Default 256.</summary>
    public int UnscopedBufferSize { get; set; } = 256;

    /// <summary>
    /// Walk the stack on every command to find the user frame. Expensive, so the default depends on the process: on when a test framework
    /// (xUnit, NUnit, MSTest, TUnit) is loaded, off otherwise. Production can use EF Core's <c>TagWithCallSite()</c> or <see cref="CallSiteSamplingInterval"/> instead.
    /// </summary>
    public bool CaptureCallSites { get; set; } = Internal.TestEnvironment.IsTestProcess;

    /// <summary>
    /// How many user-code frames to record per command when the stack is walked: 1 keeps only the call site, more record the path it was reached
    /// through (innermost first), which is what tells you which endpoint or handler asked for the query. Clamped to 1..8. Default 3.
    /// The frames come out of the walk that already happens for the call site, so a deeper path costs no extra walk.
    /// </summary>
    public int CallPathDepth { get; set; } = 3;

    /// <summary>
    /// Assembly-name or namespace prefixes of code that issues queries on behalf of its callers: repositories, specification evaluators, a data-access
    /// library of your own. Frames matching one of these are still recorded in the path, but the query is attributed to the first caller above them,
    /// because that is where the decision to ask for this data was made. Empty by default, which blames the innermost user frame.
    /// </summary>
    public IList<string> InfrastructurePrefixes { get; } = new List<string>();

    /// <summary>
    /// Let rules read source files to produce real unified diffs (and the CLI's LLM prompts). <c>null</c> (default) follows <see cref="CaptureCallSites"/>:
    /// on in tests, off in production, where QueryShape must never touch the file system on the request path. Set <c>true</c>/<c>false</c> to decide explicitly.
    /// </summary>
    public bool? ReadSourceFiles { get; set; }

    /// <summary>Effective value of <see cref="ReadSourceFiles"/>.</summary>
    internal bool ShouldReadSourceFiles => ReadSourceFiles ?? CaptureCallSites;

    /// <summary>
    /// Production alternative to <see cref="CaptureCallSites"/>: walk the stack for the first execution of every query shape and then for one in every
    /// N executions of that shape (0 = off, default). Commands in between reuse the last sampled call site (<see cref="CallSiteOrigin.Cached"/>),
    /// which rules use but the OpenTelemetry listener does not export. Costs ≈ 15 µs per sampled command; nothing otherwise.
    /// </summary>
    public int CallSiteSamplingInterval { get; set; }

    /// <summary>Keep parameter values on captured commands. PII risk: values end up in snapshots, logs and telemetry. Default off.</summary>
    public bool IncludeParameterValues { get; set; }

    /// <summary>Let the OpenTelemetry package start its own activity when none is ambient. Default off: never create a competing trace.</summary>
    public bool CreateActivitiesWhenNoneExist { get; set; }

    /// <summary>Base URL for rule documentation; the rule id plus <c>.md</c> is appended. Point it at your own copy of <c>docs/rules</c> (a wiki, an internal site) when the GitHub repository is not reachable from where diagnoses are read.</summary>
    public string DocsBaseUrl { get; set; } = "https://github.com/DanilAntyp/QueryShape/blob/main/docs/rules/";

    /// <summary>Rules run by <see cref="QueryShapeScope.Analyze"/>. Starts with every built-in rule; remove or replace as needed.</summary>
    public IList<IRule> Rules { get; } = new List<IRule>(BuiltInRules.CreateAll());

    /// <summary>Listeners notified on capture and scope completion.</summary>
    public IList<IQueryShapeListener> Listeners { get; } = new List<IQueryShapeListener>();

    /// <summary>Logger factory for QueryShape's own Debug-level diagnostics. Optional.</summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Builds the documentation URL for a rule.</summary>
    public string DocsUrlFor(string ruleId) => DocsBaseUrl.TrimEnd('/') + "/" + ruleId + ".md";
}
