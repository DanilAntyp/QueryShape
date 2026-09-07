using System.Net;
using QueryShape.Reporting;
using Xunit.Abstractions;

namespace QueryShape.SampleApp.Tests;

/// <summary>Every /bad endpoint must be diagnosed by its rule; every /good twin must be clean of that rule.</summary>
[Collection(SampleAppCollection.Name)]
public class BadEndpointTests(SampleAppFixture app, ITestOutputHelper output)
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

[Collection(SampleAppCollection.Name)]
public class RemainingBadEndpointTests(SampleAppFixture app)
{
    [RuntimeMatchedTheory]
    [InlineData("/bad/cartesian-explosion", "QS002", "Cartesian explosion: 1,200 rows for 40 Customer entities (Orders, Orders.Lines, Addresses)")]
    [InlineData("/bad/tracking-read-only", "QS005", "Tracked read-only query: 10 Product entities loaded with change tracking but never modified")]
    [InlineData("/bad/missing-split-query", "QS006", "Split query candidate: 2 collection includes (Orders, Addresses) in one query, 40 rows for 10 Customer entities")]
    [InlineData("/bad/contains-large-collection", "QS007", "Contains over 600 values on the OrderLine query (threshold 500)")]
    [InlineData("/bad/query-in-loop", "QS009", "Queries in a loop: Summaries.ForOrderAsync issued 16 queries of 2 shapes (Customer, OrderLine) in one scope")]
    [InlineData("/bad/raw-sql-concat", "QS010", "Raw SQL built from values: 3 text variants of \"SELECT * FROM Customers WHERE Name = ?\"")]
    public async Task Bad_endpoint_is_diagnosed(string path, string ruleId, string titleStart)
    {
        var (response, _, diagnoses) = await app.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var d = diagnoses.Should().ContainSingle(d => d.RuleId == ruleId).Subject;
        d.Title.Should().StartWith(titleStart);
        d.SuggestedFix.Should().NotBeNull();
    }

    [RuntimeMatchedTheory]
    [InlineData("/good/cartesian-explosion", "QS002")]
    [InlineData("/good/cartesian-explosion", "QS006")]
    [InlineData("/good/tracking-read-only", "QS005")]
    [InlineData("/good/raw-sql-concat", "QS010")]
    public async Task Good_twins_are_clean(string path, string ruleId)
    {
        var (response, _, diagnoses) = await app.GetAsync(path);
        response.EnsureSuccessStatusCode();
        diagnoses.Should().NotContain(d => d.RuleId == ruleId);
    }
}
