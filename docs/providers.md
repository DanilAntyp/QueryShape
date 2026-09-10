# Providers

QueryShape captures through EF Core's own interceptors and a `DbConnection` interceptor, so nothing in the capture path is written against a
particular database. `QueryShape.Core` depends on `Microsoft.EntityFrameworkCore.Relational` and nothing else; there is no provider package
anywhere in its dependency graph. What differs between providers is the SQL text, and that is handled in normalization.

Every example in this repository uses SQLite because it needs no server and starts in microseconds. That is a property of the tests, not a limit
of the tool.

## What is actually run

| Provider | Where it runs | What is verified |
|---|---|---|
| SQLite | Every test run, everywhere | The full rule set, snapshots, scenarios, the sample app, three external applications |
| SQL Server 2022 | CI on Linux, every push (Testcontainers) | Shape normalization, expression→command correlation, QS001 and QS004 on real SQL Server SQL |
| PostgreSQL 16 | CI on Linux, every push (Testcontainers) | The same, on Npgsql |
| MySQL 8.4 | CI on Linux, every push (Testcontainers) | The same, on `MySql.EntityFrameworkCore` |
| Oracle Free 23 | The manual **Provider integration** workflow (`QUERYSHAPE_ORACLE=1`) | The same, on `Oracle.EntityFrameworkCore` |

The **Provider integration** workflow runs all four on EF Core 10 and the first three on EF Core 8; its last run was green on
[2026-09-10](https://github.com/DanilAntyp/QueryShape/actions/runs/34498861141).

Windows CI skips all of these: its Docker daemon serves Windows containers, and these images are Linux. Local runs skip them too unless Docker is
reachable — that is why a normal `dotnet test` reports two or four skipped tests.

Oracle is separated because its image is measured in gigabytes and takes minutes to become ready. Running it on every push would slow every push
for a provider few of these tests exercise differently.

## What the provider tests assert

The same operation on every provider: read six customers, then read each customer's orders in a loop. Then:

- the six order queries collapse to **one fingerprint** — six executions differing only by argument are one shape;
- the shape matches the dialect the provider actually emitted (`[Orders]` on SQL Server, `"Orders"` on PostgreSQL, backticks on MySQL, quoted
  upper case on Oracle), with identifiers and parameters canonicalized;
- the LINQ expression is correlated back to the command, with `Order.CustomerId` recognised as the key filter and `Customer.Orders` as the
  navigation that would fix it;
- rows returned are counted through the provider's own reader;
- QS001 and QS004 fire with the same titles and counts as on SQLite.

## What running Oracle for the first time changed

Oracle was recognised in the code but had never been executed. Doing so found two bugs that no `@name` provider could expose:

- **Bind variables were not canonicalized.** The normalizer matched `@name` only, so `:p_customer_Id` survived into the shape. EF Core derives
  that name from the C# expression, which is exactly what canonicalization exists to erase (ADR-0006): renaming a local variable changed the
  fingerprint on Oracle. Parameters now match `@name` and `:name`, while PostgreSQL's `::` casts and `@@` server variables are left alone.
- **Every parameterized query was uncorrelated.** The rule that a key filter must leave a parameter in the SQL looked for the literal `"@p"`,
  and the parameter-name check could not find `customer_Id` inside Oracle's `p_customer_Id`. Without correlation there is no expression, so
  QS001 could not name the navigation to include. Both are fixed; the Oracle test asserts the full N+1 diagnosis, as the other providers do.

One difference is left as-is: Oracle does not allow `AS` before a table alias, and the alias canonicalizer requires it, so `FROM "Orders" "o"`
keeps the alias EF generated. Fingerprints stay stable within Oracle — EF assigns those aliases deterministically — and shapes were never
comparable between dialects, which is what the provider suffix on snapshot files is for.

## Where provider differences are deliberate

- **Normalization** canonicalizes `[bracketed]`, `"quoted"` and `` `backticked` `` identifiers, EF Core's generated aliases, and parameters
  (`@p0`, `:p0`) to positional names, so the same LINQ produces the same fingerprint whatever the dialect prints.
- **QS007** names the pattern the provider actually uses: `OPENJSON` on SQL Server, `= ANY(...)` on PostgreSQL, and the generic chunking advice
  elsewhere.
- **Collection parameters** are read either as provider arrays (Npgsql) or as the JSON array EF Core 8+ sends.
- **Snapshots** carry a provider suffix (`.sqlite.json`, `.sqlserver.json`) by default, because the SQL text they record is dialect-specific and a
  suite that runs SQLite locally and SQL Server in CI would otherwise fail on the difference.
- **Write commands never have a row count.** Npgsql casts its write-batch readers to a concrete type, so QueryShape does not wrap readers for
  `SaveChanges` on any provider (ADR-0013). Row budgets and capture-completeness checks exclude write commands accordingly.

## Overhead

Measured on PostgreSQL 16 and SQL Server 2022: capture costs 19–40 µs and ≈ 4 KB per two-query read, analysis a further 7–25 µs. The percentage
that represents depends entirely on the round trip, and the measurement's limits matter — see [performance](performance.md).

## Not verified

- **p99 under concurrent load** against any real provider.
- **Provider-specific EF Core features** that change the shape of what a rule sees: temporal tables, JSON columns, `HierarchyId`, provider-specific
  functions. Nothing suggests they misbehave; nothing tests them either.
- **Overhead on MySQL and Oracle**: the benchmark covers PostgreSQL and SQL Server only.
- **Any provider not in the table**: Cosmos (not relational, so out of scope), MariaDB, SQLite-adjacent forks, DB2, Firebird. They may well work —
  the capture path has nothing provider-specific in it — but that is an argument, not a measurement.
