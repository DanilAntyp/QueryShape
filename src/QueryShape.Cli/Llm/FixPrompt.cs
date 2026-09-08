using System.Globalization;
using System.Text;
using System.Text.Json;
using QueryShape.Reporting;

namespace QueryShape.Cli.Llm;

/// <summary>Builds the "propose a patch" prompt: the diagnosis, the enclosing method with line numbers, and QueryShape's own partial patch when there is one.</summary>
internal static class FixPrompt
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const string SystemPrompt =
        "You are an Entity Framework Core performance expert. You receive one diagnosis from QueryShape (a runtime analyzer) and the source method it points at, " +
        "with line numbers. Respond with exactly one unified diff in git format that fixes the diagnosed problem with the smallest reasonable change, and nothing else: " +
        "no prose before or after, no comments inside the diff. Rules: paths must be exactly the repository-relative path given; use `--- a/<path>` and `+++ b/<path>` headers; " +
        "context and removed lines must be copied verbatim from the source shown (without the line-number prefixes); hunk headers may be approximate, line counts are recounted. " +
        "Keep the change inside the shown method. If the fix needs code outside the shown method or you are not confident, respond with the single word CANNOT.";

    public static string Build(ScopeReportDiagnosis d, string repoRoot)
    {
        var payload = new
        {
            ruleId = d.RuleId,
            severity = d.Severity,
            title = d.Title,
            explanation = d.Explanation,
            callSite = d.CallSite,
            sampleSql = d.SampleSql,
            linqExpression = d.SampleExpression,
            suggestedFix = d.FixSummary,
            fixRationale = d.FixRationale,
            manualStep = d.ManualStep,
            docs = d.DocsUrl,
        };

        var sb = new StringBuilder();
        sb.Append("## Diagnosis (JSON)\n").Append(JsonSerializer.Serialize(payload, s_json)).Append("\n\n");

        var excerpt = SourceExcerpt.Extract(d.CallSiteFile, d.CallSiteLine);
        if (excerpt is null)
        {
            sb.Append("## Source method\n(not available)\n");
            return sb.ToString();
        }

        var rel = Path.GetRelativePath(repoRoot, excerpt.Path).Replace('\\', '/');
        sb.Append("## Source: ").Append(rel).Append(" (lines ").Append(excerpt.StartLine.ToString(CultureInfo.InvariantCulture)).Append('-')
          .Append(excerpt.EndLine.ToString(CultureInfo.InvariantCulture)).Append(", each line prefixed with its number and a colon)\n```csharp\n");
        var lines = excerpt.Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append((excerpt.StartLine + i).ToString(CultureInfo.InvariantCulture)).Append(": ").Append(lines[i]).Append('\n');
        }

        sb.Append("```\n");

        if (d.UnifiedDiff is not null)
        {
            sb.Append("\n## QueryShape's own patch (").Append(d.FixIsPartial ? "partial: complete it" : "complete: improve it only if you see a better fix").Append(")\n```diff\n")
              .Append(d.UnifiedDiff.TrimEnd('\n')).Append("\n```\n");
        }

        return sb.ToString();
    }
}
