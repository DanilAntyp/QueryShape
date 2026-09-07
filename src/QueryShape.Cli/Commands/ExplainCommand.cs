using System.Text;
using System.Text.Json;
using QueryShape.Cli.Llm;
using QueryShape.Reporting;

namespace QueryShape.Cli.Commands;

/// <summary>`queryshape explain`: rule-based explanations, or (--llm) a richer one from an LLM given the diagnosis and the source method.</summary>
internal sealed class ExplainCommand
{
    public string? Project { get; init; }

    public string? TestFilter { get; init; }

    public string? ReportDirectory { get; init; }

    public string? RuleFilter { get; init; }

    public bool UseLlm { get; init; }

    public string? Model { get; init; }

    public bool ShowPrompt { get; init; }

    public Func<string, string?, ILlmClient>? ClientFactory { get; init; }

    public async Task<int> ExecuteAsync(TextWriter out_, TextWriter err, CancellationToken ct)
    {
        RunMetrics metrics;
        if (ReportDirectory is not null)
        {
            metrics = RunMetrics.Load(ReportDirectory);
        }
        else
        {
            var dir = Path.Combine(Path.GetTempPath(), "queryshape-explain", Guid.NewGuid().ToString("N"));
            (metrics, _) = await TestRun.RunAsync(Directory.GetCurrentDirectory(), Project, TestFilter, dir, noBuild: false, null, out_, ct);
        }

        var diagnoses = metrics.Diagnostics.Where(d => RuleFilter is null || string.Equals(d.RuleId, RuleFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (diagnoses.Count == 0)
        {
            out_.WriteLine("No diagnoses to explain.");
            return 0;
        }

        ILlmClient? client = null;
        if (UseLlm)
        {
            var key = Environment.GetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable);
            if (string.IsNullOrWhiteSpace(key))
            {
                err.WriteLine($"explain --llm is disabled: set {AnthropicLlmClient.ApiKeyVariable} to enable it. Nothing was sent anywhere.");
                err.WriteLine("The rule-based explanation below needs no key.");
                UseLlmFallback(out_, diagnoses);
                return 2;
            }

            client = (ClientFactory ?? ((k, m) => new AnthropicLlmClient(k, m)))(key, Model);
        }

        foreach (var d in diagnoses)
        {
            out_.WriteLine($"{d.RuleId} {d.Severity.ToUpperInvariant()}  {d.Title}");
            out_.WriteLine("  why  " + d.Explanation);
            if (d.FixSummary is not null)
            {
                out_.WriteLine("  fix  " + d.FixSummary);
            }

            if (client is null)
            {
                out_.WriteLine();
                continue;
            }

            var (system, user) = BuildPrompt(d);
            if (ShowPrompt)
            {
                out_.WriteLine();
                out_.WriteLine("---- prompt sent to " + client.Model + " (system) ----");
                out_.WriteLine(system);
                out_.WriteLine("---- prompt sent to " + client.Model + " (user) ----");
                out_.WriteLine(user);
                out_.WriteLine("---- end of prompt ----");
            }

            string answer;
            try
            {
                answer = await client.CompleteAsync(system, user, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                err.WriteLine($"explain --llm: the model call failed: {ex.GetType().Name}: {ex.Message}");
                return 2;
            }

            out_.WriteLine();
            out_.WriteLine($"==== LLM explanation ({client.Model}) — generated text, not verified by QueryShape; detection above is rule-based ====");
            out_.WriteLine(answer.Trim());
            out_.WriteLine("==== end of LLM explanation ====");
            out_.WriteLine();
        }

        return 0;
    }

    private static void UseLlmFallback(TextWriter out_, IEnumerable<ScopeReportDiagnosis> diagnoses)
    {
        foreach (var d in diagnoses)
        {
            out_.WriteLine($"{d.RuleId} {d.Severity.ToUpperInvariant()}  {d.Title}");
            out_.WriteLine("  why  " + d.Explanation);
            if (d.FixSummary is not null)
            {
                out_.WriteLine("  fix  " + d.FixSummary);
            }
        }
    }

    private static readonly JsonSerializerOptions s_promptJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Exactly what is sent: the diagnosis (no parameter values, no connection strings) and the enclosing source method.</summary>
    internal static (string System, string User) BuildPrompt(ScopeReportDiagnosis d)
    {
        const string system =
            "You are an Entity Framework Core performance expert helping a developer understand a diagnosis produced by QueryShape, a static+runtime analyzer. " +
            "Explain, in plain English a junior developer understands, why EF Core produced the observed queries for this code, then propose the smallest code change that fixes it, " +
            "as a unified diff against the source excerpt when possible. Be concrete: use the real entity, navigation and method names from the input. " +
            "Do not invent facts about the schema or data that are not in the input; say so when something is uncertain. Keep it under 300 words plus the diff.";

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
            fixBefore = d.FixBefore,
            fixAfter = d.FixAfter,
            docs = d.DocsUrl,
        };

        var sb = new StringBuilder();
        sb.Append("## Diagnosis (JSON)\n").Append(JsonSerializer.Serialize(payload, s_promptJson)).Append("\n\n");

        var excerpt = SourceExcerpt.Extract(d.CallSiteFile, d.CallSiteLine);
        if (excerpt is not null)
        {
            var rel = QueryShape.Rules.SourcePatcher.RepositoryRelativePath(excerpt.Path);
            sb.Append("## Source method (").Append(rel).Append(", lines ").Append(excerpt.StartLine).Append('-').Append(excerpt.EndLine).Append(")\n```csharp\n").Append(excerpt.Text).Append("\n```\n");
        }
        else
        {
            sb.Append("## Source method\n(not available: call site unknown or file not readable)\n");
        }

        return (system, sb.ToString());
    }
}
