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

    [Fact]
    public void String_literals_are_left_alone_by_the_comment_whitespace_and_parameter_passes()
    {
        SqlNormalizer.Shape("SELECT * FROM T WHERE A = 'x -- not a comment' AND B = 'y /* nor this */'")
            .Should().Be("SELECT * FROM T WHERE A = 'x -- not a comment' AND B = 'y /* nor this */'");
        SqlNormalizer.Shape("SELECT 'two  spaces', 'it''s', N'unicode' FROM T").Should().Be("SELECT 'two  spaces', 'it''s', N'unicode' FROM T");
        SqlNormalizer.Shape("SELECT * FROM T WHERE Email = 'a@b.com' AND Id = @id").Should().Be("SELECT * FROM T WHERE Email = 'a@b.com' AND Id = @p0");
        SqlNormalizer.Shape("SELECT 1 -- it's a comment\nFROM T").Should().Be("SELECT 1 FROM T", "a quote inside a comment does not open a literal");
        SqlNormalizer.Shape("SELECT [it's] FROM [T] AS [t]").Should().Be("SELECT [it's] FROM [T] AS [t0]", "a quote inside a bracketed identifier does not open a literal");
        SqlNormalizer.Shape("SELECT \"say \"\"hi\"\"\" FROM \"T\" AS t").Should().Be("SELECT \"say \"\"hi\"\"\" FROM \"T\" AS t0");
    }

    [Fact]
    public void Schema_qualified_tables_get_canonical_aliases()
    {
        SqlNormalizer.Shape("SELECT [c].[Id] FROM [dbo].[Customers] AS [c] INNER JOIN [sales].[Orders] AS [o] ON [o].[CustomerId] = [c].[Id]")
            .Should().Be("SELECT [t0].[Id] FROM [dbo].[Customers] AS [t0] INNER JOIN [sales].[Orders] AS [t1] ON [t1].[CustomerId] = [t0].[Id]");
        SqlNormalizer.Shape("SELECT c.\"Id\" FROM public.\"Customers\" AS c").Should().Be("SELECT t0.\"Id\" FROM public.\"Customers\" AS t0");
    }

    [Fact]
    public void Masked_shapes_replace_string_and_numeric_literals_but_keep_identifiers_and_parameters()
    {
        SqlNormalizer.Normalize("SELECT * FROM Customers AS c WHERE Name = 'O''Brien' AND Age > 30 AND Id = @id", maskLiterals: true).Shape
            .Should().Be("SELECT * FROM Customers AS t0 WHERE Name = ? AND Age > ? AND Id = @p0");
        SqlNormalizer.Normalize("SELECT [t0].[D2], N'x' FROM [T1] AS [t0]", maskLiterals: true).Shape.Should().Be("SELECT [t0].[D2], ? FROM [T1] AS [t0]");
        SqlNormalizer.Normalize("SELECT 'a', 'b'", maskLiterals: false).Shape.Should().Be("SELECT 'a', 'b'");
    }
}
