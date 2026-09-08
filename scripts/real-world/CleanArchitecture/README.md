# CleanArchitecture validation

Runs the original `ListContributorsQueryService` and `AppDbContext` from [Ardalis CleanArchitecture](https://github.com/ardalis/CleanArchitecture), pinned to `fbdc0951879f5e8dca1bebc273d4b28cb2934469`.

```sh
git clone https://github.com/ardalis/CleanArchitecture.git /tmp/queryshape-cleanarchitecture-validation
git -C /tmp/queryshape-cleanarchitecture-validation checkout fbdc0951879f5e8dca1bebc273d4b28cb2934469
export CleanArchitectureRoot=/tmp/queryshape-cleanarchitecture-validation
dotnet test scripts/real-world/CleanArchitecture/CleanArchitecture.Tests.csproj
dotnet run --project src/QueryShape.Cli -f net10.0 -- scale \
  --project scripts/real-world/CleanArchitecture/CleanArchitecture.Tests.csproj \
  --test ContributorTests.Ordered_projected_page_has_bounded_returned_rows --json
dotnet run --project src/QueryShape.Cli -f net10.0 -- reduce \
  --project scripts/real-world/CleanArchitecture/CleanArchitecture.Tests.csproj \
  --test ContributorTests.Reduce_a_result_regression_in_a_candidate_using_the_real_service \
  --out /tmp/queryshape-clean-reproducer --json
```

The harness references the upstream Infrastructure project, including its original mappings and value objects. It uses in-memory SQLite and EF Core 10.0.11 with synthetic contributors, some missing phone numbers. Fixture IDs are explicitly positive and deterministic because upstream `ContributorId` validation rejects EF's temporary negative generated IDs. This is fixture wiring; upstream query code and schema mappings are unchanged.

The page check covers 0/1/10/100 contributors, page size four, twice each. The negative control calls the real service but deliberately filters missing-phone contributors out of the candidate result. That candidate is intentionally wrong; it is not an upstream defect. Reduction preserves the result-difference signature while shrinking the seeded contributor list. Both implementations observe `Id`, `Name`, and phone number before and after.

These tests cover service queries and selected state. They do not measure HTTP endpoints, provider execution plans, production data distributions, or latency improvements.
