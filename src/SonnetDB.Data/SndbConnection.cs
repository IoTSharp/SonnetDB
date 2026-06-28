using System.Data;
using System.Data.Common;
using System.Globalization;
using SonnetDB.Data.Embedded;
using SonnetDB.Data.Internal;
using SonnetDB.Data.Remote;
using SonnetDB.Tables;

namespace SonnetDB.Data;

/// <summary>
/// SonnetDB 的 ADO.NET 连接对象。同一类型同时承载嵌入式与远程两种实现，
/// 由 <see cref="SndbConnectionStringBuilder.ResolveMode"/> 推断分发。
/// </summary>
/// <remarks>
/// <para>
/// 嵌入式：连接字符串形如 <c>Data Source=./data</c> 或 <c>Data Source=sonnetdb://./data</c>，
/// 内部直接打开 <see cref="SonnetDB.Engine.Tsdb"/>，并通过引用计数共享同一进程内的同目录实例。
/// </para>
/// <para>
/// 远程：连接字符串形如 <c>Data Source=sonnetdb+http://host:port/dbname;Token=xxx</c>，
/// 内部使用 <see cref="System.Net.Http.HttpClient"/> 调用 <c>POST /v1/db/{db}/sql</c>，
/// 结果以 ndjson 流式反序列化。
/// </para>
/// <para>轻事务支持同一数据库内多个关系表的小批量 DML。</para>
/// </remarks>
public sealed class SndbConnection : DbConnection
{
    private const string TablesCollectionName = "Tables";
    private const string ColumnsCollectionName = "Columns";
    private const string IndexesCollectionName = "Indexes";

    private string _connectionString = string.Empty;
    private SndbConnectionStringBuilder _builder = new();
    private IConnectionImpl? _impl;
    private SndbTransaction? _currentTransaction;
    private bool _disposed;

    /// <summary>使用空连接字符串构造，必须随后赋值 <see cref="ConnectionString"/> 再 <see cref="Open"/>。</summary>
    public SndbConnection() { }

    /// <summary>使用指定的连接字符串构造。</summary>
    public SndbConnection(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
            ConnectionString = connectionString;
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (State != ConnectionState.Closed)
                throw new InvalidOperationException("不能在连接打开状态下修改 ConnectionString。");
            _connectionString = value ?? string.Empty;
            _builder = new SndbConnectionStringBuilder(_connectionString);
        }
    }

    /// <inheritdoc />
    public override string Database => _impl?.Database ?? _builder.Database ?? _builder.DataSource;

    /// <inheritdoc />
    public override string DataSource => _impl?.DataSource ?? _builder.DataSource;

    /// <inheritdoc />
    public override string ServerVersion
        => _impl?.ServerVersion ?? typeof(SndbConnection).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    /// <inheritdoc />
    public override ConnectionState State => _impl?.State ?? ConnectionState.Closed;

    /// <summary>当前连接所采用的运行模式。</summary>
    public SndbProviderMode ProviderMode => _builder.ResolveMode();

    /// <summary>
    /// 仅嵌入式模式可用：返回底层 <see cref="SonnetDB.Engine.Tsdb"/> 引擎实例；远程模式或未打开时为 null。
    /// </summary>
    public SonnetDB.Engine.Tsdb? UnderlyingTsdb
        => _impl is EmbeddedConnectionImpl emb ? emb.Tsdb : null;

    /// <inheritdoc />
    /// <remarks>
    /// 远程模式下相当于执行 <c>USE &lt;databaseName&gt;</c>：仅修改当前连接的目标库路由，
    /// 后续 <see cref="SndbCommand"/> 会发往 <c>POST /v1/db/{databaseName}/sql</c>；
    /// 不会做服务端校验，若库不存在或无权限会在下一条 SQL 上自然报错。
    /// 嵌入式模式下因 Data Source 等价于物理路径，此方法仍抛出 <see cref="NotSupportedException"/>。
    /// </remarks>
    public override void ChangeDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        if (_impl is null || State != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开，无法切换数据库。");

        using var cmd = new SndbCommand($"USE `{databaseName.Replace("`", "``")}`", this);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public override void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State == ConnectionState.Open)
            return;

        _impl = _builder.ResolveMode() switch
        {
            SndbProviderMode.Embedded => new EmbeddedConnectionImpl(_builder),
            SndbProviderMode.Remote => new RemoteConnectionImpl(_builder),
            _ => throw new InvalidOperationException("未知的 SndbProviderMode。"),
        };
        try
        {
            _impl.Open();
        }
        catch
        {
            _impl.Dispose();
            _impl = null;
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (State == ConnectionState.Open)
            return;

        _impl = _builder.ResolveMode() switch
        {
            SndbProviderMode.Embedded => new EmbeddedConnectionImpl(_builder),
            SndbProviderMode.Remote => new RemoteConnectionImpl(_builder),
            _ => throw new InvalidOperationException("未知的 SndbProviderMode。"),
        };
        try
        {
            await _impl.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _impl.Dispose();
            _impl = null;
            throw;
        }
    }

    /// <inheritdoc />
    public override void Close()
    {
        if (_currentTransaction is { IsCompleted: false } tx)
        {
            try { RollbackTransaction(tx); }
            catch { /* Close best-effort rollback. */ }
        }

        var impl = _impl;
        _impl = null;
        _currentTransaction = null;
        impl?.Close();
        impl?.Dispose();
    }

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => new SndbCommand { Connection = this };

    /// <inheritdoc />
    public override DataTable GetSchema()
        => GetSchema(DbMetaDataCollectionNames.MetaDataCollections);

    /// <inheritdoc />
    public override DataTable GetSchema(string collectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        if (string.Equals(collectionName, DbMetaDataCollectionNames.MetaDataCollections, StringComparison.OrdinalIgnoreCase))
            return BuildMetaDataCollectionsSchema();
        if (string.Equals(collectionName, DbMetaDataCollectionNames.DataSourceInformation, StringComparison.OrdinalIgnoreCase))
            return BuildDataSourceInformationSchema();
        if (string.Equals(collectionName, DbMetaDataCollectionNames.DataTypes, StringComparison.OrdinalIgnoreCase))
            return BuildDataTypesSchema();
        if (string.Equals(collectionName, DbMetaDataCollectionNames.ReservedWords, StringComparison.OrdinalIgnoreCase))
            return BuildReservedWordsSchema();
        if (string.Equals(collectionName, TablesCollectionName, StringComparison.OrdinalIgnoreCase))
            return BuildTablesSchema(restrictionValues: null);
        if (string.Equals(collectionName, ColumnsCollectionName, StringComparison.OrdinalIgnoreCase))
            return BuildColumnsSchema(restrictionValues: null);
        if (string.Equals(collectionName, IndexesCollectionName, StringComparison.OrdinalIgnoreCase))
            return BuildIndexesSchema(restrictionValues: null);

        throw new ArgumentException($"SonnetDB provider metadata collection '{collectionName}' is not supported.", nameof(collectionName));
    }

    /// <inheritdoc />
    public override DataTable GetSchema(string collectionName, string?[] restrictionValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        if (string.Equals(collectionName, TablesCollectionName, StringComparison.OrdinalIgnoreCase))
            return BuildTablesSchema(restrictionValues);
        if (string.Equals(collectionName, ColumnsCollectionName, StringComparison.OrdinalIgnoreCase))
            return BuildColumnsSchema(restrictionValues);
        if (string.Equals(collectionName, IndexesCollectionName, StringComparison.OrdinalIgnoreCase))
            return BuildIndexesSchema(restrictionValues);

        return GetSchema(collectionName);
    }

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        var impl = GetOpenImpl();
        if (_currentTransaction is { IsCompleted: false })
            throw new InvalidOperationException("当前连接已有活动轻事务。");

        var state = impl.BeginTransaction(isolationLevel);
        var effectiveIsolation = isolationLevel == IsolationLevel.Unspecified
            ? IsolationLevel.ReadCommitted
            : isolationLevel;
        _currentTransaction = new SndbTransaction(this, effectiveIsolation, state);
        return _currentTransaction;
    }

    /// <summary>开始一段 SonnetDB 轻事务。</summary>
    public new SndbTransaction BeginTransaction()
        => BeginTransaction(IsolationLevel.Unspecified);

    /// <summary>开始一段 SonnetDB 轻事务。</summary>
    public new SndbTransaction BeginTransaction(IsolationLevel isolationLevel)
        => (SndbTransaction)BeginDbTransaction(isolationLevel);

    /// <summary>异步开始一段 SonnetDB 轻事务。</summary>
    public new ValueTask<SndbTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => BeginTransactionAsync(IsolationLevel.Unspecified, cancellationToken);

    /// <summary>异步开始一段 SonnetDB 轻事务。</summary>
    public new ValueTask<SndbTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BeginTransaction(isolationLevel));
    }

    /// <summary>使用强类型返回 <see cref="SndbCommand"/>。</summary>
    public new SndbCommand CreateCommand() => new() { Connection = this };

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing) Close();
        _disposed = true;
        base.Dispose(disposing);
    }

    internal IConnectionImpl GetOpenImpl()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_impl is null || _impl.State != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开。");
        return _impl;
    }

    internal object? GetTransactionStateForCommand(SndbTransaction? transaction)
    {
        if (transaction is null)
            return null;
        if (!ReferenceEquals(transaction.Connection, this))
            throw new InvalidOperationException("Command.Transaction 不属于当前连接。");
        if (!ReferenceEquals(transaction, _currentTransaction) || transaction.IsCompleted)
            throw new InvalidOperationException("Command.Transaction 不是当前连接的活动轻事务。");

        return transaction.TransactionState;
    }

    internal void CommitTransaction(SndbTransaction transaction)
    {
        var impl = GetOpenImpl();
        EnsureCurrentTransaction(transaction);
        try
        {
            impl.CommitTransaction(transaction.TransactionState);
            transaction.MarkCompletedFromConnection();
            _currentTransaction = null;
        }
        catch
        {
            _currentTransaction = null;
            transaction.MarkCompletedFromConnection();
            throw;
        }
    }

    internal async Task CommitTransactionAsync(SndbTransaction transaction, CancellationToken cancellationToken)
    {
        var impl = GetOpenImpl();
        EnsureCurrentTransaction(transaction);
        try
        {
            await impl.CommitTransactionAsync(transaction.TransactionState, cancellationToken).ConfigureAwait(false);
            transaction.MarkCompletedFromConnection();
            _currentTransaction = null;
        }
        catch
        {
            _currentTransaction = null;
            transaction.MarkCompletedFromConnection();
            throw;
        }
    }

    internal void RollbackTransaction(SndbTransaction transaction)
    {
        var impl = GetOpenImpl();
        EnsureCurrentTransaction(transaction);
        try
        {
            impl.RollbackTransaction(transaction.TransactionState);
        }
        finally
        {
            transaction.MarkCompletedFromConnection();
            _currentTransaction = null;
        }
    }

    internal async Task RollbackTransactionAsync(SndbTransaction transaction, CancellationToken cancellationToken)
    {
        var impl = GetOpenImpl();
        EnsureCurrentTransaction(transaction);
        try
        {
            await impl.RollbackTransactionAsync(transaction.TransactionState, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            transaction.MarkCompletedFromConnection();
            _currentTransaction = null;
        }
    }

    private void EnsureCurrentTransaction(SndbTransaction transaction)
    {
        if (!ReferenceEquals(transaction, _currentTransaction))
            throw new InvalidOperationException("事务不是当前连接的活动轻事务。");
    }

    private static DataTable BuildMetaDataCollectionsSchema()
    {
        var table = CreateSchemaTable(DbMetaDataCollectionNames.MetaDataCollections);
        table.Columns.Add("CollectionName", typeof(string));
        table.Columns.Add("NumberOfRestrictions", typeof(int));
        table.Columns.Add("NumberOfIdentifierParts", typeof(int));
        table.Rows.Add(DbMetaDataCollectionNames.MetaDataCollections, 0, 0);
        table.Rows.Add(DbMetaDataCollectionNames.DataSourceInformation, 0, 0);
        table.Rows.Add(DbMetaDataCollectionNames.DataTypes, 0, 0);
        table.Rows.Add(DbMetaDataCollectionNames.ReservedWords, 0, 0);
        table.Rows.Add(TablesCollectionName, 4, 1);
        table.Rows.Add(ColumnsCollectionName, 4, 1);
        table.Rows.Add(IndexesCollectionName, 4, 2);
        return table;
    }

    private DataTable BuildDataSourceInformationSchema()
    {
        var table = CreateSchemaTable(DbMetaDataCollectionNames.DataSourceInformation);
        table.Columns.Add(DbMetaDataColumnNames.CompositeIdentifierSeparatorPattern, typeof(string));
        table.Columns.Add(DbMetaDataColumnNames.DataSourceProductName, typeof(string));
        table.Columns.Add(DbMetaDataColumnNames.DataSourceProductVersion, typeof(string));
        table.Columns.Add(DbMetaDataColumnNames.DataSourceProductVersionNormalized, typeof(string));
        table.Columns.Add(DbMetaDataColumnNames.GroupByBehavior, typeof(GroupByBehavior));
        table.Columns.Add(DbMetaDataColumnNames.IdentifierPattern, typeof(string));
        table.Columns.Add(DbMetaDataColumnNames.IdentifierCase, typeof(IdentifierCase));
        table.Columns.Add(DbMetaDataColumnNames.OrderByColumnsInSelect, typeof(bool));
        table.Columns.Add(DbMetaDataColumnNames.ParameterMarkerFormat, typeof(string));
        table.Columns.Add(DbMetaDataColumnNames.ParameterMarkerPattern, typeof(string));
        table.Columns.Add(DbMetaDataColumnNames.ParameterNameMaxLength, typeof(int));
        table.Columns.Add(DbMetaDataColumnNames.ParameterNamePattern, typeof(string));
        table.Columns.Add(DbMetaDataColumnNames.QuotedIdentifierPattern, typeof(string));
        table.Columns.Add(DbMetaDataColumnNames.QuotedIdentifierCase, typeof(IdentifierCase));
        table.Columns.Add(DbMetaDataColumnNames.StatementSeparatorPattern, typeof(string));
        table.Columns.Add(DbMetaDataColumnNames.StringLiteralPattern, typeof(string));
        table.Columns.Add(DbMetaDataColumnNames.SupportedJoinOperators, typeof(SupportedJoinOperators));

        table.Rows.Add(
            "\\.",
            "SonnetDB",
            ServerVersion,
            NormalizeVersion(ServerVersion),
            GroupByBehavior.MustContainAll,
            @"^[A-Za-z_][A-Za-z0-9_]*$",
            IdentifierCase.Sensitive,
            false,
            "@{0}",
            @"^[@:][A-Za-z_][A-Za-z0-9_]*$",
            128,
            @"^[A-Za-z_][A-Za-z0-9_]*$",
            "^\"([^\"\\\\]|\\\\.)*\"$",
            IdentifierCase.Sensitive,
            ";",
            "^'([^']|'')*'$",
            SupportedJoinOperators.Inner);

        return table;
    }

    private DataTable BuildTablesSchema(string?[]? restrictionValues)
    {
        var table = CreateSchemaTable(TablesCollectionName);
        table.Columns.Add("TABLE_CATALOG", typeof(string));
        table.Columns.Add("TABLE_SCHEMA", typeof(string));
        table.Columns.Add("TABLE_NAME", typeof(string));
        table.Columns.Add("TABLE_TYPE", typeof(string));
        table.Columns.Add("CREATED_UTC", typeof(DateTime));

        var tableNameRestriction = Restriction(restrictionValues, 2);
        foreach (var schema in SnapshotTables())
        {
            if (!MatchesRestriction(schema.Name, tableNameRestriction))
                continue;

            table.Rows.Add(
                Database,
                string.Empty,
                schema.Name,
                "BASE TABLE",
                new DateTime(schema.CreatedAtUtcTicks, DateTimeKind.Utc));
        }

        return table;
    }

    private DataTable BuildColumnsSchema(string?[]? restrictionValues)
    {
        var table = CreateSchemaTable(ColumnsCollectionName);
        table.Columns.Add("TABLE_CATALOG", typeof(string));
        table.Columns.Add("TABLE_SCHEMA", typeof(string));
        table.Columns.Add("TABLE_NAME", typeof(string));
        table.Columns.Add("COLUMN_NAME", typeof(string));
        table.Columns.Add("ORDINAL_POSITION", typeof(int));
        table.Columns.Add("COLUMN_DEFAULT", typeof(string));
        table.Columns.Add("IS_NULLABLE", typeof(bool));
        table.Columns.Add("DATA_TYPE", typeof(string));
        table.Columns.Add("CHARACTER_MAXIMUM_LENGTH", typeof(int));
        table.Columns.Add("NUMERIC_PRECISION", typeof(short));
        table.Columns.Add("NUMERIC_SCALE", typeof(short));
        table.Columns.Add("IS_PRIMARY_KEY", typeof(bool));
        table.Columns.Add("IS_ROW_VERSION", typeof(bool));

        var tableNameRestriction = Restriction(restrictionValues, 2);
        var columnNameRestriction = Restriction(restrictionValues, 3);
        foreach (var schema in SnapshotTables())
        {
            if (!MatchesRestriction(schema.Name, tableNameRestriction))
                continue;

            foreach (var column in schema.Columns)
            {
                if (!MatchesRestriction(column.Name, columnNameRestriction))
                    continue;

                table.Rows.Add(
                    Database,
                    string.Empty,
                    schema.Name,
                    column.Name,
                    column.Ordinal + 1,
                    DBNull.Value,
                    column.IsNullable,
                    FormatTableColumnType(column.DataType),
                    GetCharacterMaximumLength(column.DataType),
                    GetNumericPrecision(column.DataType),
                    GetNumericScale(column.DataType),
                    column.IsPrimaryKey,
                    column.IsRowVersion);
            }
        }

        return table;
    }

    private DataTable BuildIndexesSchema(string?[]? restrictionValues)
    {
        var table = CreateSchemaTable(IndexesCollectionName);
        table.Columns.Add("TABLE_CATALOG", typeof(string));
        table.Columns.Add("TABLE_SCHEMA", typeof(string));
        table.Columns.Add("TABLE_NAME", typeof(string));
        table.Columns.Add("INDEX_NAME", typeof(string));
        table.Columns.Add("IS_UNIQUE", typeof(bool));
        table.Columns.Add("COLUMN_NAME", typeof(string));
        table.Columns.Add("ORDINAL_POSITION", typeof(int));
        table.Columns.Add("JSON_PATH", typeof(string));
        table.Columns.Add("CREATED_UTC", typeof(DateTime));

        var tableNameRestriction = Restriction(restrictionValues, 2);
        var indexNameRestriction = Restriction(restrictionValues, 3);
        foreach (var schema in SnapshotTables())
        {
            if (!MatchesRestriction(schema.Name, tableNameRestriction))
                continue;

            foreach (var index in schema.Indexes)
            {
                if (!MatchesRestriction(index.Name, indexNameRestriction))
                    continue;

                for (var i = 0; i < index.Columns.Count; i++)
                {
                    table.Rows.Add(
                        Database,
                        string.Empty,
                        schema.Name,
                        index.Name,
                        index.IsUnique,
                        index.Columns[i],
                        i + 1,
                        index.JsonPath ?? string.Empty,
                        new DateTime(index.CreatedAtUtcTicks, DateTimeKind.Utc));
                }
            }
        }

        return table;
    }

    private static DataTable BuildDataTypesSchema()
    {
        var table = CreateSchemaTable(DbMetaDataCollectionNames.DataTypes);
        table.Columns.Add("TypeName", typeof(string));
        table.Columns.Add("ProviderDbType", typeof(int));
        table.Columns.Add("ColumnSize", typeof(long));
        table.Columns.Add("CreateFormat", typeof(string));
        table.Columns.Add("CreateParameters", typeof(string));
        table.Columns.Add("DataType", typeof(string));
        table.Columns.Add("IsAutoIncrementable", typeof(bool));
        table.Columns.Add("IsBestMatch", typeof(bool));
        table.Columns.Add("IsCaseSensitive", typeof(bool));
        table.Columns.Add("IsFixedLength", typeof(bool));
        table.Columns.Add("IsFixedPrecisionScale", typeof(bool));
        table.Columns.Add("IsLong", typeof(bool));
        table.Columns.Add("IsNullable", typeof(bool));
        table.Columns.Add("IsSearchable", typeof(bool));
        table.Columns.Add("IsSearchableWithLike", typeof(bool));
        table.Columns.Add("IsUnsigned", typeof(bool));
        table.Columns.Add("MaximumScale", typeof(short));
        table.Columns.Add("MinimumScale", typeof(short));
        table.Columns.Add("LiteralPrefix", typeof(string));
        table.Columns.Add("LiteralSuffix", typeof(string));

        AddDataType(table, "INT", DbType.Int64, typeof(long), 19, searchable: true, fixedLength: true);
        AddDataType(table, "FLOAT", DbType.Double, typeof(double), 15, searchable: true, fixedLength: true, maximumScale: 15);
        AddDataType(table, "BOOL", DbType.Boolean, typeof(bool), 1, searchable: true, fixedLength: true);
        AddDataType(table, "STRING", DbType.String, typeof(string), int.MaxValue, searchable: true, searchableWithLike: true, caseSensitive: true, isLong: true, literalPrefix: "'", literalSuffix: "'");
        AddDataType(table, "DATETIME", DbType.DateTime, typeof(DateTime), 8, searchable: true, fixedLength: true);
        AddDataType(table, "BLOB", DbType.Binary, typeof(byte[]), int.MaxValue, searchable: false, isLong: true);
        AddDataType(table, "JSON", DbType.String, typeof(string), int.MaxValue, searchable: true, searchableWithLike: true, caseSensitive: true, isLong: true, literalPrefix: "'", literalSuffix: "'");
        return table;
    }

    private static DataTable BuildReservedWordsSchema()
    {
        var table = CreateSchemaTable(DbMetaDataCollectionNames.ReservedWords);
        table.Columns.Add("ReservedWord", typeof(string));
        foreach (var word in new[]
        {
            "ALTER", "BEGIN", "BOOL", "COMMIT", "CREATE", "DATABASE", "DELETE", "DROP",
            "FLOAT", "FROM", "GROUP", "INDEX", "INSERT", "INT", "JOIN", "JSON", "KEY",
            "NULL", "PRIMARY", "ROLLBACK", "SELECT", "SET", "STRING", "TABLE", "UPDATE", "WHERE",
        }.Order(StringComparer.Ordinal))
        {
            table.Rows.Add(word);
        }

        return table;
    }

    private static DataTable CreateSchemaTable(string name)
        => new(name) { Locale = CultureInfo.InvariantCulture };

    private static void AddDataType(
        DataTable table,
        string typeName,
        DbType dbType,
        Type runtimeType,
        long columnSize,
        bool searchable,
        bool searchableWithLike = false,
        bool fixedLength = false,
        bool caseSensitive = false,
        bool isLong = false,
        short maximumScale = 0,
        string literalPrefix = "",
        string literalSuffix = "")
    {
        table.Rows.Add(
            typeName,
            (int)dbType,
            columnSize,
            typeName,
            string.Empty,
            runtimeType.FullName ?? runtimeType.Name,
            false,
            true,
            caseSensitive,
            fixedLength,
            maximumScale != 0,
            isLong,
            true,
            searchable,
            searchableWithLike,
            false,
            maximumScale,
            (short)0,
            literalPrefix,
            literalSuffix);
    }

    private static string NormalizeVersion(string version)
    {
        if (Version.TryParse(version, out var parsed))
            return parsed.ToString();
        return "1.0.0";
    }

    private IReadOnlyList<TableSchema> SnapshotTables()
    {
        if (_impl is null || _impl.State != ConnectionState.Open)
            return Array.Empty<TableSchema>();

        return _impl.SnapshotTables();
    }

    private static string? Restriction(string?[]? restrictionValues, int index)
        => restrictionValues is not null && restrictionValues.Length > index
            ? restrictionValues[index]
            : null;

    private static bool MatchesRestriction(string value, string? restriction)
        => string.IsNullOrEmpty(restriction)
            || string.Equals(value, restriction, StringComparison.Ordinal);

    private static string FormatTableColumnType(TableColumnType type) => type switch
    {
        TableColumnType.Int64 => "INT",
        TableColumnType.Float64 => "FLOAT",
        TableColumnType.Boolean => "BOOL",
        TableColumnType.String => "STRING",
        TableColumnType.DateTime => "DATETIME",
        TableColumnType.Blob => "BLOB",
        TableColumnType.Json => "JSON",
        _ => type.ToString().ToUpperInvariant(),
    };

    private static int GetCharacterMaximumLength(TableColumnType type)
        => type is TableColumnType.String or TableColumnType.Json or TableColumnType.Blob
            ? int.MaxValue
            : -1;

    private static short GetNumericPrecision(TableColumnType type) => type switch
    {
        TableColumnType.Int64 => 19,
        TableColumnType.Float64 => 15,
        _ => 0,
    };

    private static short GetNumericScale(TableColumnType type)
        => type == TableColumnType.Float64 ? (short)15 : (short)0;
}
