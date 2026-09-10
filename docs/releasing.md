# Releasing

A version pushed to NuGet.org cannot be replaced or deleted — only unlisted or deprecated, with the file still downloadable by anyone who names it. Everything below is designed so that mistakes are caught before that point.

## Before tagging

1. **Decide the version** and set it in `Directory.Build.props` (`VersionPrefix`, `VersionSuffix`). The release workflow refuses to publish a tag that disagrees with that file.
2. **Move the public API.** Anything new sits in each project's `PublicAPI.Unshipped.txt`; a release moves it into `PublicAPI.Shipped.txt` (`python3 scripts/update-public-api.py <project>` keeps Unshipped in sync while developing, then move the entries by hand or with a small script). After a release, removing or changing a shipped symbol is a deliberate, visible edit — that is the point.
3. **Green CI on the release commit**, on Linux and Windows, both runtimes.
4. **Check the names are still free** for a first release: `curl -s -o /dev/null -w '%{http_code}' https://api.nuget.org/v3-flatcontainer/queryshape.core/index.json` returns 404 when the id is unused.
5. **Pack and verify locally**:

   ```sh
   dotnet pack QueryShape.slnx --configuration Release --output artifacts
   scripts/verify-packages.sh artifacts 0.1.0-preview.1
   ```

   That installs the packed libraries into throwaway consumer projects — net10.0 on the current EF Core 10 patch, net8.0 on the EF Core 8 floor the libraries reference — detects an N+1 through the package rather than a project reference, and installs and runs the CLI as a tool. A missing dependency or a broken target framework fails here instead of in someone else's build.
6. **Dry run the workflow** (`Release` → *Run workflow* → version): it builds, tests, packs, verifies and uploads the packages as artifacts. It never pushes to NuGet on `workflow_dispatch`.

## Publishing

1. Store an API key scoped to these package ids as the `NUGET_API_KEY` repository secret. For a first push the key needs the *Push new packages and package versions* scope with a glob (`QueryShape.*`), because none of the ids exist yet.
2. Add required reviewers to the `nuget` environment if a tag should not be enough on its own.
3. Tag and push:

   ```sh
   git tag v0.1.0-preview.1
   git push origin v0.1.0-preview.1
   ```

   The workflow runs the full suite (including the slow end-to-end `verify` tests), packs, runs `scripts/verify-packages.sh`, pushes every `.nupkg` (symbols travel with them as `.snupkg`), and creates the GitHub release.
4. Indexing takes a few minutes. Then check one package end to end from nuget.org itself, in a directory with no local feed:

   ```sh
   dotnet new xunit -o /tmp/queryshape-install-check && cd /tmp/queryshape-install-check
   dotnet add package QueryShape.Testing --version 0.1.0-preview.1 --prerelease
   ```

## After publishing

- Update `docs/installation.md` and the README: the source-preview wording exists because no package was published. Say `dotnet add package QueryShape.Testing --prerelease` first, and keep building from source as the alternative.
- `docs/ci.md` states that the preview defaults to `0.1.0-preview.1` and that no package should be assumed on NuGet. Once one exists, name the published version the action's `version` input expects.
- Bump `VersionSuffix` in `Directory.Build.props` for the next development cycle, so builds after the release are not mistaken for it.

## What is published

| Package | What it is |
|---|---|
| `QueryShape.Core` | Capture, normalization, rules, diagnosis model |
| `QueryShape.Testing` | Snapshots, budgets, scenarios, reduction |
| `QueryShape.Testing.Xunit`, `.Xunit.v3`, `.NUnit`, `.MSTest` | `[QueryBudget]` adapters |
| `QueryShape.AspNetCore` | `app.UseQueryShape()` request scopes |
| `QueryShape.OpenTelemetry` | Activity tags, diagnosis events, metrics |
| `QueryShape.Analyzers` | Roslyn analyzer QSA001 (netstandard2.0, own cadence) |
| `QueryShape.Cli` | `dotnet queryshape` tool (packed as `dotnet-queryshape`) |
