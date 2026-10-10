using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

/// <summary>Preview 的有界表与 measurement 元数据；不遍历存储目录、备份或索引统计。</summary>
internal static class WorkbenchPreviewMetadata
{
    internal static SchemaResponse Read(Tsdb database, CancellationToken cancellationToken)
    {
        const int maximumItems = 1000;
        if (database.Measurements.Count > maximumItems || database.Tables.Catalog.Count > maximumItems)
            throw new InvalidOperationException("Preview schema 最多允许 1000 个资源及列。");
        int remainingItems = maximumItems;
        long remainingBytes = WorkbenchPreviewSql.MaximumResultBytes;
        void Reserve(params string?[] values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (--remainingItems < 0)
                throw new InvalidOperationException("Preview schema 最多允许 1000 个资源及列。");
            // DTO、字符串、JSON 转义的保守预算；不代表进程物理堆上限。
            remainingBytes -= 1024;
            foreach (string? value in values)
                remainingBytes -= (long)(value?.Length ?? 0) * 8;
            if (remainingBytes < 0)
                throw new InvalidOperationException("Preview schema 超过 4 MiB 预算。");
        }

        var measurements = new List<MeasurementInfo>();
        foreach (var schema in database.Measurements.Snapshot())
        {
            Reserve(schema.Name);
            if (schema.Columns.Count > remainingItems)
                throw new InvalidOperationException("Preview schema 列数超限。");
            var columns = new List<ColumnInfo>();
            foreach (var column in schema.Columns)
            {
                Reserve(column.Name);
                columns.Add(new ColumnInfo(column.Name, column.Role.ToString(), column.DataType.ToString(), column.VectorDimension));
            }
            measurements.Add(new MeasurementInfo(schema.Name, columns));
        }
        var tables = new List<TableInfo>();
        foreach (var schema in database.Tables.Catalog.Snapshot())
        {
            Reserve(schema.Name);
            if (schema.Columns.Count + schema.PrimaryKey.Count > remainingItems)
                throw new InvalidOperationException("Preview schema 列数超限。");
            var columns = new List<TableColumnInfo>();
            foreach (var column in schema.Columns)
            {
                Reserve(column.Name, column.DefaultExpressionSql);
                columns.Add(new TableColumnInfo(column.Name, column.DataType.ToString(), column.IsPrimaryKey,
                    column.IsNullable, column.Ordinal, column.IsRowVersion,
                    column.DecimalPrecision == 0 ? null : column.DecimalPrecision,
                    column.DecimalScale == 0 ? null : column.DecimalScale)
                {
                    IsAutoIncrement = column.IsAutoIncrement,
                    DefaultExpressionSql = column.DefaultExpressionSql,
                });
            }
            foreach (string name in schema.PrimaryKey)
                Reserve(name);
            tables.Add(new TableInfo(schema.Name, columns, schema.PrimaryKey.ToList(), [],
                new DateTimeOffset(schema.CreatedAtUtcTicks, TimeSpan.Zero)));
        }
        return new SchemaResponse(measurements, tables, MeasurementSchemaRevision: database.MeasurementSchemaRevision);
    }

    internal static async Task WriteAsync(HttpContext context, Tsdb database)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (context.Request.Headers["X-SonnetDB-Workbench-Profile"] != "preview-1")
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var response = Read(database, deadline.Token);
            await System.Text.Json.JsonSerializer.SerializeAsync(context.Response.Body, response,
                ServerJsonContext.Default.SchemaResponse, deadline.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
    }
}
