using Microsoft.VisualStudio.TestTools.UnitTesting;
using QueryShape.Testing;

namespace QueryShape.Testing.MSTest;

/// <summary>
/// Replaces <c>[TestMethod]</c>: runs the test inside a <see cref="QueryShapeScope"/> and fails it when the budget is exceeded:
/// <c>[QueryBudgetTestMethod(MaxQueries = 2, MaxDurationMs = 200, FailOn = Severity.Error)]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class QueryBudgetTestMethodAttribute : TestMethodAttribute
{
    private int _maxQueries = -1;
    private double _maxDurationMs = -1;

    /// <summary>Creates the attribute; the caller info is what MSTest uses to locate the test in the IDE.</summary>
    public QueryBudgetTestMethodAttribute([System.Runtime.CompilerServices.CallerFilePath] string callerFilePath = "", [System.Runtime.CompilerServices.CallerLineNumber] int callerLineNumber = -1)
        : base(callerFilePath, callerLineNumber)
    {
    }

    /// <summary>Maximum number of commands. Unset: unlimited.</summary>
    public int MaxQueries { get => _maxQueries; set => _maxQueries = value; }

    /// <summary>Maximum summed command duration in milliseconds. Unset: unlimited.</summary>
    public double MaxDurationMs { get => _maxDurationMs; set => _maxDurationMs = value; }

    /// <summary>Diagnoses at or above this severity fail the test. Default <see cref="Severity.Warning"/>.</summary>
    public Severity FailOn { get; set; } = Severity.Warning;

    /// <summary>Set to <c>true</c> to check only counts and duration, never diagnostics.</summary>
    public bool IgnoreDiagnostics { get; set; }

    /// <summary>The budget this attribute expresses.</summary>
    public QueryBudget ToBudget()
        => new()
        {
            MaxQueries = _maxQueries >= 0 ? _maxQueries : null,
            MaxDurationMs = _maxDurationMs >= 0 ? _maxDurationMs : null,
            FailOn = IgnoreDiagnostics ? null : FailOn,
        };

    /// <inheritdoc />
    public override async Task<TestResult[]> ExecuteAsync(ITestMethod testMethod)
    {
        ArgumentNullException.ThrowIfNull(testMethod);
        var method = testMethod.MethodInfo;
        QueryBudgetEnforcer.Begin(method);
        TestResult[] results;
        try
        {
            results = await base.ExecuteAsync(testMethod).ConfigureAwait(false);
        }
        catch
        {
            QueryBudgetEnforcer.Discard(method);
            throw;
        }

        try
        {
            QueryBudgetEnforcer.End(method, ToBudget());
        }
        catch (QueryBudgetExceededException ex)
        {
            foreach (var r in results.Where(r => r.Outcome == UnitTestOutcome.Passed))
            {
                r.Outcome = UnitTestOutcome.Failed;
                r.TestFailureException = ex;
            }
        }

        return results;
    }
}
