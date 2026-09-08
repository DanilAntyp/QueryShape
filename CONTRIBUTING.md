# Contributing to QueryShape

Useful contributions include a query that produces a confusing finding, a first-run problem, a reproducible missed regression, or a documented trial against another application.

## Run the project

Install Git and the .NET 10 SDK. CI also installs the native .NET 8 runtime. From the repository root:

```sh
dotnet build QueryShape.slnx
dotnet test QueryShape.slnx --filter 'Category!=Slow'
dotnet test tests/QueryShape.Cli.Tests --filter 'Category=Slow'
```

Run the slow tests separately: they rebuild sample projects while checking Git worktree verification. Docker enables SQL Server/PostgreSQL tests; skipped provider tests do not validate those providers. External application harnesses under `scripts/real-world` are opt-in and document their pinned upstream revisions.

## Send a useful change

1. Start from a failing scenario or a concrete documentation problem.
2. Keep the change focused. Rules need a reproduction, test coverage, and matching `docs/rules` documentation.
3. Keep public API files in sync; the build reports changes through PublicApiAnalyzers. See `scripts/update-public-api.py`.
4. Run the checks affected by your change. Include the result, runtime and any skips in the pull request.

Read [CLAUDE.md](CLAUDE.md) for architecture and repository conventions, and [the ADRs](docs/adr) for decisions. New evidence should separate actual upstream findings from intentionally broken candidates, state fixture adaptations, and distinguish returned rows from server work. Never include credentials, connection strings, customer data or private source in a reproduction.

## Report a problem

Include the QueryShape and EF Core versions, database provider, selected test/CLI command, expected behavior, actual output, and the smallest synthetic reproduction you can share. For a noisy finding, include its rule ID and why the query is intentional. Redact sensitive values and source paths before attaching reports.

Contributions are made under the repository's [MIT license](LICENSE).
