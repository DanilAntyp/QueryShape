using System.Globalization;

namespace QueryShape.Rules;

/// <summary>QS009: one call site issuing many queries of several shapes in a scope: a helper called inside a loop.</summary>
public sealed class QueryInLoopRule : IRule
{
    /// <summary>Rule id.</summary>
    public const string RuleId = "QS009";

    /// <inheritdoc />
    public string Id => RuleId;

    /// <inheritdoc />
    public string Name => "Query in loop over navigation";

    /// <inheritdoc />
    public Severity DefaultSeverity => Severity.Warning;

    /// <inheritdoc />
    public IEnumerable<Diagnosis> Analyze(QueryShapeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var threshold = Math.Max(2, scope.Options.NPlusOneThreshold);

        // Group by the member that issued the query (file + member), not the line: a helper with two queries on two lines is one loop body.
        // A call site from TagWithCallSite has no member, only file and line; grouping those by file would turn any two tagged queries in a file into a "loop".
        var groups = RuleHelpers.ReadQueries(scope)
            .Where(c => c.CallSite is not null)
            .GroupBy(c => (c.CallSite!.FilePath ?? string.Empty) + "|" + (c.CallSite.Member.Length > 0 ? c.CallSite.Member : "#" + c.CallSite.Line.ToString(CultureInfo.InvariantCulture)), StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var commands = group.OrderBy(c => c.Sequence).ToList();
            var shapes = commands.GroupBy(c => c.Fingerprint, StringComparer.Ordinal).ToList();
            if (commands.Count < threshold || shapes.Count < 2)
            {
                continue; // one shape repeated is QS001/QS008 territory
            }

            // Every shape must repeat: that is what distinguishes a loop from a method that simply runs several different queries once.
            var repeatsPerShape = shapes.Min(g => g.Count());
            if (repeatsPerShape < 2)
            {
                continue;
            }

            var first = commands[0];
            var site = first.CallSite!;
            var label = site.Label;
            var lines = commands.Select(c => c.CallSite!.Line).Where(l => l > 0).Distinct().OrderBy(l => l).ToList();
            var lineText = lines.Count > 0 ? " (lines " + string.Join(", ", lines.Select(l => l.ToString(CultureInfo.InvariantCulture))) + ")" : string.Empty;
            var roots = shapes.Select(g => g.First().Query?.RootEntityShortName ?? "raw").Distinct().ToList();

            // Shapes that QS001 reports on their own (repeated enough, with varying parameters): say so instead of pretending this is a separate problem.
            var alsoNPlusOne = shapes
                .Where(g => g.Count() >= threshold && g.Select(c => c.ParameterHash).Distinct(StringComparer.Ordinal).Count() >= 2)
                .Select(g => g.First().Query?.RootEntityShortName ?? "raw")
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var overlap = alsoNPlusOne.Count == 0
                ? string.Empty
                : $" The {string.Join(" and ", alsoNPlusOne)} shape{(alsoNPlusOne.Count > 1 ? "s are" : " is")} also reported as QS001 with a fix of its own; this finding is the loop that issues them together.";

            yield return new Diagnosis(
                RuleId,
                DefaultSeverity,
                $"Queries in a loop: {label} issued {commands.Count} queries of {shapes.Count} shapes ({string.Join(", ", roots)}) in one scope",
                $"{label}{lineText} ran {commands.Count} queries in this scope, with {shapes.Count} different shapes each repeated at least {repeatsPerShape} times. " +
                "That is the signature of a method that fetches related data for one item and is called from a loop over the items: every iteration pays " +
                $"{shapes.Count} round trips, so the total is the number of items times {shapes.Count}. EF Core sees each call as an independent query and cannot batch them. " +
                "Unlike a plain N+1 (QS001), the per-iteration queries here are of different shapes, so a single Include will not fix all of them." + overlap,
                site,
                shapes.Select(g => g.Key).ToArray(),
                new Evidence(
                    Count: commands.Count,
                    TotalDuration: RuleHelpers.Sum(commands),
                    Rows: commands.Sum(c => (long?)c.RowsReturned ?? 0),
                    SampleSql: first.Shape,
                    SampleExpression: first.Query?.Expression,
                    Details: RuleHelpers.Details(
                        ("shapes", shapes.Count.ToString(CultureInfo.InvariantCulture)),
                        ("repeatsPerShape", repeatsPerShape.ToString(CultureInfo.InvariantCulture)))),
                new Fix(
                    $"Load the data {label} needs for all items before the loop (Include/Select on the outer query, or one batched query per shape keyed by the item ids), then pass it in",
                    FixKind.CodeChange,
                    null,
                    null,
                    null,
                    $"Moving the {shapes.Count} lookups out of the loop turns items x {shapes.Count} round trips into {shapes.Count} (or one with Include/projection). " +
                    "A dictionary built from a single batched query (ToDictionaryAsync) keeps the per-item code simple.",
                    scope.Options.DocsUrlFor(RuleId)));
        }
    }
}
