using SonnetDB.Engine;

namespace SonnetDB.Mcp;

/// <summary>
/// MCP schema 读取结果按持久化 revision 失效，并有 30 秒兜底过期时间。
/// </summary>
internal sealed class SonnetDbMcpSchemaCache
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CacheEntry<McpMeasurementListSnapshot>> _measurementListCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CacheEntry<McpMeasurementSchemaResult>> _measurementSchemaCache = new(StringComparer.Ordinal);

    public SonnetDbMcpSchemaCache()
        : this(TimeProvider.System)
    {
    }

    internal SonnetDbMcpSchemaCache(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 获取当前数据库的 measurement 名称快照。
    /// </summary>
    public McpMeasurementListSnapshot GetMeasurements(string databaseName, Tsdb tsdb)
    {
        ArgumentException.ThrowIfNullOrEmpty(databaseName);
        ArgumentNullException.ThrowIfNull(tsdb);

        string revision = tsdb.MeasurementSchemaRevision;
        return GetOrCreate(
            _measurementListCache,
            $"measurements::{databaseName}",
            revision,
            () =>
            {
                var snapshot = tsdb.GetMeasurementSchemaSnapshot();
                return (
                    new McpMeasurementListSnapshot(
                        snapshot.Measurements.Select(static measurement => measurement.Name).ToArray(),
                        snapshot.Revision),
                    snapshot.Revision);
            });
    }

    /// <summary>
    /// 获取指定 measurement 的 schema 快照。
    /// </summary>
    public McpMeasurementSchemaResult GetMeasurementSchema(string databaseName, string measurementName, Tsdb tsdb)
    {
        ArgumentException.ThrowIfNullOrEmpty(databaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(measurementName);
        ArgumentNullException.ThrowIfNull(tsdb);

        string revision = tsdb.MeasurementSchemaRevision;
        return GetOrCreate(
            _measurementSchemaCache,
            $"measurement::{databaseName}::{measurementName}",
            revision,
            () =>
            {
                var snapshot = tsdb.GetMeasurementSchemaSnapshot();
                var schema = snapshot.Measurements.FirstOrDefault(measurement =>
                    string.Equals(measurement.Name, measurementName, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"measurement '{measurementName}' 不存在。");

                var columns = new List<McpMeasurementColumnResult>(schema.Columns.Count);
                foreach (var column in schema.Columns)
                {
                    columns.Add(new McpMeasurementColumnResult(
                        Name: column.Name,
                        ColumnType: column.Role == SonnetDB.Catalog.MeasurementColumnRole.Tag ? "tag" : "field",
                        DataType: FormatColumnDataType(column)));
                }

                return (
                    new McpMeasurementSchemaResult(
                        databaseName, schema.Name, columns, snapshot.Revision),
                    snapshot.Revision);
            });
    }

    private T GetOrCreate<T>(
        Dictionary<string, CacheEntry<T>> cache,
        string key,
        string revision,
        Func<(T Value, string Revision)> valueFactory)
    {
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            if (cache.TryGetValue(key, out var cached)
                && cached.ExpiresAt > now
                && string.Equals(cached.Revision, revision, StringComparison.Ordinal))
                return cached.Value;

            var (value, valueRevision) = valueFactory();
            cache[key] = new CacheEntry<T>(value, valueRevision, now + CacheTtl);
            return value;
        }
    }

    private static string FormatColumnDataType(SonnetDB.Catalog.MeasurementColumn column)
    {
        if (column.DataType == SonnetDB.Storage.Format.FieldType.Vector && column.VectorDimension is int dimension)
            return $"vector({dimension})";

        return column.DataType switch
        {
            SonnetDB.Storage.Format.FieldType.Float64 => "float64",
            SonnetDB.Storage.Format.FieldType.Int64 => "int64",
            SonnetDB.Storage.Format.FieldType.Boolean => "boolean",
            SonnetDB.Storage.Format.FieldType.String => "string",
            SonnetDB.Storage.Format.FieldType.Vector => "vector",
            _ => column.DataType.ToString().ToLowerInvariant(),
        };
    }

    private sealed record CacheEntry<T>(T Value, string Revision, DateTimeOffset ExpiresAt);
}

internal sealed record McpMeasurementListSnapshot(IReadOnlyList<string> Measurements, string SchemaRevision);
