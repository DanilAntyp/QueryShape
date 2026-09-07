# ADR-0001: Name is QueryShape; ASP.NET Core middleware ships in its own package

Date: 2026-09-07. Status: accepted.

## Context
CLAUDE.md was written with the working name *EfLens*. The repository, solution and the owner's decision use **QueryShape**.
Section 4.2 asks for ASP.NET Core middleware (`app.UseQueryShape()`), which needs a `FrameworkReference` to `Microsoft.AspNetCore.App`.
Putting that reference in `QueryShape.Core` would drag the ASP.NET shared framework into every consumer, including test projects and the CLI.

## Decision
- Everything is named QueryShape: namespaces `QueryShape.*`, packages `QueryShape.Core`, `QueryShape.Testing`, `QueryShape.AspNetCore`,
  `QueryShape.OpenTelemetry`, `QueryShape.Cli` (tool command `queryshape`), rule ids `QS001`…`QS010`, overflow marker `QS_OVERFLOW`,
  OpenTelemetry attribute prefix `queryshape.`, meter `QueryShape`, env var `QUERYSHAPE_UPDATE_SNAPSHOTS`, snapshot folder `__querysnapshots__`.
- The middleware lives in `src/QueryShape.AspNetCore` (one extra project versus section 3). Core keeps zero framework references beyond EF Core.

## Consequences
- The three-line setup becomes `services.AddQueryShape()`, `options.UseQueryShape()`, `app.UseQueryShape()` with two package references for web apps.
- CLAUDE.md in this repository is the renamed copy and is the source of truth from here on.
