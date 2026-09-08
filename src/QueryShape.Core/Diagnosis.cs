namespace QueryShape;

/// <summary>One finding produced by a rule for one scope.</summary>
/// <param name="RuleId">Stable rule identifier such as <c>QS001</c>.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Title">One line, names the code that caused it when known.</param>
/// <param name="Explanation">Why EF Core produced this, in plain English.</param>
/// <param name="CallSite">Where in user code the query ran, or <c>null</c> if unknown.</param>
/// <param name="Fingerprints">Fingerprints of the commands involved.</param>
/// <param name="Evidence">The facts the rule used: counts, timings, sample SQL.</param>
/// <param name="SuggestedFix">A concrete, applicable fix, or <c>null</c> when none can be derived.</param>
public sealed record Diagnosis(
    string RuleId,
    Severity Severity,
    string Title,
    string Explanation,
    CallSite? CallSite,
    IReadOnlyList<string> Fingerprints,
    Evidence Evidence,
    Fix? SuggestedFix);

/// <summary>What kind of change a fix is.</summary>
public enum FixKind
{
    /// <summary>Edit the LINQ / C# code.</summary>
    CodeChange = 0,

    /// <summary>Change DbContext or provider configuration.</summary>
    ConfigChange = 1,

    /// <summary>Change the database schema (index, column, etc.).</summary>
    SchemaChange = 2,

    /// <summary>Needs a human decision; guidance only.</summary>
    Manual = 3,
}

/// <summary>A concrete fix for a diagnosis.</summary>
/// <param name="Summary">Imperative one-liner, e.g. <c>Add .Include(c =&gt; c.Orders)</c>.</param>
/// <param name="Kind">What kind of change this is.</param>
/// <param name="BeforeSnippet">The offending code as reconstructed from the expression tree, or <c>null</c>.</param>
/// <param name="AfterSnippet">The proposed replacement, or <c>null</c>.</param>
/// <param name="UnifiedDiff">A real patch against the source file when the call site is known and readable, otherwise <c>null</c>.</param>
/// <param name="Rationale">Why this fix works.</param>
/// <param name="DocsUrl">Link to the rule's documentation page.</param>
public sealed record Fix(
    string Summary,
    FixKind Kind,
    string? BeforeSnippet,
    string? AfterSnippet,
    string? UnifiedDiff,
    string Rationale,
    string DocsUrl)
{
    /// <summary><c>true</c> when <see cref="UnifiedDiff"/> is only part of the fix and <see cref="ManualStep"/> is still required. Tools must not present a partial patch as a complete fix.</summary>
    public bool IsPartial { get; init; }

    /// <summary>What a person still has to change by hand when the patch is partial (or when no patch could be produced).</summary>
    public string? ManualStep { get; init; }
}

/// <summary>The facts a rule used to reach its conclusion.</summary>
/// <param name="Count">How many commands were involved.</param>
/// <param name="TotalDuration">Combined duration of the involved commands.</param>
/// <param name="Rows">Rows returned or affected, when known.</param>
/// <param name="SampleSql">The normalized SQL shape of a representative command.</param>
/// <param name="SampleExpression">The LINQ expression of a representative command, when known.</param>
/// <param name="Details">Rule-specific facts, sorted by key when rendered.</param>
public sealed record Evidence(
    int? Count = null,
    TimeSpan? TotalDuration = null,
    long? Rows = null,
    string? SampleSql = null,
    string? SampleExpression = null,
    IReadOnlyDictionary<string, string>? Details = null);
