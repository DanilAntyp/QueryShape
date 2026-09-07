using System.Globalization;
using System.Text.RegularExpressions;

namespace QueryShape.Rules;

/// <summary>QS010: raw SQL whose text varies between executions only in literal values, i.e. values were concatenated instead of parameterized.</summary>
public sealed partial class RawSqlConcatenationRule : IRule
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

        var raw = scope.Commands.Where(c => c.Source == QuerySource.Raw && !c.Failed).ToList();
        foreach (var group in raw.GroupBy(c => Structure(c.Shape), StringComparer.Ordinal))
        {
            var variants = group.GroupBy(c => c.Shape, StringComparer.Ordinal).ToList();
            if (variants.Count < 2)
            {
                continue;
            }

            var commands = group.OrderBy(c => c.Sequence).ToList();
            var first = commands[0];
            var structure = group.Key;
            var samples = variants.Take(3).Select(v => Truncate(v.Key, 120)).ToList();

            yield return new Diagnosis(
                RuleId,
                DefaultSeverity,
                $"Raw SQL built from values: {variants.Count} text variants of \"{Truncate(structure, 80)}\"{RuleHelpers.AtCallSite(first)}",
                $"The same raw statement ran {commands.Count} times with {variants.Count} different texts that differ only in literal values, " +
                "which is what string interpolation or concatenation of user data into SQL produces. A value inside the SQL text is executed as SQL: a name containing a quote " +
                "breaks the statement, and a crafted one changes it (SQL injection). It also defeats plan caching, because the database sees a new statement for every value. " +
                "Parameters fix both: the text stays constant and the value is sent separately, never interpreted.",
                first.CallSite,
                variants.Select(v => v.First().Fingerprint).ToArray(),
                new Evidence(
                    Count: commands.Count,
                    TotalDuration: RuleHelpers.Sum(commands),
                    SampleSql: string.Join(" | ", samples),
                    Details: RuleHelpers.Details(
                        ("variants", variants.Count.ToString(CultureInfo.InvariantCulture)),
                        ("structure", structure))),
                new Fix(
                    "Pass values as parameters: FromSql($\"... WHERE Name = {name}\") / FromSqlInterpolated, FromSqlRaw(\"... {0}\", name), or Dapper's anonymous parameter object",
                    FixKind.CodeChange,
                    Truncate(first.CommandText.Trim(), 200),
                    Structure(first.Shape).Replace("?", "{0}", StringComparison.Ordinal),
                    null,
                    "EF Core's FromSql / FromSqlInterpolated turn every interpolated hole into a DbParameter, so the SQL text never contains user data. " +
                    "FromSqlRaw is only safe with placeholders ({0}) and arguments, never with a pre-interpolated string.",
                    scope.Options.DocsUrlFor(RuleId)));
        }
    }

    /// <summary>Replaces string and numeric literals with <c>?</c> so texts that differ only in values collapse to one structure.</summary>
    internal static string Structure(string shape)
    {
        var s = StringLiteral().Replace(shape, "?");
        s = NumericLiteral().Replace(s, "?");
        return s;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex StringLiteral();

    // Numbers that are not part of an identifier or a parameter name (@p0, t0, [c1]).
    [GeneratedRegex(@"(?<![\w@\]""`.])\b\d+(?:\.\d+)?\b(?![\w\]""`])")]
    private static partial Regex NumericLiteral();
}
