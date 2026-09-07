namespace QueryShape.Testing;

/// <summary>A fingerprint whose execution count differs between snapshot and run.</summary>
/// <param name="Fingerprint">The fingerprint.</param>
/// <param name="Shape">Normalized SQL.</param>
/// <param name="Source">Query source.</param>
/// <param name="ExpectedCount">Executions in the snapshot.</param>
/// <param name="ActualCount">Executions in this run.</param>
/// <param name="Sample">A captured command from this run, when the query ran (for call site / expression).</param>
public sealed record QueryDelta(string Fingerprint, string Shape, string Source, int ExpectedCount, int ActualCount, CapturedCommand? Sample);

/// <summary>Result of comparing a run against its snapshot.</summary>
public sealed class SnapshotComparison
{
    /// <summary>Fingerprints executed more often than in the snapshot (including entirely new ones).</summary>
    public required IReadOnlyList<QueryDelta> Added { get; init; }

    /// <summary>Fingerprints executed less often than in the snapshot (including ones that disappeared).</summary>
    public required IReadOnlyList<QueryDelta> Removed { get; init; }

    /// <summary>Query count in the snapshot.</summary>
    public required int ExpectedQueryCount { get; init; }

    /// <summary>Query count in this run.</summary>
    public required int ActualQueryCount { get; init; }

    /// <summary>Diagnoses of severity at or above the threshold that were not in the snapshot.</summary>
    public required IReadOnlyList<Diagnosis> NewDiagnoses { get; init; }

    /// <summary>Diagnoses from this run that the snapshot already lists (known debt).</summary>
    public required IReadOnlyList<Diagnosis> KnownDiagnoses { get; init; }

    /// <summary><c>true</c> when nothing fails the comparison.</summary>
    public bool IsMatch => Added.Count == 0 && Removed.Count == 0 && ExpectedQueryCount == ActualQueryCount && NewDiagnoses.Count == 0;

    /// <summary>Compares a run (<paramref name="actual"/>, plus the full diagnoses for messages) against <paramref name="expected"/>.</summary>
    public static SnapshotComparison Compare(QuerySnapshot expected, QuerySnapshot actual, IReadOnlyList<Diagnosis> diagnoses, IReadOnlyList<CapturedCommand> commands, Severity failOn)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(diagnoses);
        ArgumentNullException.ThrowIfNull(commands);

        var expectedCounts = expected.Queries.GroupBy(q => q.Fingerprint, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var actualCounts = actual.Queries.GroupBy(q => q.Fingerprint, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var added = new List<QueryDelta>();
        var removed = new List<QueryDelta>();
        foreach (var fp in expectedCounts.Keys.Union(actualCounts.Keys, StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal))
        {
            var e = expectedCounts.TryGetValue(fp, out var el) ? el.Count : 0;
            var a = actualCounts.TryGetValue(fp, out var al) ? al.Count : 0;
            if (e == a)
            {
                continue;
            }

            var sampleQuery = (al ?? el)![0];
            var sampleCommand = commands.FirstOrDefault(c => c.Fingerprint == fp);
            var delta = new QueryDelta(fp, sampleQuery.Shape, sampleQuery.Source, e, a, sampleCommand);
            (a > e ? added : removed).Add(delta);
        }

        // Diagnostics: multiset of (ruleId, severity); anything at/above failOn not covered by the snapshot is new.
        var known = expected.Diagnostics.GroupBy(d => (d.RuleId, d.Severity)).ToDictionary(g => g.Key, g => g.Count());
        var newDiagnoses = new List<Diagnosis>();
        var knownDiagnoses = new List<Diagnosis>();
        foreach (var d in diagnoses.Where(d => d.RuleId != QueryShapeScope.OverflowRuleId).OrderByDescending(d => d.Severity).ThenBy(d => d.RuleId, StringComparer.Ordinal))
        {
            var key = (d.RuleId, d.Severity.ToString());
            if (known.TryGetValue(key, out var remaining) && remaining > 0)
            {
                known[key] = remaining - 1;
                knownDiagnoses.Add(d);
            }
            else if (d.Severity >= failOn)
            {
                newDiagnoses.Add(d);
            }
            else
            {
                knownDiagnoses.Add(d);
            }
        }

        return new SnapshotComparison
        {
            Added = added,
            Removed = removed,
            ExpectedQueryCount = expected.QueryCount,
            ActualQueryCount = actual.QueryCount,
            NewDiagnoses = newDiagnoses,
            KnownDiagnoses = knownDiagnoses,
        };
    }
}
