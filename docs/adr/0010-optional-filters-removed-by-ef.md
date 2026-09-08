# ADR-0010: Preserve correlation when EF removes an optional filter

Date: 2026-09-08. Status: accepted.

Testing the unchanged catalog service in dotnet-architecture/eShopOnWeb at `4da8212117e87d808d4bbc7da6286fd2147ce606` exposed a false negative. Its optional brand/type predicate becomes always true when both filters are unset. EF removes the WHERE clause, but QueryShape still marked the expression as filtered. The correlation guard rejected the SQL, losing both QS011's EF warning and QS005's tracking diagnosis.

The expression analyzer now recognizes predicates whose boolean constants prove they are always true after EF's parameter extraction. It inspects constant booleans, NOT, AND and OR without executing user code. Such predicates do not contribute a filter, key comparisons, or parameters from unreachable branches. Other predicates and the correlation guards retain their existing behavior.

Regression tests exercise the actual optional-filter query on EF Core 8 and 10, on initial and cached executions. The eShopOnWeb harness also verifies the result against the upstream repository/specification layer.
