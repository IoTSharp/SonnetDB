using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Json;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Format;

namespace SonnetDB.Endpoints;

/// <summary>
/// 地理空间 REST 端点处理逻辑。
/// </summary>
internal static class GeoEndpointHandler
{
    /// <summary>
    /// 输出指定 measurement 的轨迹 GeoJSON。
    /// </summary>
    public static async Task HandleTrajectoryAsync(HttpContext context, Tsdb tsdb, string measurement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tsdb);

        var schema = tsdb.Measurements.TryGet(measurement);
        if (schema is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "measurement_not_found",
                $"Measurement '{measurement}' 不存在。").ConfigureAwait(false);
            return;
        }

        var geoColumn = ResolveGeoPointColumn(schema, context.Request.Query["field"].ToString());
        if (geoColumn is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "geopoint_field_not_found",
                $"Measurement '{measurement}' 未找到 GEOPOINT FIELD 列。").ConfigureAwait(false);
            return;
        }

        var rangeResult = await TryParseRangeAsync(context).ConfigureAwait(false);
        if (rangeResult is null)
            return;
        var (from, to) = rangeResult.Value;

        string format = context.Request.Query["format"].ToString();
        bool asLineString = string.Equals(format, "linestring", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(format) && !asLineString && !string.Equals(format, "points", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "bad_request",
                "format 仅支持 points 或 linestring。").ConfigureAwait(false);
            return;
        }

        var tagFilter = BuildTagFilter(schema, context.Request.Query);
        var seriesList = tsdb.Catalog.Find(measurement, tagFilter);
        var trajectories = ReadTrajectories(tsdb, seriesList, geoColumn.Name, new TimeRange(from, to));

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/geo+json; charset=utf-8";
        await using var writer = new Utf8JsonWriter(context.Response.BodyWriter,
            new JsonWriterOptions { Indented = false });
        if (asLineString)
            WriteLineStringCollection(writer, trajectories);
        else
            WritePointFeatureCollection(writer, trajectories);
        await writer.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static MeasurementColumn? ResolveGeoPointColumn(MeasurementSchema schema, string requestedField)
    {
        if (!string.IsNullOrWhiteSpace(requestedField))
        {
            var col = schema.TryGetColumn(requestedField);
            return col is { Role: MeasurementColumnRole.Field, DataType: FieldType.GeoPoint } ? col : null;
        }

        return schema.FieldColumns.FirstOrDefault(c => c.DataType == FieldType.GeoPoint);
    }

    private static async Task<(long From, long To)?> TryParseRangeAsync(HttpContext context)
    {
        long from = 0;
        long to = long.MaxValue;
        string fromText = context.Request.Query["from"].ToString();
        string toText = context.Request.Query["to"].ToString();
        if (!string.IsNullOrWhiteSpace(fromText) && !long.TryParse(fromText, out from))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "bad_request", "from 必须是 Unix 毫秒时间戳。").ConfigureAwait(false);
            return null;
        }
        if (!string.IsNullOrWhiteSpace(toText) && !long.TryParse(toText, out to))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "bad_request", "to 必须是 Unix 毫秒时间戳。").ConfigureAwait(false);
            return null;
        }
        if (from < 0 || to < 0 || from > to)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "bad_request", "时间范围必须满足 0 <= from <= to。").ConfigureAwait(false);
            return null;
        }
        return (from, to);
    }

    private static Dictionary<string, string>? BuildTagFilter(MeasurementSchema schema, IQueryCollection query)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in schema.TagColumns)
        {
            string value = query[tag.Name].ToString();
            if (!string.IsNullOrEmpty(value))
                tags[tag.Name] = value;
        }
        return tags.Count == 0 ? null : tags;
    }

    private static List<TrajectorySeries> ReadTrajectories(
        Tsdb tsdb,
        IReadOnlyList<SeriesEntry> seriesList,
        string fieldName,
        TimeRange range)
    {
        var result = new List<TrajectorySeries>(seriesList.Count);
        foreach (var series in seriesList)
        {
            var samples = new List<(long Timestamp, GeoPoint Point)>();
            foreach (var dp in tsdb.Query.Execute(new PointQuery(series.Id, fieldName, range)))
            {
                if (dp.Value.Type == FieldType.GeoPoint)
                    samples.Add((dp.Timestamp, dp.Value.AsGeoPoint()));
            }
            if (samples.Count > 0)
                result.Add(new TrajectorySeries(series.Tags, samples));
        }
        return result;
    }

    private static void WritePointFeatureCollection(Utf8JsonWriter writer, IReadOnlyList<TrajectorySeries> trajectories)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");
        writer.WritePropertyName("features");
        writer.WriteStartArray();
        foreach (var trajectory in trajectories)
        {
            foreach (var (timestamp, point) in trajectory.Points)
                GeoJsonWriter.WritePointFeature(writer, point, timestamp, trajectory.Tags);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteLineStringCollection(Utf8JsonWriter writer, IReadOnlyList<TrajectorySeries> trajectories)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "FeatureCollection");
        writer.WritePropertyName("features");
        writer.WriteStartArray();
        foreach (var trajectory in trajectories)
            GeoJsonWriter.WriteLineStringFeature(writer, trajectory.Points, trajectory.Tags);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        if (context.Response.HasStarted)
            return;
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new Contracts.ErrorResponse(code, message),
            ServerJsonContext.Default.ErrorResponse,
            context.RequestAborted).ConfigureAwait(false);
    }

    private sealed record TrajectorySeries(
        IReadOnlyDictionary<string, string> Tags,
        IReadOnlyList<(long Timestamp, GeoPoint Point)> Points);
}
