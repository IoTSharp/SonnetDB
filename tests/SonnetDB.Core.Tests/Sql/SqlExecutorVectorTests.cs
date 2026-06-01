using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// PR #58 b：CREATE/INSERT 中 VECTOR(dim) 列的端到端 + Schema 持久化测试。
/// </summary>
public class SqlExecutorVectorTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorVectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-vec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    [Fact]
    public void CreateMeasurement_WithVectorColumn_RegistersDim()
    {
        using var db = Tsdb.Open(Options());
        var schema = Assert.IsType<MeasurementSchema>(SqlExecutor.Execute(db,
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(4))"));

        var col = schema.TryGetColumn("embedding")!;
        Assert.Equal(FieldType.Vector, col.DataType);
        Assert.Equal(4, col.VectorDimension);
    }

    [Fact]
    public void CreateMeasurement_WithVectorHnswIndex_PersistsAcrossReopen()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db,
                "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(384) WITH INDEX hnsw(m=16, ef=200))");
        }

        using var db2 = Tsdb.Open(Options());
        var schema = db2.Measurements.TryGet("docs")!;
        var col = schema.TryGetColumn("embedding")!;
        Assert.NotNull(col.VectorIndex);
        Assert.Equal(VectorIndexKind.Hnsw, col.VectorIndex!.Kind);
        var hnsw = col.VectorIndex.Hnsw!;
        Assert.Equal(16, hnsw.M);
        Assert.Equal(200, hnsw.Ef);
    }

    [Fact]
    public void Insert_VectorLiteral_RoundTripsThroughEngine()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        SqlExecutor.Execute(db,
            "INSERT INTO docs (source, embedding, time) VALUES ('a', [0.1, 0.2, -0.3], 1700000000000)");

        var seriesId = SeriesId.Compute(new SeriesKey("docs",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "a" }));
        var points = db.Query.Execute(new PointQuery(seriesId, "embedding",
            new TimeRange(0, long.MaxValue))).ToList();
        Assert.Single(points);
        Assert.Equal(FieldType.Vector, points[0].Value.Type);
        var vec = points[0].Value.AsVector().ToArray();
        Assert.Equal(3, vec.Length);
        Assert.Equal(0.1f, vec[0], 6);
        Assert.Equal(0.2f, vec[1], 6);
        Assert.Equal(-0.3f, vec[2], 6);
    }

    [Fact]
    public void Insert_VectorLiteral_WrongDim_Throws()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db,
                "INSERT INTO docs (source, embedding) VALUES ('a', [0.1, 0.2])"));
        Assert.Contains("维度不匹配", ex.Message);
    }

    [Fact]
    public void Insert_VectorColumn_RejectsScalarLiteral()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db,
                "INSERT INTO docs (source, embedding) VALUES ('a', 1.0)"));
        Assert.Contains("VECTOR", ex.Message);
    }

    [Fact]
    public void Insert_NonVectorColumn_RejectsVectorLiteral()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT m (host TAG, value FIELD FLOAT)");

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db,
                "INSERT INTO m (host, value) VALUES ('a', [0.1, 0.2])"));
    }

    [Fact]
    public void Describe_VectorColumn_FormatsWithDim()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(384))");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "DESCRIBE docs"));
        // 找到 embedding 行
        var embRow = result.Rows.Single(r => (string)r[0]! == "embedding");
        Assert.Equal("field", (string)embRow[1]!);
        Assert.Equal("vector(384)", (string)embRow[2]!);
    }

    [Fact]
    public void Schema_VectorColumn_PersistsAcrossReopen()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db,
                "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(384))");
        }

        using var db2 = Tsdb.Open(Options());
        var schema = db2.Measurements.TryGet("docs")!;
        var col = schema.TryGetColumn("embedding")!;
        Assert.Equal(FieldType.Vector, col.DataType);
        Assert.Equal(384, col.VectorDimension);
    }

    [Fact]
    public void Select_VectorScalarFunctions_ReturnExpectedValues()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        SqlExecutor.Execute(db,
            "INSERT INTO docs (time, source, embedding) VALUES " +
            "(1000, 'a', [1, 0, 0]), (2000, 'b', [0, 1, 0])");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT cosine_distance(embedding, [1,0,0]), l2_distance(embedding, [1,0,0]), " +
            "inner_product(embedding, [1,0,0]), vector_norm(embedding) FROM docs"));

        Assert.Equal(2, result.Rows.Count);

        Assert.Equal(0.0, Convert.ToDouble(result.Rows[0][0]), 6);
        Assert.Equal(0.0, Convert.ToDouble(result.Rows[0][1]), 6);
        Assert.Equal(1.0, Convert.ToDouble(result.Rows[0][2]), 6);
        Assert.Equal(1.0, Convert.ToDouble(result.Rows[0][3]), 6);

        Assert.Equal(1.0, Convert.ToDouble(result.Rows[1][0]), 6);
        Assert.Equal(Math.Sqrt(2.0), Convert.ToDouble(result.Rows[1][1]), 6);
        Assert.Equal(0.0, Convert.ToDouble(result.Rows[1][2]), 6);
        Assert.Equal(1.0, Convert.ToDouble(result.Rows[1][3]), 6);
    }

    [Fact]
    public void Select_VectorOperators_ArePgVectorCompatibleAliases()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        SqlExecutor.Execute(db,
            "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1, 0, 0])");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT embedding <=> [1,0,0], embedding <-> [0,1,0], embedding <#> [1,0,0] FROM docs"));

        Assert.Single(result.Rows);
        Assert.Equal(0.0, Convert.ToDouble(result.Rows[0][0]), 6);
        Assert.Equal(Math.Sqrt(2.0), Convert.ToDouble(result.Rows[0][1]), 6);
        Assert.Equal(1.0, Convert.ToDouble(result.Rows[0][2]), 6);
    }

    [Fact]
    public void Select_Centroid_ReturnsPerDimensionMean()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        SqlExecutor.Execute(db,
            "INSERT INTO docs (time, source, embedding) VALUES " +
            "(1000, 'a', [1, 2, 3]), (2000, 'a', [3, 4, 5]), (3000, 'a', [5, 6, 7])");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT centroid(embedding) FROM docs"));

        var centroid = Assert.IsType<float[]>(result.Rows[0][0]);
        Assert.Equal(new[] { 3f, 4f, 5f }, centroid);
    }
}
