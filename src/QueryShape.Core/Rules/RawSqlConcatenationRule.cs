using System.Globalization;
using QueryShape.Normalization;

namespace QueryShape.Rules;

/// <summary>QS010: raw SQL whose text varies between executions only in literal values, i.e. values were concatenated instead of parameterized.</summary>
public sealed class RawSqlConcatenationRule : IRule
{
    /// <summary>Rule id.</summary>
    public const string RuleId = "QS010";

    /// <inheritdoc />
    public string Id => RuleId;

    /// <inheritdoc />
    public string Name => "Raw SQL with string concatenation";

    /// <inheritdoc />
    public Severity DefaultSeverity => Severity.Error;

    /// <inheritdoc />
    public IEnumerable<Diagnosis> Analyze(QueryShapeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // Raw shapes already mask literals, so one shape groups every text that differs only in values. The texts themselves stay out of the diagnosis.
        var raw = scope.Commands.Where(c => c.Source == QuerySource.Raw && !c.Failed).ToList();
        foreach (var group in raw.GroupBy(c => c.Shape, StringComparer.Ordinal))
        {
            var variants = group.Select(c => SqlNormalizer.Shape(c.CommandText)).Distinct(StringComparer.Ordinal).Count();
            if (variants < 2)
            {
                continue;
            }

            var commands = group.OrderBy(c => c.Sequence).ToList();
            var first = commands[0];
            var structure = group.Key;

            yield return new Diagnosis(
                RuleId,
                DefaultSeverity,
                $"Raw SQL built from values: {variants} text variants of \"{Truncate(structure, 80)}\"{RuleHelpers.AtCallSite(first)}",
                $"The same raw statement ran {commands.Count} times with {variants} different texts that differ only in literal values, " +
                "which is what string interpolation or concatenation of user data into SQL produces. A value inside the SQL text is executed as SQL: a name containing a quote " +
                "breaks the statement, and a crafted one changes it (SQL injection). It also defeats plan caching, because the database sees a new statement for every value. " +
                "Parameters fix both: the text stays constant and the value is sent separately, never interpreted.",
                first.CallSite,
                [first.Fingerprint],
                new Evidence(
                    Count: commands.Count,
                    TotalDuration: RuleHelpers.Sum(commands),
                    SampleSql: structure,
                    Details: RuleHelpers.Details(
                        ("variants", variants.ToString(CultureInfo.InvariantCulture)),
                        ("structure", structure))),
                new Fix(
                    "Pass values as parameters: FromSql($\"... WHERE Name = {name}\") / FromSqlInterpolated, FromSqlRaw(\"... {0}\", name), or Dapper's anonymous parameter object",
                    FixKind.CodeChange,
                    Truncate(structure, 200),
                    structure.Replace("?", "{0}", StringComparison.Ordinal),
                    null,
                    "EF Core's FromSql / FromSqlInterpolated turn every interpolated hole into a DbParameter, so the SQL text never contains user data. " +
                    "FromSqlRaw is only safe with placeholders ({0}) and arguments, never with a pre-interpolated string.",
                    scope.Options.DocsUrlFor(RuleId)));
        }
    }

    /// <summary>Replaces string and numeric literals with <c>?</c> so texts that differ only in values collapse to one structure.</summary>
    internal static string Structure(string shape) => SqlNormalizer.MaskLiterals(shape);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
