using System.Globalization;
using System.Text;
using QueryShape.Reporting;

namespace QueryShape.Testing;

/// <summary>Builds the failure message. This message is the product: it must tell the reader what changed, where, and how to fix it.</summary>
internal static class SnapshotMessageBuilder
{
    public static string Mismatch(string testName, string snapshotPath, SnapshotComparison c, Severity failOn)
    {
        var sb = new StringBuilder();
        sb.Append("QueryShape snapshot mismatch: ").Append(testName).Append('\n');
        sb.Append("  snapshot: ").Append(snapshotPath).Append('\n');

        if (c.ExpectedQueryCount != c.ActualQueryCount || c.Added.Count > 0 || c.Removed.Count > 0)
        {
            var diff = c.ActualQueryCount - c.ExpectedQueryCount;
            sb.Append('\n').Append("Queries: ").Append(c.ExpectedQueryCount).Append(" in snapshot, ").Append(c.ActualQueryCount).Append(" now");
            if (diff != 0)
            {
                sb.Append(" (").Append(diff > 0 ? "+" : "").Append(diff.ToString(CultureInfo.InvariantCulture)).Append(')');
            }

            sb.Append('\n');

            foreach (var d in c.Added)
            {
                AppendDelta(sb, '+', d);
            }

            foreach (var d in c.Removed)
            {
                AppendDelta(sb, '-', d);
            }
        }

        if (c.NewDiagnoses.Count > 0)
        {
            sb.Append('\n').Append("New diagnostics (severity >= ").Append(failOn).Append("):\n");
            sb.Append(DiagnosisFormatter.Summarize(c.NewDiagnoses, "  ")).Append('\n');
            sb.Append(DiagnosisFormatter.Format(c.NewDiagnoses, "  "));
        }

        if (c.KnownDiagnoses.Count > 0)
        {
            sb.Append('\n').Append("Known diagnostics already in the snapshot (not failing): ");
            sb.Append(string.Join(", ", c.KnownDiagnoses.GroupBy(d => d.RuleId + " " + d.Severity).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key)));
            sb.Append('\n');
        }

        sb.Append('\n');
        sb.Append("If this change is intended, update the snapshot: ").Append(SnapshotOptions.UpdateEnvironmentVariable).Append("=1 dotnet test, or `dotnet queryshape snapshots update`.\n");
        return sb.ToString();
    }

    public static string Missing(string testName, string snapshotPath)
        => $"QueryShape snapshot missing: {testName}\n" +
           $"  snapshot: {snapshotPath}\n\n" +
           "CI mode (the CI environment variable is set) does not create snapshots. Run the test locally once to create the file and commit it,\n" +
           $"or run with {SnapshotOptions.UpdateEnvironmentVariable}=1 in CI if you really want snapshots written there.\n";

    private static void AppendDelta(StringBuilder sb, char sign, QueryDelta d)
    {
        sb.Append("  ").Append(sign).Append(' ');
        if (d.ExpectedCount > 0 && d.ActualCount > 0)
        {
            sb.Append('x').Append(d.ExpectedCount.ToString(CultureInfo.InvariantCulture)).Append(" -> x").Append(d.ActualCount.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append('x').Append(Math.Max(d.ExpectedCount, d.ActualCount).ToString(CultureInfo.InvariantCulture));
        }

        sb.Append("  ").Append(d.Fingerprint).Append("  ").Append(d.Source).Append("  ").Append(Truncate(d.Shape, 200)).Append('\n');

        if (d.Sample is { } s)
        {
            if (s.CallSite is not null)
            {
                sb.Append("        at ").Append(s.CallSite).Append('\n');
            }
            else if (s.Tags.Count > 0)
            {
                sb.Append("        tags: ").Append(string.Join(", ", s.Tags)).Append('\n');
            }

            if (s.Query is { } q)
            {
                sb.Append("        linq: ").Append(Truncate(System.Text.RegularExpressions.Regex.Replace(q.Expression, @"\s+", " "), 200)).Append('\n');
            }
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
