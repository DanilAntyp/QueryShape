using System.Net;
using QueryShape.Reporting;
using Xunit.Abstractions;

namespace QueryShape.SampleApp.Tests;

/// <summary>Every /bad endpoint must be diagnosed by its rule; every /good twin must be clean of that rule.</summary>
public class BadEndpointTests(SampleAppFixture app, ITestOutputHelper output) : IClassFixture<SampleAppFixture>
{
    [RuntimeMatchedFact]
    public async Task N_plus_one_is_diagnosed_with_include_fix()
    {
        var (response, scope, diagnoses) = await app.GetAsync("/bad/n-plus-one");
        output.WriteLine(DiagnosisFormatter.Format(diagnoses));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-QueryShape").Single().Should().StartWith("41 queries; QS001 Error");
        scope.CommandCount.Should().Be(41);

        var n1 = diagnoses.Should().ContainSingle(d => d.RuleId == "QS001").Subject;
        n1.Title.Should().StartWith("N+1 query: Order by CustomerId executed 40 times at BadEndpoints.cs:");
        n1.SuggestedFix!.Summary.Should().StartWith("Add .Include(c => c.Orders) to the Customer query at BadEndpoints.cs:");
        n1.SuggestedFix.UnifiedDiff.Should().Contain("+            var customers = await db.Customers.Include(c => c.Orders).ToListAsync();");
    }

    [RuntimeMatchedFact]
    public async Task Client_evaluation_is_diagnosed()
    {
        var (_, _, diagnoses) = await app.GetAsync("/bad/client-evaluation");
        var d = diagnoses.Should().ContainSingle(d => d.RuleId == "QS003").Subject;
        d.Title.Should().StartWith("Client-side evaluation: PriceFormatter.Format(Decimal) runs in memory for every Order row at BadEndpoints.cs:");
        d.Evidence.Rows.Should().Be(200);
    }

    [RuntimeMatchedFact]
    public async Task Unbounded_is_diagnosed()
    {
        var (_, _, diagnoses) = await app.GetAsync("/bad/unbounded");
        var d = diagnoses.Should().ContainSingle(d => d.RuleId == "QS004").Subject;
        d.Title.Should().StartWith("Unbounded query: loads every OrderLine row (600 rows) at BadEndpoints.cs:");
    }

    [RuntimeMatchedFact]
    public async Task Duplicate_query_is_diagnosed()
    {
        var (_, _, diagnoses) = await app.GetAsync("/bad/duplicate-query");
        var d = diagnoses.Should().ContainSingle(d => d.RuleId == "QS008").Subject;
        d.Title.Should().StartWith("Identical query executed 2 times: Customer");
    }

    [RuntimeMatchedTheory]
    [InlineData("/good/n-plus-one", "QS001")]
    [InlineData("/good/client-evaluation", "QS003")]
    [InlineData("/good/unbounded", "QS004")]
    [InlineData("/good/duplicate-query", "QS008")]
    public async Task Good_twins_are_clean(string path, string ruleId)
    {
        var (response, _, diagnoses) = await app.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        diagnoses.Should().NotContain(d => d.RuleId == ruleId);
    }

    [RuntimeMatchedFact]
    public async Task Excluded_and_root_paths_still_work()
    {
        var response = await app.Client.GetAsync(new Uri("/", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-QueryShape").Single().Should().Be("0 queries");
    }
}
