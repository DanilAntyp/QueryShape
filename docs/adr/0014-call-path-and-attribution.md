# ADR 0014: Call path, and which frame a query is attributed to

Scanning Jellyfin showed the limit of reporting one frame. Every finding pointed at `BaseItemRepository`, the data-access layer that runs the query — correct, but the same query is reached from several callers, and what decides its cost is the `DtoOptions` the caller passed. The frame QueryShape named was not the frame a reader can act on.

The stack walk now keeps the frames above the first one: `CallPathDepth` (default 3, innermost first, hard cap 8) records them on `CapturedCommand.CallPath` and on `CommandStart`. This costs no extra walk. Building `StackTrace(fNeedFileInfo: true)` resolves symbols for every frame up front and dominates the ≈ 15 µs; keeping three of the resulting frames instead of one adds comparisons and a small array, and the loop stops once it has the frames it needs. `CallPathDepth = 1` records only the call site, as before.

`InfrastructurePrefixes` (assembly-name or namespace prefixes, empty by default) marks code that issues queries on behalf of its callers: repositories, specification evaluators, an in-house data-access library. Frames matching it stay in the path but are not blamed; the call site becomes the first caller above them that has source information. Matching accepts namespaces as well as assembly names because layering is often a namespace convention inside one assembly. Nothing is inferred: an unconfigured application behaves exactly as before.

Attribution and patching are separate. A blamed caller says which operation asked for the data; only the frame that wrote the LINQ can be edited, so `RuleHelpers.PatchSite` targets the innermost user frame with source and every rule's unified diff goes there. Blaming the caller must never move a patch onto a line that does not contain the query.

The path is attached to diagnoses centrally, in `QueryShapeScope.Analyze`, keyed by the diagnosis's fingerprints, so rules stay pure functions of the scope and every rule reports it identically. It renders as a `from A ← B ← C` line in failure messages and as `callPath` on a scope report's diagnoses.

Telemetry keeps `queryshape.callsite` only. A path is several file paths per span, its cardinality is unbounded, and section 8's attribute list is deliberately short; anyone who needs it can read the scope report.
