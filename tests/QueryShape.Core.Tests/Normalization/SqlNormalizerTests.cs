using QueryShape.Normalization;

namespace QueryShape.Core.Tests.Normalization;

public class SqlNormalizerTests
{
    [Fact]
    public void Collapses_whitespace_and_strips_comments()
    {
        var sql = "SELECT   [c].[Id],\n    [c].[Name] /* hi */\r\nFROM [Customers] AS [c]  -- trailing\nWHERE [c].[Id] = @__id_0";
        var result = SqlNormalizer.Normalize(sql);
        result.Shape.Should().Be("SELECT [t0].[Id], [t0].[Name] FROM [Customers] AS [t0] WHERE [t0].[Id] = @p0");
        result.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Extracts_leading_tags()
    {
        var sql = "-- OrderService.GetOrders\n-- File: /src/OrderService.cs:42\n\nSELECT \"o\".\"Id\" FROM \"Orders\" AS \"o\"";
        var result = SqlNormalizer.Normalize(sql);
        result.Tags.Should().Equal("OrderService.GetOrders", "File: /src/OrderService.cs:42");
        result.Shape.Should().Be("SELECT \"t0\".\"Id\" FROM \"Orders\" AS \"t0\"");
    }

    [Fact]
    public void Extracts_multiple_tags_separated_by_blank_lines()
    {
        var sql = "-- first\n\n-- second\n\nSELECT 1";
        SqlNormalizer.Normalize(sql).Tags.Should().Equal("first", "second");
    }

    [Fact]
    public void Comments_after_the_statement_start_are_not_tags()
    {
        var result = SqlNormalizer.Normalize("SELECT 1 -- not a tag\nFROM T");
        result.Tags.Should().BeEmpty();
        result.Shape.Should().Be("SELECT 1 FROM T");
    }

    [Fact]
    public void Canonicalizes_aliases_positionally_for_each_quote_style()
    {
        SqlNormalizer.Shape("SELECT [o].[Id], [c].[Name] FROM [Orders] AS [o] INNER JOIN [Customers] AS [c] ON [o].[CustomerId] = [c].[Id]")
            .Should().Be("SELECT [t0].[Id], [t1].[Name] FROM [Orders] AS [t0] INNER JOIN [Customers] AS [t1] ON [t0].[CustomerId] = [t1].[Id]");

        SqlNormalizer.Shape("SELECT o.\"Id\", c.\"Name\" FROM \"Orders\" AS o INNER JOIN \"Customers\" AS c ON o.\"CustomerId\" = c.\"Id\"")
            .Should().Be("SELECT t0.\"Id\", t1.\"Name\" FROM \"Orders\" AS t0 INNER JOIN \"Customers\" AS t1 ON t0.\"CustomerId\" = t1.\"Id\"");
    }

    [Fact]
    public void Same_query_with_different_generated_alias_names_has_same_shape()
    {
        var a = SqlNormalizer.Shape("SELECT [o].[Id] FROM [Orders] AS [o] WHERE [o].[CustomerId] = @p");
        var b = SqlNormalizer.Shape("SELECT [o0].[Id] FROM [Orders] AS [o0] WHERE [o0].[CustomerId] = @p");
        a.Should().Be(b);
    }

    [Fact]
    public void Derived_table_aliases_are_canonicalized_too()
    {
        var sql = "SELECT [t].[Id] FROM (SELECT TOP(1) [o].[Id] FROM [Orders] AS [o]) AS [t] LEFT JOIN [OrderLines] AS [o0] ON [t].[Id] = [o0].[OrderId]";
        SqlNormalizer.Shape(sql).Should().Be("SELECT [t1].[Id] FROM (SELECT TOP(1) [t0].[Id] FROM [Orders] AS [t0]) AS [t1] LEFT JOIN [OrderLines] AS [t2] ON [t1].[Id] = [t2].[OrderId]");
    }

    [Fact]
    public void Column_aliases_and_table_names_are_left_alone()
    {
        var sql = "SELECT [c].[Name] AS [CustomerName], COUNT(*) AS [Count] FROM [Customers] AS [c] GROUP BY [c].[Name]";
        SqlNormalizer.Shape(sql).Should().Be("SELECT [t0].[Name] AS [CustomerName], COUNT(*) AS [Count] FROM [Customers] AS [t0] GROUP BY [t0].[Name]");
    }

    [Fact]
    public void Parameter_names_are_canonicalized_positionally()
    {
        var ef8 = SqlNormalizer.Shape("SELECT * FROM [O] AS [o] WHERE [o].[A] = @__customerId_0 AND [o].[B] > @__min_1 OR [o].[A] = @__customerId_0");
        var ef10 = SqlNormalizer.Shape("SELECT * FROM [O] AS [o] WHERE [o].[A] = @customerId AND [o].[B] > @min OR [o].[A] = @customerId");
        ef8.Should().Be("SELECT * FROM [O] AS [t0] WHERE [t0].[A] = @p0 AND [t0].[B] > @p1 OR [t0].[A] = @p0");
        ef10.Should().Be(ef8, "renaming a C# variable must not change the shape");
        SqlNormalizer.Shape("SELECT @@ROWCOUNT, 'a@b'").Should().Be("SELECT @@ROWCOUNT, 'a@b'");
    }

    [Fact]
    public void Fingerprint_is_12_lowercase_hex_chars_and_stable()
    {
        var fp = Fingerprint.Compute("SELECT 1");
        fp.Should().MatchRegex("^[0-9a-f]{12}$");
        Fingerprint.Compute("SELECT 1").Should().Be(fp);
        Fingerprint.Compute("SELECT 2").Should().NotBe(fp);
    }
}
