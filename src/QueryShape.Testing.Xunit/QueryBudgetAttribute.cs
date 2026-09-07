using System.Reflection;
using QueryShape.Testing;
using Xunit.Sdk;

namespace QueryShape.Testing.Xunit;

/// <summary>
/// Wraps an xUnit test in a <see cref="QueryShapeScope"/> and fails it when the budget is exceeded:
/// <c>[Fact, QueryBudget(MaxQueries = 2, MaxDurationMs = 200, FailOn = Severity.Error)]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class QueryBudgetAttribute : BeforeAfterTestAttribute
{
    private int _maxQueries = -1;
    private double _maxDurationMs = -1;
    private Severity _failOn = Severity.Warning;
    private bool _ignoreDiagnostics;

    /// <summary>Maximum number of commands. Unset: unlimited.</summary>
    public int MaxQueries { get => _maxQueries; set => _maxQueries = value; }

    /// <summary>Maximum summed command duration in milliseconds. Unset: unlimited.</summary>
    public double MaxDurationMs { get => _maxDurationMs; set => _maxDurationMs = value; }

    /// <summary>Diagnoses at or above this severity fail the test. Default <see cref="Severity.Warning"/>.</summary>
    public Severity FailOn { get => _failOn; set => _failOn = value; }

    /// <summary>Set to <c>true</c> to check only counts and duration, never diagnostics.</summary>
    public bool IgnoreDiagnostics { get => _ignoreDiagnostics; set => _ignoreDiagnostics = value; }

    /// <summary>The budget this attribute expresses.</summary>
    public QueryBudget ToBudget()
        => new()
        {
            MaxQueries = _maxQueries >= 0 ? _maxQueries : null,
            MaxDurationMs = _maxDurationMs >= 0 ? _maxDurationMs : null,
            FailOn = _ignoreDiagnostics ? null : _failOn,
        };

    /// <inheritdoc />
    public override void Before(MethodInfo methodUnderTest) => QueryBudgetEnforcer.Begin(methodUnderTest);

    /// <inheritdoc />
    public override void After(MethodInfo methodUnderTest) => QueryBudgetEnforcer.End(methodUnderTest, ToBudget());
}
