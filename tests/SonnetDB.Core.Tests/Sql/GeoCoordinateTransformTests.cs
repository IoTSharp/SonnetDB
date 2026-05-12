using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class GeoCoordinateTransformTests : IDisposable
{
    private readonly string _root;

    public GeoCoordinateTransformTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-geo-transform-" + Guid.NewGuid().ToString("N"));
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
    public void Select_GeoCoordinateTransformFunctions_ReturnRoundTripValues()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT route (device TAG, position FIELD GEOPOINT)");
        SqlExecutor.Execute(db,
            "INSERT INTO route (time, device, position) VALUES (1000, 'car-1', POINT(39.9042, 116.4074))");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT geo_wgs84_to_gcj02(position), geo_gcj02_to_wgs84(geo_wgs84_to_gcj02(position)), " +
            "geo_wgs84_to_bd09(position), geo_bd09_to_wgs84(geo_wgs84_to_bd09(position)), " +
            "geo_transform(position, 'wgs84', 'bd09') FROM route"));

        var row = Assert.Single(result.Rows);
        var original = new GeoPoint(39.9042, 116.4074);

        var gcj02 = Assert.IsType<GeoPoint>(row[0]);
        Assert.NotEqual(original, gcj02);

        var roundTripGcj02 = Assert.IsType<GeoPoint>(row[1]);
        Assert.Equal(original.Lat, roundTripGcj02.Lat, 4);
        Assert.Equal(original.Lon, roundTripGcj02.Lon, 4);

        var bd09 = Assert.IsType<GeoPoint>(row[2]);
        Assert.NotEqual(original, bd09);

        var roundTripBd09 = Assert.IsType<GeoPoint>(row[3]);
        Assert.Equal(original.Lat, roundTripBd09.Lat, 4);
        Assert.Equal(original.Lon, roundTripBd09.Lon, 4);

        Assert.Equal(bd09, Assert.IsType<GeoPoint>(row[4]));
    }

    [Fact]
    public void Select_GeoCoordinateTransformFunctions_OutsideChina_ReturnOriginalPoint()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT route (device TAG, position FIELD GEOPOINT)");
        SqlExecutor.Execute(db,
            "INSERT INTO route (time, device, position) VALUES (1000, 'nyc', POINT(40.7128, -74.0060))");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT geo_wgs84_to_gcj02(position), geo_wgs84_to_bd09(position), geo_transform(position, 'gps', 'baidu') FROM route"));

        var row = Assert.Single(result.Rows);
        var original = new GeoPoint(40.7128, -74.0060);
        Assert.Equal(original, Assert.IsType<GeoPoint>(row[0]));
        Assert.Equal(original, Assert.IsType<GeoPoint>(row[1]));
        Assert.Equal(original, Assert.IsType<GeoPoint>(row[2]));
    }
}
