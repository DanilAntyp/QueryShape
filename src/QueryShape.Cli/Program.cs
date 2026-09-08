using System.CommandLine;
using QueryShape.Cli;
using QueryShape.Cli.Commands;

var projectOption = new Option<string?>("--project", "-p") { Description = "Test project, solution or directory passed to `dotnet test` (default: current directory)." };
var testOption = new Option<string?>("--test", "-t") { Description = "Test filter, e.g. \"OrderServiceTests.GetOrders_query_shape\" (becomes --filter FullyQualifiedName~...)." };
var ruleOption = new Option<string?>("--rule") { Description = "Only consider diagnoses of this rule id (e.g. QS001)." };
var reportDirOption = new Option<string?>("--report-dir") { Description = "Read existing scope reports from this directory instead of running tests." };

var root = new RootCommand("QueryShape: checks EF Core query contracts and reports measured evidence.");

var doctorJson = new Option<bool>("--json");
var doctor = new Command("doctor", "Run an existing integration test and diagnose missing instrumentation, incomplete capture and observation coverage.")
{ projectOption, testOption, reportDirOption, doctorJson };
doctor.SetAction((parse, ct) => new SetupCommand { Project = parse.GetValue(projectOption), TestFilter = ToFilter(parse.GetValue(testOption)),
    ReportDirectory = parse.GetValue(reportDirOption), Json = parse.GetValue(doctorJson) }.DoctorAsync(Console.Out, Console.Error, ct));
root.Subcommands.Add(doctor);
var initOut = new Option<string>("--out") { DefaultValueFactory = _ => "QueryShapeSmokeTests.cs" };
var initFramework = new Option<string>("--framework") { DefaultValueFactory = _ => "xunit" };
initFramework.AcceptOnlyFromAmong("xunit", "nunit", "mstest");
var init = new Command("init", "Scaffold a smoke test around your existing operation; never overwrites files or silently changes dependencies.") { initOut, initFramework };
init.SetAction((parse, ct) => SetupCommand.InitAsync(parse.GetValue(initOut)!, parse.GetValue(initFramework)!, Console.Out, Console.Error, ct));
root.Subcommands.Add(init);

// verify
var patchOption = new Option<string?>("--patch") { Description = "Unified diff to apply in a clean worktree of HEAD." };
var fromDiagnosisOption = new Option<bool>("--patch-from-diagnosis") { Description = "Use the patch QueryShape itself proposed in the baseline run." };
var allowDirtyOption = new Option<bool>("--allow-dirty") { Description = "Proceed with uncommitted changes (they are carried into the worktree)." };
var runsOption = new Option<int>("--runs") { Description = "How many times to run the patched tests; the median is reported.", DefaultValueFactory = _ => 3 };
var keepOption = new Option<bool>("--keep-worktree") { Description = "Leave the temporary worktree in place for inspection." };
var runPartialOption = new Option<bool>("--run-partial") { Description = "With --patch-from-diagnosis: measure even when the patch is only part of the fix." };
var formatOption = new Option<string>("--format") { Description = "Output format: text (default), json, markdown. Progress goes to stderr for json/markdown.", DefaultValueFactory = _ => "text" };
formatOption.AcceptOnlyFromAmong("text", "json", "markdown");
var performanceOnlyOption = new Option<bool>("--performance-only") { Description = "Explicitly skip requiring result/state observations. Tests must still pass." };
var maxCommandsOption = new Option<int?>("--max-commands") { Description = "Total command budget; explicitly permits increases within this budget." };
var maxRowsOption = new Option<long?>("--max-rows") { Description = "Total returned-row budget; explicitly permits increases within this budget." };
var maxMsOption = new Option<double?>("--max-duration-ms") { Description = "Maximum summed command execution time; not endpoint latency." };
var durationIncreaseOption = new Option<double>("--max-duration-increase-percent") { DefaultValueFactory = _ => 10, Description = "Allowed command-duration increase, also allowing baseline spread and a 1 ms floor." };
var allowedWarningsOption = new Option<string[]>("--allow-new-warning") { Description = "Explicitly permit new finding identities for selected Warning rule IDs; never permits new Errors." };
var verify = new Command("verify", "Run tests before and after a patch and print a before/after table. Exit 0 when improved with no new Error diagnoses.")
{
    projectOption, testOption, patchOption, fromDiagnosisOption, ruleOption, allowDirtyOption, runsOption, keepOption, runPartialOption, formatOption, performanceOnlyOption, maxCommandsOption, maxRowsOption, maxMsOption, durationIncreaseOption, allowedWarningsOption,
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
    PerformanceOnly = parse.GetValue(performanceOnlyOption),
    RunPartial = parse.GetValue(runPartialOption),
    Policy = new VerificationPolicy { MaxCommands = parse.GetValue(maxCommandsOption), MaxRows = parse.GetValue(maxRowsOption), MaxDurationMs = parse.GetValue(maxMsOption), MaxDurationIncreasePercent = parse.GetValue(durationIncreaseOption), AllowedNewWarningRules = parse.GetValue(allowedWarningsOption) ?? [] },
    Format = parse.GetValue(formatOption) ?? "text",
}.ExecuteAsync(Console.Out, Console.Error, ct));
root.Subcommands.Add(verify);

// report
var jsonOption = new Option<bool>("--json") { Description = "Print the raw scope reports as JSON." };
var markdownOption = new Option<bool>("--markdown") { Description = "Print a Markdown summary (for $GITHUB_STEP_SUMMARY or a PR comment)." };
var acceptancesOption = new Option<string?>("--acceptances", "--baseline") { Description = "Reviewed finding acceptances with reasons, occurrence limits and expiry dates." };
var failOnOption = new Option<string>("--fail-on") { DefaultValueFactory = _ => "error", Description = "Severity that fails a report gate after applying acceptances." };
failOnOption.AcceptOnlyFromAmong("info", "warning", "error");
var report = new Command("report", "Run tests with QueryShape reporting and print every diagnosis with its fix.") { projectOption, testOption, reportDirOption, jsonOption, markdownOption, acceptancesOption, failOnOption };
report.SetAction((parse, ct) => new ReportCommand
{
    Project = parse.GetValue(projectOption),
    TestFilter = ToFilter(parse.GetValue(testOption)),
    ReportDirectory = parse.GetValue(reportDirOption),
    Json = parse.GetValue(jsonOption),
    Markdown = parse.GetValue(markdownOption),
    Acceptances = parse.GetValue(acceptancesOption),
    FailOn = parse.GetValue(failOnOption)!,
}.ExecuteAsync(Console.Out, Console.Error, ct));
root.Subcommands.Add(report);

var baselineOut = new Option<string>("--out") { Required = true };
var baselineReason = new Option<string>("--reason") { Required = true };
var baselineExpiry = new Option<string>("--expires") { Required = true };
var baselineId = new Option<string?>("--id") { Description = "Accept only this finding ID; omit to baseline all observed findings." };
var baseline = new Command("baseline", "Write reviewable, expiring acceptances from complete captured reports. Existing files are never overwritten.")
{ reportDirOption, baselineOut, baselineReason, baselineExpiry, baselineId };
baseline.SetAction((parse, ct) => new BaselineCommand
{
    ReportDirectory = parse.GetValue(reportDirOption) ?? "", OutputPath = parse.GetValue(baselineOut)!,
    Reason = parse.GetValue(baselineReason)!, Expires = parse.GetValue(baselineExpiry)!, FindingId = parse.GetValue(baselineId),
}.ExecuteAsync(Console.Out, Console.Error, ct));
root.Subcommands.Add(baseline);

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
var fix = new Command("fix", "Ask a language model for a patch for the worst diagnosis of a test, then check its measurements and observations with verify. Requires --llm and ANTHROPIC_API_KEY.")
{
    projectOption, testOption, ruleOption, llmOption, modelOption, showPromptOption, outPatchOption, runsOption, allowDirtyOption, formatOption, performanceOnlyOption, maxCommandsOption, maxRowsOption, maxMsOption, durationIncreaseOption, allowedWarningsOption,
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
    Policy = new VerificationPolicy { MaxCommands = parse.GetValue(maxCommandsOption), MaxRows = parse.GetValue(maxRowsOption), MaxDurationMs = parse.GetValue(maxMsOption), MaxDurationIncreasePercent = parse.GetValue(durationIncreaseOption), AllowedNewWarningRules = parse.GetValue(allowedWarningsOption) ?? [] },
    PerformanceOnly = parse.GetValue(performanceOnlyOption),
    Runs = parse.GetValue(runsOption),
    AllowDirty = parse.GetValue(allowDirtyOption),
    Format = parse.GetValue(formatOption) ?? "text",
}.ExecuteAsync(Console.Out, Console.Error, ct));
root.Subcommands.Add(fix);

var sizesOption = new Option<string?>("--sizes") { Description = "Comma-separated dataset sizes, for example 10,100,1000." };
var scale = new Command("scale", "Run scenario tests across dataset sizes and check command/row growth contracts.")
{ projectOption, testOption, reportDirOption, sizesOption, jsonOption };
scale.SetAction((parse, ct) => new ScenarioCommand
{
    Kind = "scaling", Project = parse.GetValue(projectOption), TestFilter = ToFilter(parse.GetValue(testOption)),
    ReportDirectory = parse.GetValue(reportDirOption), Sizes = parse.GetValue(sizesOption), Json = parse.GetValue(jsonOption),
}.ExecuteAsync(Console.Out, Console.Error, ct));
root.Subcommands.Add(scale);
var reductionOut = new Option<string?>("--out") { Description = "Directory for reduced JSON inputs and generated regression tests." };
var attempts = new Option<int>("--max-attempts") { DefaultValueFactory = _ => 200 };
var reduce = new Command("reduce", "Run a reduction scenario and retain smaller inputs reproducing the same failure.")
{ projectOption, testOption, reportDirOption, reductionOut, attempts, jsonOption };
reduce.SetAction((parse, ct) => new ScenarioCommand
{
    Kind = "reduction", Project = parse.GetValue(projectOption), TestFilter = ToFilter(parse.GetValue(testOption)),
    ReportDirectory = parse.GetValue(reportDirOption), OutputDirectory = parse.GetValue(reductionOut),
    MaxAttempts = parse.GetValue(attempts), Json = parse.GetValue(jsonOption),
}.ExecuteAsync(Console.Out, Console.Error, ct));
root.Subcommands.Add(reduce);

try { return await root.Parse(args).InvokeAsync(); }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
{
    Console.Error.WriteLine("queryshape: " + ex.Message);
    return 2;
}

static string? ToFilter(string? test)
    => string.IsNullOrWhiteSpace(test) ? null : test.Contains('~', StringComparison.Ordinal) || test.Contains('=', StringComparison.Ordinal) ? test : "FullyQualifiedName~" + test;
