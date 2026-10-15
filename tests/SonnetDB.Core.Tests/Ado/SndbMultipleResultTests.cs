using System.Data;
using System.Data.Common;
using System.Reflection;
using SonnetDB.Data;
using SonnetDB.Data.Embedded;
using SonnetDB.Data.Internal;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Core.Tests.Ado;

/// <summary>GH-Issue #229：多条 SELECT 的行集、元数据、预检及读取器生命周期合同。</summary>
public sealed class SndbMultipleResultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-multiple-" + Guid.NewGuid().ToString("N"));

    /// <summary>回收本测试的数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NextResult_EmptyOrPartiallyReadFirstSet_RefreshesRowsAndMetadata(bool useAsync, bool firstEmpty)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM items WHERE id {(firstEmpty ? "< 0" : "> 0")} ORDER BY id; SELECT name FROM items WHERE id = @id";
        command.Parameters.AddWithValue("id", 2L);
        using var reader = useAsync ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        Assert.Equal("id", reader.GetName(0));
        Assert.Equal(typeof(long), reader.GetFieldType(0));
        Assert.Equal(!firstEmpty, reader.HasRows);
        if (!firstEmpty)
        {
            Assert.True(useAsync ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
        }
        Assert.True(useAsync ? await reader.NextResultAsync() : reader.NextResult());
        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
        Assert.Equal("name", reader.GetName(0));
        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.Equal("name", reader.GetSchemaTable()!.Rows[0]["ColumnName"]);
        Assert.Equal("name", reader.GetColumnSchema()[0].ColumnName);
        Assert.True(reader.HasRows);
        Assert.True(useAsync ? await reader.ReadAsync() : reader.Read());
        Assert.Equal("second", reader.GetString(0));
        Assert.False(useAsync ? await reader.ReadAsync() : reader.Read());
        Assert.False(useAsync ? await reader.NextResultAsync() : reader.NextResult());
        Assert.False(reader.HasRows);
        Assert.False(await reader.NextResultAsync());
    }

    [Theory]
    [InlineData("-- leading ; @ignored\n")]
    [InlineData("/* leading ; @ignored */")]
    [InlineData("// leading ; @ignored\n")]
    [InlineData("rem leading ; @ignored\n")]
    public void ExecuteReader_CommentAndQuotedSemicolons_PreservesStatementBoundaries(string prefix)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = prefix + "SELECT 'a;''b' AS \"first;column\" FROM items WHERE id = 1; /* ; */ SELECT name AS \"part;name\" FROM items WHERE id = 2; -- ;\n";
        using var reader = command.ExecuteReader();
        Assert.Equal("first;column", reader.GetName(0));
        Assert.True(reader.Read());
        Assert.Equal("a;'b", reader.GetString(0));
        Assert.True(reader.NextResult());
        Assert.Equal("part;name", reader.GetName(0));
        Assert.True(reader.Read());
        Assert.Equal("second", reader.GetString(0));
        Assert.False(reader.NextResult());
    }

    [Fact]
    public void ExecuteReader_ParametersChangeAfterFirstResult_UsesFrozenNamedAndPositionalValues()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM items WHERE id = @first; SELECT name FROM items WHERE id = ?";
        command.Parameters.AddWithValue("first", 1L);
        command.Parameters.AddWithValue("second", 2L);
        using var reader = command.ExecuteReader();
        command.Parameters[0].Value = 2L;
        command.Parameters[1].Value = 1L;
        command.Parameters.Clear();
        command.CommandText = "SELECT id FROM items";
        Assert.True(reader.Read());
        Assert.Equal("first", reader.GetString(0));
        Assert.True(reader.NextResult());
        Assert.True(reader.Read());
        Assert.Equal("second", reader.GetString(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteReader_UnmatchedNamedParameters_UsesFrozenGlobalOccurrenceOrdinals(bool useAsync)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT @a AS value; SELECT :b AS value";
        command.Parameters.AddWithValue("x", 1L);
        command.Parameters.AddWithValue("y", 2L);
        using var reader = useAsync ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        command.Parameters[1].Value = 99L;
        command.Parameters.Clear();
        Assert.True(useAsync ? await reader.ReadAsync() : reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(useAsync ? await reader.NextResultAsync() : reader.NextResult());
        Assert.True(useAsync ? await reader.ReadAsync() : reader.Read());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.False(useAsync ? await reader.NextResultAsync() : reader.NextResult());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteReader_MixedAndRepeatedPlaceholders_PrefersNamesAndCountsEveryOccurrence(bool useAsync)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT @missing + :missing AS value; SELECT ? + @BOUND + @last AS value";
        command.Parameters.AddWithValue(":bound", 10L);
        command.Parameters.AddWithValue("p1", 2L);
        command.Parameters.AddWithValue("p2", 3L);
        command.Parameters.AddWithValue("p3", 4L);
        command.Parameters.AddWithValue("p4", 5L);
        using var reader = useAsync ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        Assert.True(useAsync ? await reader.ReadAsync() : reader.Read());
        Assert.Equal(12L, reader.GetInt64(0));
        Assert.True(useAsync ? await reader.NextResultAsync() : reader.NextResult());
        Assert.True(useAsync ? await reader.ReadAsync() : reader.Read());
        Assert.Equal(18L, reader.GetInt64(0));
        Assert.False(useAsync ? await reader.NextResultAsync() : reader.NextResult());
    }

    [Fact]
    public void ExecuteReader_OrdinalFallbackNameCollision_PreservesExplicitAndPositionalValues()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT @missing AS value; SELECT @__sndb_batch_0 AS named_value, ? AS positional_value";
        command.Parameters.AddWithValue("__sndb_batch_0", 7L);
        command.Parameters.AddWithValue("p1", 8L);
        command.Parameters.AddWithValue("p2", 9L);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(7L, reader.GetInt64(0));
        Assert.True(reader.NextResult());
        Assert.True(reader.Read());
        Assert.Equal(7L, reader.GetInt64(0));
        Assert.Equal(9L, reader.GetInt64(1));
        Assert.False(reader.NextResult());
    }

    [Theory]
    [InlineData("SELECT @missing")]
    [InlineData("SELECT ?")]
    public void ExecuteReader_MissingGlobalOrdinal_RejectsInsteadOfReusingFirstStatementParameter(string second)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT @x; " + second;
        command.Parameters.AddWithValue("x", 1L);
        Assert.Throws<InvalidOperationException>(() => command.ExecuteReader());
    }

    [Fact]
    public void ExecuteReader_MutableBlobParameter_UsesFrozenBytesInLaterResult()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        byte[] bytes = [1, 2, 3];
        command.Parameters.AddWithValue("blob", bytes);
        command.CommandText = "SELECT id FROM items WHERE id=1; SELECT @blob AS payload";
        using var reader = command.ExecuteReader();
        bytes[0] = 9;
        Assert.True(reader.NextResult());
        Assert.True(reader.Read());
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(reader.GetValue(0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecuteReader_MultipleResults_DataSetLoadsBothTables(bool useView)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (useView)
        {
            command.CommandText = "CREATE VIEW item_names AS SELECT id,name FROM items";
            command.ExecuteNonQuery();
        }
        command.CommandText = "SELECT id FROM items WHERE id=1; SELECT name FROM "
            + (useView ? "item_names" : "items") + " WHERE id=2";
        using var reader = command.ExecuteReader();
        using var data = new DataSet();
        data.Load(reader, LoadOption.OverwriteChanges, "ids", "names");
        Assert.Equal(2, data.Tables.Count);
        Assert.Equal(1L, data.Tables[0].Rows[0]["id"]);
        Assert.Equal("second", data.Tables[1].Rows[0]["name"]);
        Assert.Equal(-1, data.Tables[1].Columns["name"]!.MaxLength);
    }

    [Fact]
    public void GetSchemaTable_UnboundedDeclaredString_AdaptsClassicLengthWithoutInventingDeclaration()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM items WHERE id=2";
        using var reader = command.ExecuteReader();
        Assert.Null(reader.GetColumnSchema()[0].ColumnSize);
        Assert.Equal(-1, reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.ColumnSize]);
        using var table = new DataTable();
        table.Load(reader);
        Assert.Equal("second", table.Rows[0]["name"]);
        Assert.Equal(-1, table.Columns["name"]!.MaxLength);
    }

    [Theory]
    [InlineData("DELETE FROM items WHERE id=1")]
    [InlineData("CREATE TABLE hidden (id INT, PRIMARY KEY(id))")]
    [InlineData("BEGIN")]
    [InlineData("CALL routine()")]
    [InlineData("SELECT FROM")]
    [InlineData("SELECT id FROM items FOR UPDATE")]
    public void ExecuteReader_InvalidLaterStatement_PreflightExecutesNothing(string second)
    {
        using var implementation = new ProbeConnectionImpl();
        using var connection = Inject(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1; " + second;
        Assert.NotNull(Record.Exception(() => command.ExecuteReader()));
        Assert.Equal(0, implementation.ExecutionCount);
        command.CommandText = "SELECT 1; SELECT 2";
        using var reader = command.ExecuteReader();
        Assert.Equal(1, implementation.ExecutionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextResult_LaterSemanticError_PropagatesAndDisposeReleasesLease(bool useAsync)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM items; SELECT id FROM missing_table";
        var reader = useAsync ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.NotNull(useAsync
            ? await Record.ExceptionAsync(() => reader.NextResultAsync())
            : Record.Exception(() => reader.NextResult()));
        Assert.False(await reader.NextResultAsync());
        reader.Dispose();
        command.CommandText = "SELECT id FROM items WHERE id=2";
        using var next = command.ExecuteReader();
        Assert.True(next.Read());
        Assert.Equal(2L, next.GetInt64(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextResult_SlowLaterExecution_UsesReadTimeoutAndDisposesFirstResult(bool useAsync)
    {
        using var implementation = new ProbeConnectionImpl(blockSecond: true);
        using var connection = Inject(implementation);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "SELECT 1; SELECT 2";
        using var reader = await command.ExecuteReaderAsync();
        var error = useAsync
            ? await Record.ExceptionAsync(() => reader.NextResultAsync()).WaitAsync(TimeSpan.FromSeconds(5))
            : await Record.ExceptionAsync(() => Task.Run(reader.NextResult)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<TimeoutException>(error);
        Assert.Equal(2, implementation.ExecutionCount);
        Assert.Equal(1, implementation.DisposedResults);
        Assert.False(await reader.NextResultAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextResult_CallerOrCommandCancelsLaterExecution_PreservesCancellation(bool cancelCommand)
    {
        using var implementation = new ProbeConnectionImpl(blockSecond: true);
        using var connection = Inject(implementation);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = "SELECT 1; SELECT 2";
        using var reader = await command.ExecuteReaderAsync();
        using var cancellation = new CancellationTokenSource();
        var move = reader.NextResultAsync(cancellation.Token);
        await implementation.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancelCommand)
            command.Cancel();
        else
            cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move)
            .WaitAsync(TimeSpan.FromSeconds(3));
        if (!cancelCommand)
            Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, implementation.DisposedResults);
        reader.Close();
        command.CommandText = "SELECT 3";
        using var next = await command.ExecuteReaderAsync();
    }

    [Fact]
    public async Task NextResult_PreCanceledToken_DoesNotExecuteOrDiscardCurrentResult()
    {
        using var implementation = new ProbeConnectionImpl();
        using var connection = Inject(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1; SELECT 2";
        using var reader = await command.ExecuteReaderAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.NextResultAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, implementation.ExecutionCount);
        Assert.Equal(0, implementation.DisposedResults);
        Assert.True(reader.Read());
        Assert.True(await reader.NextResultAsync());
    }

    [Theory]
    [InlineData(CommandBehavior.Default)]
    [InlineData(CommandBehavior.SingleResult)]
    [InlineData(CommandBehavior.CloseConnection)]
    public void Close_PendingResults_ExecutesNoLaterQueryAndReleasesResources(CommandBehavior behavior)
    {
        using var implementation = new ProbeConnectionImpl();
        using var connection = Inject(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1; SELECT 2";
        var reader = command.ExecuteReader(behavior);
        Assert.Throws<InvalidOperationException>(() => command.ExecuteReader());
        if (behavior == CommandBehavior.SingleResult)
            Assert.False(reader.NextResult());
        reader.Close();
        reader.Close();
        Assert.Equal(1, implementation.ExecutionCount);
        Assert.Equal(1, implementation.DisposedResults);
        Assert.False(reader.NextResult());
        Assert.Equal(behavior == CommandBehavior.CloseConnection, implementation.Closed);
        if (behavior != CommandBehavior.CloseConnection)
        {
            command.CommandText = "SELECT 3";
            using var next = command.ExecuteReader();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExecuteScalarOrNonQuery_LaterQueryFails_DoesNotSilentlyIgnoreError(bool scalar)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM items; SELECT id FROM missing_table";
        Assert.NotNull(Record.Exception(() =>
        {
            if (scalar) command.ExecuteScalar();
            else command.ExecuteNonQuery();
        }));
    }

    [Fact]
    public void ExecuteReader_BatchLimits_RejectsBeforeExecutingAndAcceptsStatementBoundary()
    {
        using var implementation = new ProbeConnectionImpl();
        using var connection = Inject(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = string.Join(';', Enumerable.Repeat("SELECT 1", SqlSelectBatch.MaximumStatements + 1));
        Assert.Throws<NotSupportedException>(() => command.ExecuteReader());
        command.CommandText = "SELECT '" + new string('a', SqlSelectBatch.MaximumSqlLength) + "'; SELECT 2";
        Assert.Throws<NotSupportedException>(() => command.ExecuteReader());
        Assert.Equal(0, implementation.ExecutionCount);
        command.CommandText = string.Join(';', Enumerable.Repeat("SELECT 1", SqlSelectBatch.MaximumStatements));
        using var reader = command.ExecuteReader();
        int sets = 1;
        while (reader.NextResult()) sets++;
        Assert.Equal(SqlSelectBatch.MaximumStatements, sets);
        Assert.Equal(SqlSelectBatch.MaximumStatements, implementation.DisposedResults);
    }

    [Fact]
    public void ExecuteReader_LargeSingleStatement_DoesNotApplyMultipleResultBudget()
    {
        using var implementation = new ProbeConnectionImpl();
        using var connection = Inject(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT '" + new string('a', SqlSelectBatch.MaximumSqlLength) + "';";
        using var reader = command.ExecuteReader();
        Assert.Equal(1, implementation.ExecutionCount);
    }

    private SndbConnection Open()
    {
        var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = "CREATE TABLE items (id INT, name STRING, PRIMARY KEY(id))";
        setup.ExecuteNonQuery();
        setup.CommandText = "INSERT INTO items(id,name) VALUES(1,'first'),(2,'second')";
        setup.ExecuteNonQuery();
        return connection;
    }

    private static SndbConnection Inject(IConnectionImpl implementation)
    {
        var connection = new SndbConnection();
        typeof(SndbConnection).GetField("_impl", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(connection, implementation);
        return connection;
    }

    private sealed class ProbeConnectionImpl(bool blockSecond = false) : IConnectionImpl
    {
        public int ExecutionCount { get; private set; }
        public int DisposedResults { get; private set; }
        public bool Closed { get; private set; }
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DataSource => "multiple-test";
        public string Database => "multiple-test";
        public string ServerVersion => "test";
        public ConnectionState State => Closed ? ConnectionState.Closed : ConnectionState.Open;
        public void Open() { }
        public ValueTask OpenAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public void Close() => Closed = true;
        public void Dispose() => Close();
        public IExecutionResult Execute(string sql, SndbParameterCollection parameters, CommandBehavior behavior, object? transactionState)
            => throw new NotSupportedException();
        public async Task<IExecutionResult> ExecuteAsync(string sql, SndbParameterCollection parameters,
            CommandBehavior behavior, object? transactionState, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            if (blockSecond && ExecutionCount == 2)
            {
                SecondStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new ProbeResult(() => DisposedResults++);
        }
        public IExecutionResult ExecuteBulk(string commandText, SndbParameterCollection parameters, object? transactionState)
            => throw new NotSupportedException();
        public Task<IExecutionResult> ExecuteBulkAsync(string commandText, SndbParameterCollection parameters,
            object? transactionState, CancellationToken cancellationToken) => throw new NotSupportedException();
        public object BeginTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        public void CommitTransaction(object transactionState) => throw new NotSupportedException();
        public Task CommitTransactionAsync(object transactionState, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void RollbackTransaction(object transactionState) => throw new NotSupportedException();
        public Task RollbackTransactionAsync(object transactionState, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<TableSchema> SnapshotTables() => throw new NotSupportedException();
    }

    private sealed class ProbeResult(Action disposed) : IExecutionResult
    {
        private readonly IExecutionResult _inner = MaterializedExecutionResult.FromSelect(
            new SelectExecutionResult(["value"], [new object?[] { 1L }]));
        public int RecordsAffected => -1;
        public IReadOnlyList<string> Columns => _inner.Columns;
        public bool ReadNextRow() => _inner.ReadNextRow();
        public ValueTask<bool> ReadNextRowAsync(CancellationToken cancellationToken) => _inner.ReadNextRowAsync(cancellationToken);
        public object? GetValue(int ordinal) => _inner.GetValue(ordinal);
        public Type GetFieldType(int ordinal) => typeof(long);
        public void Dispose() { _inner.Dispose(); disposed(); }
    }
}
