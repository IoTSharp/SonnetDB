using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class RelationalInputPushdownTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-relational-pushdown-{Guid.NewGuid():N}");

    [Fact]
    public void Execute_InnerJoinSingleInputPredicates_UseIndexesAndPruneColumns()
    {
        using var database = CreateDatabase();
        var taskStore = database.Tables.Open("push_tasks");
        var deviceStore = database.Tables.Open("push_devices");
        long taskScans = taskStore.FullScanCount;
        long deviceScans = deviceStore.FullScanCount;

        var (result, metrics) = Execute(database, """
            SELECT t.id, d.region
            FROM push_tasks t
            JOIN push_devices d ON t.device_id = d.id
            WHERE T.STATUS = 'ready' AND D.REGION = 'north'
            ORDER BY t.id
            """);

        Assert.Equal([new object?[] { 1L, "north" }], result.Rows);
        Assert.Equal(taskScans, taskStore.FullScanCount);
        Assert.Equal(deviceScans, deviceStore.FullScanCount);
        Assert.Equal(2, metrics.InputPredicatePushdownCount);
        Assert.Equal(2, metrics.InputProjectionPushdownCount);
        Assert.Equal(9, metrics.InputSourceColumns);
        Assert.Equal(5, metrics.InputProjectedColumns);
        Assert.Equal(4, metrics.InputCandidateRows);
        Assert.Equal(4, metrics.InputRetainedRows);
    }

    [Fact]
    public void Execute_CrossInputPredicate_KeepsResidualAndScansBothInputs()
    {
        using var database = CreateDatabase();
        var taskStore = database.Tables.Open("push_tasks");
        var deviceStore = database.Tables.Open("push_devices");
        long taskScans = taskStore.FullScanCount;
        long deviceScans = deviceStore.FullScanCount;

        var (result, metrics) = Execute(database, """
            SELECT t.id
            FROM push_tasks t
            JOIN push_devices d ON t.device_id = d.id
            WHERE t.status = 'ready' OR d.region = 'north'
            ORDER BY t.id
            """);

        Assert.Equal([1L, 2L, 3L], result.Rows.Select(static row => (long)row[0]!));
        Assert.Equal(taskScans + 1, taskStore.FullScanCount);
        Assert.Equal(deviceScans + 1, deviceStore.FullScanCount);
        Assert.Equal(0, metrics.InputPredicatePushdownCount);
        Assert.Equal(2, metrics.InputProjectionPushdownCount);
    }

    [Fact]
    public void Execute_LeftJoinRightNullPredicate_DoesNotChangeOuterJoinSemantics()
    {
        using var database = CreateDatabase();

        var (result, metrics) = Execute(database, """
            SELECT t.id
            FROM push_tasks t
            LEFT JOIN push_devices d ON t.device_id = d.id
            WHERE d.id IS NULL
            ORDER BY t.id
            """);

        Assert.Equal([4L], result.Rows.Select(static row => (long)row[0]!));
        Assert.Equal(0, metrics.InputPredicatePushdownCount);
    }

    [Fact]
    public void Execute_AggregateJoin_PushesSafeInputPredicate()
    {
        using var database = CreateDatabase();

        var (result, metrics) = Execute(database, """
            SELECT d.region, COUNT(*) AS task_count
            FROM push_tasks t
            JOIN push_devices d ON t.device_id = d.id
            WHERE t.status = 'ready'
            GROUP BY d.region
            ORDER BY d.region
            """);

        Assert.Equal(
            [new object?[] { "north", 1L }, new object?[] { "south", 1L }],
            result.Rows);
        Assert.Equal(1, metrics.InputPredicatePushdownCount);
        Assert.Equal(2, metrics.InputProjectionPushdownCount);
    }

    [Fact]
    public void Execute_CorrelatedSubquery_DisablesInputPushdownAndPreservesResults()
    {
        using var database = CreateDatabase();

        var (result, metrics) = Execute(database, """
            SELECT t.id
            FROM push_tasks t
            JOIN push_devices d ON t.device_id = d.id
            WHERE EXISTS (
                SELECT 1 FROM push_tasks x
                WHERE x.id = t.id AND x.status = 'ready')
            ORDER BY t.id
            """);

        Assert.Equal([1L, 3L], result.Rows.Select(static row => (long)row[0]!));
        Assert.Equal(0, metrics.InputPredicatePushdownCount);
        Assert.Equal(0, metrics.InputProjectionPushdownCount);
    }

    [Fact]
    public void Execute_LeftJoinWithoutResidual_PushesSafeInputLimitWindow()
    {
        using var database = CreateDatabase();

        var (result, metrics) = Execute(database, """
            SELECT t.id, d.region
            FROM push_tasks t
            LEFT JOIN push_devices d ON t.device_id = d.id
            LIMIT 2 OFFSET 1
            """);

        Assert.Equal(
            [new object?[] { 2L, "north" }, new object?[] { 3L, "south" }],
            result.Rows);
        Assert.Equal(1, metrics.InputLimitPushdownCount);
        Assert.Equal(3, metrics.InputRetainedRows - database.Tables.Open("push_devices").RowCount);

        var (empty, emptyMetrics) = Execute(database, """
            SELECT t.id
            FROM push_tasks t
            LEFT JOIN push_devices d ON t.device_id = d.id
            LIMIT 0
            """);
        Assert.Empty(empty.Rows);
        Assert.Equal(1, emptyMetrics.InputLimitPushdownCount);
    }

    [Fact]
    public void Execute_LogicalViewJoin_FallsBackWithoutChangingResults()
    {
        using var database = CreateDatabase();
        SqlExecutor.Execute(database, """
            CREATE VIEW ready_push_tasks AS
            SELECT id, device_id FROM push_tasks WHERE status = 'ready'
            """);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, """
            SELECT t.id, d.region
            FROM ready_push_tasks t
            JOIN push_devices d ON t.device_id = d.id
            ORDER BY t.id
            """));

        Assert.Equal(
            [new object?[] { 1L, "north" }, new object?[] { 3L, "south" }],
            result.Rows);
    }

    [Fact]
    public void Execute_TransactionOverlayWithInputPredicates_PreservesReadYourWrites()
    {
        using var database = CreateDatabase();
        var transaction = new SqlTransactionContext();
        SqlExecutor.ExecuteStatement(
            database,
            databaseName: null,
            SqlParser.Parse("""
                INSERT INTO push_tasks (id, device_id, status, payload, note)
                VALUES (5, 20, 'ready', 'task-5', 'n5')
                """),
            controlPlane: null,
            transaction);
        SqlExecutor.ExecuteStatement(
            database,
            databaseName: null,
            SqlParser.Parse("UPDATE push_devices SET region = 'east' WHERE id = 20"),
            controlPlane: null,
            transaction);

        var taskStore = database.Tables.Open("push_tasks");
        var deviceStore = database.Tables.Open("push_devices");
        long taskScans = taskStore.FullScanCount;
        long deviceScans = deviceStore.FullScanCount;

        using var transactionScope = SqlTransactionContext.EnterScope(transaction);
        var (result, metrics) = Execute(database, """
            SELECT t.id, d.region
            FROM push_tasks t
            JOIN push_devices d ON t.device_id = d.id
            WHERE t.status = 'ready' AND d.region = 'east'
            ORDER BY t.id
            """);

        Assert.Equal(
            [new object?[] { 3L, "east" }, new object?[] { 5L, "east" }],
            result.Rows);
        Assert.Equal(taskScans + 1, taskStore.FullScanCount);
        Assert.Equal(deviceScans + 1, deviceStore.FullScanCount);
        Assert.Equal(2, metrics.InputPredicatePushdownCount);
        Assert.Equal(2, metrics.InputProjectionPushdownCount);
        Assert.Equal(7, metrics.InputCandidateRows);
        Assert.Equal(5, metrics.InputRetainedRows);
    }

    [Fact]
    public void Execute_StatefulScalarPredicate_KeepsPredicateAfterJoin()
    {
        using var database = CreateDatabase();
        var evaluatedIds = new HashSet<long>();
        database.Functions.RegisterScalar(
            "first_evaluation",
            arguments => evaluatedIds.Add((long)arguments[0]!),
            minArgumentCount: 1,
            maxArgumentCount: 1);

        using var functionScope = SonnetDB.Query.Functions.UserFunctionRegistry.EnterScope(
            database.Functions);
        var (result, metrics) = Execute(database, """
            SELECT t.id
            FROM push_tasks t
            JOIN push_devices d ON t.device_id = d.id
            WHERE first_evaluation(t.id)
            ORDER BY t.id
            """);

        Assert.Equal([1L, 2L, 3L], result.Rows.Select(static row => (long)row[0]!));
        Assert.Equal(3, evaluatedIds.Count);
        Assert.Equal(0, metrics.InputPredicatePushdownCount);
        Assert.Equal(2, metrics.InputProjectionPushdownCount);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private Tsdb CreateDatabase()
    {
        var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, """
            CREATE TABLE push_tasks (
                id INT,
                device_id INT,
                status STRING,
                payload STRING,
                note STRING,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(database, """
            CREATE TABLE push_devices (
                id INT,
                region STRING,
                payload STRING,
                note STRING,
                PRIMARY KEY (id))
            """);
        SqlExecutor.Execute(database, "CREATE INDEX ix_push_tasks_status ON push_tasks (status)");
        SqlExecutor.Execute(database, "CREATE INDEX ix_push_devices_region ON push_devices (region)");
        SqlExecutor.Execute(database, """
            INSERT INTO push_tasks (id, device_id, status, payload, note) VALUES
                (1, 10, 'ready', 'task-1', 'n1'),
                (2, 10, 'blocked', 'task-2', 'n2'),
                (3, 20, 'ready', 'task-3', 'n3'),
                (4, 30, 'ready', 'task-4', 'n4')
            """);
        SqlExecutor.Execute(database, """
            INSERT INTO push_devices (id, region, payload, note) VALUES
                (10, 'north', 'device-10', 'n10'),
                (20, 'south', 'device-20', 'n20')
            """);
        return database;
    }

    private static (SelectExecutionResult Result, RelationalSelectExecutionMetrics Metrics) Execute(
        Tsdb database,
        string sql)
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(sql));
        var metrics = new RelationalSelectExecutionMetrics();
        return (RelationalSelectExecutor.Execute(database, statement, metrics), metrics);
    }
}
