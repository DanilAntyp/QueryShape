using System.Data.Common;
using Microsoft.Data.Sqlite;
using QueryShape.Capture;

namespace QueryShape.Core.Tests.Capture;

/// <summary>The raw ADO.NET wrapper must behave like the connection it wraps for the code around it.</summary>
public class RawConnectionTests
{
    private static async Task<QueryShapeDbConnection> OpenAsync()
    {
        var connection = new QueryShapeDbConnection(new SqliteConnection("DataSource=:memory:"), new QueryShapeOptions { CaptureCallSites = false });
        await connection.OpenAsync();
        await using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE T (Id INTEGER PRIMARY KEY, Name TEXT)";
        await create.ExecuteNonQueryAsync();
        return connection;
    }

    [Fact]
    public async Task Transactions_begun_on_the_wrapper_report_the_wrapper_as_their_connection()
    {
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        tx.Connection.Should().BeSameAs(connection, "code that checks tx.Connection == connection must keep working");

        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.Transaction.Should().BeSameAs(tx, "the command reports what was assigned");
        insert.Connection.Should().BeSameAs(connection);
        insert.CommandText = "INSERT INTO T (Name) VALUES ('a')";
        await insert.ExecuteNonQueryAsync();
        await tx.CommitAsync();

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM T";
        (await count.ExecuteScalarAsync()).Should().Be(1L);
    }

    [Fact]
    public async Task Counting_reader_exposes_the_column_schema_and_survives_double_disposal()
    {
        await using var connection = await OpenAsync();
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT Id, Name FROM T";

        var reader = await select.ExecuteReaderAsync();
        reader.Should().BeOfType<CountingDataReader>();
        reader.GetColumnSchema().Select(c => c.ColumnName).Should().Equal("Id", "Name");
        reader.GetSchemaTable().Should().NotBeNull();

        await reader.DisposeAsync();
        var dispose = () => reader.Dispose();
        dispose.Should().NotThrow("the provider's reader is disposed exactly once; later calls are no-ops");
        var close = () => reader.Close();
        close.Should().NotThrow();
    }

    [Fact]
    public async Task Provider_factory_is_the_inner_providers()
    {
        await using var connection = await OpenAsync();
        DbProviderFactories.GetFactory(connection).Should().BeSameAs(SqliteFactory.Instance);
    }
}
