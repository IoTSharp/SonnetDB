using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public class SqlExecutorInsertTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorInsertTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-insert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    private static Tsdb OpenWithSchema(TsdbOptions options)
    {
        var db = Tsdb.Open(options);
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT cpu (host TAG, region TAG, usage FIELD FLOAT, count FIELD INT, ok FIELD BOOL, label FIELD STRING)");
        return db;
    }

    [Fact]
    public void Insert_SingleRow_WithExplicitTime_WritesPoint()
    {
        using var db = OpenWithSchema(Options());

        var result = SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, region, usage) VALUES (1700000000000, 'h1', 'cn', 1.5)");

        var insert = Assert.IsType<InsertExecutionResult>(result);
        Assert.Equal("cpu", insert.Measurement);
        Assert.Equal(1, insert.RowsInserted);

        var seriesId = SeriesId.Compute(new SeriesKey("cpu", new Dictionary<string, string>
        {
            ["host"] = "h1",
            ["region"] = "cn",
        }));
        var points = db.Query.Execute(new PointQuery(seriesId, "usage",
            new TimeRange(0, long.MaxValue))).ToList();
        Assert.Single(points);
        Assert.Equal(1700000000000L, points[0].Timestamp);
        Assert.Equal(1.5, points[0].Value.AsDouble());
    }

    [Fact]
    public void Insert_MultipleRows_BatchWrites()
    {
        using var db = OpenWithSchema(Options());

        var result = (InsertExecutionResult)SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 1.0), (2000, 'h1', 2.0), (3000, 'h1', 3.0)")!;

        Assert.Equal(3, result.RowsInserted);

        var seriesId = SeriesId.Compute(new SeriesKey("cpu", new Dictionary<string, string>
        {
            ["host"] = "h1",
        }));
        var points = db.Query.Execute(new PointQuery(seriesId, "usage",
            new TimeRange(0, long.MaxValue))).ToList();
        Assert.Equal(3, points.Count);
        Assert.Equal([1000L, 2000L, 3000L], points.Select(p => p.Timestamp));
        Assert.Equal([1.0, 2.0, 3.0], points.Select(p => p.Value.AsDouble()));
    }

    [Fact]
    public void Insert_TimeOmitted_DefaultsToNow()
    {
        using var db = OpenWithSchema(Options());
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        SqlExecutor.Execute(db, "INSERT INTO cpu (host, usage) VALUES ('h1', 9.0)");

        long after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var seriesId = SeriesId.Compute(new SeriesKey("cpu", new Dictionary<string, string>
        {
            ["host"] = "h1",
        }));
        var points = db.Query.Execute(new PointQuery(seriesId, "usage",
            new TimeRange(0, long.MaxValue))).ToList();
        Assert.Single(points);
        Assert.InRange(points[0].Timestamp, before, after);
    }

    [Fact]
    public void Insert_IntegerLiteralPromotesToFloat()
    {
        using var db = OpenWithSchema(Options());

        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 7)");

        var seriesId = SeriesId.Compute(new SeriesKey("cpu", new Dictionary<string, string>
        {
            ["host"] = "h1",
        }));
        var p = db.Query.Execute(new PointQuery(seriesId, "usage",
            new TimeRange(0, long.MaxValue))).Single();
        Assert.Equal(FieldType.Float64, p.Value.Type);
        Assert.Equal(7.0, p.Value.AsDouble());
    }

    [Fact]
    public void Insert_AllFieldTypes_RoundTrip()
    {
        using var db = OpenWithSchema(Options());

        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage, count, ok, label) VALUES (1000, 'h1', 1.5, 42, TRUE, 'hello')");

        var seriesId = SeriesId.Compute(new SeriesKey("cpu", new Dictionary<string, string>
        {
            ["host"] = "h1",
        }));
        var range = new TimeRange(0, long.MaxValue);
        Assert.Equal(1.5, db.Query.Execute(new PointQuery(seriesId, "usage", range)).Single().Value.AsDouble());
        Assert.Equal(42L, db.Query.Execute(new PointQuery(seriesId, "count", range)).Single().Value.AsLong());
        Assert.True(db.Query.Execute(new PointQuery(seriesId, "ok", range)).Single().Value.AsBool());
        Assert.Equal("hello", db.Query.Execute(new PointQuery(seriesId, "label", range)).Single().Value.AsString());
    }

    [Fact]
    public void Insert_NegativeNumericValue_WritesPoint()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT telemetry (file_msgqueue_size FIELD FLOAT)");

        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO telemetry (time, file_msgqueue_size) VALUES (1, -0.25)"));

        Assert.Equal(1, result.RowsInserted);
        var seriesId = SeriesId.Compute(new SeriesKey("telemetry", new Dictionary<string, string>()));
        var point = db.Query.Execute(new PointQuery(seriesId, "file_msgqueue_size", TimeRange.All)).Single();
        Assert.Equal(-0.25, point.Value.AsDouble());
    }

    [Fact]
    public void Insert_MeasurementMissing_AutoCreatesSchema()
    {
        using var db = Tsdb.Open(Options());

        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO ghost (time, host, usage) VALUES (1, 'h1', 1.5)"));

        Assert.Equal(1, result.RowsInserted);
        var schema = db.Measurements.TryGet("ghost");
        Assert.NotNull(schema);
        Assert.Equal(MeasurementColumnRole.Tag, schema!.TryGetColumn("host")!.Role);
        Assert.Equal(MeasurementColumnRole.Field, schema.TryGetColumn("usage")!.Role);
        Assert.Equal(FieldType.Float64, schema.TryGetColumn("usage")!.DataType);
    }

    [Fact]
    public void Insert_UnknownColumns_AutoExtendsSchema()
    {
        using var db = OpenWithSchema(Options());

        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, rack, temperature) VALUES (1, 'h1', 'r1', 42.5)");

        var schema = db.Measurements.TryGet("cpu")!;
        Assert.Equal(MeasurementColumnRole.Tag, schema.TryGetColumn("rack")!.Role);
        Assert.Equal(MeasurementColumnRole.Field, schema.TryGetColumn("temperature")!.Role);
        Assert.Equal(FieldType.Float64, schema.TryGetColumn("temperature")!.DataType);
    }

    [Fact]
    public void Insert_UnknownStringOnlyColumn_OnExistingMeasurement_TreatsColumnAsField()
    {
        using var db = OpenWithSchema(Options());

        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, PARAM_M1BLACK) VALUES (1, '202606301810')"));

        Assert.Equal(1, result.RowsInserted);
        var schema = db.Measurements.TryGet("cpu")!;
        var column = schema.TryGetColumn("PARAM_M1BLACK");
        Assert.NotNull(column);
        Assert.Equal(MeasurementColumnRole.Field, column!.Role);
        Assert.Equal(FieldType.String, column.DataType);

        var seriesId = SeriesId.Compute(new SeriesKey("cpu", new Dictionary<string, string>()));
        var point = db.Query.Execute(new PointQuery(seriesId, "PARAM_M1BLACK", TimeRange.All)).Single();
        Assert.Equal("202606301810", point.Value.AsString());
    }

    [Fact]
    public void Insert_UnknownStringColumn_OnTaglessMeasurement_TreatsColumnAsFieldEvenWhenBlank()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT telemetry (cpuheadinfo FIELD STRING)");

        var result = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO telemetry (time, cpuheadinfo, wdsIPAddress, cpuRate) VALUES (1, '2026063021', '', 36.5)"));

        Assert.Equal(1, result.RowsInserted);
        var schema = db.Measurements.TryGet("telemetry")!;
        var ipColumn = schema.TryGetColumn("wdsIPAddress");
        var cpuRateColumn = schema.TryGetColumn("cpuRate");
        Assert.NotNull(ipColumn);
        Assert.Equal(MeasurementColumnRole.Field, ipColumn!.Role);
        Assert.Equal(FieldType.String, ipColumn.DataType);
        Assert.NotNull(cpuRateColumn);
        Assert.Equal(MeasurementColumnRole.Field, cpuRateColumn!.Role);
        Assert.Equal(FieldType.Float64, cpuRateColumn.DataType);

        var seriesId = SeriesId.Compute(new SeriesKey("telemetry", new Dictionary<string, string>()));
        var ip = db.Query.Execute(new PointQuery(seriesId, "wdsIPAddress", TimeRange.All)).Single();
        Assert.Equal(string.Empty, ip.Value.AsString());
    }

    [Fact]
    public void Insert_DuplicateColumn_Throws()
    {
        using var db = OpenWithSchema(Options());

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO cpu (host, host, usage) VALUES ('a', 'b', 1.0)"));
    }

    [Fact]
    public void Insert_FieldTypeMismatch_Throws()
    {
        using var db = OpenWithSchema(Options());

        // count 是 INT，但传入字符串
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, count) VALUES (1, 'h1', 'not-int')"));

        // ok 是 BOOL，但传入整数
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, ok) VALUES (1, 'h1', 1)"));

        // label 是 STRING，但传入浮点
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, label) VALUES (1, 'h1', 1.5)"));

        // usage 是 FLOAT，但传入字符串（int → float 允许，string → float 不允许）
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, usage) VALUES (1, 'h1', 'oops')"));
    }

    [Fact]
    public void Insert_IntFieldWithFloatValue_PromotesSchemaToFloat()
    {
        using var db = OpenWithSchema(Options());

        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, count) VALUES (1, 'h1', 1)");
        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, count) VALUES (2, 'h1', 1.5)");

        var schema = db.Measurements.TryGet("cpu")!;
        Assert.Equal(FieldType.Float64, schema.TryGetColumn("count")!.DataType);

        var seriesId = SeriesId.Compute(new SeriesKey("cpu", new Dictionary<string, string>
        {
            ["host"] = "h1",
        }));
        var points = db.Query.Execute(new PointQuery(seriesId, "count",
            new TimeRange(0, long.MaxValue))).ToList();
        Assert.Equal(2, points.Count);
        Assert.Equal(FieldType.Int64, points[0].Value.Type);
        Assert.Equal(FieldType.Float64, points[1].Value.Type);

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT count FROM cpu WHERE host = 'h1'"));
        Assert.Equal(2, selected.Rows.Count);
        Assert.IsType<double>(selected.Rows[0][0]);
        Assert.IsType<double>(selected.Rows[1][0]);
    }

    [Fact]
    public void Insert_FloatFieldWithIntegerValue_KeepsFloatSchemaAndStoresFloat()
    {
        using var db = OpenWithSchema(Options());

        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, usage) VALUES (1, 'h1', 7)");

        var schema = db.Measurements.TryGet("cpu")!;
        Assert.Equal(FieldType.Float64, schema.TryGetColumn("usage")!.DataType);

        var seriesId = SeriesId.Compute(new SeriesKey("cpu", new Dictionary<string, string>
        {
            ["host"] = "h1",
        }));
        var point = db.Query.Execute(new PointQuery(seriesId, "usage",
            new TimeRange(0, long.MaxValue))).Single();
        Assert.Equal(FieldType.Float64, point.Value.Type);
        Assert.Equal(7.0, point.Value.AsDouble());
    }

    [Fact]
    public void Insert_TagMustBeString_Throws()
    {
        using var db = OpenWithSchema(Options());

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, usage) VALUES (1, 123, 1.0)"));
    }

    [Fact]
    public void Insert_NullForTagOrField_Throws()
    {
        using var db = OpenWithSchema(Options());

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, usage) VALUES (1, NULL, 1.0)"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, usage) VALUES (1, 'h1', NULL)"));
    }

    [Fact]
    public void Insert_NoFieldColumn_Throws()
    {
        using var db = OpenWithSchema(Options());

        // 只有 tag，没有任何 field
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO cpu (time, host) VALUES (1, 'h1')"));
    }

    [Fact]
    public void Insert_NegativeTimestamp_Throws()
    {
        using var db = OpenWithSchema(Options());

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, usage) VALUES (-1, 'h1', 1.0)"));
    }

    [Fact]
    public void Insert_TimeColumn_CaseInsensitive()
    {
        using var db = OpenWithSchema(Options());

        SqlExecutor.Execute(db, "INSERT INTO cpu (TIME, host, usage) VALUES (5000, 'h1', 1.0)");

        var seriesId = SeriesId.Compute(new SeriesKey("cpu", new Dictionary<string, string>
        {
            ["host"] = "h1",
        }));
        var p = db.Query.Execute(new PointQuery(seriesId, "usage",
            new TimeRange(0, long.MaxValue))).Single();
        Assert.Equal(5000L, p.Timestamp);
    }

    [Fact]
    public void Insert_NoTags_OnlyFields_Works()
    {
        using var db = OpenWithSchema(Options());

        SqlExecutor.Execute(db, "INSERT INTO cpu (time, usage) VALUES (1000, 3.14)");

        var seriesId = SeriesId.Compute(new SeriesKey("cpu"));
        var p = db.Query.Execute(new PointQuery(seriesId, "usage",
            new TimeRange(0, long.MaxValue))).Single();
        Assert.Equal(3.14, p.Value.AsDouble());
    }

    [Fact]
    public void Insert_PartialBatchFails_FirstRowsAlreadyWritten()
    {
        using var db = OpenWithSchema(Options());

        // 第二行 ok 列类型不匹配；第一行已经成功写入
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db,
                "INSERT INTO cpu (time, host, usage, ok) VALUES (1000, 'h1', 1.0, TRUE), (2000, 'h1', 2.0, 5)"));

        var seriesId = SeriesId.Compute(new SeriesKey("cpu", new Dictionary<string, string>
        {
            ["host"] = "h1",
        }));
        var points = db.Query.Execute(new PointQuery(seriesId, "usage",
            new TimeRange(0, long.MaxValue))).ToList();
        Assert.Single(points);
        Assert.Equal(1000L, points[0].Timestamp);
    }

    [Fact]
    public void ExecuteInsert_NullArgs_Throw()
    {
        using var db = Tsdb.Open(Options());
        var stmt = (InsertStatement)SqlParser.Parse("INSERT INTO m (a) VALUES (1)");
        Assert.Throws<ArgumentNullException>(() => SqlExecutor.ExecuteInsert(null!, stmt));
        Assert.Throws<ArgumentNullException>(() => SqlExecutor.ExecuteInsert(db, null!));
    }
}
