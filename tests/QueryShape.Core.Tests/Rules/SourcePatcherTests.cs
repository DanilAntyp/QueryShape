using QueryShape.Rules;

namespace QueryShape.Core.Tests.Rules;

public class SourcePatcherTests
{
    private static readonly HashSet<string> s_terminals = new(StringComparer.Ordinal) { "ToListAsync", "ToList", "Count", "Any", "First" };

    [Theory]
    [InlineData("var x = await db.A.Where(a => a.B.Any()).ToListAsync();", ".ToListAsync(")]
    [InlineData("var n = db.A.Count(a => a.X == 1);", ".Count(")]
    [InlineData("foreach (var c in cs) c.Orders = await db.Orders.Where(o => o.CustomerId == c.Id).ToListAsync();", ".ToListAsync(")]
    [InlineData("var s = db.A.Where(a => a.Name == \").First(\").ToListAsync();", ".ToListAsync(")]
    [InlineData("        .ToListAsync();", ".ToListAsync(")]
    public void Top_level_call_skips_lambdas_and_string_literals(string line, string expectedStart)
    {
        var at = SourcePatcher.FindTopLevelCall(line, s_terminals);
        at.Should().BeGreaterOrEqualTo(0);
        line[at..].Should().StartWith(expectedStart);
    }

    [Theory]
    [InlineData("        c.Active).ToListAsync();")]          // closes a parenthesis opened on a previous line: not a shape we patch
    [InlineData("var x = db.A.Where(a => a.Items.Any());")]  // the only terminal is inside the lambda
    public void Top_level_call_is_not_found_when_the_line_is_not_self_contained(string line)
        => SourcePatcher.FindTopLevelCall(line, s_terminals).Should().Be(-1);

    [Theory]
    [InlineData("var entity = await _repository.FirstOrDefaultAsync(spec, cancellationToken);", false)]   // Ardalis.Specification: no IQueryable on this line
    [InlineData("var entity = await db.Contributors.FirstOrDefaultAsync(c => c.Id == id);", true)]          // DbSet member
    [InlineData("var entity = await db.Set<Contributor>().FirstOrDefaultAsync(c => c.Id == id);", true)]
    [InlineData("var list = await query.Where(c => c.Active).ToListAsync();", true)]                          // one of the query's own operators
    [InlineData("var list = await service.LoadAsync(id);", false)]
    public void Only_lines_that_hold_an_ef_query_get_operator_patches(string line, bool patchable)
    {
        var query = new QueryInfo
        {
            Expression = "DbSet<Contributor>()\n    .Where(c => c.Id == @__id_0)\n    .FirstOrDefault()",
            ExpressionHash = "x",
            RootEntityShortName = "Contributor",
            RootTableName = "Contributors",
            Operators = ["Where", "FirstOrDefault"],
        };
        SourcePatcher.LooksLikeEfQueryLine(line, query).Should().Be(patchable);
        SourcePatcher.LooksLikeEfQueryLine(line, null).Should().BeTrue("without query facts the old behavior stands");
    }

    [Fact]
    public void Chain_parser_splits_calls_and_rejects_non_chains()
    {
        SourcePatcher.ParseChain("db.Orders.Where(o => o.CustomerId == c.Id).ToListAsync()")
            .Should().Equal(("Where", "o => o.CustomerId == c.Id"), ("ToListAsync", ""));
        SourcePatcher.ParseChain("_db.Customers.SingleAsync(c => c.Id == order.CustomerId)")
            .Should().Equal(("SingleAsync", "c => c.Id == order.CustomerId"));
        SourcePatcher.ParseChain("db.Orders.Where(o => o.A == b.C).OrderBy(o => o.D).Take(3).ToListAsync()")
            .Should().HaveCount(4);
        SourcePatcher.ParseChain("db.Orders.ToListAsync().Count").Should().BeNull();
        SourcePatcher.ParseChain("(await db.Orders.ToListAsync())").Should().BeNull();
        SourcePatcher.ParseChain("db.Orders.Where(o => o.Name == \"a)b\").ToListAsync()")
            .Should().Equal(("Where", "o => o.Name == \"a)b\""), ("ToListAsync", ""));
    }
}
