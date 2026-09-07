using Microsoft.Data.Sqlite;
using QueryShape.Capture;

namespace QueryShape.Core.Tests.Capture;

public class RawConnectionTests
{
    [Fact]
    public async Task Wrapped_connection_captures_raw_commands_with_row_counts()
    {
        var options = new QueryShapeOptions();
        await using var inner = new SqliteConnection("DataSource=:memory:");
        await using var connection = new QueryShapeDbConnection(inner, options);
        await connection.OpenAsync();

        using var scope = QueryShapeScope.Begin(options: options);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE T (Id INTEGER PRIMARY KEY, Name TEXT); INSERT INTO T VALUES (1, 'a'), (2, 'b'), (3, 'c')";
            await create.ExecuteNonQueryAsync();
        }

        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT Id, Name FROM T WHERE Id > @min";
            var p = select.CreateParameter();
            p.ParameterName = "@min";
            p.Value = 1;
            select.Parameters.Add(p);
            await using var reader = await select.ExecuteReaderAsync();
            var rows = 0;
            while (await reader.ReadAsync())
            {
                rows++;
            }

            rows.Should().Be(2);
        }

        using (var scalar = connection.CreateCommand())
        {
            scalar.CommandText = "SELECT COUNT(*) FROM T";
            scalar.ExecuteScalar().Should().Be(3L);
        }

        scope.Commands.Should().HaveCount(3);
        scope.Commands.Should().OnlyContain(c => c.Source == QuerySource.Raw && c.Query == null);
        scope.Commands[1].RowsReturned.Should().Be(2);
        scope.Commands[1].Parameters.Should().ContainSingle().Which.Name.Should().Be("@min");
        scope.Commands[1].Shape.Should().Be("SELECT Id, Name FROM T WHERE Id > @min");
        scope.Commands[2].IsAsync.Should().BeFalse();
    }
}
