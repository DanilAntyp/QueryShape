using System.Globalization;
using System.Text;

namespace QueryShape.Reporting;

/// <summary>Renders diagnoses as readable text. Used by test failure messages and the CLI.</summary>
public static class DiagnosisFormatter
{
    /// <summary>Formats one diagnosis as an indented block.</summary>
    public static string Format(Diagnosis diagnosis, string indent = "")
    {
        ArgumentNullException.ThrowIfNull(diagnosis);
        var sb = new StringBuilder();
        Append(sb, diagnosis, indent);
        return sb.ToString();
    }

    /// <summary>Formats a list of diagnoses, most severe first.</summary>
    public static string Format(IEnumerable<Diagnosis> diagnoses, string indent = "")
    {
        ArgumentNullException.ThrowIfNull(diagnoses);
        var sb = new StringBuilder();
        var first = true;
        foreach (var d in diagnoses.OrderByDescending(d => d.Severity).ThenBy(d => d.RuleId, StringComparer.Ordinal))
        {
            if (!first)
            {
                sb.Append('\n');
            }

            first = false;
            Append(sb, d, indent);
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, Diagnosis d, string indent)
    {
        sb.Append(indent).Append(d.RuleId).Append(' ').Append(d.Severity.ToString().ToUpperInvariant()).Append("  ").Append(d.Title).Append('\n');
        if (d.CallSite is not null && !d.Title.Contains(d.CallSite.ToString(), StringComparison.Ordinal))
        {
            sb.Append(indent).Append("  at   ").Append(d.CallSite).Append('\n');
        }

        AppendWrapped(sb, indent + "  why  ", indent + "       ", d.Explanation);

        if (d.SuggestedFix is { } fix)
        {
            AppendWrapped(sb, indent + "  fix  ", indent + "       ", fix.Summary);
            if (fix.BeforeSnippet is not null)
            {
                AppendSnippet(sb, indent + "       before: ", fix.BeforeSnippet);
            }

            if (fix.AfterSnippet is not null)
            {
                AppendSnippet(sb, indent + "       after:  ", fix.AfterSnippet);
            }

            if (fix.UnifiedDiff is not null)
            {
                sb.Append(indent).Append(fix.IsPartial ? "       patch (partial):\n" : "       patch:\n");
                foreach (var line in fix.UnifiedDiff.TrimEnd('\n').Split('\n'))
                {
                    sb.Append(indent).Append("         ").Append(line).Append('\n');
                }
            }

            if (fix.ManualStep is not null)
            {
                AppendWrapped(sb, indent + "       manual step: ", indent + "                    ", fix.ManualStep);
            }
        }

        var ev = d.Evidence;
        var parts = new List<string>();
        if (ev.Count is { } n)
        {
            parts.Add(n == 1 ? "1 query" : n.ToString("N0", CultureInfo.InvariantCulture) + " queries");
        }

        if (ev.TotalDuration is { } t)
        {
            parts.Add(t.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture) + " ms");
        }

        if (ev.Rows is { } rows)
        {
            parts.Add(rows.ToString("N0", CultureInfo.InvariantCulture) + " rows");
        }

        if (ev.Details is not null)
        {
            foreach (var kv in ev.Details.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                parts.Add(kv.Key + "=" + kv.Value);
            }
        }

        if (parts.Count > 0)
        {
            sb.Append(indent).Append("  data ").Append(string.Join(", ", parts)).Append('\n');
        }

        if (ev.SampleSql is not null)
        {
            sb.Append(indent).Append("  sql  ").Append(Truncate(ev.SampleSql, 300)).Append('\n');
        }

        if (d.SuggestedFix is not null)
        {
            sb.Append(indent).Append("  docs ").Append(d.SuggestedFix.DocsUrl).Append('\n');
        }
    }

    private static void AppendSnippet(StringBuilder sb, string label, string snippet)
    {
        var lines = snippet.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        sb.Append(label).Append(lines[0]).Append('\n');
        var pad = new string(' ', label.Length);
        for (var i = 1; i < lines.Length; i++)
        {
            sb.Append(pad).Append(lines[i]).Append('\n');
        }
    }

    private static void AppendWrapped(StringBuilder sb, string firstPrefix, string restPrefix, string text, int width = 110)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder(firstPrefix);
        var lineHasWords = false;
        foreach (var word in words)
        {
            if (lineHasWords && line.Length + 1 + word.Length > width)
            {
                sb.Append(line).Append('\n');
                line.Clear().Append(restPrefix);
                lineHasWords = false;
            }

            if (lineHasWords)
            {
                line.Append(' ');
            }

            line.Append(word);
            lineHasWords = true;
        }

        sb.Append(line).Append('\n');
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
