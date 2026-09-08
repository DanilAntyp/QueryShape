using System.Diagnostics;

namespace QueryShape;

/// <summary>
/// The unit of analysis: one HTTP request, one test, or one explicit <c>using var scope = QueryShapeScope.Begin();</c>.
/// Flows across <c>await</c> via <see cref="AsyncLocal{T}"/>. Every command captured while the scope is current is recorded here.
/// Scopes nest: a command is recorded by the innermost scope and by every scope enclosing it, so a test scope around an in-process
/// request sees the queries the request middleware's scope saw.
/// </summary>
public sealed class QueryShapeScope : IDisposable
{
    /// <summary>Pseudo rule id reported when a scope hit <see cref="QueryShapeOptions.MaxCommandsPerScope"/>.</summary>
    public const string OverflowRuleId = "QS_OVERFLOW";

    private static readonly AsyncLocal<QueryShapeScope?> s_current = new();

    private readonly object _gate = new();
    private readonly List<CapturedCommand> _commands = [];
    private readonly List<SaveChangesRecord> _saveChanges = [];
    private readonly SortedDictionary<string, string> _annotations = new(StringComparer.Ordinal);
    private readonly QueryShapeScope? _parent;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private IReadOnlyList<Diagnosis>? _cachedDiagnoses;
    private int _nextSequence;
    private bool _disposed;

    private QueryShapeScope(string? name, QueryShapeOptions options, QueryShapeScope? parent)
    {
        Name = name;
        Options = options;
        _parent = parent;
        StartedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>The innermost scope for the current async flow, or <c>null</c>.</summary>
    public static QueryShapeScope? Current => s_current.Value;

    /// <summary>Opens a scope and makes it current. Dispose it to close.</summary>
    /// <param name="name">Optional label (test name, request path).</param>
    /// <param name="options">Thresholds and rules; defaults to <see cref="QueryShapeOptions.Default"/>.</param>
    public static QueryShapeScope Begin(string? name = null, QueryShapeOptions? options = null)
    {
        var scope = new QueryShapeScope(name, options ?? QueryShapeOptions.Default, s_current.Value);
        s_current.Value = scope;
        return scope;
    }

    /// <summary>Optional label.</summary>
    public string? Name { get; }

    /// <summary>Options in effect for analysis.</summary>
    public QueryShapeOptions Options { get; }

    /// <summary>The scope this one was begun inside, or <c>null</c> for an outermost scope.</summary>
    public QueryShapeScope? Parent => _parent;

    /// <summary>When the scope was begun.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Time since <see cref="Begin"/>, frozen at dispose.</summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary><c>true</c> once more than <see cref="QueryShapeOptions.MaxCommandsPerScope"/> commands were seen; later commands were dropped.</summary>
    public bool Overflowed { get; private set; }

    /// <summary><c>true</c> after <see cref="Dispose"/>.</summary>
    public bool IsCompleted => _disposed;

    /// <summary>Snapshot of the recorded commands in execution order.</summary>
    public IReadOnlyList<CapturedCommand> Commands
    {
        get { lock (_gate) { return _commands.ToArray(); } }
    }

    /// <summary>Snapshot of the <c>SaveChanges</c> calls seen.</summary>
    public IReadOnlyList<SaveChangesRecord> SaveChanges
    {
        get { lock (_gate) { return _saveChanges.ToArray(); } }
    }

    /// <summary>Free-form facts attached by tools (e.g. the snapshot outcome), included in scope reports. Sorted by key.</summary>
    public IReadOnlyDictionary<string, string> Annotations
    {
        get { lock (_gate) { return new Dictionary<string, string>(_annotations, StringComparer.Ordinal); } }
    }

    /// <summary>Attaches or replaces an annotation.</summary>
    public void Annotate(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            _annotations[key] = value;
        }
    }

    /// <summary>Number of commands recorded (excluding dropped overflow).</summary>
    public int CommandCount
    {
        get { lock (_gate) { return _commands.Count; } }
    }

    /// <summary>Sum of command durations.</summary>
    public TimeSpan TotalCommandDuration
    {
        get
        {
            lock (_gate)
            {
                var total = TimeSpan.Zero;
                foreach (var c in _commands)
                {
                    total += c.Duration;
                }

                return total;
            }
        }
    }

    /// <summary>Runs every rule in <see cref="QueryShapeOptions.Rules"/> over the recorded commands. Safe to call more than once; results are recomputed until the scope is disposed.</summary>
    public IReadOnlyList<Diagnosis> Analyze()
    {
        if (_disposed && _cachedDiagnoses is not null)
        {
            return _cachedDiagnoses;
        }

        var results = new List<Diagnosis>();
        foreach (var rule in Options.Rules)
        {
            try
            {
                results.AddRange(rule.Analyze(this));
            }
            catch (Exception ex)
            {
                // A broken rule must never take down analysis for the others.
                Internal.Log.RuleFailed(Options, rule.Id, ex);
            }
        }

        if (Overflowed)
        {
            results.Add(new Diagnosis(
                OverflowRuleId,
                Severity.Warning,
                $"Scope recorded more than {Options.MaxCommandsPerScope} commands and stopped capturing",
                "QueryShape keeps a bounded list of commands per scope so it cannot grow memory without limit inside your app. " +
                "This scope hit the limit; later commands were counted but not recorded, so rule results for it are incomplete.",
                null,
                [],
                new Evidence(Count: Options.MaxCommandsPerScope),
                new Fix("Raise QueryShapeOptions.MaxCommandsPerScope or split the work into smaller scopes",
                    FixKind.ConfigChange, null, null, null,
                    "A scope with thousands of commands is usually itself the finding: look at the N+1 diagnoses first.",
                    Options.DocsUrlFor(OverflowRuleId))));
        }

        results.Sort(static (a, b) =>
        {
            var bySeverity = b.Severity.CompareTo(a.Severity);
            return bySeverity != 0 ? bySeverity : string.CompareOrdinal(a.RuleId, b.RuleId);
        });

        if (_disposed)
        {
            _cachedDiagnoses = results;
        }

        return results;
    }

    /// <summary>Closes the scope, restores the parent as current and notifies listeners.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopwatch.Stop();

        if (ReferenceEquals(s_current.Value, this))
        {
            s_current.Value = _parent;
        }

        if (Options.Listeners.Count == 0 && !Reporting.ScopeReportWriter.IsEnabled)
        {
            return;
        }

        IReadOnlyList<Diagnosis> diagnoses;
        try
        {
            diagnoses = Analyze();
        }
        catch (Exception ex)
        {
            Internal.Log.Swallowed(Options, "scope analysis", ex);
            diagnoses = [];
        }

        Reporting.ScopeReportWriter.TryWrite(this, diagnoses);

        foreach (var listener in Options.Listeners)
        {
            try
            {
                listener.OnScopeCompleted(this, diagnoses);
            }
            catch (Exception ex)
            {
                Internal.Log.Swallowed(Options, "listener.OnScopeCompleted", ex);
            }
        }
    }

    /// <summary>
    /// Records a command in this scope and in every enclosing scope. Returns <c>false</c> when this scope dropped it because of overflow or because it is closed
    /// (enclosing scopes decide for themselves).
    /// </summary>
    internal bool Record(CapturedCommand command)
    {
        // The outermost scope numbers commands, so Sequence orders commands consistently in every scope of the chain.
        var root = this;
        while (root._parent is not null)
        {
            root = root._parent;
        }

        lock (root._gate)
        {
            command.Sequence = root._nextSequence++;
        }

        var recorded = RecordHere(command);
        for (var ancestor = _parent; ancestor is not null; ancestor = ancestor._parent)
        {
            ancestor.RecordHere(command);
        }

        return recorded;
    }

    private bool RecordHere(CapturedCommand command)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_commands.Count >= Options.MaxCommandsPerScope)
            {
                Overflowed = true;
                return false;
            }

            _commands.Add(command);
            _cachedDiagnoses = null;
            return true;
        }
    }

    internal void Record(SaveChangesRecord saveChanges)
    {
        for (var scope = this; scope is not null; scope = scope._parent)
        {
            lock (scope._gate)
            {
                if (!scope._disposed)
                {
                    scope._saveChanges.Add(saveChanges);
                    scope._cachedDiagnoses = null;
                }
            }
        }
    }

    internal void InvalidateAnalysis()
    {
        lock (_gate)
        {
            _cachedDiagnoses = null;
        }
    }
}
