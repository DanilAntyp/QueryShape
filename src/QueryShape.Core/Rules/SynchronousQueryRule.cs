using System.Globalization;

namespace QueryShape.Rules;

/// <summary>QS012: a command executed through EF Core's synchronous API inside a scope that declared itself asynchronous.</summary>
public sealed class SynchronousQueryRule : IRule
{
    /// <summary>Rule id.</summary>
    public const string RuleId = "QS012";

    /// <inheritdoc />
    public string Id => RuleId;

    /// <inheritdoc />
    public string Name => "Synchronous query on an async path";

    /// <inheritdoc />
    public Severity DefaultSeverity => Severity.Warning;

    /// <inheritdoc />
    public IEnumerable<Diagnosis> Analyze(QueryShapeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // Only a scope that says its host is asynchronous can turn a synchronous call into a finding (ADR-0015).
        // Console applications, batch jobs and plain unit tests never say so, and stay silent.
        if (!IsAsyncHost(scope))
        {
            yield break;
        }

        var reported = new HashSet<string>(StringComparer.Ordinal);
        var byShape = scope.Commands
            .Where(c => !c.IsAsync && !c.Failed && c.Source is QuerySource.Linq or QuerySource.Raw or QuerySource.SaveChanges)
            .GroupBy(c => c.Fingerprint + "\n" + (c.CallSite?.ToString() ?? string.Empty), StringComparer.Ordinal);

        foreach (var group in byShape)
        {
            var commands = group.OrderBy(c => c.Sequence).ToList();
            var first = commands[0];
            if (!reported.Add(group.Key))
            {
                continue;
            }

            var what = first.Source == QuerySource.SaveChanges ? "SaveChanges()" : "query";
            var subject = first.Source == QuerySource.SaveChanges ? "SaveChanges" : RuleHelpers.Describe(first);
            var times = commands.Count == 1 ? "once" : RuleHelpers.N(commands.Count) + " times";
            var blocked = RuleHelpers.Sum(commands);

            yield return new Diagnosis(
                RuleId,
                DefaultSeverity,
                $"Blocking {what}: {subject} ran synchronously {times} in an async request, blocking the thread for {RuleHelpers.Ms(blocked)}{RuleHelpers.AtCallSite(first)}",
                $"This scope is served by an asynchronous host, so its thread comes from the thread pool and is shared with every other request in flight. " +
                $"EF Core's synchronous methods ({(first.Source == QuerySource.SaveChanges ? "SaveChanges" : "ToList, First, Count, Any")}…) hold that thread while the database works, " +
                "instead of returning it to the pool the way the async overloads do. Nothing looks slow in isolation: the query takes as long as it always did, " +
                "and the cost only appears under concurrency, as requests queueing for threads that are all parked on the database. " +
                "The thread pool grows to compensate, but slowly (roughly one thread per second beyond the minimum), so a burst is served at that rate.",
                first.CallSite,
                commands.Select(c => c.Fingerprint).Distinct(StringComparer.Ordinal).ToArray(),
                new Evidence(
                    Count: commands.Count,
                    TotalDuration: blocked,
                    Rows: commands.Sum(c => (long?)(c.RowsReturned ?? c.RowsAffected) ?? 0),
                    SampleSql: first.Shape,
                    SampleExpression: first.Query?.Expression,
                    Details: RuleHelpers.Details(
                        ("asyncCommandsInScope", scope.Commands.Count(c => c.IsAsync).ToString(CultureInfo.InvariantCulture)),
                        ("blockedMs", blocked.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture)),
                        ("source", first.Source.ToString()))),
                new Fix(
                    first.Source == QuerySource.SaveChanges
                        ? "Call await SaveChangesAsync(cancellationToken) instead of SaveChanges()"
                        : "Await the async overload of the terminal operator (ToListAsync, FirstOrDefaultAsync, CountAsync, AnyAsync…)",
                    FixKind.CodeChange,
                    first.Source == QuerySource.SaveChanges ? "db.SaveChanges();" : "var result = query.ToList();",
                    first.Source == QuerySource.SaveChanges ? "await db.SaveChangesAsync(cancellationToken);" : "var result = await query.ToListAsync(cancellationToken);",
                    null, // the expression tree stops before the terminal operator, and awaiting also changes the enclosing method (ADR-0015)
                    "The async overloads release the thread while the database works, so the same hardware serves more concurrent requests; they do not make an individual query faster. " +
                    "If the call sits in a synchronous method, make that method async up to its caller rather than blocking with .Result or .Wait(), which reintroduces the same problem one frame up.",
                    scope.Options.DocsUrlFor(RuleId))
                {
                    IsPartial = true,
                    ManualStep = "Change the terminal call to its async overload, await it, and make the enclosing method async (QueryShape cannot patch this: EF Core's expression tree does not include the terminal operator).",
                });
        }
    }

    private static bool IsAsyncHost(QueryShapeScope scope)
    {
        for (var s = scope; s is not null; s = s.Parent)
        {
            if (s.Annotations.TryGetValue(QueryShapeScope.AsyncHostAnnotation, out var value)
                && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
