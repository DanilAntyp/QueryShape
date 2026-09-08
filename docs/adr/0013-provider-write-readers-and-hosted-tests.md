# ADR 0013: Preserve provider write readers

The first hosted PostgreSQL run failed during fixture `SaveChanges`: Npgsql's modification batch casts the returned reader to `NpgsqlDataReader`. Replacing it with a general `DbDataReader` decorator breaks that provider contract, even when the decorator forwards every base method.

Keep capturing write commands, timings and save-change entity observations, but never wrap or register open readers for `QuerySource.SaveChanges`. Their returned-row count stays unknown; query-result row budgets and completeness already exclude write commands. Read-query readers continue to provide measured counts. Regression checks exercise both synchronous and asynchronous saves and ensure reports remain complete without inventing write-row counts. The PostgreSQL/SQL Server CI fixtures also execute actual writes.

Hosted validation uses dedicated disposable application databases, with Linux provider images on Ubuntu. Test/sample projects preserve local source paths so patch and snapshot assertions can read checked-out source; shipped packages retain deterministic source mapping. External checkouts live outside QueryShape's directory so upstream builds do not inherit its MSBuild properties.
