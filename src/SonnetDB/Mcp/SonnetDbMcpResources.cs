using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SonnetDB.Json;

namespace SonnetDB.Mcp;

/// <summary>
/// SonnetDB 服务端的只读 MCP resources。
/// </summary>
[McpServerResourceType]
internal sealed class SonnetDbMcpResources
{
    /// <summary>
    /// 当前数据库 measurement 列表资源。
    /// </summary>
    [McpServerResource(
        UriTemplate = "sonnetdb://schema/measurements",
        Name = "measurements",
        Title = "Measurement List",
        MimeType = "application/json")]
    public static TextResourceContents GetMeasurements(
        SonnetDbMcpContextAccessor contextAccessor,
        SonnetDbMcpSchemaCache schemaCache)
    {
        var databaseName = contextAccessor.GetDatabaseName();
        var tsdb = contextAccessor.GetDatabase();
        var snapshot = schemaCache.GetMeasurements(databaseName, tsdb);
        var measurements = snapshot.Measurements;

        var names = new List<string>(Math.Min(measurements.Count, SonnetDbMcpResults.ResourceRowLimit));
        for (int i = 0; i < measurements.Count && i < SonnetDbMcpResults.ResourceRowLimit; i++)
            names.Add(measurements[i]);

        var payload = new McpMeasurementListResult(
            databaseName,
            names,
            Truncated: measurements.Count > SonnetDbMcpResults.ResourceRowLimit,
            SchemaRevision: snapshot.SchemaRevision);

        return SonnetDbMcpResults.Resource(
            "sonnetdb://schema/measurements",
            payload,
            ServerJsonContext.Default.McpMeasurementListResult);
    }

    /// <summary>
    /// 指定 measurement schema 资源。
    /// </summary>
    [McpServerResource(
        UriTemplate = "sonnetdb://schema/measurement/{name}",
        Name = "measurement_schema",
        Title = "Measurement Schema",
        MimeType = "application/json")]
    public static TextResourceContents GetMeasurementSchema(
        string name,
        SonnetDbMcpContextAccessor contextAccessor,
        SonnetDbMcpSchemaCache schemaCache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var databaseName = contextAccessor.GetDatabaseName();
        var tsdb = contextAccessor.GetDatabase();
        var payload = schemaCache.GetMeasurementSchema(databaseName, name, tsdb);
        return SonnetDbMcpResults.Resource(
            $"sonnetdb://schema/measurement/{name}",
            payload,
            ServerJsonContext.Default.McpMeasurementSchemaResult);
    }

    /// <summary>
    /// 当前数据库统计资源。
    /// </summary>
    [McpServerResource(
        UriTemplate = "sonnetdb://stats/database",
        Name = "database_stats",
        Title = "Database Stats",
        MimeType = "application/json")]
    public static TextResourceContents GetDatabaseStats(SonnetDbMcpContextAccessor contextAccessor)
    {
        var databaseName = contextAccessor.GetDatabaseName();
        var tsdb = contextAccessor.GetDatabase();
        var payload = new McpDatabaseStatsResult(
            databaseName,
            MeasurementCount: tsdb.Measurements.Count,
            SegmentCount: tsdb.Segments.SegmentCount,
            MemTablePointCount: tsdb.MemTable.PointCount,
            NextSegmentId: tsdb.NextSegmentId,
            CheckpointLsn: tsdb.CheckpointLsn);

        return SonnetDbMcpResults.Resource(
            "sonnetdb://stats/database",
            payload,
            ServerJsonContext.Default.McpDatabaseStatsResult);
    }
}
