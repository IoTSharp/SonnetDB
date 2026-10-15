using System.Collections.Concurrent;
using SonnetDB.Backup;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlSequenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-sequence231-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1L, 1L)]
    [InlineData(-5L, 3L)]
    [InlineData(5L, -3L)]
    [InlineData(long.MinValue, long.MaxValue)]
    public void Next_WithStartAndIncrement_PersistsExactInt64(long start, long increment)
    {
        using (var db = Open())
        {
            Execute(db, $"CREATE SEQUENCE Numbers START WITH {start} INCREMENT BY {increment} NO CYCLE");
            Assert.Equal(start, Next(db, "numbers"));
        }
        using var reopened = Open();
        Assert.Equal(checked(start + increment), Next(reopened, "NUMBERS"));
    }

    [Theory]
    [InlineData(long.MaxValue, 1L)]
    [InlineData(long.MinValue, -1L)]
    public void Next_Exhausted_RemainsExhaustedAfterReopen(long start, long increment)
    {
        using (var db = Open())
        {
            Execute(db, $"CREATE SEQUENCE edge START WITH {start} INCREMENT BY {increment}");
            Assert.Equal(start, Next(db, "edge"));
            Assert.Equal(SequenceException.Exhausted, Assert.Throws<SequenceException>(() => Next(db, "edge")).Code);
        }
        using var reopened = Open();
        Assert.Equal(SequenceException.Exhausted, Assert.Throws<SequenceException>(() => Next(reopened, "edge")).Code);
    }

    [Fact]
    public void Lifecycle_QuotedNamesAndDrop_PreserveIdentityAndResetExplicitly()
    {
        using var db = Open();
        Execute(db, "CREATE SEQUENCE \"Mixed\"\"Name\" START WITH 12");
        Assert.Equal(12L, Next(db, "\"Mixed\"\"Name\""));
        Assert.Equal(SequenceException.NotFound, Assert.Throws<SequenceException>(() => Next(db, "\"mixed\"\"name\"")).Code);
        Assert.Equal(SequenceException.AlreadyExists,
            Assert.Throws<SequenceException>(() => Execute(db, "CREATE SEQUENCE \"MIXED\"\"NAME\"")).Code);
        Execute(db, "CREATE SEQUENCE IF NOT EXISTS \"Mixed\"\"Name\" START WITH 999");
        Assert.Equal(13L, Next(db, "\"Mixed\"\"Name\""));
        Execute(db, "DROP SEQUENCE \"Mixed\"\"Name\"");
        Execute(db, "DROP SEQUENCE IF EXISTS \"Mixed\"\"Name\"");
        Assert.Equal(SequenceException.NotFound,
            Assert.Throws<SequenceException>(() => Execute(db, "DROP SEQUENCE missing")).Code);
        Execute(db, "CREATE SEQUENCE \"Mixed\"\"Name\" START WITH 3");
        Assert.Equal(3L, Next(db, "\"Mixed\"\"Name\""));
    }

    [Theory]
    [InlineData("CREATE SEQUENCE x INCREMENT BY 0")]
    [InlineData("CREATE SEQUENCE x CYCLE")]
    [InlineData("CREATE SEQUENCE a.b")]
    [InlineData("CREATE SEQUENCE x START WITH 9223372036854775808")]
    [InlineData("CREATE SEQUENCE x START WITH 1 START WITH 2")]
    public void Create_InvalidDefinition_RejectsWithoutCreating(string sql)
    {
        using var db = Open();
        Assert.Throws<SqlParseException>(() => Execute(db, sql));
        Assert.Equal(SequenceException.NotFound, Assert.Throws<SequenceException>(() => Next(db, "x")).Code);
    }

    [Fact]
    public void Insert_DefaultsExpressionsAndBulk_AllocateForEveryExecutedRow()
    {
        using var db = Open();
        Execute(db, "CREATE SEQUENCE shared START WITH 101 INCREMENT BY 2");
        Execute(db, "CREATE TABLE items(id INT DEFAULT nextval('shared'), value INT, PRIMARY KEY(id))");
        Execute(db, "INSERT INTO items(value) VALUES(10),(20)");
        Execute(db, "INSERT INTO items(id,value) VALUES(DEFAULT,30),(nextval('shared')+10,40)");
        Execute(db, "CREATE TABLE other(id INT,PRIMARY KEY(id))");
        Execute(db, "INSERT INTO other(id) SELECT nextval('shared') FROM items");
        Assert.Equal(new long[] { 101, 103, 105, 117 }, Query(db, "SELECT id FROM items ORDER BY id").Rows.Select(row => (long)row[0]!).ToArray());
        Assert.Equal(new long[] { 109, 111, 113, 115 }, Query(db, "SELECT id FROM other ORDER BY id").Rows.Select(row => (long)row[0]!).ToArray());
        Assert.Equal(117L, Next(db, "shared"));
    }

    [Fact]
    public void Default_CreateAlterAndEmptyRead_DoNotConsumeDuringValidation()
    {
        using var db = Open();
        Execute(db, "CREATE SEQUENCE numbers");
        Execute(db, "CREATE TABLE items(id INT DEFAULT nextval('numbers'), PRIMARY KEY(id))");
        Execute(db, "ALTER TABLE items ALTER COLUMN id SET DEFAULT nextval('numbers')");
        Assert.Empty(Query(db, "SELECT nextval('numbers') FROM items").Rows);
        _ = Execute(db, "EXPLAIN SELECT nextval('numbers') FROM items");
        Assert.Equal(1L, Next(db, "numbers"));
        Execute(db, "INSERT INTO items DEFAULT VALUES");
        Assert.Equal(2L, Assert.Single(Query(db, "SELECT id FROM items").Rows)[0]);
        Execute(db, "DROP SEQUENCE numbers");
        Assert.Equal(SequenceException.NotFound,
            Assert.Throws<SequenceException>(() => Execute(db, "INSERT INTO items DEFAULT VALUES")).Code);
    }

    [Fact]
    public void Expression_LazyBranches_OnlyExecutedCallsConsume()
    {
        using var db = Open();
        Execute(db, "CREATE SEQUENCE numbers");
        var result = Query(db, "SELECT CASE WHEN FALSE THEN nextval('numbers') ELSE 8 END, coalesce(7,nextval('numbers'))");
        Assert.Equal(new object?[] { 8L, 7L }, Assert.Single(result.Rows));
        Assert.Equal(1L, Next(db, "numbers"));
    }

    [Fact]
    public void Transaction_RollbackAndFailedBatch_DoNotReturnReservedNumbers()
    {
        using var connection = OpenConnection();
        Command(connection, "CREATE SEQUENCE numbers");
        Command(connection, "CREATE TABLE items(id INT DEFAULT nextval('numbers'), value INT NOT NULL, PRIMARY KEY(id))");
        using (var transaction = connection.BeginTransaction())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO items(value) VALUES(10),(20)";
            Assert.Equal(2, command.ExecuteNonQuery());
            command.CommandText = "SELECT nextval('numbers')";
            Assert.Equal(3L, command.ExecuteScalar());
            transaction.Rollback();
        }
        using (var failed = connection.CreateCommand())
        {
            failed.CommandText = "INSERT INTO items(value) VALUES(10),(NULL)";
            Assert.ThrowsAny<Exception>(() => failed.ExecuteNonQuery());
        }
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT nextval('numbers')";
        Assert.Equal(6L, query.ExecuteScalar());
        query.CommandText = "SELECT COUNT(*) FROM items";
        Assert.Equal(0L, query.ExecuteScalar());
    }

    [Fact]
    public async Task Next_TwoConnectionsConcurrent_ProducesUniqueDurableNumbers()
    {
        using (var first = OpenConnection())
        using (var second = OpenConnection())
        {
            Command(first, "CREATE SEQUENCE shared");
            var values = new ConcurrentBag<long>();
            await Task.WhenAll(Task.Run(() => Allocate(first)), Task.Run(() => Allocate(second)));
            Assert.Equal(Enumerable.Range(1, 80).Select(static n => (long)n), values.Order());

            void Allocate(SndbConnection connection)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT nextval('shared')";
                for (int i = 0; i < 40; i++) values.Add((long)command.ExecuteScalar()!);
            }
        }
        using var reopened = Open();
        Assert.Equal(81L, Next(reopened, "shared"));
    }

    [Fact]
    public void Next_ZeroRowsPermissionCancellationAndUdf_DoNotBypassContract()
    {
        using var db = Open();
        Execute(db, "CREATE SEQUENCE numbers");
        db.Functions.RegisterScalar("nextval", static _ => -1L, 1, 1);
        var select = SqlParser.Parse("SELECT nextval('numbers')");
        var denied = Assert.Throws<SequenceException>(() => SqlExecutor.ExecuteStatement(db, null, select, null, null, new SqlExecutionOptions { CanWrite = false }));
        Assert.Equal("forbidden", denied.Code);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<Exception>(() => SqlExecutor.ExecuteStatement(db, null, select, null, null, new SqlExecutionOptions { CancellationToken = canceled.Token }));
        Assert.Equal(1L, Next(db, "numbers"));
    }

    [Theory]
    [InlineData("SELECT nextval('n') WHERE nextval('n') > 0")]
    [InlineData("SELECT nextval('n') ORDER BY nextval('n')")]
    [InlineData("SELECT DISTINCT nextval('n')")]
    [InlineData("SELECT COUNT(nextval('n'))")]
    [InlineData("CREATE VIEW v AS SELECT nextval('n')")]
    [InlineData("CREATE TABLE t(id STRING DEFAULT nextval('n'),PRIMARY KEY(id))")]
    [InlineData("CREATE TABLE t(id INT DEFAULT nextval('n')+1,PRIMARY KEY(id))")]
    [InlineData("SELECT 0 UNION SELECT nextval('n')")]
    [InlineData("SELECT 0 UNION ALL SELECT nextval('n')")]
    [InlineData("SELECT 0 INTERSECT SELECT nextval('n')")]
    [InlineData("SELECT 0 EXCEPT SELECT nextval('n')")]
    [InlineData("SELECT nextval('n') + ((SELECT 1) IS NULL)")]
    [InlineData("SELECT nextval('n') IN (SELECT 1)")]
    public void Next_UnsupportedSideEffectLocations_RejectWithoutConsuming(string sql)
    {
        using var db = Open();
        Execute(db, "CREATE SEQUENCE n");
        Assert.ThrowsAny<Exception>(() => Execute(db, sql));
        Assert.Equal(1L, Next(db, "n"));
    }

    [Fact]
    public void Next_BackupRestored_KeepsConsumedHighWaterMark()
    {
        string backup = _root + "-backup";
        string restored = _root + "-restore";
        try
        {
            var service = new BackupService();
            using (var db = Open())
            {
                Execute(db, "CREATE SEQUENCE n START WITH 18");
                Assert.Equal(18L, Next(db, "n"));
                _ = service.Create(db, new BackupCreateOptions { DestinationDirectory = backup });
            }
            _ = service.Restore(new BackupRestoreOptions { BackupDirectory = backup, TargetDirectory = restored });
            using var db2 = Tsdb.Open(new TsdbOptions { RootDirectory = restored });
            Assert.Equal(19L, Next(db2, "n"));
        }
        finally
        {
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(restored)) Directory.Delete(restored, true);
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("drop")]
    [InlineData("next")]
    public void Persist_AmbiguousFailure_IsolatesUntilReopen(string operation)
    {
        using (var db = Open())
        {
            if (operation != "create") Execute(db, "CREATE SEQUENCE n");
            db.Sequences.AfterPersistTestHook = _ => throw new IOException("injected post-WAL failure");
            Assert.Throws<IOException>(() =>
            {
                if (operation == "create") Execute(db, "CREATE SEQUENCE n");
                else if (operation == "drop") Execute(db, "DROP SEQUENCE n");
                else Next(db, "n");
            });
            db.Sequences.AfterPersistTestHook = null;
            Assert.Equal(SequenceException.RecoveryRequired, Assert.Throws<SequenceException>(() => Next(db, "n")).Code);
            Assert.Equal(SequenceException.RecoveryRequired,
                Assert.Throws<SequenceException>(() => Execute(db, "CREATE SEQUENCE other")).Code);
        }
        using var reopened = Open();
        if (operation == "drop")
            Assert.Equal(SequenceException.NotFound, Assert.Throws<SequenceException>(() => Next(reopened, "n")).Code);
        else Assert.Equal(operation == "next" ? 2L : 1L, Next(reopened, "n"));
    }

    [Fact]
    public async Task Backup_HoldsSequenceFreezeThroughCopy_CancellationDoesNotConsume()
    {
        using var db = Open();
        Execute(db, "CREATE SEQUENCE n");
        using var copying = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var requesting = new ManualResetEventSlim();
        using var canceled = new CancellationTokenSource();
        var backup = Task.Run(() => db.Sequences.ExecuteConsistentBackup(() =>
        {
            copying.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return 0;
        }));
        try
        {
            Assert.True(copying.Wait(TimeSpan.FromSeconds(5)));
            var waiting = Task.Run(() =>
            {
                requesting.Set();
                Assert.ThrowsAny<OperationCanceledException>(() => db.Sequences.Next("n", canceled.Token));
            });
            Assert.True(requesting.Wait(TimeSpan.FromSeconds(5)));
            canceled.Cancel();
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        await backup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1L, Next(db, "n"));
    }

    [Fact]
    public void Ddl_ActiveTransaction_RejectsBeforeChangingSequence()
    {
        using var db = Open();
        var transaction = new SqlTransactionContext();
        Assert.Throws<NotSupportedException>(() => SqlExecutor.ExecuteStatement(db, null,
            SqlParser.Parse("CREATE SEQUENCE n"), null, transaction, SqlExecutionOptions.Default));
        Assert.Equal(SequenceException.NotFound, Assert.Throws<SequenceException>(() => Next(db, "n")).Code);
    }

    [Fact]
    public void PublicInsert_DefaultsAndExpressions_UseCurrentDatabaseScope()
    {
        using var db = Open();
        Execute(db, "CREATE SEQUENCE n");
        Execute(db, "CREATE TABLE items(id INT DEFAULT nextval('n'),PRIMARY KEY(id))");
        SqlExecutor.ExecuteInsert(db, Assert.IsType<InsertStatement>(SqlParser.Parse("INSERT INTO items DEFAULT VALUES")));
        SqlExecutor.ExecuteInsert(db, Assert.IsType<InsertStatement>(SqlParser.Parse("INSERT INTO items(id) VALUES(nextval('n'))")));
        Assert.Equal(new long[] { 1, 2 }, Query(db, "SELECT id FROM items ORDER BY id").Rows.Select(row => (long)row[0]!).ToArray());
    }

    [Fact]
    public void Readers_TwoDatabasesAndEarlyClose_ConsumeAtExecuteWithinOriginalScope()
    {
        string otherRoot = _root + "-other";
        try
        {
            using var first = OpenConnection();
            using var second = new SndbConnection($"Data Source={otherRoot}");
            second.Open();
            Command(first, "CREATE SEQUENCE n");
            Command(second, "CREATE SEQUENCE n START WITH 101");
            Command(first, "CREATE TABLE items(id INT,PRIMARY KEY(id))");
            Command(first, "INSERT INTO items(id) VALUES(1),(2),(3)");
            using var firstQuery = first.CreateCommand();
            firstQuery.CommandText = "SELECT nextval('n') FROM items";
            using (var firstReader = firstQuery.ExecuteReader())
            {
                using var secondQuery = second.CreateCommand();
                secondQuery.CommandText = "SELECT nextval('n')";
                using var secondReader = secondQuery.ExecuteReader();
                Assert.True(secondReader.Read());
                Assert.Equal(101L, secondReader.GetInt64(0));
                Assert.True(firstReader.Read());
                Assert.Equal(1L, firstReader.GetInt64(0));
            }
            firstQuery.CommandText = "SELECT nextval('n') FROM items LIMIT 0";
            using (var empty = firstQuery.ExecuteReader())
            {
                Assert.Equal(typeof(long), empty.GetFieldType(0));
                Assert.False(empty.Read());
            }
            firstQuery.CommandText = "SELECT nextval('n')";
            Assert.Equal(4L, firstQuery.ExecuteScalar());
        }
        finally
        {
            if (Directory.Exists(otherRoot)) Directory.Delete(otherRoot, true);
        }
    }

    [Fact]
    public async Task Next_TableLockAndConcurrentSchemaMutation_DoNotInvertLocks()
    {
        using var db = Open();
        Execute(db, "CREATE SEQUENCE n");
        using var tableHeld = new ManualResetEventSlim();
        using var schemaHeld = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        db.SchemaMutationLockAcquiredTestHook = schemaHeld.Set;
        var allocation = Task.Run(() => db.Tables.ExecuteLocked(() =>
        {
            tableHeld.Set();
            Assert.True(schemaHeld.Wait(TimeSpan.FromSeconds(5)));
            return db.Sequences.Next("n", cancellation.Token);
        }));
        Assert.True(tableHeld.Wait(TimeSpan.FromSeconds(5)));
        var schema = Task.Run(() => db.ExecuteSchemaMutation(() => db.Tables.ExecuteLocked(() => 0)));
        try
        {
            Assert.Equal(1L, await allocation.WaitAsync(TimeSpan.FromSeconds(5)));
            await schema.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
            db.SchemaMutationLockAcquiredTestHook = null;
            await Task.WhenAll(allocation, schema).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Classifier_NestedStatementContainers_AlwaysDetectsSequenceWrites()
    {
        var next = new FunctionCallExpression("nextval", [LiteralExpression.String("n")]);
        var query = Assert.IsType<SelectStatement>(SqlParser.Parse("SELECT 1"));
        var join = new JoinClause("items", "i", next);
        SqlStatement[] statements =
        [
            query with { SetOperations = [new SqlSetOperation(SqlSetOperationKind.Except, query with { Projections = [new SelectItem(next, null)] })] },
            query with { GraphTable = new GraphTableSource("g", new GraphPatternVertex("a", "1"), new GraphPatternEdge("e", "1"),
                new GraphPatternVertex("b", "1"), SonnetDB.Graphs.GraphDirection.Outgoing, next, []) },
            query with { GraphTable = new GraphTableSource("g", new GraphPatternVertex("a", "1"), new GraphPatternEdge("e", "1"),
                new GraphPatternVertex("b", "1"), SonnetDB.Graphs.GraphDirection.Outgoing, null, [new SelectItem(next, null)]) },
            new UpdateStatement("items", [], LiteralExpression.Bool(true)) { FromClauses = [join] },
            new InsertStatement("items", [], []) { OnConflict = new SqlOnConflictClause([]) { UpdateWhere = next } },
            new InsertGraphStatement("g", GraphMutationKind.Vertex, [], [[next]]),
            new CreateProcedureStatement("p", [], [query with { Projections = [new SelectItem(next, null)] }], "SELECT nextval('n')"),
        ];
        Assert.All(statements, statement => Assert.True(SqlSequenceRuntime.Contains(statement)));
    }

    [Theory]
    [InlineData("UPDATE items SET value=DEFAULT")]
    [InlineData("UPDATE items SET value=DEFAULT FROM other WHERE items.id=other.id")]
    [InlineData("INSERT INTO items(id) VALUES(1) ON CONFLICT DO NOTHING")]
    [InlineData("INSERT INTO items(id,value) VALUES(1,DEFAULT) ON CONFLICT DO NOTHING")]
    [InlineData("INSERT INTO items(id,value) VALUES(1,99) ON CONFLICT(id) DO UPDATE SET value=DEFAULT")]
    public void Default_UnsupportedMutationLocations_RejectBeforeConsuming(string sql)
    {
        using var db = Open();
        Execute(db, "CREATE SEQUENCE n");
        Execute(db, "CREATE TABLE items(id INT,value INT DEFAULT nextval('n'),PRIMARY KEY(id))");
        Execute(db, "CREATE TABLE other(id INT,PRIMARY KEY(id))");
        Execute(db, "INSERT INTO items(id,value) VALUES(1,99)");
        Execute(db, "INSERT INTO other(id) VALUES(1)");
        Assert.Equal(SequenceException.InvalidDefinition, Assert.Throws<SequenceException>(() => Execute(db, sql)).Code);
        Assert.Equal(99L, Assert.Single(Query(db, "SELECT value FROM items").Rows)[0]);
        Assert.Equal(1L, Next(db, "n"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trigger_ImplicitSequenceDefault_RejectsWithoutConsumptionAndRollsBackTarget(bool reopenBeforeExecution)
    {
        using (var db = Open())
        {
            Execute(db, "CREATE SEQUENCE n");
            Execute(db, "CREATE TABLE source(id INT,PRIMARY KEY(id))");
            Execute(db, "CREATE TABLE audit(id INT DEFAULT nextval('n'),PRIMARY KEY(id))");
            Execute(db, "CREATE TRIGGER source_audit AFTER INSERT ON source FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO audit DEFAULT VALUES; END");
            if (!reopenBeforeExecution) Verify(db);
        }
        if (reopenBeforeExecution)
        {
            using var reopened = Open();
            Verify(reopened);
        }

        static void Verify(Tsdb db)
        {
            Assert.ThrowsAny<Exception>(() => Execute(db, "INSERT INTO source(id) VALUES(1)"));
            Assert.Empty(Query(db, "SELECT id FROM source").Rows);
            Assert.Empty(Query(db, "SELECT id FROM audit").Rows);
            Assert.Equal(1L, Next(db, "n"));
        }
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root, SyncWalOnEveryWrite = false });
    private SndbConnection OpenConnection()
    {
        var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        return connection;
    }
    private static void Command(SndbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static object? Execute(Tsdb db, string sql) => SqlExecutor.Execute(db, sql);
    private static SelectExecutionResult Query(Tsdb db, string sql) => Assert.IsType<SelectExecutionResult>(Execute(db, sql));
    private static long Next(Tsdb db, string reference) => Assert.IsType<long>(Assert.Single(Query(db,
        "SELECT nextval('" + reference.Replace("'", "''", StringComparison.Ordinal) + "')").Rows)[0]);
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
