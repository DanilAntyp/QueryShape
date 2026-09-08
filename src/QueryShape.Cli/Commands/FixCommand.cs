using QueryShape.Cli.Llm;

namespace QueryShape.Cli.Commands;

/// <summary>`queryshape fix --llm`: the model proposes a patch for a diagnosis, `verify` proves or rejects it. Nothing is trusted from the model.</summary>
internal sealed class FixCommand
{
    public required string TestFilter { get; init; }

    public string? Project { get; init; }

    public string? RuleFilter { get; init; }

    public bool UseLlm { get; init; }

    public string? Model { get; init; }

    public bool ShowPrompt { get; init; }

    public string? OutputPatch { get; init; }

    public int Runs { get; init; } = 3;

    public bool AllowDirty { get; init; }

    public string Format { get; init; } = "text";

    public Func<string, string?, ILlmClient>? ClientFactory { get; init; }

    public async Task<int> ExecuteAsync(TextWriter out_, TextWriter err, CancellationToken ct)
    {
        if (!UseLlm)
        {
            err.WriteLine("fix: pass --llm. The only source of patches here is a language model; to measure a hand-written patch use `queryshape verify --patch`, " +
                          "and for QueryShape's own rule-based patch use `queryshape verify --patch-from-diagnosis`.");
            return 2;
        }

        var key = Environment.GetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            err.WriteLine($"fix --llm is disabled: set {AnthropicLlmClient.ApiKeyVariable} to enable it. Nothing was sent anywhere.");
            return 2;
        }

        var client = (ClientFactory ?? ((k, m) => new AnthropicLlmClient(k, m)))(key, Model);
        var progress = string.Equals(Format, "text", StringComparison.OrdinalIgnoreCase) ? out_ : err;

        var verify = new VerifyCommand
        {
            Project = Project,
            TestFilter = TestFilter,
            Runs = Runs,
            AllowDirty = AllowDirty,
            Format = Format,
            PatchProvider = async (baseline, work, repoRoot) =>
            {
                var candidates = baseline.Diagnostics
                    .Where(d => RuleFilter is null || string.Equals(d.RuleId, RuleFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.Severity == "Error" ? 2 : d.Severity == "Warning" ? 1 : 0)
                    .ThenBy(d => d.RuleId, StringComparer.Ordinal)
                    .ToList();
                if (candidates.Count == 0)
                {
                    err.WriteLine("fix: the baseline run produced no diagnosis" + (RuleFilter is null ? string.Empty : $" for {RuleFilter}") + "; nothing to fix.");
                    return null;
                }

                var target = candidates[0];
                progress.WriteLine();
                progress.WriteLine($"[llm] asking {client.Model} for a patch for {target.RuleId}: {target.Title}");
                var user = FixPrompt.Build(target, repoRoot);
                if (ShowPrompt)
                {
                    progress.WriteLine("---- prompt sent to " + client.Model + " (system) ----");
                    progress.WriteLine(FixPrompt.SystemPrompt);
                    progress.WriteLine("---- prompt sent to " + client.Model + " (user) ----");
                    progress.WriteLine(user);
                    progress.WriteLine("---- end of prompt ----");
                }

                string answer;
                try
                {
                    answer = await client.CompleteAsync(FixPrompt.SystemPrompt, user, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    err.WriteLine($"fix --llm: the model call failed: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }

                if (answer.Trim().StartsWith(LlmPatch.CannotMarker, StringComparison.Ordinal))
                {
                    err.WriteLine("fix --llm: the model declined (CANNOT): the fix needs changes outside the shown method or it was not confident. Nothing was measured.");
                    return null;
                }

                var diff = LlmPatch.Extract(answer);
                if (diff is null)
                {
                    err.WriteLine("fix --llm: the model did not return a unified diff. Its answer, unverified:");
                    err.WriteLine(answer);
                    return null;
                }

                if (LlmPatch.Validate(diff, repoRoot) is { } problem)
                {
                    err.WriteLine("fix --llm: rejected the model's patch before applying it: " + problem);
                    err.WriteLine(diff);
                    return null;
                }

                var patchPath = OutputPatch is not null ? Path.GetFullPath(OutputPatch) : Path.Combine(work, "llm.diff");
                await File.WriteAllTextAsync(patchPath, diff, ct);

                progress.WriteLine();
                progress.WriteLine($"==== LLM-proposed patch ({client.Model}) — generated, not yet verified; the table below is the proof ====");
                progress.Write(diff);
                progress.WriteLine("==== end of LLM-proposed patch ====");
                progress.WriteLine($"patch written to {patchPath}");

                return ($"LLM patch for {target.RuleId} ({client.Model}): {target.FixSummary ?? target.Title}", patchPath);
            },
        };

        return await verify.ExecuteAsync(out_, err, ct);
    }
}
