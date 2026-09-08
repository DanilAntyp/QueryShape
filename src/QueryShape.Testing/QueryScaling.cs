using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QueryShape.Testing;

/// <summary>Contracts across observed dataset sizes, not mathematical complexity proofs.</summary>
public sealed class ScalingOptions
{
    public string Dimension { get; init; } = "items";
    public int Repeats { get; init; } = 2;
    public bool ConstantCommands { get; init; } = true;
    public bool ConstantRows { get; init; }
    public int? MaxCommands { get; init; }
    public long? MaxRows { get; init; }
    public double? MaxRowsPerItem { get; init; }
    public Func<int, int>? MaxCommandsForSize { get; init; }
    public Func<int, long>? MaxRowsForSize { get; init; }
    public Func<ScalingPoint, IEnumerable<string>>? ValidatePoint { get; init; }
}

public sealed record ScalingCase<TInput>(string Name, int Size, TInput Input);

public sealed record ScalingPoint(int Size, int Run, int Commands, long Rows, bool Complete, IReadOnlyList<string> Origins)
{
    public string? Case { get; init; }
    public double ElapsedMs { get; init; }
    public IReadOnlyDictionary<string, double> Metrics { get; init; } = new Dictionary<string, double>();
}

public sealed record ScalingReport(string Scenario, string Dimension, IReadOnlyList<ScalingPoint> Points, IReadOnlyList<string> Violations)
{
    public bool Passed => Violations.Count == 0;
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    public string ToText()
    {
        var text = new StringBuilder($"QueryShape scaling: {Scenario} ({Dimension})\n\ncase\tsize\trun\tcommands\trows\n");
        foreach (var p in Points) text.Append(CultureInfo.InvariantCulture, $"{p.Case}\t{p.Size}\t{p.Run}\t{p.Commands}\t{p.Rows}\n");
        foreach (var v in Violations) text.Append("FAIL: ").Append(v).Append('\n');
        foreach (var origin in Points.SelectMany(p => p.Origins).Distinct(StringComparer.Ordinal)) text.Append("  at ").Append(origin).Append('\n');
        text.Append(Passed ? "passed" : "failed").Append("; measured sizes only, not a complexity proof\n");
        return text.ToString();
    }
    public void AssertSatisfied()
    {
        if (!Passed) throw new QueryContractException(ToText());
    }
}

public static class QueryScaling
{
    public static async Task<ScalingReport> RunAsync<TFixture, TResult>(QueryScenario<int, TFixture, TResult> scenario,
        IReadOnlyList<int> sizes, ScalingOptions? options = null, CancellationToken cancellationToken = default) where TFixture : IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(sizes);
        options ??= new ScalingOptions();
        // CLI overrides are scoped to the launched test process.
        if (Environment.GetEnvironmentVariable("QUERYSHAPE_SIZES") is { Length: > 0 } overrideSizes)
            sizes = overrideSizes.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var ordered = sizes.Distinct().Order().ToArray();
        if (ordered.Length < 2 || ordered.Length > 100 || ordered.Any(s => s < 0)) throw new ArgumentException("Supply 2–100 distinct nonnegative sizes.", nameof(sizes));
        return await RunCasesAsync(scenario, ordered.Select(size => new ScalingCase<int>(size.ToString(CultureInfo.InvariantCulture), size, size)).ToArray(), options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Named inputs permit empty, skewed and multi-dimensional datasets. Size is the caller's denominator for row-rate contracts.</summary>
    public static async Task<ScalingReport> RunCasesAsync<TInput, TFixture, TResult>(QueryScenario<TInput, TFixture, TResult> scenario,
        IReadOnlyList<ScalingCase<TInput>> cases, ScalingOptions? options = null, CancellationToken cancellationToken = default) where TFixture : IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(cases);
        options ??= new();
        if (cases.Count is < 2 or > 100 || cases.Any(c => c.Size < 0 || string.IsNullOrWhiteSpace(c.Name)) || cases.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != cases.Count)
            throw new ArgumentException("Supply 2–100 uniquely named cases with nonnegative sizes.", nameof(cases));
        if (options.Repeats is < 1 or > 20 || options.MaxCommands < 0 || options.MaxRows < 0 ||
            options.MaxRowsPerItem is { } rate && (!double.IsFinite(rate) || rate < 0)) throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Dimension);
        var points = new List<ScalingPoint>();
        foreach (var input in cases)
        {
            for (var run = 1; run <= options.Repeats; run++)
            {
                var measured = await scenario.RunAsync(input.Input, $"{scenario.Name}/{options.Dimension}={input.Name}/run={run}", cancellationToken).ConfigureAwait(false);
                points.Add(new ScalingPoint(input.Size, run, measured.Commands, measured.Rows, measured.Complete,
                    measured.Report.Diagnostics.Select(d => d.CallSite).OfType<string>().Distinct().ToArray()) { Case = input.Name, ElapsedMs = measured.ElapsedMs, Metrics = measured.Metrics });
            }
        }

        var violations = new List<string>();
        if (points.Any(p => !p.Complete)) violations.Add("Capture is incomplete (disabled capture, overflow, failed command, or an unfinished reader).");
        if (points.All(p => p.Commands == 0)) violations.Add("No database commands captured; check instrumentation and the scenario.");
        if (options.ConstantCommands && points.Select(p => p.Commands).Distinct().Count() > 1) violations.Add("Command count changes with dataset size or repetition.");
        if (options.ConstantRows && points.Select(p => p.Rows).Distinct().Count() > 1) violations.Add("Rows read change with dataset size or repetition.");
        foreach (var p in points)
        {
            if (options.MaxCommandsForSize is { } commandBudget)
            {
                var limit = commandBudget(p.Size);
                if (limit < 0) throw new ArgumentOutOfRangeException(nameof(options), "Command budgets must be nonnegative.");
                if (p.Commands > limit) violations.Add($"case {p.Case}, run {p.Run}: {p.Commands} commands > size budget {limit}.");
            }
            if (options.MaxRowsForSize is { } rowBudget)
            {
                var limit = rowBudget(p.Size);
                if (limit < 0) throw new ArgumentOutOfRangeException(nameof(options), "Row budgets must be nonnegative.");
                if (p.Rows > limit) violations.Add($"case {p.Case}, run {p.Run}: {p.Rows} rows > size budget {limit}.");
            }
            if (options.ValidatePoint is { } validate)
                violations.AddRange(validate(p).Select(v => $"case {p.Case}, run {p.Run}: {v}"));
            if (options.MaxCommands is { } max && p.Commands > max) violations.Add($"size {p.Size}, run {p.Run}: {p.Commands} commands > {max}.");
            if (options.MaxRows is { } rows && p.Rows > rows) violations.Add($"size {p.Size}, run {p.Run}: {p.Rows} rows > {rows}.");
            if (options.MaxRowsPerItem is { } perItem && p.Rows > perItem * p.Size) violations.Add($"size {p.Size}, run {p.Run}: rows per {options.Dimension} exceed {perItem.ToString(CultureInfo.InvariantCulture)}.");
        }

        var report = new ScalingReport(scenario.Name, options.Dimension, points, violations);
        using var summary = QueryShapeScope.Begin(scenario.Name + "/scaling");
        summary.Annotate("scaling.report", report.ToJson());
        return report;
    }
}
