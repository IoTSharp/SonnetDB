using SonnetDB.Model;

namespace SonnetDB.Query.Functions;

internal enum GeoCoordinateSystem
{
    Wgs84,
    Gcj02,
    Bd09,
}

internal static class GeoCoordinateTransforms
{
    private const double Pi = Math.PI;
    private const double EarthSemiMajorAxis = 6_378_245.0d;
    private const double EarthEccentricitySquared = 0.006_693_421_622_965_943d;
    private const double BdXPi = 3000.0d * Pi / 180.0d;

    public static GeoPoint Transform(GeoPoint point, GeoCoordinateSystem from, GeoCoordinateSystem to)
    {
        if (from == to)
            return point;

        return (from, to) switch
        {
            (GeoCoordinateSystem.Wgs84, GeoCoordinateSystem.Gcj02) => Wgs84ToGcj02(point),
            (GeoCoordinateSystem.Gcj02, GeoCoordinateSystem.Wgs84) => Gcj02ToWgs84(point),
            (GeoCoordinateSystem.Gcj02, GeoCoordinateSystem.Bd09) => Gcj02ToBd09(point),
            (GeoCoordinateSystem.Bd09, GeoCoordinateSystem.Gcj02) => Bd09ToGcj02(point),
            (GeoCoordinateSystem.Wgs84, GeoCoordinateSystem.Bd09) => Gcj02ToBd09(Wgs84ToGcj02(point)),
            (GeoCoordinateSystem.Bd09, GeoCoordinateSystem.Wgs84) => Gcj02ToWgs84(Bd09ToGcj02(point)),
            _ => throw new InvalidOperationException($"不支持从 {from} 转换到 {to}。"),
        };
    }

    public static GeoCoordinateSystem ParseCoordinateSystem(string? value, string functionName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"函数 {functionName} 的参数 {parameterName} 不能为空。");

        return Normalize(value) switch
        {
            "wgs84" or "gps" => GeoCoordinateSystem.Wgs84,
            "gcj02" or "gcj" or "amap" or "gaode" or "tencent" or "qq" => GeoCoordinateSystem.Gcj02,
            "bd09" or "bd" or "baidu" => GeoCoordinateSystem.Bd09,
            _ => throw new InvalidOperationException(
                $"函数 {functionName} 的参数 {parameterName} 仅支持 WGS84 / GCJ02 / BD09（或 GPS / AMap / Tencent / Baidu 别名）。"),
        };
    }

    public static GeoPoint Wgs84ToGcj02(GeoPoint point)
    {
        if (IsOutsideChina(point.Lat, point.Lon))
            return point;

        var delta = Delta(point.Lat, point.Lon);
        return GeoPoint.Create(point.Lat + delta.Lat, point.Lon + delta.Lon);
    }

    public static GeoPoint Gcj02ToWgs84(GeoPoint point)
    {
        if (IsOutsideChina(point.Lat, point.Lon))
            return point;

        return Wgs84ApproximateInverse(point);
    }

    public static GeoPoint Gcj02ToBd09(GeoPoint point)
    {
        if (IsOutsideChina(point.Lat, point.Lon))
            return point;

        double x = point.Lon;
        double y = point.Lat;
        double z = Math.Sqrt(x * x + y * y) + 0.00002d * Math.Sin(y * BdXPi);
        double theta = Math.Atan2(y, x) + 0.000003d * Math.Cos(x * BdXPi);
        double bdLon = z * Math.Cos(theta) + 0.0065d;
        double bdLat = z * Math.Sin(theta) + 0.006d;
        return GeoPoint.Create(bdLat, bdLon);
    }

    public static GeoPoint Bd09ToGcj02(GeoPoint point)
    {
        if (IsOutsideChina(point.Lat, point.Lon))
            return point;

        double x = point.Lon - 0.0065d;
        double y = point.Lat - 0.006d;
        double z = Math.Sqrt(x * x + y * y) - 0.00002d * Math.Sin(y * BdXPi);
        double theta = Math.Atan2(y, x) - 0.000003d * Math.Cos(x * BdXPi);
        double lon = z * Math.Cos(theta);
        double lat = z * Math.Sin(theta);
        return GeoPoint.Create(lat, lon);
    }

    private static GeoPoint Wgs84ApproximateInverse(GeoPoint point)
    {
        double minLat = point.Lat - 0.01d;
        double maxLat = point.Lat + 0.01d;
        double minLon = point.Lon - 0.01d;
        double maxLon = point.Lon + 0.01d;

        for (int i = 0; i < 30; i++)
        {
            double midLat = (minLat + maxLat) / 2d;
            double midLon = (minLon + maxLon) / 2d;
            var converted = Wgs84ToGcj02(GeoPoint.Create(midLat, midLon));

            if (converted.Lat > point.Lat)
                maxLat = midLat;
            else
                minLat = midLat;

            if (converted.Lon > point.Lon)
                maxLon = midLon;
            else
                minLon = midLon;

            if (Math.Abs(converted.Lat - point.Lat) < 1e-7d && Math.Abs(converted.Lon - point.Lon) < 1e-7d)
                break;
        }

        return GeoPoint.Create((minLat + maxLat) / 2d, (minLon + maxLon) / 2d);
    }

    private static (double Lat, double Lon) Delta(double lat, double lon)
    {
        double dLat = TransformLat(lon - 105.0d, lat - 35.0d);
        double dLon = TransformLon(lon - 105.0d, lat - 35.0d);

        double radLat = lat / 180.0d * Pi;
        double magic = Math.Sin(radLat);
        magic = 1d - EarthEccentricitySquared * magic * magic;
        double sqrtMagic = Math.Sqrt(magic);
        dLat = (dLat * 180.0d) / (((EarthSemiMajorAxis * (1d - EarthEccentricitySquared)) / (magic * sqrtMagic)) * Pi);
        dLon = (dLon * 180.0d) / ((EarthSemiMajorAxis / sqrtMagic) * Math.Cos(radLat) * Pi);
        return (dLat, dLon);
    }

    private static double TransformLat(double x, double y)
    {
        double result = -100.0d + 2.0d * x + 3.0d * y + 0.2d * y * y + 0.1d * x * y + 0.2d * Math.Sqrt(Math.Abs(x));
        result += (20.0d * Math.Sin(6.0d * x * Pi) + 20.0d * Math.Sin(2.0d * x * Pi)) * 2.0d / 3.0d;
        result += (20.0d * Math.Sin(y * Pi) + 40.0d * Math.Sin(y / 3.0d * Pi)) * 2.0d / 3.0d;
        result += (160.0d * Math.Sin(y / 12.0d * Pi) + 320.0d * Math.Sin(y * Pi / 30.0d)) * 2.0d / 3.0d;
        return result;
    }

    private static double TransformLon(double x, double y)
    {
        double result = 300.0d + x + 2.0d * y + 0.1d * x * x + 0.1d * x * y + 0.1d * Math.Sqrt(Math.Abs(x));
        result += (20.0d * Math.Sin(6.0d * x * Pi) + 20.0d * Math.Sin(2.0d * x * Pi)) * 2.0d / 3.0d;
        result += (20.0d * Math.Sin(x * Pi) + 40.0d * Math.Sin(x / 3.0d * Pi)) * 2.0d / 3.0d;
        result += (150.0d * Math.Sin(x / 12.0d * Pi) + 300.0d * Math.Sin(x / 30.0d * Pi)) * 2.0d / 3.0d;
        return result;
    }

    private static bool IsOutsideChina(double lat, double lon)
        => lon < 72.004d || lon > 137.8347d || lat < 0.8293d || lat > 55.8271d;

    private static string Normalize(string value)
        => value.Trim().ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
}
