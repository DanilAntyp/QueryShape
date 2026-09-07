using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using QueryShape.Testing;

namespace QueryShape.Testing.NUnit;

/// <summary>
/// Wraps an NUnit test in a <see cref="QueryShapeScope"/> and fails it when the budget is exceeded:
/// <c>[Test, QueryBudget(MaxQueries = 2, MaxDurationMs = 200, FailOn = Severity.Error)]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class QueryBudgetAttribute : NUnitAttribute, ITestAction
{
    private int _maxQueries = -1;
    private double _maxDurationMs = -1;

    /// <summary>Maximum number of commands. Unset: unlimited.</summary>
    public int MaxQueries { get => _maxQueries; set => _maxQueries = value; }

    /// <summary>Maximum summed command duration in milliseconds. Unset: unlimited.</summary>
    public double MaxDurationMs { get => _maxDurationMs; set => _maxDurationMs = value; }

    /// <summary>Diagnoses at or above this severity fail the test. Default <see cref="Severity.Warning"/>.</summary>
    public Severity FailOn { get; set; } = Severity.Warning;

    /// <summary>Set to <c>true</c> to check only counts and duration, never diagnostics.</summary>
    public bool IgnoreDiagnostics { get; set; }

    /// <inheritdoc />
    public ActionTargets Targets => ActionTargets.Test;

    /// <summary>The budget this attribute expresses.</summary>
    public QueryBudget ToBudget()
        => new()
        {
            MaxQueries = _maxQueries >= 0 ? _maxQueries : null,
            MaxDurationMs = _maxDurationMs >= 0 ? _maxDurationMs : null,
            FailOn = IgnoreDiagnostics ? null : FailOn,
        };

    /// <inheritdoc />
    public void BeforeTest(ITest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (test.Method?.MethodInfo is { } method)
        {
            QueryBudgetEnforcer.Begin(method);
        }
    }

    /// <inheritdoc />
    public void AfterTest(ITest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (test.Method?.MethodInfo is { } method)
        {
            QueryBudgetEnforcer.End(method, ToBudget());
        }
    }
}
