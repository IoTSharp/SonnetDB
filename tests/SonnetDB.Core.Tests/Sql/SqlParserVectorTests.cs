using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// PR #58 b：CREATE MEASUREMENT 中 <c>VECTOR(dim)</c> 列声明 + 表达式层
/// <c>[v0, v1, ...]</c> 向量字面量解析测试。
/// </summary>
public class SqlParserVectorTests
{
    // ── CREATE MEASUREMENT ... VECTOR(dim) ────────────────────────────────

    [Fact]
    public void Parse_CreateMeasurement_VectorColumn_ReturnsAstWithDim()
    {
        var stmt = (CreateMeasurementStatement)SqlParser.Parse(
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(384))");

        Assert.Equal("docs", stmt.Name);
        Assert.Equal(2, stmt.Columns.Count);
        Assert.Equal(new ColumnDefinition("source", ColumnKind.Tag, SqlDataType.String), stmt.Columns[0]);
        Assert.Equal(
            new ColumnDefinition("embedding", ColumnKind.Field, SqlDataType.Vector, 384),
            stmt.Columns[1]);
    }

    [Fact]
    public void Parse_CreateMeasurement_VectorColumnWithHnswIndex_ReturnsAstWithIndex()
    {
        var stmt = (CreateMeasurementStatement)SqlParser.Parse(
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(384) WITH INDEX hnsw(m=16, ef=200))");

        var column = stmt.Columns[1];
        var index = Assert.IsType<HnswVectorIndexSpec>(column.VectorIndex);
        Assert.Equal(16, index.M);
        Assert.Equal(200, index.Ef);
    }

    [Fact]
    public void Parse_CreateMeasurement_VectorColumnWithIvfIndex_ReturnsAstWithIndex()
    {
        var stmt = (CreateMeasurementStatement)SqlParser.Parse(
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(384) WITH INDEX ivf(nlist=32, nprobe=8, max_iterations=12))");

        var column = stmt.Columns[1];
        var index = Assert.IsType<IvfVectorIndexSpec>(column.VectorIndex);
        Assert.Equal(32, index.NList);
        Assert.Equal(8, index.NProbe);
        Assert.Equal(12, index.MaxIterations);
    }

    [Fact]
    public void Parse_CreateMeasurement_VectorColumnWithIvfPqIndex_ReturnsAstWithIndex()
    {
        var stmt = (CreateMeasurementStatement)SqlParser.Parse(
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(384) WITH INDEX ivf_pq(nlist=32, nprobe=8, max_iterations=12, m=8, nbits=8))");

        var column = stmt.Columns[1];
        var index = Assert.IsType<IvfPqVectorIndexSpec>(column.VectorIndex);
        Assert.Equal(32, index.NList);
        Assert.Equal(8, index.NProbe);
        Assert.Equal(12, index.MaxIterations);
        Assert.Equal(8, index.M);
        Assert.Equal(8, index.NBits);
    }

    [Fact]
    public void Parse_CreateMeasurement_VectorColumnWithVamanaIndex_ReturnsAstWithIndex()
    {
        var stmt = (CreateMeasurementStatement)SqlParser.Parse(
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(384) WITH INDEX vamana(max_degree=32, search_list_size=75, alpha=1.2, beam_width=4))");

        var column = stmt.Columns[1];
        var index = Assert.IsType<VamanaVectorIndexSpec>(column.VectorIndex);
        Assert.Equal(32, index.MaxDegree);
        Assert.Equal(75, index.SearchListSize);
        Assert.Equal(1.2f, index.Alpha);
        Assert.Equal(4, index.BeamWidth);
    }

    [Fact]
    public void Parse_CreateMeasurement_NonVectorWithIndex_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("CREATE MEASUREMENT m (score FIELD FLOAT WITH INDEX hnsw(m=16, ef=200))"));
    }

    // ── #223：metric= 与 ef_construction= 解析 ────────────────────────────────

    [Fact]
    public void Parse_Hnsw_DefaultsMetricCosineAndEfConstructionMaxEf200()
    {
        var stmt = (CreateMeasurementStatement)SqlParser.Parse(
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(384) WITH INDEX hnsw(m=16, ef=50))");

        var index = Assert.IsType<HnswVectorIndexSpec>(stmt.Columns[1].VectorIndex);
        Assert.Equal(SonnetDB.Query.KnnMetric.Cosine, index.Metric);
        Assert.Equal(50, index.Ef);
        Assert.Equal(200, index.EfConstruction); // 缺省 max(ef, 200)
    }

    [Fact]
    public void Parse_Hnsw_WithMetricAndEfConstruction()
    {
        var stmt = (CreateMeasurementStatement)SqlParser.Parse(
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(384) WITH INDEX hnsw(m=16, ef=64, ef_construction=256, metric='l2'))");

        var index = Assert.IsType<HnswVectorIndexSpec>(stmt.Columns[1].VectorIndex);
        Assert.Equal(SonnetDB.Query.KnnMetric.L2, index.Metric);
        Assert.Equal(64, index.Ef);
        Assert.Equal(256, index.EfConstruction);
    }

    [Theory]
    [InlineData("'cosine'", SonnetDB.Query.KnnMetric.Cosine)]
    [InlineData("'l2'", SonnetDB.Query.KnnMetric.L2)]
    [InlineData("'inner_product'", SonnetDB.Query.KnnMetric.InnerProduct)]
    [InlineData("'ip'", SonnetDB.Query.KnnMetric.InnerProduct)]
    [InlineData("'euclidean'", SonnetDB.Query.KnnMetric.L2)]
    public void Parse_Hnsw_MetricAliases(string metricLiteral, SonnetDB.Query.KnnMetric expected)
    {
        var stmt = (CreateMeasurementStatement)SqlParser.Parse(
            $"CREATE MEASUREMENT docs (embedding FIELD VECTOR(8) WITH INDEX hnsw(m=16, ef=200, metric={metricLiteral}))");

        Assert.Equal(expected, Assert.IsType<HnswVectorIndexSpec>(stmt.Columns[0].VectorIndex).Metric);
    }

    [Fact]
    public void Parse_Ivf_WithMetric()
    {
        var stmt = (CreateMeasurementStatement)SqlParser.Parse(
            "CREATE MEASUREMENT docs (embedding FIELD VECTOR(8) WITH INDEX ivf(nlist=32, nprobe=8, metric='l2'))");

        var index = Assert.IsType<IvfVectorIndexSpec>(stmt.Columns[0].VectorIndex);
        Assert.Equal(SonnetDB.Query.KnnMetric.L2, index.Metric);
        Assert.Equal(32, index.NList);
    }

    [Fact]
    public void Parse_Vamana_WithMetric()
    {
        var stmt = (CreateMeasurementStatement)SqlParser.Parse(
            "CREATE MEASUREMENT docs (embedding FIELD VECTOR(8) WITH INDEX vamana(max_degree=32, alpha=1.2, metric='inner_product'))");

        var index = Assert.IsType<VamanaVectorIndexSpec>(stmt.Columns[0].VectorIndex);
        Assert.Equal(SonnetDB.Query.KnnMetric.InnerProduct, index.Metric);
        Assert.Equal(1.2f, index.Alpha);
    }

    [Fact]
    public void Parse_Hnsw_UnknownMetric_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("CREATE MEASUREMENT m (e FIELD VECTOR(8) WITH INDEX hnsw(m=16, ef=200, metric='manhattan'))"));
    }

    [Fact]
    public void Parse_CreateMeasurement_VectorWithoutDim_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("CREATE MEASUREMENT m (e FIELD VECTOR)"));
    }

    [Fact]
    public void Parse_CreateMeasurement_VectorEmptyParens_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("CREATE MEASUREMENT m (e FIELD VECTOR())"));
    }

    [Fact]
    public void Parse_CreateMeasurement_VectorZeroDim_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("CREATE MEASUREMENT m (e FIELD VECTOR(0))"));
    }

    [Fact]
    public void Parse_CreateMeasurement_VectorNegativeDim_Throws()
    {
        // -1 进入 ParseFieldDataType 时，'-' 不是 IntegerLiteral，先报"VECTOR 必须声明维度"。
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("CREATE MEASUREMENT m (e FIELD VECTOR(-1))"));
    }

    [Fact]
    public void Parse_CreateMeasurement_TagVector_Throws()
    {
        // Tag 列只能 STRING：解析阶段就拒绝 VECTOR。
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("CREATE MEASUREMENT m (host TAG VECTOR(3))"));
    }

    // ── 向量字面量 [v0, v1, ...] ──────────────────────────────────────────

    [Fact]
    public void Parse_Insert_VectorLiteral_ParsesComponents()
    {
        var stmt = (InsertStatement)SqlParser.Parse(
            "INSERT INTO docs (source, embedding) VALUES ('a', [0.1, 0.2, -0.3])");

        var row = stmt.Rows[0];
        Assert.IsType<LiteralExpression>(row[0]);
        var vec = Assert.IsType<VectorLiteralExpression>(row[1]);
        Assert.Equal(new double[] { 0.1, 0.2, -0.3 }, vec.Components);
    }

    [Fact]
    public void Parse_Insert_VectorLiteral_AllowsIntComponents()
    {
        var stmt = (InsertStatement)SqlParser.Parse(
            "INSERT INTO docs (source, embedding) VALUES ('a', [1, -2, 3])");

        var vec = Assert.IsType<VectorLiteralExpression>(stmt.Rows[0][1]);
        Assert.Equal(new double[] { 1.0, -2.0, 3.0 }, vec.Components);
    }

    [Fact]
    public void Parse_Insert_VectorLiteral_SingleComponent()
    {
        var stmt = (InsertStatement)SqlParser.Parse(
            "INSERT INTO docs (source, embedding) VALUES ('a', [42])");
        var vec = Assert.IsType<VectorLiteralExpression>(stmt.Rows[0][1]);
        Assert.Equal(new double[] { 42.0 }, vec.Components);
    }

    [Fact]
    public void Parse_VectorLiteral_Empty_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("INSERT INTO docs (e) VALUES ([])"));
    }

    [Fact]
    public void Parse_VectorLiteral_Unclosed_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("INSERT INTO docs (e) VALUES ([1, 2, 3)"));
    }

    [Fact]
    public void Parse_VectorLiteral_NonNumericComponent_Throws()
    {
        Assert.Throws<SqlParseException>(() =>
            SqlParser.Parse("INSERT INTO docs (e) VALUES ([1, 'x', 3])"));
    }

    [Theory]
    [InlineData("<->", "l2_distance")]
    [InlineData("<=>", "cosine_distance")]
    [InlineData("<#>", "inner_product")]
    public void Parse_Select_VectorOperator_RewritesToFunctionCall(string op, string functionName)
    {
        var stmt = (SelectStatement)SqlParser.Parse(
            $"SELECT embedding {op} [1, 2, 3] FROM docs");

        var fn = Assert.IsType<FunctionCallExpression>(stmt.Projections[0].Expression);
        Assert.Equal(functionName, fn.Name);
        Assert.Equal(new IdentifierExpression("embedding"), fn.Arguments[0]);
        var vector = Assert.IsType<VectorLiteralExpression>(fn.Arguments[1]);
        Assert.Equal(new double[] { 1, 2, 3 }, vector.Components);
    }
}
