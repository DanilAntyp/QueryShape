using System.Collections;
using System.Data;
using System.Data.Common;

namespace QueryShape.Capture;

/// <summary>
/// Wraps a <see cref="DbDataReader"/> to count rows actually read and report once on close/dispose.
/// EF Core's own read counter counts calls (including the final false), so we count ourselves.
/// </summary>
internal sealed class CountingDataReader : DbDataReader, IDbColumnSchemaGenerator
{
    private readonly DbDataReader _inner;
    private readonly Action<int, int, int?> _onClosed;
    private readonly bool _trackFirstColumn;
    private int _rows;
    private int _distinctFirstColumn;
    private object? _lastFirstColumn;
    private bool _reported;
    private bool _disposed;

    /// <param name="inner">The provider reader.</param>
    /// <param name="onClosed">(rows read, records affected, distinct first-column values or null).</param>
    /// <param name="trackFirstColumn">Count distinct consecutive values of column 0 (root cardinality estimate for include queries).</param>
    public CountingDataReader(DbDataReader inner, Action<int, int, int?> onClosed, bool trackFirstColumn = false)
    {
        _inner = inner;
        _onClosed = onClosed;
        _trackFirstColumn = trackFirstColumn;
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

    /// <summary><c>reader.GetColumnSchema()</c> is an extension that needs this interface on the outermost reader.</summary>
    public System.Collections.ObjectModel.ReadOnlyCollection<DbColumn> GetColumnSchema() => _inner.GetColumnSchema();

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
            OnRow();
        }

        return result;
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (result)
        {
            OnRow();
        }

        return result;
    }

    private void OnRow()
    {
        _rows++;
        if (!_trackFirstColumn)
        {
            return;
        }

        try
        {
            var value = _inner.FieldCount > 0 ? _inner.GetValue(0) : null;
            if (_rows == 1 || !Equals(value, _lastFirstColumn))
            {
                _distinctFirstColumn++;
                _lastFirstColumn = value;
            }
        }
        catch
        {
            // Estimation only.
        }
    }

    public override void Close()
    {
        Report();
        if (!_disposed)
        {
            _inner.Close();
        }
    }

    public override async Task CloseAsync()
    {
        Report();
        if (!_disposed)
        {
            await _inner.CloseAsync().ConfigureAwait(false);
        }
    }

    // The base implementations of Dispose(bool)/DisposeAsync() call Close() and Dispose() again; the provider's reader must see each exactly once.
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Report();
            _inner.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Report();
        await _inner.DisposeAsync().ConfigureAwait(false);
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
            _onClosed(_rows, affected, _trackFirstColumn ? _distinctFirstColumn : null);
        }
        catch
        {
            // Reporting must never fail the reader.
        }
    }
}
