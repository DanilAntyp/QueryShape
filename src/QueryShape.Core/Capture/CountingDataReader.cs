using System.Collections;
using System.Data;
using System.Data.Common;

namespace QueryShape.Capture;

/// <summary>
/// Wraps a <see cref="DbDataReader"/> to count rows actually read and report once on close/dispose.
/// EF Core's own read counter counts calls (including the final false), so we count ourselves.
/// </summary>
internal sealed class CountingDataReader : DbDataReader
{
    private readonly DbDataReader _inner;
    private readonly Action<int, int> _onClosed;
    private int _rows;
    private bool _reported;

    public CountingDataReader(DbDataReader inner, Action<int, int> onClosed)
    {
        _inner = inner;
        _onClosed = onClosed;
    }

    public DbDataReader Inner => _inner;

    public override object this[int ordinal] => _inner[ordinal];

    public override object this[string name] => _inner[name];

    public override int Depth => _inner.Depth;

    public override int FieldCount => _inner.FieldCount;

    public override bool HasRows => _inner.HasRows;

    public override bool IsClosed => _inner.IsClosed;

    public override int RecordsAffected => _inner.RecordsAffected;

    public override int VisibleFieldCount => _inner.VisibleFieldCount;

    public override bool GetBoolean(int ordinal) => _inner.GetBoolean(ordinal);

    public override byte GetByte(int ordinal) => _inner.GetByte(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

    public override char GetChar(int ordinal) => _inner.GetChar(ordinal);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

    public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(ordinal);

    public override DateTime GetDateTime(int ordinal) => _inner.GetDateTime(ordinal);

    public override decimal GetDecimal(int ordinal) => _inner.GetDecimal(ordinal);

    public override double GetDouble(int ordinal) => _inner.GetDouble(ordinal);

    public override IEnumerator GetEnumerator() => _inner.GetEnumerator();

    public override Type GetFieldType(int ordinal) => _inner.GetFieldType(ordinal);

    public override float GetFloat(int ordinal) => _inner.GetFloat(ordinal);

    public override Guid GetGuid(int ordinal) => _inner.GetGuid(ordinal);

    public override short GetInt16(int ordinal) => _inner.GetInt16(ordinal);

    public override int GetInt32(int ordinal) => _inner.GetInt32(ordinal);

    public override long GetInt64(int ordinal) => _inner.GetInt64(ordinal);

    public override string GetName(int ordinal) => _inner.GetName(ordinal);

    public override int GetOrdinal(string name) => _inner.GetOrdinal(name);

    public override string GetString(int ordinal) => _inner.GetString(ordinal);

    public override object GetValue(int ordinal) => _inner.GetValue(ordinal);

    public override int GetValues(object[] values) => _inner.GetValues(values);

    public override T GetFieldValue<T>(int ordinal) => _inner.GetFieldValue<T>(ordinal);

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => _inner.GetFieldValueAsync<T>(ordinal, cancellationToken);

    public override Type GetProviderSpecificFieldType(int ordinal) => _inner.GetProviderSpecificFieldType(ordinal);

    public override object GetProviderSpecificValue(int ordinal) => _inner.GetProviderSpecificValue(ordinal);

    public override int GetProviderSpecificValues(object[] values) => _inner.GetProviderSpecificValues(values);

    public override Stream GetStream(int ordinal) => _inner.GetStream(ordinal);

    public override TextReader GetTextReader(int ordinal) => _inner.GetTextReader(ordinal);

    public override DataTable? GetSchemaTable() => _inner.GetSchemaTable();

    public override Task<DataTable?> GetSchemaTableAsync(CancellationToken cancellationToken = default) => _inner.GetSchemaTableAsync(cancellationToken);

    public override Task<System.Collections.ObjectModel.ReadOnlyCollection<DbColumn>> GetColumnSchemaAsync(CancellationToken cancellationToken = default) => _inner.GetColumnSchemaAsync(cancellationToken);

    public override bool IsDBNull(int ordinal) => _inner.IsDBNull(ordinal);

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => _inner.IsDBNullAsync(ordinal, cancellationToken);

    public override bool NextResult() => _inner.NextResult();

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => _inner.NextResultAsync(cancellationToken);

    public override bool Read()
    {
        var result = _inner.Read();
        if (result)
        {
            _rows++;
        }

        return result;
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (result)
        {
            _rows++;
        }

        return result;
    }

    public override void Close()
    {
        Report();
        _inner.Close();
    }

    public override async Task CloseAsync()
    {
        Report();
        await _inner.CloseAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Report();
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        Report();
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void Report()
    {
        if (_reported)
        {
            return;
        }

        _reported = true;
        int affected;
        try
        {
            affected = _inner.RecordsAffected;
        }
        catch
        {
            affected = -1;
        }

        try
        {
            _onClosed(_rows, affected);
        }
        catch
        {
            // Reporting must never fail the reader.
        }
    }
}
