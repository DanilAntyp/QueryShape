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

    public async Task<int> ExecuteAsync(TextWriter out_, TextWriter err, CancellationToken ct)
    {
        if (PatchPath is null && !PatchFromDiagnosis)
        {
            err.WriteLine("verify: pass --patch <file.diff> or --patch-from-diagnosis.");
            return 2;
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

        var dirty = await ProcessRunner.GitAsync(repoRoot, "status", "--porcelain", "--untracked-files=no");
        if (dirty.Length > 0 && !AllowDirty)
        {
            err.WriteLine("verify: the repository has uncommitted changes. Commit or stash them, or pass --allow-dirty to carry them into the worktree:");
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

        string patchFile;
        string fixTitle;
        if (PatchFromDiagnosis)
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
            await File.WriteAllTextAsync(patchFile, string.Concat(diffs.Select(d => d.UnifiedDiff!.EndsWith('\n') ? d.UnifiedDiff : d.UnifiedDiff + "\n")), ct);
            fixTitle = string.Join(" | ", diffs.Select(d => d.FixSummary ?? d.Title).Distinct());
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
                    out_.WriteLine("  carried uncommitted changes into the worktree (--allow-dirty); untracked files are not included");
                }
            }

            var apply = await ProcessRunner.RunAsync("git", ["apply", "--whitespace=nowarn", patchFile], worktree);
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
                if (!afterProcess.Success && i == 0)
                {
                    notes.Add($"dotnet test exited with code {afterProcess.ExitCode} after the patch; expected when the test asserts the old query count or a snapshot, otherwise check the test output");
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
            if (!baselineProcess.Success)
            {
                notes.Add($"dotnet test exited with code {baselineProcess.ExitCode} in the baseline run");
            }

            var table = VerifyTable.Render(fixTitle, before, median, afterRuns, notes);
            out_.WriteLine();
            out_.Write(table.Text);
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

    private static string Tail(string text, int lines)
    {
        var all = text.Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }
}
