using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// Milestone 15 PR #70��GEOPOINT ���͡�POINT �������� lat/lon �����������ԡ�
/// </summary>
public sealed class SqlExecutorGeoPointTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorGeoPointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-geo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private TsdbOptions Options() => new()
    {
        RootDirectory = _root,
        SegmentWriterOptions = new SonnetDB.Storage.Segments.SegmentWriterOptions { FsyncOnCommit = false },
    };

    [Fact]
    public void CreateMeasurement_WithGeoPointColumn_RegistersType()
    {
        using var db = Tsdb.Open(Options());

        var schema = Assert.IsType<MeasurementSchema>(SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT)"));

        var col = schema.TryGetColumn("position")!;
        Assert.Equal(FieldType.GeoPoint, col.DataType);
        Assert.Null(col.VectorDimension);
    }

    [Fact]
    public void Insert_PointLiteral_RoundTripsThroughEngine()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT)");
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position) VALUES (1000, 'car-1', POINT(39.9042, 116.4074))");

        var seriesId = SeriesId.Compute(new SeriesKey("vehicle",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["device"] = "car-1" }));
        var points = db.Query.Execute(new PointQuery(seriesId, "position",
            new TimeRange(0, long.MaxValue))).ToList();

        var point = Assert.Single(points);
        Assert.Equal(FieldType.GeoPoint, point.Value.Type);
        Assert.Equal(new GeoPoint(39.9042, 116.4074), point.Value.AsGeoPoint());
    }

    [Fact]
    public void Select_LatLon_ReturnsCoordinates()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT)");
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position) VALUES (1000, 'car-1', POINT(31.2304, 121.4737))");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT lat(position), lon(position) FROM vehicle"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(31.2304, Convert.ToDouble(row[0]), 6);
        Assert.Equal(121.4737, Convert.ToDouble(row[1]), 6);
    }

    [Fact]
    public void Select_GeoScalarFunctions_ReturnExpectedValues()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT route (device TAG, p1 FIELD GEOPOINT, p2 FIELD GEOPOINT)");
        SqlExecutor.Execute(db,
            "INSERT INTO route (time, device, p1, p2) VALUES " +
            "(1000, 'car-1', POINT(39.9042, 116.4074), POINT(31.2304, 121.4737))");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT geo_distance(p1, p2), geo_bearing(p1, p2), " +
            "geo_within(p1, 39.9042, 116.4074, 1), " +
            "geo_bbox(p2, 31.0, 121.0, 32.0, 122.0), " +
            "geo_speed(p1, p2, 1000), ST_Distance(p1, p2), ST_DWithin(p1, 39.9042, 116.4074, 1) FROM route"));

        var row = Assert.Single(result.Rows);
        double distance = Convert.ToDouble(row[0]);
        Assert.InRange(distance, 1_060_000d, 1_080_000d);
        Assert.InRange(Convert.ToDouble(row[1]), 145d, 155d);
        Assert.True((bool)row[2]!);
        Assert.True((bool)row[3]!);
        Assert.InRange(Convert.ToDouble(row[4]), 1_060_000d, 1_080_000d);
        Assert.Equal(distance, Convert.ToDouble(row[5]), 6);
        Assert.True((bool)row[6]!);
    }

    [Fact]
    public void Select_GeoWithinOutsideRadius_ReturnsFalse()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT)");
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position) VALUES (1000, 'car-1', POINT(31.2304, 121.4737))");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT geo_within(position, 39.9042, 116.4074, 1000), ST_Within(position, 39.9042, 116.4074, 1000) FROM vehicle"));

        var row = Assert.Single(result.Rows);
        Assert.False((bool)row[0]!);
        Assert.False((bool)row[1]!);
    }

    [Fact]
    public void Select_WhereGeoWithin_FiltersRowsAfterFlush()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT)");
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position) VALUES " +
            "(1000, 'car-1', POINT(39.9042, 116.4074)), " +
            "(2000, 'car-1', POINT(31.2304, 121.4737)), " +
            "(3000, 'car-1', POINT(22.5431, 114.0579))");
        db.FlushNow();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT time, position FROM vehicle WHERE geo_within(position, 39.9042, 116.4074, 1000)"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(1000L, row[0]);
        Assert.Equal(new GeoPoint(39.9042, 116.4074), Assert.IsType<GeoPoint>(row[1]));
    }

    [Fact]
    public void Select_WhereGeoBbox_FiltersAggregateAfterFlush()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT, speed FIELD FLOAT)");
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position, speed) VALUES " +
            "(1000, 'car-1', POINT(39.9042, 116.4074), 10), " +
            "(2000, 'car-1', POINT(31.2304, 121.4737), 20), " +
            "(3000, 'car-1', POINT(22.5431, 114.0579), 30)");
        db.FlushNow();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT count(position), trajectory_length(position) FROM vehicle WHERE geo_bbox(position, 30, 120, 32, 122)"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(1d, Convert.ToDouble(row[0]));
        Assert.Equal(0d, Convert.ToDouble(row[1]));
    }

    // ── #282：跨字段 Geo 聚合正确性（Geo 谓词挂 GeoPoint 字段、聚合字段是数值字段）─────────

    [Fact]
    public void Select_CrossFieldGeoBbox_NumericAggregates_OnlyIncludeMatchingTimestamps()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT, speed FIELD FLOAT)");
        // 仅 t=2000 的 position 落在 bbox [30,120]~[32,122] 内（上海）；t=1000（北京）与 t=3000（深圳）在外。
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position, speed) VALUES " +
            "(1000, 'car-1', POINT(39.9042, 116.4074), 10), " +
            "(2000, 'car-1', POINT(31.2304, 121.4737), 20), " +
            "(3000, 'car-1', POINT(22.5431, 114.0579), 30)");
        db.FlushNow();

        // avg/count/min/max(speed) WHERE geo_bbox(position,...) — 跨字段 Geo 约束必须生效，只纳入 t=2000 (speed=20)。
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT count(speed), avg(speed), min(speed), max(speed) FROM vehicle " +
            "WHERE geo_bbox(position, 30, 120, 32, 122)"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(1L, Convert.ToInt64(row[0]));
        Assert.Equal(20d, Convert.ToDouble(row[1]));
        Assert.Equal(20d, Convert.ToDouble(row[2]));
        Assert.Equal(20d, Convert.ToDouble(row[3]));
    }

    [Fact]
    public void Select_CrossFieldGeoBbox_CountStar_OnlyCountsMatchingTimestamps()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT, speed FIELD FLOAT)");
        // 两个点落在 bbox 内（上海附近），一个在外（北京）。
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position, speed) VALUES " +
            "(1000, 'car-1', POINT(39.9042, 116.4074), 10), " +
            "(2000, 'car-1', POINT(31.2304, 121.4737), 20), " +
            "(3000, 'car-1', POINT(31.3000, 121.5000), 30)");
        db.FlushNow();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT count(*) FROM vehicle WHERE geo_bbox(position, 30, 120, 32, 122)"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(2L, Convert.ToInt64(row[0]));
    }

    [Fact]
    public void Select_CrossFieldGeoBbox_AggregateMatchesPerPointReference()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT, speed FIELD FLOAT)");
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position, speed) VALUES " +
            "(1000, 'car-1', POINT(31.10, 121.10), 11), " +
            "(2000, 'car-1', POINT(31.20, 121.20), 22), " +
            "(3000, 'car-1', POINT(40.00, 116.00), 99), " +   // 北京，在 bbox 外
            "(4000, 'car-1', POINT(31.30, 121.30), 33), " +
            "(5000, 'car-1', POINT(10.00, 100.00), 88)");      // 在 bbox 外
        db.FlushNow();

        // 逐点参考实现：先取 position 落在 bbox 内的时刻，再对这些时刻的 speed 聚合 → {11,22,33}。
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT count(speed), sum(speed), avg(speed), min(speed), max(speed) FROM vehicle " +
            "WHERE geo_bbox(position, 31.0, 121.0, 31.5, 121.5)"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(3L, Convert.ToInt64(row[0]));
        Assert.Equal(66d, Convert.ToDouble(row[1]));
        Assert.Equal(22d, Convert.ToDouble(row[2]));
        Assert.Equal(11d, Convert.ToDouble(row[3]));
        Assert.Equal(33d, Convert.ToDouble(row[4]));
    }

    [Fact]
    public void Select_CrossFieldGeoWithin_AvgSpeed_Filters()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT, speed FIELD FLOAT)");
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position, speed) VALUES " +
            "(1000, 'car-1', POINT(39.9042, 116.4074), 40), " +   // 命中中心
            "(2000, 'car-1', POINT(39.9045, 116.4078), 60), " +   // 命中中心附近
            "(3000, 'car-1', POINT(22.5431, 114.0579), 200)");    // 深圳，半径外
        db.FlushNow();

        // 半径 1km 内只含 t=1000/2000 → avg(speed)=50。
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT avg(speed) FROM vehicle WHERE geo_within(position, 39.9042, 116.4074, 1000)"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(50d, Convert.ToDouble(row[0]));
    }

    [Fact]
    public void Select_CrossFieldGeoBbox_GroupByTime_BucketsMatchReference()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT, speed FIELD FLOAT)");
        // 桶 [0,1000): t=100 命中, t=500 未命中；桶 [1000,2000): t=1500 命中。
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position, speed) VALUES " +
            "(100, 'car-1', POINT(31.20, 121.20), 10), " +
            "(500, 'car-1', POINT(40.00, 116.00), 999), " +
            "(1500, 'car-1', POINT(31.30, 121.30), 30)");
        db.FlushNow();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT avg(speed), count(speed) FROM vehicle " +
            "WHERE geo_bbox(position, 31.0, 121.0, 31.5, 121.5) GROUP BY time(1000ms)"));

        Assert.Equal(2, result.Rows.Count);
        // 桶0：只 t=100 命中 → avg=10, count=1。
        Assert.Equal(10d, Convert.ToDouble(result.Rows[0][0]));
        Assert.Equal(1L, Convert.ToInt64(result.Rows[0][1]));
        // 桶1：t=1500 命中 → avg=30, count=1。
        Assert.Equal(30d, Convert.ToDouble(result.Rows[1][0]));
        Assert.Equal(1L, Convert.ToInt64(result.Rows[1][1]));
    }

    [Fact]
    public void Select_CrossFieldGeoBbox_MemTableAndSegment_ConsistentResult()
    {
        // 快慢两路一致性：一半点在段（已 flush），一半在 MemTable（未 flush），Geo 约束都要生效。
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT, speed FIELD FLOAT)");
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position, speed) VALUES " +
            "(1000, 'car-1', POINT(31.20, 121.20), 10), " +
            "(2000, 'car-1', POINT(40.00, 116.00), 999)");
        db.FlushNow();
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position, speed) VALUES " +
            "(3000, 'car-1', POINT(31.30, 121.30), 30), " +
            "(4000, 'car-1', POINT(10.00, 100.00), 888)");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT count(speed), sum(speed) FROM vehicle " +
            "WHERE geo_bbox(position, 31.0, 121.0, 31.5, 121.5)"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(2L, Convert.ToInt64(row[0]));   // t=1000(段) + t=3000(MemTable)
        Assert.Equal(40d, Convert.ToDouble(row[1])); // 10 + 30
    }

    [Fact]
    public void FlushAndReopen_GeoPointSegment_RoundTrips()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db,
                "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT)");
            SqlExecutor.Execute(db,
                "INSERT INTO vehicle (time, device, position) VALUES " +
                "(1000, 'car-1', POINT(39.9042, 116.4074)), " +
                "(2000, 'car-1', POINT(31.2304, 121.4737))");
            db.FlushNow();
        }

        using var reopened = Tsdb.Open(Options());
        var seriesId = SeriesId.Compute(new SeriesKey("vehicle",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["device"] = "car-1" }));
        var points = reopened.Query.Execute(new PointQuery(seriesId, "position",
            new TimeRange(0, long.MaxValue))).ToList();

        Assert.Equal(2, points.Count);
        Assert.Equal(new GeoPoint(39.9042, 116.4074), points[0].Value.AsGeoPoint());
        Assert.Equal(new GeoPoint(31.2304, 121.4737), points[1].Value.AsGeoPoint());
    }

    [Fact]
    public void Select_TrajectoryAggregates_ReturnExpectedValues()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT)");
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position) VALUES " +
            "(1000, 'car-1', POINT(0, 0)), " +
            "(2000, 'car-1', POINT(0, 0.001)), " +
            "(4000, 'car-1', POINT(0.001, 0.001))");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT trajectory_length(position), trajectory_centroid(position), trajectory_bbox(position), " +
            "trajectory_speed_max(position, time), trajectory_speed_avg(position, time), trajectory_speed_p95(position, time) FROM vehicle"));

        var row = Assert.Single(result.Rows);
        Assert.InRange(Convert.ToDouble(row[0]), 220d, 225d);
        var centroid = Assert.IsType<GeoPoint>(row[1]);
        Assert.Equal(0.00033333333333333332d, centroid.Lat, 12);
        Assert.Equal(0.00066666666666666664d, centroid.Lon, 12);
        var bbox = Assert.IsType<string>(row[2]);
        Assert.Contains("\"min_lat\":0", bbox);
        Assert.Contains("\"min_lon\":0", bbox);
        Assert.Contains("\"max_lat\":0.001", bbox);
        Assert.Contains("\"max_lon\":0.001", bbox);
        Assert.InRange(Convert.ToDouble(row[3]), 110d, 112d);
        Assert.InRange(Convert.ToDouble(row[4]), 83d, 84d);
        Assert.InRange(Convert.ToDouble(row[5]), 107d, 112d);
    }

    [Fact]
    public void Select_TrajectoryAggregates_GroupByTime_ReturnBucketedValues()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT)");
        SqlExecutor.Execute(db,
            "INSERT INTO vehicle (time, device, position) VALUES " +
            "(1, 'car-1', POINT(0, 0)), " +
            "(500, 'car-1', POINT(0, 0.001)), " +
            "(1500, 'car-1', POINT(0.5, 0.5)), " +
            "(2000, 'car-1', POINT(1, 1)), " +
            "(2500, 'car-1', POINT(1, 1.001))");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT trajectory_length(position), trajectory_speed_avg(position, time) FROM vehicle GROUP BY time(1000ms)"));

        Assert.Equal(3, result.Rows.Count);
        Assert.InRange(Convert.ToDouble(result.Rows[0][0]), 110d, 112d);
        Assert.InRange(Convert.ToDouble(result.Rows[0][1]), 220d, 225d);
        Assert.Equal(0d, Convert.ToDouble(result.Rows[1][0]));
        Assert.Null(result.Rows[1][1]);
        Assert.InRange(Convert.ToDouble(result.Rows[2][0]), 110d, 112d);
        Assert.InRange(Convert.ToDouble(result.Rows[2][1]), 220d, 225d);
    }

}

