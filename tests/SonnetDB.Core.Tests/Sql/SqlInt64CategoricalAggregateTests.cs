using SonnetDB.Engine;
using SonnetDB.Query.Functions.Aggregates;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>measurement Int64 分类聚合的内存、段、旧 sketch 与重开回归。</summary>
public sealed class SqlInt64CategoricalAggregateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-int64-aggregate-" + Guid.NewGuid().ToString("N"));

    /// <summary>按测试归属回收数据库目录。</summary>
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>大整数在所有已支持编码与重开后保持众数原值和基数。</summary>
    [Theory]
    [InlineData(BlockEncoding.None, BlockEncoding.None)]
    [InlineData(BlockEncoding.DeltaTimestamp, BlockEncoding.None)]
    [InlineData(BlockEncoding.DeltaTimestamp, BlockEncoding.DeltaValue)]
    public void Select_Int64ModeAndDistinct_AfterFlushAndReopen_PreservesIdentities(
        BlockEncoding timestampEncoding, BlockEncoding valueEncoding)
    {
        TsdbOptions options = Options(timestampEncoding, valueEncoding);
        using (var db = Tsdb.Open(options))
        {
            SqlExecutor.Execute(db, "CREATE MEASUREMENT samples (device TAG, value FIELD INT)");
            SqlExecutor.Execute(db,
                "INSERT INTO samples (time, device, value) VALUES " +
                "(1, 'd1', 9007199254740992), (2, 'd1', 9007199254740993), " +
                "(3, 'd1', 9007199254740993), (4, 'd1', -9223372036854775808), " +
                "(5, 'd1', 9223372036854775807)");
            AssertResult(db);
            Assert.NotNull(db.FlushNow());
            AssertResult(db);
        }
        using var reopened = Tsdb.Open(options);
        AssertResult(reopened);
        Assert.Equal([9007199254740993L, 1L], Assert.Single(Select(reopened,
            "SELECT mode(value), distinct_count(value) FROM samples WHERE device='d1' AND time>=2 AND time<=3").Rows));
    }

    /// <summary>旧数值 sketch 的大整数身份已不可恢复，SQL 必须扫描原始值。</summary>
    [Fact]
    public void Select_Int64Distinct_WithLegacyDoubleSketch_ScansOriginalValues()
    {
        TsdbOptions options = Options(BlockEncoding.None, BlockEncoding.None);
        using var db = Tsdb.Open(options);
        SqlExecutor.Execute(db, "CREATE MEASUREMENT samples (device TAG, value FIELD INT)");
        SqlExecutor.Execute(db, "INSERT INTO samples (time, device, value) VALUES " +
            "(1, 'd1', 9007199254740992), (2, 'd1', 9007199254740993)");
        Assert.NotNull(db.FlushNow());

        string segmentPath = Assert.Single(Directory.GetFiles(_root, "*.SDBSEG", SearchOption.AllDirectories));
        using var reader = SegmentReader.Open(segmentPath);
        BlockDescriptor descriptor = Assert.Single(reader.Blocks);
        Assert.True(reader.TryGetAggregateSketch(descriptor, out BlockAggregateSketch sketch));
        Assert.Equal(1L, Assert.IsType<HyperLogLog>(sketch.HyperLogLog).Estimate());
        Assert.Equal(2L, Assert.Single(Select(db, "SELECT distinct_count(value) FROM samples WHERE device='d1'").Rows)[0]);
    }

    /// <summary>SQL 直方图在内存、段与重开后都拒绝无法表示的桶。</summary>
    [Fact]
    public void Select_HistogramWithCollapsedBoundary_AfterFlushAndReopen_RejectsExplicitly()
    {
        TsdbOptions options = Options(BlockEncoding.None, BlockEncoding.None);
        using (var db = Tsdb.Open(options))
        {
            SqlExecutor.Execute(db, "CREATE MEASUREMENT readings (device TAG, value FIELD FLOAT)");
            SqlExecutor.Execute(db, "INSERT INTO readings (time, device, value) VALUES (1, 'd1', 9007199254740992.0)");
            Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT histogram(value, 1) FROM readings"));
            Assert.NotNull(db.FlushNow());
            Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT histogram(value, 1) FROM readings"));
        }
        using var reopened = Tsdb.Open(options);
        Assert.Throws<InvalidOperationException>(() => Select(reopened, "SELECT histogram(value, 1) FROM readings"));
        Assert.Equal(1L, Assert.Single(Select(reopened, "SELECT count(value) FROM readings").Rows)[0]);
    }

    private TsdbOptions Options(BlockEncoding timestampEncoding, BlockEncoding valueEncoding) => new()
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new SonnetDB.Engine.Compaction.CompactionPolicy { Enabled = false },
        SegmentWriterOptions = new SegmentWriterOptions
        {
            FsyncOnCommit = false,
            TimestampEncoding = timestampEncoding,
            ValueEncoding = valueEncoding,
        },
    };

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

    private static void AssertResult(Tsdb db)
    {
        IReadOnlyList<object?> row = Assert.Single(Select(db,
            "SELECT mode(value), distinct_count(value) FROM samples WHERE device='d1'").Rows);
        Assert.Equal(9007199254740993L, Assert.IsType<long>(row[0]));
        Assert.Equal(4L, Assert.IsType<long>(row[1]));
    }
}
