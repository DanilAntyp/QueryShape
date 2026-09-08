namespace QueryShape.Cli.Commands;

/// <summary>`queryshape verify`: prove a fix with before/after numbers.</summary>
internal sealed class VerifyCommand
{
    public required string? Project { get; init; }

    public required string TestFilter { get; init; }

    public string? PatchPath { get; init; }

    public bool PatchFromDiagnosis { get; init; }

    public string? RuleFilter { get; init; }

    public bool AllowDirty { get; init; }

    public int Runs { get; init; } = 3;

    public bool KeepWorktree { get; init; }

    public bool PerformanceOnly { get; init; }
    public VerificationPolicy Policy { get; init; } = new();

    /// <summary>Run the after leg even when the diagnosis patch is partial (default: refuse, since the table would be misleading).</summary>
    public bool RunPartial { get; init; }

    /// <summary>text (default), json or markdown. Progress goes to stderr for json/markdown so stdout is the document.</summary>
    public string Format { get; init; } = "text";

    /// <summary>
    /// Alternative patch source used after the baseline run (the `fix` command's model call). Receives the baseline metrics, the working directory and
    /// the repository root; returns the fix title and a patch path, or <c>null</c> when there is nothing to measure.
    /// </summary>
    public Func<RunMetrics, string, string, Task<(string FixTitle, string PatchPath)?>>? PatchProvider { get; init; }

    public async Task<int> ExecuteAsync(TextWriter out_, TextWriter err, CancellationToken ct)
    {
        try { Policy.Validate(); }
        catch (ArgumentException ex) { err.WriteLine(ex.Message); return 2; }
        if (PatchPath is null && !PatchFromDiagnosis && PatchProvider is null)
        {
            err.WriteLine("verify: pass --patch <file.diff> or --patch-from-diagnosis.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(TestFilter) || Runs < 1 || Runs > 20)
        {
            err.WriteLine("verify: select a test with --test and use --runs between 1 and 20.");
            return 2;
        }

        var document = out_;
        if (!string.Equals(Format, "text", StringComparison.OrdinalIgnoreCase))
        {
            out_ = err; // progress lines go to stderr; the document (json/markdown) is the only thing on stdout
        }

        var cwd = Directory.GetCurrentDirectory();
        string repoRoot;
        try
        {
            repoRoot = await ProcessRunner.GitAsync(cwd, "rev-parse", "--show-toplevel");
        }
        catch (InvalidOperationException ex)
        {
            err.WriteLine("verify: not inside a git repository (" + ex.Message + ").");
            return 2;
        }

        // Untracked (non-ignored) files count: a patch that needs one would build here and fail in the worktree.
        var dirty = await ProcessRunner.GitAsync(repoRoot, "status", "--porcelain", "--untracked-files=all");
        if (dirty.Length > 0 && !AllowDirty)
        {
            err.WriteLine("verify: the repository has uncommitted changes or untracked files. Commit or stash them, or pass --allow-dirty to carry them into the worktree:");
            err.WriteLine(dirty);
            return 2;
        }

        var projectRelative = Project is null ? null : Path.GetRelativePath(repoRoot, Path.GetFullPath(Project, cwd));
        var work = Path.Combine(Path.GetTempPath(), "queryshape-verify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        out_.WriteLine($"QueryShape verify: {TestFilter}");
        out_.WriteLine($"repository: {repoRoot}");
        out_.WriteLine();
        out_.WriteLine("[1/3] baseline");
        var (before, baselineProcess) = await TestRun.RunAsync(repoRoot, projectRelative, TestFilter, Path.Combine(work, "before"), noBuild: false, null, out_, ct);
        if (!baselineProcess.Success)
        {
            err.WriteLine("verify: baseline tests failed. Separate behavior tests from assertions that require the old query shape.");
            err.WriteLine(Tail(baselineProcess.StdOut + baselineProcess.StdErr, 40));
            return 2;
        }
        if (baselineProcess.ExecutedTests is not > 0)
        {
            err.WriteLine("verify: no executed tests confirmed by the TRX logger (empty filter, skipped tests, or unsupported runner).");
            return 2;
        }
        if (before.Scopes == 0)
        {
            err.WriteLine("verify: the baseline run produced no QueryShape scope reports.");
            err.WriteLine("        The test must open a scope (QueryShapeScope.Begin / QueryShapeTest.Begin / MatchSnapshotAsync, or the QueryShape middleware under WebApplicationFactory)");
            err.WriteLine("        and the filter must match at least one test. dotnet test exit code: " + baselineProcess.ExitCode);
            if (!baselineProcess.Success)
            {
                err.WriteLine(Tail(baselineProcess.StdOut + baselineProcess.StdErr, 40));
            }

            return 2;
        }

        var baselineProblems = BehaviorVerification.Compare(before, before, PerformanceOnly);
        if (baselineProblems.Count > 0)
        {
            foreach (var problem in baselineProblems) err.WriteLine("verify: " + problem);
            return 2;
        }

        var baselineRuns = new List<RunMetrics> { before };
        for (var i = 1; i < Runs; i++)
        {
            var (repeat, process) = await TestRun.RunAsync(repoRoot, projectRelative, TestFilter, Path.Combine(work, $"before-{i}"), true, null, out_, ct);
            if (!process.Success || !SameTests(baselineProcess, process) || BehaviorVerification.Compare(before, repeat, PerformanceOnly).Count > 0)
            {
                err.WriteLine("verify: baseline execution or recorded behavior is unstable; make the fixture deterministic before comparing a patch.");
                return 2;
            }
            baselineRuns.Add(repeat);
        }
        before = RunMetrics.Median(baselineRuns);
        var baselineSpread = baselineRuns.Max(r => r.DurationMs) - baselineRuns.Min(r => r.DurationMs);

        string patchFile;
        string fixTitle;
        if (PatchProvider is not null)
        {
            var provided = await PatchProvider(before, work, repoRoot);
            if (provided is null)
            {
                return 2;
            }

            (fixTitle, patchFile) = provided.Value;
        }
        else if (PatchFromDiagnosis)
        {
            var diffs = before.Diagnostics
                .Where(d => d.UnifiedDiff is not null && (RuleFilter is null || string.Equals(d.RuleId, RuleFilter, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (diffs.Count == 0)
            {
                err.WriteLine("verify: no diagnosis in the baseline carries a unified diff" + (RuleFilter is null ? string.Empty : $" for {RuleFilter}") +
                              ". QueryShape produces one when the call site is known (CaptureCallSites) and the source file is readable.");
                return 2;
            }

            patchFile = Path.Combine(work, "from-diagnosis.diff");
            await File.WriteAllTextAsync(patchFile, CombinePatches(diffs.Select(d => d.UnifiedDiff!)), ct);
            fixTitle = string.Join(" | ", diffs.Select(d => d.FixSummary ?? d.Title).Distinct());

            var partial = diffs.Where(d => d.FixIsPartial).ToList();
            if (partial.Count > 0)
            {
                out_.WriteLine();
                out_.WriteLine("partial fix, manual step required:");
                foreach (var d in partial)
                {
                    out_.WriteLine($"  {d.RuleId}: {d.ManualStep ?? "see the diagnosis"}");
                }

                if (!RunPartial)
                {
                    out_.WriteLine();
                    out_.WriteLine("The proposed patch is only part of the fix, so a before/after table would not measure the fix. Apply the manual step, commit, and verify with --patch, or pass --run-partial to measure the partial patch anyway.");
                    return 2;
                }

                out_.WriteLine("  (--run-partial: measuring the partial patch anyway)");
            }
        }
        else
        {
            patchFile = Path.GetFullPath(PatchPath!, cwd);
            if (!File.Exists(patchFile))
            {
                err.WriteLine($"verify: patch file not found: {patchFile}");
                return 2;
            }

            fixTitle = Path.GetFileName(patchFile);
        }

        out_.WriteLine();
        out_.WriteLine("[2/3] worktree + patch");
        var worktree = Path.Combine(work, "wt");
        try
        {
            await ProcessRunner.GitAsync(repoRoot, "worktree", "add", "--detach", worktree, "HEAD");
            if (dirty.Length > 0)
            {
                var local = Path.Combine(work, "local-changes.diff");
                var diff = await ProcessRunner.RunAsync("git", ["diff", "HEAD", "--binary"], repoRoot);
                await File.WriteAllTextAsync(local, diff.StdOut, ct);
                if (diff.StdOut.Length > 0)
                {
                    await ProcessRunner.GitAsync(worktree, "apply", "--whitespace=nowarn", local);
                }

                var untracked = await CopyUntrackedAsync(repoRoot, worktree);
                out_.WriteLine($"  carried uncommitted changes into the worktree (--allow-dirty): {(diff.StdOut.Length > 0 ? "modified tracked files" : "no tracked changes")}, {untracked} untracked file(s)");
            }

            // --recount: hunk line counts are recomputed from the patch body, so a hand- or model-written hunk header does not have to be exact.
            var apply = await ProcessRunner.RunAsync("git", ["apply", "--recount", "--whitespace=nowarn", patchFile], worktree);
            if (!apply.Success)
            {
                err.WriteLine("verify: the patch does not apply to a clean checkout of HEAD:");
                err.WriteLine(apply.StdErr.Trim());
                return 2;
            }

            out_.WriteLine($"  applied {Path.GetFileName(patchFile)} in {worktree}");

            out_.WriteLine();
            out_.WriteLine($"[3/3] after ({Runs} runs, median)");
            var afterRuns = new List<RunMetrics>();
            var notes = new List<string>();
            for (var i = 0; i < Math.Max(1, Runs); i++)
            {
                var (after, afterProcess) = await TestRun.RunAsync(worktree, projectRelative, TestFilter, Path.Combine(work, $"after-{i}"), noBuild: i > 0, null, out_, ct);
                if (!afterProcess.Success)
                {
                    err.WriteLine("verify: patched tests failed; a faster failing test is not an accepted fix.");
                    err.WriteLine(Tail(afterProcess.StdOut + afterProcess.StdErr, 40));
                    return 2;
                }
                if (!SameTests(baselineProcess, afterProcess))
                {
                    err.WriteLine("verify: executed test coverage changed after the patch.");
                    return 2;
                }

                if (after.Scopes == 0)
                {
                    err.WriteLine("verify: the patched run produced no QueryShape scope reports (did the test fail to build or run?). dotnet test exit code: " + afterProcess.ExitCode);
                    err.WriteLine(Tail(afterProcess.StdOut + afterProcess.StdErr, 40));
                    return 2;
                }

                afterRuns.Add(after);
            }

            var median = RunMetrics.Median(afterRuns);
            var blockers = afterRuns.SelectMany(after => BehaviorVerification.Compare(before, after, PerformanceOnly)).Distinct().ToList();
            blockers.AddRange(afterRuns.SelectMany(after => Policy.Check(before, after, baselineSpread)).Distinct());
            foreach (var problem in blockers) err.WriteLine("verify: " + problem);
            notes.AddRange(blockers);
            var behaviorChecked = BehaviorVerification.Observations(before).Count > 0;
            notes.Add(behaviorChecked ? "Behavior observations preserved in every patched run; checks cover only the recorded result/state projections and fixtures."
                : "Performance-only verification: behavior preservation was not checked.");
            var coverage = before.Reports.SelectMany(r => (r.Annotations ?? new Dictionary<string, string>())
                .Where(a => a.Key.StartsWith("behavior.coverage.", StringComparison.Ordinal)).Select(a => a.Key + "=" + a.Value)).Distinct();
            notes.Add("Observation coverage: " + string.Join("; ", coverage.DefaultIfEmpty("explicit named observations only; unrecorded outputs/state are unchecked")));
            notes.Add("Budgets checked in every patched run. Duration is summed command execution time, not endpoint latency or server rows scanned.");
            if (Policy.AllowedNewWarningRules.Count > 0) notes.Add("Explicitly permitted new warning identities for: " + string.Join(", ", Policy.AllowedNewWarningRules) + ". Error findings remain blocking.");
            var table = VerifyTable.Render(fixTitle, before, median, afterRuns, notes, Policy, baselineSpread);
            if (blockers.Count > 0) table = table with { Improved = false, Text = table.Text.Replace("verdict: improved", "verdict: not improved", StringComparison.Ordinal) };
            document.Write(Format == "json" ? table.ToJson() : Format == "markdown" ? table.ToMarkdown() : table.Text);
            return table.Improved ? 0 : 1;
        }
        finally
        {
            if (!KeepWorktree)
            {
                try
                {
                    await ProcessRunner.RunAsync("git", ["worktree", "remove", "--force", worktree], repoRoot);
                    Directory.Delete(work, recursive: true);
                }
                catch (Exception)
                {
                    // Best-effort cleanup.
                }
            }
            else
            {
                out_.WriteLine($"worktree kept at {worktree}");
            }
        }
    }

    internal static bool SameTests(ProcessResult before, ProcessResult after) => before.ExecutedTestIdentities is { Count: > 0 } identities
        && after.ExecutedTestIdentities is { } actual && identities.SequenceEqual(actual);

    // Repeated scopes and target frameworks can propose the same source edit. Apply it once.
    internal static string CombinePatches(IEnumerable<string> patches)
        => string.Concat(patches.Select(p => p.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n").Distinct(StringComparer.Ordinal));

    /// <summary>Copies untracked, non-ignored files (git ls-files --others --exclude-standard) into the worktree so a patch that needs them builds there too.</summary>
    private static async Task<int> CopyUntrackedAsync(string repoRoot, string worktree)
    {
        var listed = await ProcessRunner.GitAsync(repoRoot, "ls-files", "--others", "--exclude-standard", "-z");
        var count = 0;
        foreach (var relative in listed.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var source = Path.Combine(repoRoot, relative);
            if (!File.Exists(source))
            {
                continue;
            }

            var target = Path.Combine(worktree, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            count++;
        }

        return count;
    }

    private static string Tail(string text, int lines)
    {
        var all = text.Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }
}
