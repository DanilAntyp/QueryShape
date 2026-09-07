namespace QueryShape;

/// <summary>A pure function from a completed (or in-progress) scope to diagnoses. Rules do no I/O.</summary>
public interface IRule
{
    /// <summary>Stable id such as <c>QS001</c>.</summary>
    string Id { get; }

    /// <summary>Short human name, e.g. <c>N+1 query</c>.</summary>
    string Name { get; }

    /// <summary>Severity used unless the rule finds a reason to escalate or downgrade.</summary>
    Severity DefaultSeverity { get; }

    /// <summary>Analyzes the commands recorded in <paramref name="scope"/>. Thresholds come from <see cref="QueryShapeScope.Options"/>.</summary>
    IEnumerable<Diagnosis> Analyze(QueryShapeScope scope);
}
