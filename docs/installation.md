# Install the source preview

The repository is usable before a public NuGet release. These instructions build the packages locally; they do not assume that `QueryShape.Cli` has been published to nuget.org.

## Run without installing

Install the .NET 10 SDK and Git, then:

```sh
git clone https://github.com/DanilAntyp/QueryShape.git
cd QueryShape
dotnet run --project src/QueryShape.Cli -f net10.0 -- --help
```

Replace `--help` with any documented CLI command. The [README demo](../README.md#try-it-now) needs no database server. Package restore needs access to NuGet.

## Build and install the CLI

From the repository root, build all library/tool packages:

```sh
dotnet pack QueryShape.slnx --configuration Release --output artifacts/packages
dotnet tool install QueryShape.Cli --tool-path artifacts/tools --version 0.1.0-preview.1 --add-source artifacts/packages
```

Run the installed executable:

```sh
./artifacts/tools/dotnet-queryshape --help
```

On Windows PowerShell use `./artifacts/tools/dotnet-queryshape.exe`. Add the absolute `artifacts/tools` directory to your `PATH` to use `dotnet queryshape` from any project. Install the CLI and libraries from the same build. For an existing tool installation, use `dotnet tool update` with the same options.

## Add the library to your application

For an immediate source-based trial, add a reference from your test project to the checkout (replace the paths):

```sh
dotnet add tests/Shop.Tests/Shop.Tests.csproj reference /absolute/path/to/QueryShape/src/QueryShape.Testing/QueryShape.Testing.csproj
```

For a package-based trial, register the absolute `artifacts/packages` directory as an additional source in your application's `NuGet.Config`, keeping nuget.org for EF Core and other dependencies:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="queryshape-local" value="/absolute/path/to/QueryShape/artifacts/packages" />
  </packageSources>
</configuration>
```

Merge that entry into an existing configuration rather than replacing it. Then:

```sh
dotnet add tests/Shop.Tests/Shop.Tests.csproj package QueryShape.Testing --version 0.1.0-preview.1
```

Use the matching EF Core major: .NET 8 / EF8 or .NET 10 / EF10. `QueryShape.Testing` includes the core capture dependency. Register `.UseQueryShape()`, open a scope around the operation, and [add your first contract](getting-started.md).

## Choose only the integrations you need

| Package | Purpose |
|---|---|
| `QueryShape.Testing` | Snapshots, observations, scenarios, scaling and reduction |
| `QueryShape.Core` | Capture and rule analysis without testing adapters |
| `QueryShape.Testing.Xunit` / `.Xunit.v3` | xUnit budget attributes |
| `QueryShape.Testing.NUnit` / `.MSTest` | NUnit / MSTest budget attributes |
| `QueryShape.AspNetCore` | Per-request scopes and middleware |
| `QueryShape.OpenTelemetry` | Findings attached to existing spans |
| `QueryShape.Analyzers` | Compile-time QSA001 analysis |
| `QueryShape.Cli` | Reports, verification, setup, baselines, scaling and reduction |
