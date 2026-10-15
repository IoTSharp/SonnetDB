using System.Buffers.Binary;
using SonnetDB.Backup;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #313：普通数值 FIELD 原子替换、查询和恢复合同。</summary>
public sealed class SqlMeasurementNumericUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-numeric-update-" + Guid.NewGuid().ToString("N"));

    public SqlMeasurementNumericUpdateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Update_FlushedNumericField_PreservesOtherFieldsAndAllAggregatePaths()
    {
        using (var database = Open())
        {
            Execute(database, "CREATE MEASUREMENT metrics (Host TAG, Value FIELD FLOAT, Other FIELD INT)");
            Execute(database, "INSERT INTO metrics(time,Host,Value,Other) VALUES(1,'a',1.0,10),(2,'a',2.0,20),(3,'b',8.0,30)");
            database.FlushNow();
            Assert.Equal(1, Update(database, "UPDATE metrics SET value=3.0 WHERE time=1 AND host='a'"));
            AssertNumericViews(database);
            database.FlushNow();
            AssertNumericViews(database);
        }

        using var reopened = Open();
        AssertNumericViews(reopened);
    }

    private static void AssertNumericViews(Tsdb database)
    {
        var rows = Rows(database, "SELECT time,Value,Other FROM metrics WHERE host='a' ORDER BY time");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new object?[] { 1L, 3d, 10L }, rows[0]);
        Assert.Equal(new object?[] { 2L, 2d, 20L }, rows[1]);
        Assert.Equal(8d, Assert.Single(Rows(database, "SELECT Value FROM metrics WHERE host='b'"))[0]);
        Assert.Equal(3d, Assert.Single(Rows(database, "SELECT time,Value FROM metrics WHERE time=1 ORDER BY time DESC LIMIT 1"))[1]);
        Assert.Single(Rows(database, "SELECT Value FROM metrics WHERE Value>2 AND host='a'"));
        var aggregate = Assert.Single(Rows(database,
            "SELECT sum(Value),min(Value),max(Value),avg(Value),count(Value) FROM metrics WHERE host='a'"));
        Assert.Equal(new object?[] { 5d, 2d, 3d, 2.5d, 2L }, aggregate);
        Assert.Equal(5d, Assert.Single(Rows(database, "SELECT sum(Value) FROM metrics WHERE host='a'"))[0]);
        Assert.Equal(2L, Assert.Single(Rows(database, "SELECT count(*) FROM metrics WHERE host='a'"))[0]);
        Assert.Equal(2L, Assert.Single(Rows(database, "SELECT distinct_count(Value) FROM metrics WHERE host='a'"))[0]);
        var series = Assert.Single(database.Catalog.Find("metrics", new Dictionary<string, string> { ["Host"] = "a" }));
        Assert.Equal(new[] { 3d, 2d }, database.Query.Execute(new PointQuery(series.Id, "Value", TimeRange.All))
            .Select(point => point.Value.AsDouble()).ToArray());
        Assert.True(database.Query.TryGetLatestPoint(series.Id, "Value", new TimeRange(1, 1), out var latest));
        Assert.Equal(3d, latest.Value.AsDouble());
        Assert.Equal(5d, Assert.Single(database.Query.Execute(new AggregateQuery(series.Id, "Value", TimeRange.All, Aggregator.Sum))).Value);
    }

    [Theory]
    [InlineData("SET value=NULL WHERE host='a'", "NULL")]
    [InlineData("SET value='wrong' WHERE host='a'", "类型不匹配")]
    [InlineData("SET value=value+1 WHERE host='a'", "参数或字面量")]
    [InlineData("SET value=3 WHERE value=1", "TAG 和 time")]
    [InlineData("SET value=3 WHERE host='a' OR time=1", "TAG 和 time")]
    [InlineData("SET value=3 WHERE host!='a'", "TAG 和 time")]
    [InlineData("SET value=3 WHERE time!=1", "time !=")]
    [InlineData("SET value=3", "仅支持关系表")]
    [InlineData("SET host='b' WHERE time=1", "已声明")]
    [InlineData("SET value=3,other=4 WHERE time=1", "一个")]
    public void Update_UnsupportedShape_LeavesOriginalValues(string suffix, string diagnostic)
    {
        using var database = Open();
        Execute(database, "CREATE MEASUREMENT metrics (host TAG, value FIELD FLOAT, other FIELD INT)");
        Execute(database, "INSERT INTO metrics(time,host,value,other) VALUES(1,'a',1.0,7)");
        database.FlushNow();
        var error = Assert.ThrowsAny<Exception>(() => Execute(database, "UPDATE metrics " + suffix));
        Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, database.VectorReplacements.Count);
        Assert.Equal(new object?[] { 1d, 7L }, Assert.Single(Rows(database, "SELECT value,other FROM metrics")));
    }

    [Fact]
    public void Update_SparseField_RejectsWholeStatementAndAllowsCompleteSubset()
    {
        using var database = Open();
        Execute(database, "CREATE MEASUREMENT metrics (host TAG, value FIELD FLOAT, other FIELD INT)");
        Execute(database, "INSERT INTO metrics(time,host,value) VALUES(1,'a',1.0)");
        Execute(database, "INSERT INTO metrics(time,host,other) VALUES(2,'a',7)");
        Assert.Contains("缺少原数值 FIELD", Assert.Throws<NotSupportedException>(() =>
            Update(database, "UPDATE metrics SET value=3 WHERE host='a'")).Message);
        Assert.Equal(0, database.VectorReplacements.Count);
        Assert.Equal(1, Update(database, "UPDATE metrics SET value=3 WHERE host='a' AND time=1"));
        Assert.Equal(0, Update(database, "UPDATE metrics SET value=3 WHERE host='missing'"));
        Assert.Equal(7L, Assert.Single(Rows(database, "SELECT other FROM metrics WHERE time=2"))[0]);
    }

    [Fact]
    public void Update_Int64AndBoundaryTime_PreservesExactBitsAndRejectsFloat()
    {
        const long exact = 9007199254740993;
        using (var database = Open())
        {
            Execute(database, "CREATE MEASUREMENT Metrics (Host TAG, Value FIELD INT)");
            Execute(database, $"INSERT INTO Metrics(time,Host,Value) VALUES({long.MaxValue},'a',1)");
            Assert.Equal(1, Update(database, $"UPDATE metrics SET value={exact} WHERE time={long.MaxValue} AND host='a'"));
            Assert.Equal(exact, Assert.Single(Rows(database, "SELECT Value FROM Metrics"))[0]);
            Assert.Equal(1, Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(database, null,
                "UPDATE Metrics SET Value=@value WHERE time=@time AND Host=@host",
                new SqlParameters().AddNamed("value", long.MinValue).AddNamed("time", long.MaxValue).AddNamed("host", "a"))).RowsAffected);
            Assert.Throws<InvalidOperationException>(() => Update(database, $"UPDATE Metrics SET Value=1.5 WHERE time={long.MaxValue}"));
            Assert.Throws<InvalidOperationException>(() => Update(database, $"UPDATE Metrics SET Value=1 WHERE time>{long.MaxValue}"));
            Assert.Throws<InvalidOperationException>(() => Update(database, "UPDATE Metrics SET \"value\"=1 WHERE Host='a'"));
        }
        using var reopened = Open();
        Assert.Equal(long.MinValue, Assert.Single(Rows(reopened, "SELECT Value FROM Metrics"))[0]);
    }

    [Fact]
    public void Update_ExtendedSketchAggregates_ObserveReplacementInsteadOfOldSketch()
    {
        using var database = Open();
        Execute(database, "CREATE MEASUREMENT metrics (value FIELD FLOAT)");
        Execute(database, "INSERT INTO metrics(time,value) VALUES(1,1.0),(2,2.0)");
        database.FlushNow();
        Assert.Equal(2, Update(database, "UPDATE metrics SET value=3 WHERE time<=2"));
        var row = Assert.Single(Rows(database, "SELECT distinct_count(value),median(value),p95(value),mode(value) FROM metrics"));
        Assert.Equal(new object?[] { 1L, 3d, 3d, 3d }, row);
    }

    [Fact]
    public void Update_SubsequentSchemaPromotionAndConflictingInsert_KeepRecoveryConsistent()
    {
        using (var database = Open())
        {
            Execute(database, "CREATE MEASUREMENT metrics (value FIELD INT)");
            Execute(database, "INSERT INTO metrics(time,value) VALUES(1,1)");
            Assert.Equal(1, Update(database, "UPDATE metrics SET value=9007199254740993 WHERE time=1"));
            Assert.Throws<NotSupportedException>(() => Execute(database, "INSERT INTO metrics(time,value) VALUES(1,1.5)"));
            Assert.Equal(FieldType.Int64, database.Measurements.TryGet("metrics")!.TryGetColumn("value")!.DataType);
            Assert.Throws<NotSupportedException>(() => database.WriteMany(new[]
            {
                Point.Create("metrics", 2, fields: new Dictionary<string, FieldValue>
                {
                    ["value"] = FieldValue.FromDouble(1.5), ["extra"] = FieldValue.FromLong(7),
                }),
                Point.Create("metrics", 1, fields: new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromLong(4) }),
            }));
            Assert.Null(database.Measurements.TryGet("metrics")!.TryGetColumn("extra"));
            Assert.Equal(FieldType.Int64, database.Measurements.TryGet("metrics")!.TryGetColumn("value")!.DataType);
            Assert.Empty(Rows(database, "SELECT value FROM metrics WHERE time=2"));
            Execute(database, "INSERT INTO metrics(time,value) VALUES(2,1.5)");
            Assert.Equal(FieldType.Float64, database.Measurements.TryGet("metrics")!.TryGetColumn("value")!.DataType);
        }
        using var reopened = Open();
        var series = Assert.Single(reopened.Catalog.Find("metrics", new Dictionary<string, string>()));
        Assert.Equal(9007199254740993L, Assert.Single(reopened.Query.Execute(new PointQuery(series.Id, "value", new TimeRange(1, 1)))).Value.AsLong());
        Assert.Equal(1.5d, Assert.Single(reopened.Query.Execute(new PointQuery(series.Id, "value", new TimeRange(2, 2)))).Value.AsDouble());
        Assert.Equal((double)9007199254740993L + 1.5d, Assert.Single(Rows(reopened, "SELECT sum(value) FROM metrics"))[0]);
    }

    [Fact]
    public void Update_DuplicateTarget_CountsOnceAndRejectsSubsequentInsert()
    {
        using var database = Open();
        Execute(database, "CREATE MEASUREMENT metrics (host TAG, value FIELD FLOAT)");
        Execute(database, "INSERT INTO metrics(time,host,value) VALUES(1,'a',1.0),(1,'a',2.0),(2,'a',4.0)");
        database.FlushNow();
        Assert.Equal(1, Update(database, "UPDATE metrics SET value=-3.0 WHERE time=1 AND host='a'"));
        Assert.Equal(new object?[] { 1L, -3d }, Rows(database, "SELECT time,value FROM metrics ORDER BY time")[0]);
        Assert.Equal(2, Rows(database, "SELECT value FROM metrics").Count);
        Assert.Contains("FIELD UPDATE", Assert.Throws<NotSupportedException>(() => Execute(database,
            "INSERT INTO metrics(time,host,value) VALUES(1,'a',9.0)")).Message);
        Assert.Equal(1d, Assert.Single(Rows(database, "SELECT sum(value) FROM metrics"))[0]);
    }

    [Fact]
    public void Update_RowLimit_RejectsAll257TargetsThenAllows256()
    {
        using var database = Open();
        Execute(database, "CREATE MEASUREMENT metrics (host TAG, value FIELD FLOAT)");
        Execute(database, "INSERT INTO metrics(time,host,value) VALUES" + string.Join(',', Enumerable.Range(1, 257).Select(i => $"({i},'a',1.0)")));
        Assert.Contains("最多修改 256 行", Assert.Throws<InvalidOperationException>(() =>
            Update(database, "UPDATE metrics SET value=3 WHERE host='a'")).Message);
        Assert.Equal(0, database.VectorReplacements.Count);
        Assert.Equal(257d, Assert.Single(Rows(database, "SELECT sum(value) FROM metrics"))[0]);
        Assert.Equal(256, Update(database, "UPDATE metrics SET value=3 WHERE time<=256"));
        Assert.Equal(769d, Assert.Single(Rows(database, "SELECT sum(value) FROM metrics"))[0]);
    }

    [Fact]
    public void Update_GlobalCapacity_RejectsNewKeyAndAllowsExistingReplacement()
    {
        using var database = Open();
        Execute(database, "CREATE MEASUREMENT metrics (value FIELD INT)");
        for (int batch = 0; batch < 16; batch++)
        {
            int first = batch * 256 + 1;
            Execute(database, "INSERT INTO metrics(time,value) VALUES" + string.Join(',', Enumerable.Range(first, 256).Select(i => $"({i},1)")));
            Assert.Equal(256, Update(database, $"UPDATE metrics SET value=2 WHERE time>={first} AND time<{first + 256}"));
        }
        Execute(database, "INSERT INTO metrics(time,value) VALUES(4097,1)");
        Assert.Contains("4096", Assert.Throws<InvalidOperationException>(() =>
            Update(database, "UPDATE metrics SET value=2 WHERE time=4097")).Message);
        Assert.Equal(4096, database.VectorReplacements.Count);
        Assert.Equal(1L, Assert.Single(Rows(database, "SELECT value FROM metrics WHERE time=4097"))[0]);
        Assert.Equal(1, Update(database, "UPDATE metrics SET value=3 WHERE time=1"));
    }

    [Fact]
    public void Update_WalFailure_FencesReadsWritesAndRecoversWholeBatch()
    {
        using (var database = Open())
        {
            Execute(database, "CREATE MEASUREMENT metrics (host TAG, value FIELD FLOAT)");
            Execute(database, "INSERT INTO metrics(time,host,value) VALUES(1,'a',1.0),(2,'b',2.0)");
            database.VectorReplacements.WalSyncTestHook = () => throw new IOException("numeric sync failure");
            Assert.Throws<IOException>(() => Update(database, "UPDATE metrics SET value=3 WHERE time<=2"));
            Assert.Throws<InvalidOperationException>(() => Rows(database, "SELECT value FROM metrics"));
            Assert.Throws<InvalidOperationException>(() => Update(database, "UPDATE metrics SET value=4 WHERE time=1"));
            Assert.Throws<InvalidOperationException>(() => Execute(database, "INSERT INTO metrics(time,host,value) VALUES(3,'c',4.0)"));
            database.VectorReplacements.WalSyncTestHook = null;
        }
        using var reopened = Open();
        Assert.Equal(2, Rows(reopened, "SELECT value FROM metrics").Count);
        Assert.All(Rows(reopened, "SELECT value FROM metrics"), row => Assert.Equal(3d, row[0]));
    }

    [Fact]
    public void Update_PreAppendBudgetFailure_LeavesOriginalDataReadable()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root, Kv = new KvOptions { MaxWalBytes = KvWalFile.HeaderSize } });
        Execute(database, "CREATE MEASUREMENT metrics (value FIELD FLOAT)");
        Execute(database, "INSERT INTO metrics(time,value) VALUES(1,1.0)");
        Assert.Throws<IOException>(() => Update(database, "UPDATE metrics SET value=3 WHERE time=1"));
        Assert.Equal(0, database.VectorReplacements.Count);
        Assert.Equal(1d, Assert.Single(Rows(database, "SELECT value FROM metrics"))[0]);
    }

    [Fact]
    public void Update_MixedV1V2BackupRestore_PreservesValuesAndDeleteCleanup()
    {
        string source = Path.Combine(_root, "source");
        string backup = Path.Combine(_root, "backup");
        string restored = Path.Combine(_root, "restored");
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = source }))
        {
            Execute(database, "CREATE MEASUREMENT metrics (value FIELD INT, embedding FIELD VECTOR(2) WITH INDEX hnsw(m=4,ef=8))");
            Execute(database, "INSERT INTO metrics(time,value,embedding) VALUES(1,1,[1,0]),(2,2,[1,0])");
            database.FlushNow();
            Assert.Equal(1, Update(database, "UPDATE metrics SET embedding=[0,1] WHERE time=1"));
            Assert.Equal(2, Update(database, "UPDATE metrics SET value=9007199254740993 WHERE time<=2"));
            var entries = database.Keyspaces.OpenMeasurementVectorReplacements().ScanPrefixAfter([], [], 10);
            Assert.Single(entries, entry => entry.Value.Span[0] == 1);
            var numeric = entries.Where(entry => entry.Value.Span[0] == 2).ToArray();
            Assert.Equal(2, numeric.Length);
            Assert.All(numeric, entry => Assert.Equal(9007199254740993L, BinaryPrimitives.ReadInt64LittleEndian(entry.Value.Span[2..])));
            Execute(database, "DELETE FROM metrics WHERE time=2");
            _ = new BackupService().Create(database, new BackupCreateOptions { DestinationDirectory = backup });
        }
        _ = new BackupService().Restore(new BackupRestoreOptions { BackupDirectory = backup, TargetDirectory = restored });
        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = restored });
        Assert.Equal(2, reopened.VectorReplacements.Count);
        var row = Assert.Single(Rows(reopened, "SELECT value,embedding FROM metrics"));
        Assert.Equal(9007199254740993L, row[0]);
        Assert.Equal(new float[] { 0, 1 }, Assert.IsType<float[]>(row[1]));
        Assert.Equal(new float[] { 0, 1 }, Assert.IsType<float[]>(Assert.Single(Rows(reopened,
            "SELECT embedding FROM knn(metrics,embedding,[0,1],1)"))[0]));
        Assert.True(reopened.DropMeasurement("metrics"));
        Assert.Equal(0, reopened.VectorReplacements.Count);
    }

    [Fact]
    public async Task Update_CompactionAndReopen_KeepReplacementAggregates()
    {
        using (var database = Open())
        {
            Execute(database, "CREATE MEASUREMENT metrics (value FIELD FLOAT)");
            Execute(database, "INSERT INTO metrics(time,value) VALUES(1,1.0)");
            database.FlushNow();
            Execute(database, "INSERT INTO metrics(time,value) VALUES(2,2.0)");
            database.FlushNow();
            Assert.Equal(1, Update(database, "UPDATE metrics SET value=3 WHERE time=1"));
        }
        using (var compacted = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            Compaction = new CompactionPolicy { Enabled = true, MinTierSize = 2, PollInterval = TimeSpan.FromMilliseconds(100) },
        }))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (compacted.Segments.SegmentCount > 1)
                await Task.Delay(100, deadline.Token);
            Assert.Equal(5d, Assert.Single(Rows(compacted, "SELECT sum(value) FROM metrics"))[0]);
        }
        using var reopened = Open();
        Assert.Equal(5d, Assert.Single(Rows(reopened, "SELECT sum(value) FROM metrics"))[0]);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root, Compaction = new CompactionPolicy { Enabled = false } });

    private static void Execute(Tsdb database, string sql) => SqlExecutor.Execute(database, sql);

    private static int Update(Tsdb database, string sql) => Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(database, sql)).RowsAffected;

    private static IReadOnlyList<IReadOnlyList<object?>> Rows(Tsdb database, string sql) => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql)).Rows;
}
