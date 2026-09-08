using System.CommandLine;
using QueryShape.Cli.Commands;

var projectOption = new Option<string?>("--project", "-p") { Description = "Test project, solution or directory passed to `dotnet test` (default: current directory)." };
var testOption = new Option<string?>("--test", "-t") { Description = "Test filter, e.g. \"OrderServiceTests.GetOrders_query_shape\" (becomes --filter FullyQualifiedName~...)." };
var ruleOption = new Option<string?>("--rule") { Description = "Only consider diagnoses of this rule id (e.g. QS001)." };
var reportDirOption = new Option<string?>("--report-dir") { Description = "Read existing scope reports from this directory instead of running tests." };

var root = new RootCommand("QueryShape: detects slow and dangerous EF Core queries and proves fixes with numbers.");

// verify
var patchOption = new Option<string?>("--patch") { Description = "Unified diff to apply in a clean worktree of HEAD." };
var fromDiagnosisOption = new Option<bool>("--patch-from-diagnosis") { Description = "Use the patch QueryShape itself proposed in the baseline run." };
var allowDirtyOption = new Option<bool>("--allow-dirty") { Description = "Proceed with uncommitted changes (they are carried into the worktree)." };
var runsOption = new Option<int>("--runs") { Description = "How many times to run the patched tests; the median is reported.", DefaultValueFactory = _ => 3 };
var keepOption = new Option<bool>("--keep-worktree") { Description = "Leave the temporary worktree in place for inspection." };
var runPartialOption = new Option<bool>("--run-partial") { Description = "With --patch-from-diagnosis: measure even when the patch is only part of the fix." };
var formatOption = new Option<string>("--format") { Description = "Output format: text (default), json, markdown. Progress goes to stderr for json/markdown.", DefaultValueFactory = _ => "text" };
formatOption.AcceptOnlyFromAmong("text", "json", "markdown");
var verify = new Command("verify", "Run tests before and after a patch and print a before/after table. Exit 0 when improved with no new Error diagnoses.")
{
    projectOption, testOption, patchOption, fromDiagnosisOption, ruleOption, allowDirtyOption, runsOption, keepOption, runPartialOption, formatOption,
};
verify.SetAction((parse, ct) => new VerifyCommand
{
    Project = parse.GetValue(projectOption),
    TestFilter = ToFilter(parse.GetValue(testOption)) ?? string.Empty,
    PatchPath = parse.GetValue(patchOption),
    PatchFromDiagnosis = parse.GetValue(fromDiagnosisOption),
    RuleFilter = parse.GetValue(ruleOption),
    AllowDirty = parse.GetValue(allowDirtyOption),
    Runs = parse.GetValue(runsOption),
    KeepWorktree = parse.GetValue(keepOption),
    RunPartial = parse.GetValue(runPartialOption),
    Format = parse.GetValue(formatOption) ?? "text",
}.ExecuteAsync(Console.Out, Console.Error, ct));
root.Subcommands.Add(verify);

// report
var jsonOption = new Option<bool>("--json") { Description = "Print the raw scope reports as JSON." };
var markdownOption = new Option<bool>("--markdown") { Description = "Print a Markdown summary (for $GITHUB_STEP_SUMMARY or a PR comment)." };
var report = new Command("report", "Run tests with QueryShape reporting and print every diagnosis with its fix.") { projectOption, testOption, reportDirOption, jsonOption, markdownOption };
report.SetAction((parse, ct) => new ReportCommand
{
    Project = parse.GetValue(projectOption),
    TestFilter = ToFilter(parse.GetValue(testOption)),
    ReportDirectory = parse.GetValue(reportDirOption),
    Json = parse.GetValue(jsonOption),
    Markdown = parse.GetValue(markdownOption),
}.ExecuteAsync(Console.Out, Console.Error, ct));
root.Subcommands.Add(report);

// snapshots update
var snapshots = new Command("snapshots", "Manage query snapshots.");
var update = new Command("update", "Rewrite the snapshots of the matched tests (runs `dotnet test` with QUERYSHAPE_UPDATE_SNAPSHOTS=1).") { projectOption, testOption };
update.SetAction((parse, ct) => new SnapshotsUpdateCommand
{
    Project = parse.GetValue(projectOption),
    TestFilter = ToFilter(parse.GetValue(testOption)),
}.ExecuteAsync(Console.Out, Console.Error, ct));
snapshots.Subcommands.Add(update);
root.Subcommands.Add(snapshots);

// explain
var llmOption = new Option<bool>("--llm") { Description = "Ask an LLM (Anthropic Messages API, needs ANTHROPIC_API_KEY) for a richer explanation and diff. Off by default." };
var modelOption = new Option<string?>("--model") { Description = "Model id for --llm (default claude-opus-5)." };
var showPromptOption = new Option<bool>("--show-prompt") { Description = "Print exactly what is sent to the model." };
var explain = new Command("explain", "Explain the diagnoses of a test run. Rule-based by default; --llm adds a model-generated explanation, clearly labeled.")
{
    projectOption, testOption, reportDirOption, ruleOption, llmOption, modelOption, showPromptOption,
};
explain.SetAction((parse, ct) => new ExplainCommand
{
    Project = parse.GetValue(projectOption),
    TestFilter = ToFilter(parse.GetValue(testOption)),
    ReportDirectory = parse.GetValue(reportDirOption),
    RuleFilter = parse.GetValue(ruleOption),
    UseLlm = parse.GetValue(llmOption),
    Model = parse.GetValue(modelOption),
    ShowPrompt = parse.GetValue(showPromptOption),
}.ExecuteAsync(Console.Out, Console.Error, ct));
root.Subcommands.Add(explain);

// fix --llm
var outPatchOption = new Option<string?>("--out") { Description = "Write the model's patch here (default: the temporary work directory)." };
var fix = new Command("fix", "Ask a language model for a patch for the worst diagnosis of a test, then prove or reject it with verify. Requires --llm and ANTHROPIC_API_KEY.")
{
    projectOption, testOption, ruleOption, llmOption, modelOption, showPromptOption, outPatchOption, runsOption, allowDirtyOption, formatOption,
};
fix.SetAction((parse, ct) => new FixCommand
{
    Project = parse.GetValue(projectOption),
    TestFilter = ToFilter(parse.GetValue(testOption)) ?? string.Empty,
    RuleFilter = parse.GetValue(ruleOption),
    UseLlm = parse.GetValue(llmOption),
    Model = parse.GetValue(modelOption),
    ShowPrompt = parse.GetValue(showPromptOption),
    OutputPatch = parse.GetValue(outPatchOption),
    Runs = parse.GetValue(runsOption),
    AllowDirty = parse.GetValue(allowDirtyOption),
    Format = parse.GetValue(formatOption) ?? "text",
}.ExecuteAsync(Console.Out, Console.Error, ct));
root.Subcommands.Add(fix);

return await root.Parse(args).InvokeAsync();

static string? ToFilter(string? test)
    => string.IsNullOrWhiteSpace(test) ? null : test.Contains('~', StringComparison.Ordinal) || test.Contains('=', StringComparison.Ordinal) ? test : "FullyQualifiedName~" + test;
