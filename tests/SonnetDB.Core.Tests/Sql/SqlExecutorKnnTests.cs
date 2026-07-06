using SonnetDB.Engine;
using SonnetDB.Query;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// PR #60：knn(measurement, column, query_vector, k[, metric]) 表值函数端到端测试。
/// </summary>
public sealed class SqlExecutorKnnTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorKnnTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-knn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private Tsdb OpenDb()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        return db;
    }

    private Tsdb OpenDbWithHnsw()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3) WITH INDEX hnsw(m=4, ef=8))");
        return db;
    }

    private Tsdb OpenDbWithHnswL2()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3) WITH INDEX hnsw(m=8, ef=16, ef_construction=64, metric='l2'))");
        return db;
    }

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

    // ── 基础功能 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Knn_ReturnsTopKByDistance_Cosine()
    {
        using var db = OpenDb();
        // 插入 4 条向量，query 为 [1,0,0]
        // cosine distance:
        //   [1,0,0] → 0.0（最近）
        //   [1,1,0] → 1 - 1/√2 ≈ 0.293
        //   [0,1,0] → 1.0
        //   [-1,0,0] → 2.0（最远）
        SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
            "('a', [1, 0, 0], 1000), " +
            "('b', [1, 1, 0], 2000), " +
            "('c', [0, 1, 0], 3000), " +
            "('d', [-1, 0, 0], 4000)");

        var r = Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 2)");

        Assert.Equal(new[] { "time", "distance", "source", "embedding" }, r.Columns.ToArray());
        Assert.Equal(2, r.Rows.Count);

        // 第 1 行：[1,0,0]（distance ≈ 0）
        Assert.Equal(1000L, (long)r.Rows[0][0]!);
        Assert.Equal(0.0, (double)r.Rows[0][1]!, 6);
        Assert.Equal("a", r.Rows[0][2]);

        // 第 2 行：[1,1,0]（distance ≈ 0.293）
        Assert.Equal(2000L, (long)r.Rows[1][0]!);
        Assert.True((double)r.Rows[1][1]! > 0.0);
        Assert.Equal("b", r.Rows[1][2]);
    }

    [Fact]
    public void Knn_L2Metric_ReturnsCorrectOrder()
    {
        using var db = OpenDb();
        SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
            "('a', [0, 0, 0], 1000), " +
            "('b', [3, 4, 0], 2000), " +
            "('c', [1, 0, 0], 3000)");

        var r = Select(db, "SELECT * FROM knn(docs, embedding, [0, 0, 0], 3, 'l2')");

        Assert.Equal(3, r.Rows.Count);
        // 最近：[0,0,0] → dist=0；然后 [1,0,0] → dist=1；最远 [3,4,0] → dist=5
        Assert.Equal(0.0, (double)r.Rows[0][1]!, 6);
        Assert.Equal(1.0, (double)r.Rows[1][1]!, 6);
        Assert.Equal(5.0, (double)r.Rows[2][1]!, 6);
    }

    [Fact]
    public void Knn_InnerProductMetric_ReturnsNegativeDotOrder()
    {
        using var db = OpenDb();
        SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
            "('a', [2, 0, 0], 1000), " +
            "('b', [0, 3, 0], 2000), " +
            "('c', [-1, 0, 0], 3000)");

        // query = [1, 0, 0]，dots = [2, 0, -1]，负内积 = [-2, 0, 1]
        var r = Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 3, 'inner_product')");

        Assert.Equal(3, r.Rows.Count);
        // 最近（负内积最小 = 内积最大）：[2,0,0] → neg_dot=-2
        Assert.Equal(-2.0, (double)r.Rows[0][1]!, 6);
        // 中间：[0,3,0] → neg_dot=0
        Assert.Equal(0.0, (double)r.Rows[1][1]!, 6);
        // 最远：[-1,0,0] → neg_dot=1
        Assert.Equal(1.0, (double)r.Rows[2][1]!, 6);
    }

    [Fact]
    public void Knn_KLargerThanData_ReturnsAllData()
    {
        using var db = OpenDb();
        SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
            "('a', [1, 0, 0], 1000), " +
            "('b', [0, 1, 0], 2000)");

        var r = Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 100)");

        // k=100 > 2条数据，应返回全部 2 条
        Assert.Equal(2, r.Rows.Count);
    }

    [Fact]
    public void Knn_WithTagFilter_OnlyMatchesSeries()
    {
        using var db = OpenDb();
        SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
            "('a', [1, 0, 0], 1000), " +
            "('b', [0, 1, 0], 2000)");

        var r = Select(db, "SELECT * FROM knn(docs, embedding, [0, 1, 0], 10) WHERE source = 'a'");

        // 仅 source='a' 的一条数据
        Assert.Single(r.Rows);
        Assert.Equal("a", r.Rows[0][2]);
    }

    [Fact]
    public void Knn_WithTimeFilter_RespectsTimeRange()
    {
        using var db = OpenDb();
        SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
            "('a', [0, 0, 0], 1000), " +
            "('a', [1, 0, 0], 2000), " +
            "('a', [1, 1, 0], 3000)");

        // 仅查询 time >= 2000
        var r = Select(db,
            "SELECT * FROM knn(docs, embedding, [0, 0, 0], 10) WHERE time >= 2000");

        Assert.Equal(2, r.Rows.Count);
        Assert.All(r.Rows, row => Assert.True((long)row[0]! >= 2000L));
    }

    [Fact]
    public void Knn_WithFlushedHnswSegment_UsesIndexedMeasurementAndReturnsResults()
    {
        using (var db = OpenDbWithHnsw())
        {
            SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
                "('a', [1, 0, 0], 1000), " +
                "('b', [0.9, 0.1, 0], 2000), " +
                "('c', [0, 1, 0], 3000), " +
                "('d', [-1, 0, 0], 4000)");

            var flush = db.FlushNow();
            Assert.NotNull(flush);
            Assert.False(File.Exists(TsdbPaths.VectorIndexPath(_root, flush!.SegmentId)));
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var result = Select(reopened, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 2)");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("a", result.Rows[0][2]);
        Assert.Equal("b", result.Rows[1][2]);
    }

    // #223：声明 metric='l2' 的 HNSW 段刷盘并重开后，L2 knn 走 ANN 路径且结果正确
    // （修复前非 cosine 索引不被使用、静默退化为暴力扫描——I7）。
    [Fact]
    public void Knn_WithFlushedL2HnswSegment_ServesL2NearestNeighbor()
    {
        using (var db = OpenDbWithHnswL2())
        {
            SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
                "('a', [0, 0, 0], 1000), " +
                "('b', [3, 4, 0], 2000), " +
                "('c', [1, 0, 0], 3000), " +
                "('d', [10, 10, 0], 4000)");
            Assert.NotNull(db.FlushNow());
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var result = Select(reopened, "SELECT * FROM knn(docs, embedding, [0, 0, 0], 3, 'l2')");

        Assert.Equal(3, result.Rows.Count);
        // L2 最近邻：[0,0,0] dist=0 → [1,0,0] dist=1 → [3,4,0] dist=5
        Assert.Equal(0.0, (double)result.Rows[0][1]!, 6);
        Assert.Equal("a", result.Rows[0][2]);
        Assert.Equal(1.0, (double)result.Rows[1][1]!, 6);
        Assert.Equal("c", result.Rows[1][2]);
        Assert.Equal(5.0, (double)result.Rows[2][1]!, 6);
        Assert.Equal("b", result.Rows[2][2]);
    }

    [Fact]
    public void Knn_WithFlushedHnswSegment_AndTimeFilter_ReturnsFilteredNearestNeighbor()
    {
        using (var db = OpenDbWithHnsw())
        {
            SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
                "('a', [1, 0, 0], 1000), " +
                "('b', [0.9, 0.1, 0], 2000), " +
                "('c', [0, 1, 0], 3000), " +
                "('d', [-1, 0, 0], 4000)");
            Assert.NotNull(db.FlushNow());
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var result = Select(reopened,
            "SELECT * FROM knn(docs, embedding, [1, 0, 0], 1) WHERE time >= 3000");

        Assert.Single(result.Rows);
        Assert.Equal(3000L, (long)result.Rows[0][0]!);
        Assert.Equal("c", result.Rows[0][2]);
    }

    [Fact]
    public void Knn_WithLazyVectorSidecarCache_ReturnsSameRowsWhenCacheDisabled()
    {
        using (var db = OpenDbWithHnsw())
        {
            SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
                "('a', [1, 0, 0], 1000), " +
                "('b', [0.95, 0.05, 0], 2000), " +
                "('c', [0.8, 0.2, 0], 3000), " +
                "('d', [0, 1, 0], 4000), " +
                "('e', [-1, 0, 0], 5000)");
            Assert.NotNull(db.FlushNow());
        }

        List<(long Ts, string Source)> uncachedRows;
        using (var uncached = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            SegmentReaderOptions = new SegmentReaderOptions { VectorIndexCacheMaxBytes = 0 },
        }))
        {
            var result = Select(uncached, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 3)");
            uncachedRows = result.Rows
                .Select(row => ((long)row[0]!, (string)row[2]!))
                .ToList();
        }

        using var cached = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            SegmentReaderOptions = new SegmentReaderOptions { VectorIndexCacheMaxBytes = 1024 * 1024 },
        });
        var cachedResult = Select(cached, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 3)");
        var cachedRows = cachedResult.Rows
            .Select(row => ((long)row[0]!, (string)row[2]!))
            .ToList();

        Assert.Equal(uncachedRows, cachedRows);
    }

    [Fact]
    public void Knn_NoMatchedSeries_ReturnsEmpty()
    {
        using var db = OpenDb();
        SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
            "('a', [1, 0, 0], 1000)");

        var r = Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 5) WHERE source = 'missing'");

        Assert.Empty(r.Rows);
    }

    [Fact]
    public void Knn_OutputContainsAllFieldColumns()
    {
        // 多字段 measurement
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT items (category TAG, vec FIELD VECTOR(2), score FIELD FLOAT)");
        SqlExecutor.Execute(db,
            "INSERT INTO items (category, vec, score, time) VALUES ('x', [1, 0], 3.14, 1000)");

        var r = Select(db, "SELECT * FROM knn(items, vec, [1, 0], 1)");

        Assert.Equal(new[] { "time", "distance", "category", "vec", "score" }, r.Columns.ToArray());
        Assert.Single(r.Rows);
        Assert.Equal("x", r.Rows[0][2]);
        // score 字段应被回填
        Assert.Equal(3.14, (double)r.Rows[0][4]!, 6);
    }

    // ── 错误场景 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Knn_WrongColumnType_Throws()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT t (v FIELD FLOAT)");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Select(db, "SELECT * FROM knn(t, v, [1.0], 1)"));
        Assert.Contains("VECTOR", ex.Message);
    }

    [Fact]
    public void Knn_DimMismatch_Throws()
    {
        using var db = OpenDb();
        // embedding 是 VECTOR(3)，但 query 是 [1, 0]（2维）
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Select(db, "SELECT * FROM knn(docs, embedding, [1, 0], 1)"));
        Assert.Contains("维度", ex.Message);
    }

    [Fact]
    public void Knn_KZero_Throws()
    {
        using var db = OpenDb();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 0)"));
        Assert.Contains("正整数", ex.Message);
    }

    [Fact]
    public void Knn_UnknownMetric_Throws()
    {
        using var db = OpenDb();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 1, 'manhattan')"));
        Assert.Contains("manhattan", ex.Message);
    }

    [Fact]
    public void Knn_MeasurementNotExists_Throws()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Select(db, "SELECT * FROM knn(absent, col, [1.0], 1)"));
        Assert.Contains("absent", ex.Message);
    }

    // ── 墓碑过滤（回归：删除后 KNN 不应再返回已删除向量） ──────────────────────

    [Fact]
    public void Knn_AfterDelete_DoesNotReturnTombstonedVector()
    {
        using var db = OpenDb();
        SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
            "('a', [1, 0, 0], 1000), " +
            "('b', [0.9, 0.1, 0], 2000), " +
            "('c', [0, 1, 0], 3000)");

        // 删除最近的 'a'：cosine 距离 0
        SqlExecutor.Execute(db,
            "DELETE FROM docs WHERE source = 'a' AND time >= 1000 AND time <= 1000");

        var r = Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 3)");

        Assert.DoesNotContain(r.Rows, row => (long)row[0]! == 1000L);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(2000L, (long)r.Rows[0][0]!);
    }

    [Fact]
    public void Knn_AfterDeleteOnHnswSegment_DoesNotReturnTombstonedVector()
    {
        using (var db = OpenDbWithHnsw())
        {
            SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
                "('a', [1, 0, 0], 1000), " +
                "('b', [0.9, 0.1, 0], 2000), " +
                "('c', [0.8, 0.2, 0], 3000), " +
                "('d', [0, 1, 0], 4000)");
            Assert.NotNull(db.FlushNow());
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(reopened,
            "DELETE FROM docs WHERE source = 'a' AND time >= 1000 AND time <= 1000");

        var r = Select(reopened, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 4)");

        Assert.DoesNotContain(r.Rows, row => (long)row[0]! == 1000L);
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal("b", r.Rows[0][2]);
    }

    [Fact]
    public void Knn_AnnSegmentWithManyTombstones_StillReturnsKResults()
    {
        // 回归：ANN sidecar 候选量 = max(k*8, ef*2)；若其中大部分是已删除的点，
        // 旧路径会让最终结果数 < k。修复后：检测到此 series/field 有墓碑就放弃 ANN 走精确扫描。
        // 这里插入 32 个向量到 HNSW 索引段，然后删掉 query 最近邻的前 8 个，
        // 验证 k=4 仍然能命中 4 条幸存向量。
        using (var db = OpenDbWithHnsw())
        {
            for (int i = 0; i < 32; i++)
            {
                double dx = i * 0.01d;
                SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
                    $"('s{i:D2}', [{1.0 - dx}, {dx}, 0], {1000 + i * 1000})");
            }
            Assert.NotNull(db.FlushNow());
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        // 删除 query 最近的 8 个（time 1000..8000）
        for (long ts = 1000; ts <= 8000; ts += 1000)
            SqlExecutor.Execute(reopened,
                $"DELETE FROM docs WHERE source = 's{(ts / 1000 - 1):D2}' AND time >= {ts} AND time <= {ts}");

        var r = Select(reopened, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 4)");

        Assert.Equal(4, r.Rows.Count);
        // 应当从 s08 开始（前 8 个已删除）。
        Assert.Equal("s08", r.Rows[0][2]);
        Assert.All(r.Rows, row =>
        {
            long ts = (long)row[0]!;
            Assert.True(ts >= 9000, $"墓碑 ts={ts} 不应出现在结果里");
        });
    }

    // ── 度量别名 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Knn_MetricAliases_ProduceEquivalentResults()
    {
        using var db = OpenDb();
        SqlExecutor.Execute(db, "INSERT INTO docs (source, embedding, time) VALUES " +
            "('a', [1, 0, 0], 1000), " +
            "('b', [0, 1, 0], 2000)");

        // cosine 别名应产生与 'cosine' 相同的结果顺序
        var canonical = Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 2, 'cosine')");
        var alias1 = Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 2, 'cosine_distance')");
        Assert.Equal(canonical.Rows.Count, alias1.Rows.Count);
        for (int i = 0; i < canonical.Rows.Count; i++)
            Assert.Equal(canonical.Rows[i][0], alias1.Rows[i][0]); // 相同时间戳顺序

        // l2 别名
        var l2Canonical = Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 2, 'l2')");
        var l2Alias = Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 2, 'euclidean')");
        Assert.Equal(l2Canonical.Rows.Count, l2Alias.Rows.Count);
        for (int i = 0; i < l2Canonical.Rows.Count; i++)
            Assert.Equal(l2Canonical.Rows[i][0], l2Alias.Rows[i][0]);

        // inner_product 别名
        var ipCanonical = Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 2, 'inner_product')");
        var ipAliasDot = Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 2, 'dot')");
        var ipAliasIp = Select(db, "SELECT * FROM knn(docs, embedding, [1, 0, 0], 2, 'ip')");
        Assert.Equal(ipCanonical.Rows.Count, ipAliasDot.Rows.Count);
        Assert.Equal(ipCanonical.Rows.Count, ipAliasIp.Rows.Count);
        for (int i = 0; i < ipCanonical.Rows.Count; i++)
        {
            Assert.Equal(ipCanonical.Rows[i][0], ipAliasDot.Rows[i][0]);
            Assert.Equal(ipCanonical.Rows[i][0], ipAliasIp.Rows[i][0]);
        }
    }
}
