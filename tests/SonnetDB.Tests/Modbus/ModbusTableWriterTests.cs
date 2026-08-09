using SonnetDB.Engine;
using SonnetDB.Modbus;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Tests.Modbus;

public sealed class ModbusTableWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-modbus-quality-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WriteSuccessfulSample_LatestAndHistory_StoresGoodQualityWithModeSemantics()
    {
        using Tsdb database = OpenDatabase();
        CreateSource(database);
        CreateLatestTable(database, "latest_values", "KEEP_LAST");
        CreateHistoryTable(database, "history_values", "KEEP_LAST");
        IReadOnlyList<ModbusTableBinding> bindings = database.Modbus.Catalog.ListBindings();
        var firstAt = new DateTimeOffset(2026, 8, 9, 1, 0, 0, TimeSpan.Zero);
        var secondAt = firstAt.AddSeconds(1);

        Assert.Equal(2, ModbusTableWriter.WriteSuccessfulSample(
            database,
            bindings,
            Snapshot(10, 20),
            firstAt));
        Assert.Equal(2, ModbusTableWriter.WriteSuccessfulSample(
            database,
            bindings,
            Snapshot(11, 21),
            secondAt));

        TableRow latest = Assert.Single(database.Tables.Open("latest_values").Scan());
        Assert.Equal(0L, latest.Values[0]);
        Assert.Equal(secondAt.UtcDateTime, latest.Values[1]);
        Assert.Equal((long)ModbusSampleQuality.Good, latest.Values[2]);
        Assert.Equal(11L, latest.Values[3]);
        Assert.Equal(21L, latest.Values[4]);

        IReadOnlyList<TableRow> history = database.Tables.Open("history_values").Scan();
        Assert.Equal(2, history.Count);
        Assert.Equal(firstAt.UtcDateTime, history[0].Values[0]);
        Assert.Equal(secondAt.UtcDateTime, history[1].Values[0]);
        Assert.All(history, row => Assert.Equal((long)ModbusSampleQuality.Good, row.Values[1]));
        Assert.Equal(11L, history[1].Values[2]);
        Assert.Equal(21L, history[1].Values[3]);
    }

    [Fact]
    public void WriteFailedSample_Latest_AppliesAllErrorPoliciesAndQualityBits()
    {
        using Tsdb database = OpenDatabase();
        CreateSource(database);
        CreateLatestTable(database, "keep_values", "KEEP_LAST");
        CreateLatestTable(database, "null_values", "NULL");
        CreateLatestTable(database, "skip_values", "SKIP");
        CreateLatestTable(database, "bad_values", "MARK_BAD");
        IReadOnlyList<ModbusTableBinding> bindings = database.Modbus.Catalog.ListBindings();
        var succeededAt = new DateTimeOffset(2026, 8, 9, 2, 0, 0, TimeSpan.Zero);
        var failedAt = succeededAt.AddSeconds(1);

        Assert.Equal(4, ModbusTableWriter.WriteSuccessfulSample(
            database,
            bindings,
            Snapshot(10, 20),
            succeededAt));
        Assert.Equal(3, ModbusTableWriter.WriteFailedSample(
            database,
            bindings,
            Snapshot(11),
            failedAt));

        TableRow keep = Assert.Single(database.Tables.Open("keep_values").Scan());
        Assert.Equal(failedAt.UtcDateTime, keep.Values[1]);
        Assert.Equal((long)ModbusSampleQuality.Stale, keep.Values[2]);
        Assert.Equal(10L, keep.Values[3]);
        Assert.Equal(20L, keep.Values[4]);

        TableRow nullRow = Assert.Single(database.Tables.Open("null_values").Scan());
        Assert.Equal(
            (long)(ModbusSampleQuality.Bad | ModbusSampleQuality.NoValue),
            nullRow.Values[2]);
        Assert.Null(nullRow.Values[3]);
        Assert.Null(nullRow.Values[4]);

        TableRow skipped = Assert.Single(database.Tables.Open("skip_values").Scan());
        Assert.Equal(succeededAt.UtcDateTime, skipped.Values[1]);
        Assert.Equal((long)ModbusSampleQuality.Good, skipped.Values[2]);
        Assert.Equal(10L, skipped.Values[3]);
        Assert.Equal(20L, skipped.Values[4]);

        TableRow bad = Assert.Single(database.Tables.Open("bad_values").Scan());
        Assert.Equal(
            (long)(ModbusSampleQuality.Bad | ModbusSampleQuality.Partial | ModbusSampleQuality.NoValue),
            bad.Values[2]);
        Assert.Equal(11L, bad.Values[3]);
        Assert.Null(bad.Values[4]);
    }

    [Fact]
    public void WriteFailedSample_History_KeepLastAppendsStaleAndSkipDoesNotAppend()
    {
        using Tsdb database = OpenDatabase();
        CreateSource(database);
        CreateHistoryTable(database, "history_keep", "KEEP_LAST");
        CreateHistoryTable(database, "history_skip", "SKIP");
        IReadOnlyList<ModbusTableBinding> bindings = database.Modbus.Catalog.ListBindings();
        var succeededAt = new DateTimeOffset(2026, 8, 9, 3, 0, 0, TimeSpan.Zero);
        var failedAt = succeededAt.AddSeconds(1);

        Assert.Equal(2, ModbusTableWriter.WriteSuccessfulSample(
            database,
            bindings,
            Snapshot(30, 40),
            succeededAt));
        Assert.Equal(1, ModbusTableWriter.WriteFailedSample(
            database,
            bindings,
            new ModbusReadSnapshot(),
            failedAt));

        IReadOnlyList<TableRow> kept = database.Tables.Open("history_keep").Scan();
        Assert.Equal(2, kept.Count);
        Assert.Equal(failedAt.UtcDateTime, kept[1].Values[0]);
        Assert.Equal((long)ModbusSampleQuality.Stale, kept[1].Values[1]);
        Assert.Equal(30L, kept[1].Values[2]);
        Assert.Equal(40L, kept[1].Values[3]);

        TableRow skipped = Assert.Single(database.Tables.Open("history_skip").Scan());
        Assert.Equal(succeededAt.UtcDateTime, skipped.Values[0]);
        Assert.Equal((long)ModbusSampleQuality.Good, skipped.Values[1]);
    }

    [Fact]
    public void WriteFailedSample_AfterSameNameTableRecreated_DoesNotReuseCachedSuccessfulRow()
    {
        using Tsdb database = OpenDatabase();
        CreateSource(database);
        CreateHistoryTable(database, "recreated_values", "KEEP_LAST");
        var state = new ModbusTableWriterState();
        var succeededAt = new DateTimeOffset(2026, 8, 9, 4, 0, 0, TimeSpan.Zero);

        Assert.Equal(1, ModbusTableWriter.WriteSuccessfulSample(
            database,
            database.Modbus.Catalog.ListBindings(),
            Snapshot(50, 60),
            succeededAt,
            state));
        Assert.True(database.Modbus.DropBinding("recreated_values"));
        _ = SqlExecutor.Execute(database, "DROP TABLE recreated_values");
        CreateHistoryTable(database, "recreated_values", "KEEP_LAST");

        Assert.Equal(0, ModbusTableWriter.WriteFailedSample(
            database,
            database.Modbus.Catalog.ListBindings(),
            new ModbusReadSnapshot(),
            succeededAt.AddSeconds(1),
            state));
        Assert.Empty(database.Tables.Open("recreated_values").Scan());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private Tsdb OpenDatabase()
    {
        Directory.CreateDirectory(_root);
        return Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    }

    private static void CreateSource(Tsdb database)
        => _ = SqlExecutor.Execute(database, """
            CREATE MODBUS SOURCE plc
            WITH (
                ENDPOINT '127.0.0.1:502',
                BYTE_ORDER BIG_ENDIAN,
                WORD_ORDER BIG_ENDIAN
            )
            """);

    private static void CreateLatestTable(Tsdb database, string tableName, string policy)
        => _ = SqlExecutor.Execute(database, $"""
            CREATE TABLE {tableName} (
                id INT NOT NULL,
                sampled_at DATETIME SAMPLE_TIME,
                quality INT QUALITY,
                first_value INT NULL FROM MODBUS HOLDING_REGISTER(40001) AS UINT16,
                second_value INT NULL FROM MODBUS HOLDING_REGISTER(40002) AS UINT16,
                PRIMARY KEY (id)
            )
            USING MODBUS SOURCE plc
            WITH (TABLE_MODE LATEST, ON_ERROR {policy})
            """);

    private static void CreateHistoryTable(Tsdb database, string tableName, string policy)
        => _ = SqlExecutor.Execute(database, $"""
            CREATE TABLE {tableName} (
                sampled_at DATETIME SAMPLE_TIME,
                quality INT QUALITY,
                first_value INT NULL FROM MODBUS HOLDING_REGISTER(40001) AS UINT16,
                second_value INT NULL FROM MODBUS HOLDING_REGISTER(40002) AS UINT16,
                PRIMARY KEY (sampled_at)
            )
            USING MODBUS SOURCE plc
            WITH (TABLE_MODE HISTORY, ON_ERROR {policy})
            """);

    private static ModbusReadSnapshot Snapshot(params ushort[] values)
    {
        var snapshot = new ModbusReadSnapshot();
        snapshot.Add(
            new ModbusReadBatch(
                ModbusRegisterArea.HoldingRegister,
                StartAddress: 0,
                checked((ushort)values.Length)),
            values);
        return snapshot;
    }
}
