namespace QueryShape.Cli;

internal sealed class VerificationPolicy
{
    public int? MaxCommands { get; init; }
    public long? MaxRows { get; init; }
    public double? MaxDurationMs { get; init; }
    public double MaxDurationIncreasePercent { get; init; } = 10;
    public IReadOnlyList<string> AllowedNewWarningRules { get; init; } = [];

    internal void Validate()
    {
        if (MaxCommands < 0 || MaxRows < 0 || MaxDurationMs is { } ms && (!double.IsFinite(ms) || ms < 0)
            || !double.IsFinite(MaxDurationIncreasePercent) || MaxDurationIncreasePercent < 0)
            throw new ArgumentException("Verification budgets must be finite and nonnegative.");
    }

    internal IReadOnlyList<string> Check(RunMetrics before, RunMetrics after, double baselineSpread = 0)
    {
        Validate();
        var blockers = new List<string>();
        var durationTolerance = Math.Max(1, Math.Max(baselineSpread, before.DurationMs * MaxDurationIncreasePercent / 100));
        if (after.DurationMs > before.DurationMs + durationTolerance)
            blockers.Add("Measured command duration regressed beyond the allowed baseline variation.");
        if (MaxDurationMs is { } maxMs && after.DurationMs > maxMs) blockers.Add($"Command duration exceeds {maxMs} ms.");
        if (after.Queries > (MaxCommands ?? before.Queries)) blockers.Add("Command count exceeds its budget.");
        if (MaxRows.HasValue && !after.RowsReturned.HasValue) blockers.Add("Returned-row measurements are missing; rerun with current instrumentation.");
        if (after.RowsReturned is { } rows && rows > (MaxRows ?? before.RowsReturned ?? long.MaxValue)) blockers.Add("Returned rows exceed their budget.");
        if (FindingIdentity.Added(before, after, d => d.Severity == "Error" || d.Severity == "Warning" && !AllowedNewWarningRules.Contains(d.RuleId, StringComparer.Ordinal)) > 0)
            blockers.Add("New warning/error findings appeared at a scenario or query location.");

        // Enforce defaults per named operation too; totals cannot hide a regression in a different operation.
        foreach (var group in after.Reports.GroupBy(r => r.Scope ?? "", StringComparer.Ordinal))
        {
            var old = before.Reports.Where(r => (r.Scope ?? "") == group.Key).ToArray();
            var oldMs = old.Sum(r => r.CommandDurationMs);
            if (group.Sum(r => r.CommandDurationMs) > oldMs + Math.Max(1, Math.Max(baselineSpread, oldMs * MaxDurationIncreasePercent / 100)))
                blockers.Add("Command duration increased in scope: " + group.Key);
            if (MaxCommands is null && group.Sum(r => r.QueryCount) > old.Sum(r => r.QueryCount))
                blockers.Add("Commands increased in scope: " + group.Key);
            if (MaxRows is null && old.All(r => r.RowsReturned.HasValue) && group.All(r => r.RowsReturned.HasValue)
                && group.Sum(r => r.RowsReturned!.Value) > old.Sum(r => r.RowsReturned!.Value))
                blockers.Add("Returned rows increased in scope: " + group.Key);
        }
        return blockers;
    }
}
