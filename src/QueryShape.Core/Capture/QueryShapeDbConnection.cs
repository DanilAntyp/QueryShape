using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace QueryShape.Capture;

/// <summary>
/// Wraps any <see cref="DbConnection"/> so commands created from it (Dapper, hand-written ADO.NET) are captured with
/// <see cref="QuerySource.Raw"/>. Opt-in: <c>new QueryShapeDbConnection(inner)</c>. EF Core's own commands are captured by the interceptor instead.
/// </summary>
public sealed class QueryShapeDbConnection : DbConnection
{
    private readonly QueryShapeOptions _options;
    private readonly Guid _connectionId = Guid.NewGuid();

    /// <summary>Wraps <paramref name="inner"/>. Disposing the wrapper disposes the inner connection.</summary>
    public QueryShapeDbConnection(DbConnection inner, QueryShapeOptions? options = null)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? QueryShapeOptions.Default;
    }

    /// <summary>The wrapped connection.</summary>
    public DbConnection Inner { get; }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get => Inner.ConnectionString; set => Inner.ConnectionString = value; }

    /// <inheritdoc />
    public override string Database => Inner.Database;

    /// <inheritdoc />
    public override string DataSource => Inner.DataSource;

    /// <inheritdoc />
    public override string ServerVersion => Inner.ServerVersion;

    /// <inheritdoc />
    public override ConnectionState State => Inner.State;

    /// <inheritdoc />
    public override int ConnectionTimeout => Inner.ConnectionTimeout;

    /// <inheritdoc />
    public override bool CanCreateBatch => false; // a batch from the inner connection would bypass capture; callers fall back to commands

    /// <inheritdoc />
    protected override DbProviderFactory? DbProviderFactory => DbProviderFactories.GetFactory(Inner);

    /// <inheritdoc />
    public override void ChangeDatabase(string databaseName) => Inner.ChangeDatabase(databaseName);

    /// <inheritdoc />
    public override Task ChangeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default) => Inner.ChangeDatabaseAsync(databaseName, cancellationToken);

    /// <inheritdoc />
    public override void Close() => Inner.Close();

    /// <inheritdoc />
    public override Task CloseAsync() => Inner.CloseAsync();

    /// <inheritdoc />
    public override void Open() => Inner.Open();

    /// <inheritdoc />
    public override Task OpenAsync(CancellationToken cancellationToken) => Inner.OpenAsync(cancellationToken);

    /// <inheritdoc />
    public override void EnlistTransaction(System.Transactions.Transaction? transaction) => Inner.EnlistTransaction(transaction);

    /// <inheritdoc />
    public override DataTable GetSchema() => Inner.GetSchema();

    /// <inheritdoc />
    public override DataTable GetSchema(string collectionName) => Inner.GetSchema(collectionName);

    /// <inheritdoc />
    public override DataTable GetSchema(string collectionName, string?[] restrictionValues) => Inner.GetSchema(collectionName, restrictionValues);

    /// <inheritdoc />
    public override Task<DataTable> GetSchemaAsync(CancellationToken cancellationToken = default) => Inner.GetSchemaAsync(cancellationToken);

    /// <inheritdoc />
    public override Task<DataTable> GetSchemaAsync(string collectionName, CancellationToken cancellationToken = default) => Inner.GetSchemaAsync(collectionName, cancellationToken);

    /// <inheritdoc />
    public override Task<DataTable> GetSchemaAsync(string collectionName, string?[] restrictionValues, CancellationToken cancellationToken = default)
        => Inner.GetSchemaAsync(collectionName, restrictionValues, cancellationToken);

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new QueryShapeDbTransaction(Inner.BeginTransaction(isolationLevel), this);

    /// <inheritdoc />
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
        => new QueryShapeDbTransaction(await Inner.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false), this);

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand()
    {
        var command = Inner.CreateCommand();
        return new QueryShapeDbCommand(command, this);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await Inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    internal void Record(DbCommand inner, DbCommandMethod method, Guid commandId, DateTimeOffset start, TimeSpan duration, bool isAsync, object? result, Exception? error)
        => QueryShapeInterceptor.Instance.Capturer.Capture(_options, inner, QuerySource.Raw, CommandSource.Unknown, method, commandId, _connectionId, null, start, duration, isAsync, result, error);

    internal void ReaderClosed(Guid commandId, int rows, int recordsAffected) => QueryShapeInterceptor.Instance.Capturer.ReaderClosed(commandId, rows, recordsAffected);

    /// <summary>A transaction begun on the wrapper: its <c>Connection</c> is the wrapper, so code that checks <c>tx.Connection == connection</c> keeps working.</summary>
    private sealed class QueryShapeDbTransaction : DbTransaction
    {
        private readonly QueryShapeDbConnection _owner;

        public QueryShapeDbTransaction(DbTransaction inner, QueryShapeDbConnection owner)
        {
            Inner = inner;
            _owner = owner;
        }

        public DbTransaction Inner { get; }

        public override IsolationLevel IsolationLevel => Inner.IsolationLevel;

        protected override DbConnection? DbConnection => _owner;

        public override bool SupportsSavepoints => Inner.SupportsSavepoints;

        public override void Commit() => Inner.Commit();

        public override Task CommitAsync(CancellationToken cancellationToken = default) => Inner.CommitAsync(cancellationToken);

        public override void Rollback() => Inner.Rollback();

        public override Task RollbackAsync(CancellationToken cancellationToken = default) => Inner.RollbackAsync(cancellationToken);

        public override void Save(string savepointName) => Inner.Save(savepointName);

        public override Task SaveAsync(string savepointName, CancellationToken cancellationToken = default) => Inner.SaveAsync(savepointName, cancellationToken);

        public override void Rollback(string savepointName) => Inner.Rollback(savepointName);

        public override Task RollbackAsync(string savepointName, CancellationToken cancellationToken = default) => Inner.RollbackAsync(savepointName, cancellationToken);

        public override void Release(string savepointName) => Inner.Release(savepointName);

        public override Task ReleaseAsync(string savepointName, CancellationToken cancellationToken = default) => Inner.ReleaseAsync(savepointName, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await Inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class QueryShapeDbCommand : DbCommand
    {
        private readonly DbCommand _inner;
        private readonly QueryShapeDbConnection _owner;
        private DbConnection? _connection;
        private DbTransaction? _transaction;

        public QueryShapeDbCommand(DbCommand inner, QueryShapeDbConnection owner)
        {
            _inner = inner;
            _owner = owner;
            _connection = owner;
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get => _inner.CommandText; set => _inner.CommandText = value; }

        public override int CommandTimeout { get => _inner.CommandTimeout; set => _inner.CommandTimeout = value; }

        public override CommandType CommandType { get => _inner.CommandType; set => _inner.CommandType = value; }

        public override bool DesignTimeVisible { get => _inner.DesignTimeVisible; set => _inner.DesignTimeVisible = value; }

        public override UpdateRowSource UpdatedRowSource { get => _inner.UpdatedRowSource; set => _inner.UpdatedRowSource = value; }

        /// <summary>Reports what was assigned (the wrapper by default); the inner command always gets the unwrapped connection.</summary>
        protected override DbConnection? DbConnection
        {
            get => _connection;
            set
            {
                _connection = value;
                _inner.Connection = value is QueryShapeDbConnection w ? w.Inner : value;
            }
        }

        protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

        /// <summary>Reports what was assigned; the inner command always gets the unwrapped transaction.</summary>
        protected override DbTransaction? DbTransaction
        {
            get => _transaction ?? _inner.Transaction;
            set
            {
                _transaction = value;
                _inner.Transaction = value is QueryShapeDbTransaction t ? t.Inner : value;
            }
        }

        public override void Cancel() => _inner.Cancel();

        public override void Prepare() => _inner.Prepare();

        public override Task PrepareAsync(CancellationToken cancellationToken = default) => _inner.PrepareAsync(cancellationToken);

        protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

        public override int ExecuteNonQuery()
            => Run(DbCommandMethod.ExecuteNonQuery, false, _inner.ExecuteNonQuery, static r => r);

        public override object? ExecuteScalar()
            => Run(DbCommandMethod.ExecuteScalar, false, _inner.ExecuteScalar, static r => r);

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var id = Guid.NewGuid();
            var reader = Run(DbCommandMethod.ExecuteReader, false, () => _inner.ExecuteReader(behavior), static _ => null, id);
            return new CountingDataReader(reader, (rows, affected, _) => _owner.ReaderClosed(id, rows, affected));
        }

        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            var start = DateTimeOffset.UtcNow;
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await _inner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                _owner.Record(_inner, DbCommandMethod.ExecuteNonQuery, id, start, sw.Elapsed, true, result, null);
                return result;
            }
            catch (Exception ex)
            {
                _owner.Record(_inner, DbCommandMethod.ExecuteNonQuery, id, start, sw.Elapsed, true, null, ex);
                throw;
            }
        }

        public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            var start = DateTimeOffset.UtcNow;
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await _inner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                _owner.Record(_inner, DbCommandMethod.ExecuteScalar, id, start, sw.Elapsed, true, result, null);
                return result;
            }
            catch (Exception ex)
            {
                _owner.Record(_inner, DbCommandMethod.ExecuteScalar, id, start, sw.Elapsed, true, null, ex);
                throw;
            }
        }

        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            var start = DateTimeOffset.UtcNow;
            var sw = Stopwatch.StartNew();
            try
            {
                var reader = await _inner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
                _owner.Record(_inner, DbCommandMethod.ExecuteReader, id, start, sw.Elapsed, true, null, null);
                return new CountingDataReader(reader, (rows, affected, _) => _owner.ReaderClosed(id, rows, affected));
            }
            catch (Exception ex)
            {
                _owner.Record(_inner, DbCommandMethod.ExecuteReader, id, start, sw.Elapsed, true, null, ex);
                throw;
            }
        }

        private T Run<T>(DbCommandMethod method, bool isAsync, Func<T> action, Func<T, object?> toResult, Guid? id = null)
        {
            var commandId = id ?? Guid.NewGuid();
            var start = DateTimeOffset.UtcNow;
            var sw = Stopwatch.StartNew();
            try
            {
                var result = action();
                _owner.Record(_inner, method, commandId, start, sw.Elapsed, isAsync, toResult(result), null);
                return result;
            }
            catch (Exception ex)
            {
                _owner.Record(_inner, method, commandId, start, sw.Elapsed, isAsync, null, ex);
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
