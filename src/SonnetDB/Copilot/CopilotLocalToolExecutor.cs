using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using SonnetDB.Auth;
using SonnetDB.Catalog;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Mcp;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Copilot;

internal sealed class CopilotLocalToolExecutor
{
    private readonly SonnetDbMcpSchemaCache _schemaCache;
    private readonly SonnetDbMcpExplainSqlService _explainSqlService;
    private readonly IControlPlane _controlPlane;
    private readonly TsdbRegistry _registry;

    public CopilotLocalToolExecutor(
        SonnetDbMcpSchemaCache schemaCache,
        SonnetDbMcpExplainSqlService explainSqlService,
        IControlPlane controlPlane,
        TsdbRegistry registry)
    {
        _schemaCache = schemaCache;
        _explainSqlService = explainSqlService;
        _controlPlane = controlPlane;
        _registry = registry;
    }

    public CopilotLocalToolResult Execute(
        CopilotLocalToolContext context,
        CopilotCloudToolCallEvent cloudTool)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cloudTool);

        try
        {
            var tool = ParseToolInvocation(cloudTool);
            var json = ExecuteTool(context, tool);
            using var document = JsonDocument.Parse(json);
            return CopilotLocalToolResult.Success(document.RootElement.Clone(), json);
        }
        catch (SqlExecutionException ex)
        {
            return CopilotLocalToolResult.Failure(
                $"sql_{ex.Phase}_failed",
                ex.Message,
                CreateErrorJson($"sql_{ex.Phase}_failed", ex.Phase, ex.Message, ex.Sql));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return CopilotLocalToolResult.Failure(
                "tool_failed",
                ex.Message,
                CreateErrorJson("tool_failed", "execute", ex.Message, sql: null));
        }
    }

    private string ExecuteTool(CopilotLocalToolContext context, CopilotToolInvocation tool)
        => tool.Name switch
        {
            "list_databases" => SerializeToolResult(
                new McpDatabaseListResult(context.DatabaseName, context.VisibleDatabases),
                ServerJsonContext.Default.McpDatabaseListResult),
            "list_measurements" => ExecuteListMeasurements(context, tool),
            "describe_measurement" => ExecuteDescribeMeasurement(context, tool),
            "sample_rows" => ExecuteSampleRows(context, tool),
            "explain_sql" => ExecuteExplainSql(context, tool),
            "draft_sql" => ExecuteDraftSql(context, tool),
            "query_sql" => ExecuteQuerySql(context, tool),
            "execute_sql" => ExecuteExecuteSql(context, tool),
            _ => throw new InvalidOperationException($"不支持的 Copilot 工具 '{tool.Name}'。")
        };

    private string ExecuteListMeasurements(CopilotLocalToolContext context, CopilotToolInvocation tool)
    {
        var maxRows = tool.MaxRows ?? SonnetDbMcpResults.DefaultToolRowLimit;
        var databaseName = ResolveToolDatabaseName(context, tool);
        var database = RequireToolDatabase(context, tool, "list_measurements", DatabasePermission.Read);
        var measurements = _schemaCache.GetMeasurements(databaseName, database);
        var names = new List<string>(Math.Min(measurements.Count, maxRows));
        for (var i = 0; i < measurements.Count && i < maxRows; i++)
        {
            names.Add(measurements[i]);
        }

        return SerializeToolResult(
            new McpMeasurementListResult(databaseName, names, Truncated: measurements.Count > maxRows),
            ServerJsonContext.Default.McpMeasurementListResult);
    }

    private string ExecuteDescribeMeasurement(CopilotLocalToolContext context, CopilotToolInvocation tool)
    {
        var measurement = tool.Measurement
            ?? throw new InvalidOperationException("describe_measurement 缺少 measurement 参数。");
        var databaseName = ResolveToolDatabaseName(context, tool);
        var database = RequireToolDatabase(context, tool, "describe_measurement", DatabasePermission.Read);
        var payload = _schemaCache.GetMeasurementSchema(databaseName, measurement, database);
        return SerializeToolResult(payload, ServerJsonContext.Default.McpMeasurementSchemaResult);
    }

    private string ExecuteSampleRows(CopilotLocalToolContext context, CopilotToolInvocation tool)
    {
        var measurement = tool.Measurement
            ?? throw new InvalidOperationException("sample_rows 缺少 measurement 参数。");
        var rows = tool.N ?? SonnetDbMcpResults.DefaultSampleRowLimit;
        var databaseName = ResolveToolDatabaseName(context, tool);
        var database = RequireToolDatabase(context, tool, "sample_rows", DatabasePermission.Read);

        var statement = new SelectStatement(
            Projections: [new SelectItem(StarExpression.Instance, Alias: null)],
            Measurement: measurement,
            Where: null,
            GroupBy: [],
            TableValuedFunction: null,
            Pagination: new PaginationSpec(0, checked(rows + 1)));

        var executionResult = SqlExecutor.ExecuteStatement(database, statement);
        if (executionResult is not SelectExecutionResult selectResult)
        {
            throw new InvalidOperationException("sample_rows 未返回结果集。");
        }

        var (resultRows, truncated) = SonnetDbMcpResults.SliceRows(selectResult, rows, canTruncate: true);
        var payload = new McpSampleRowsResult(
            Database: databaseName,
            Measurement: measurement,
            RequestedRows: rows,
            Columns: selectResult.Columns,
            Rows: resultRows,
            ReturnedRows: resultRows.Count,
            Truncated: truncated);
        return SerializeToolResult(payload, ServerJsonContext.Default.McpSampleRowsResult);
    }

    private string ExecuteExplainSql(CopilotLocalToolContext context, CopilotToolInvocation tool)
    {
        var sql = tool.Sql
            ?? throw new InvalidOperationException("explain_sql 缺少 sql 参数。");
        var databaseName = ResolveToolDatabaseName(context, tool);
        var database = RequireToolDatabase(context, tool, "explain_sql", DatabasePermission.Read);
        var statement = ParseSql(sql);
        if (!IsReadOnlyStatement(statement))
        {
            throw new SqlExecutionException(
                sql,
                "validate",
                "explain_sql 仅支持 SELECT、SHOW MEASUREMENTS / SHOW TABLES 与 DESCRIBE [MEASUREMENT|TABLE]。");
        }

        var payload = _explainSqlService.Explain(databaseName, database, statement);
        return SerializeToolResult(payload, ServerJsonContext.Default.McpExplainSqlResult);
    }

    private string ExecuteDraftSql(CopilotLocalToolContext context, CopilotToolInvocation tool)
    {
        var sql = tool.Sql
            ?? throw new InvalidOperationException("draft_sql 缺少 sql 参数。");
        var statement = ParseSql(sql);
        if (!IsDraftableStatement(statement))
        {
            throw new SqlExecutionException(
                sql,
                "validate",
                "draft_sql 仅支持 CREATE DATABASE、CREATE MEASUREMENT、CREATE TABLE、DROP MEASUREMENT、DROP TABLE、INSERT、UPDATE、DELETE、SELECT、SHOW MEASUREMENTS / SHOW TABLES 与 DESCRIBE [MEASUREMENT|TABLE]。");
        }

        var databaseName = ResolveToolDatabaseName(context, tool, statement);
        var database = TryResolveToolDatabase(context, tool);
        var (statementType, measurement, isWrite) = DescribeStatement(statement);
        bool? exists = null;
        var notes = new List<string>(3);

        if (statement is CreateDatabaseStatement createDatabase)
        {
            var alreadyVisible = context.VisibleDatabases.Any(databaseItem =>
                string.Equals(databaseItem, createDatabase.DatabaseName, StringComparison.OrdinalIgnoreCase));
            notes.Add(alreadyVisible
                ? $"数据库 '{createDatabase.DatabaseName}' 已存在，可以直接复用。"
                : $"数据库 '{createDatabase.DatabaseName}' 当前不存在，可以先执行该 CREATE DATABASE 语句创建。");
        }
        else if (database is null)
        {
            notes.Add($"数据库 '{databaseName}' 当前不存在，执行该语句前需要先 CREATE DATABASE {databaseName}。");
        }

        if (isWrite && measurement is not null && database is not null &&
            HasDatabasePermission(context, databaseName, DatabasePermission.Read))
        {
            var existingMeasurement = database.Measurements.TryGet(measurement);
            var existingTable = database.Tables.Catalog.TryGet(measurement);
            var existing = existingMeasurement is not null || existingTable is not null;
            exists = existing;
            switch (statement)
            {
                case CreateMeasurementStatement when existingMeasurement is not null:
                    notes.Add($"measurement '{measurement}' 已经存在；如需追加列，请改用 INSERT 而不是 CREATE。");
                    break;
                case CreateMeasurementStatement when existingMeasurement is null:
                    notes.Add($"measurement '{measurement}' 当前不存在，可以执行该 CREATE 语句创建。");
                    break;
                case CreateTableStatement when existingTable is not null:
                    notes.Add($"关系表 '{measurement}' 已经存在；如需重建，请先确认是否需要 DROP TABLE。");
                    break;
                case CreateTableStatement when existingTable is null:
                    notes.Add($"关系表 '{measurement}' 当前不存在，可以执行该 CREATE TABLE 语句创建。");
                    break;
                case InsertStatement when !existing:
                    notes.Add($"'{measurement}' 尚未创建，执行 INSERT 之前需要先 CREATE MEASUREMENT 或 CREATE TABLE。");
                    break;
                case UpdateStatement when existingTable is null:
                    notes.Add($"关系表 '{measurement}' 不存在，UPDATE 无法执行。");
                    break;
                case DeleteStatement when !existing:
                    notes.Add($"'{measurement}' 不存在，DELETE 不会影响任何数据。");
                    break;
                case DropTableStatement when existingTable is null:
                    notes.Add($"关系表 '{measurement}' 不存在，DROP TABLE 不会删除任何数据。");
                    break;
            }
        }

        if (isWrite)
        {
            notes.Add(statement is CreateDatabaseStatement
                ? (context.AllowWrite && context.CanUseControlPlane
                    ? "当前客户端处于读写模式并具备控制面权限；执行前仍需要本地确认。"
                    : "当前客户端不会自动创建数据库；请在本地 SQL Console 确认后执行。")
                : (context.AllowWrite && HasDatabasePermission(context, databaseName, DatabasePermission.Write)
                    ? "当前客户端处于读写模式并具备写权限；执行前仍需要本地确认。"
                    : "当前凭据没有写权限或未启用读写模式；请切换到具备权限的账号后在本地确认执行。"));
        }

        var payload = new McpDraftSqlResult(
            Database: databaseName,
            StatementType: statementType,
            Sql: sql.Trim(),
            Measurement: measurement,
            IsWrite: isWrite,
            MeasurementExists: exists,
            Notes: notes);
        return SerializeToolResult(payload, ServerJsonContext.Default.McpDraftSqlResult);
    }

    private string ExecuteQuerySql(CopilotLocalToolContext context, CopilotToolInvocation tool)
    {
        var sql = tool.Sql
            ?? throw new InvalidOperationException("query_sql 缺少 sql 参数。");
        var maxRows = tool.MaxRows ?? SonnetDbMcpResults.DefaultToolRowLimit;
        var databaseName = ResolveToolDatabaseName(context, tool);
        var database = RequireToolDatabase(context, tool, "query_sql", DatabasePermission.Read);
        var statement = ParseSql(sql);
        if (!IsReadOnlyStatement(statement))
        {
            throw new SqlExecutionException(
                sql,
                "validate",
                "query_sql 仅支持 SELECT、SHOW MEASUREMENTS / SHOW TABLES、DESCRIBE [MEASUREMENT|TABLE] 与 EXPLAIN。");
        }

        SqlStatement executable = statement;
        var canTruncate = false;
        if (statement is SelectStatement select)
        {
            executable = SonnetDbMcpResults.ApplyToolRowLimit(select, maxRows, out canTruncate);
        }

        object? executionResult;
        try
        {
            executionResult = SqlExecutor.ExecuteStatement(database, executable);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            throw new SqlExecutionException(sql, "execute", ex.Message, ex);
        }

        if (executionResult is not SelectExecutionResult selectResult)
        {
            throw new SqlExecutionException(sql, "execute", "只读 SQL 未返回结果集。");
        }

        var (rows, truncated) = SonnetDbMcpResults.SliceRows(selectResult, maxRows, canTruncate);
        var payload = new McpSqlQueryResult(
            databaseName,
            StatementType: GetReadOnlyStatementType(statement),
            Columns: selectResult.Columns,
            Rows: rows,
            ReturnedRows: rows.Count,
            Truncated: truncated);
        return SerializeToolResult(payload, ServerJsonContext.Default.McpSqlQueryResult);
    }

    private string ExecuteExecuteSql(CopilotLocalToolContext context, CopilotToolInvocation tool)
    {
        var sql = tool.Sql
            ?? throw new InvalidOperationException("execute_sql 缺少 sql 参数。");
        var statement = ParseSql(sql);
        if (!IsDraftableStatement(statement))
        {
            throw new SqlExecutionException(
                sql,
                "validate",
                "execute_sql 仅支持 CREATE DATABASE、CREATE MEASUREMENT、CREATE TABLE、DROP MEASUREMENT、DROP TABLE、INSERT、UPDATE、DELETE、SELECT、SHOW MEASUREMENTS / SHOW TABLES 与 DESCRIBE [MEASUREMENT|TABLE]。");
        }

        var databaseName = ResolveToolDatabaseName(context, tool, statement);
        var (statementType, measurement, isWrite) = DescribeStatement(statement);
        if (isWrite && (!context.AllowWrite || !HasDatabasePermission(context, databaseName, DatabasePermission.Write)))
        {
            throw new SqlExecutionException(
                sql,
                "permission",
                $"当前客户端未启用读写模式，或当前凭据对数据库 '{databaseName}' 没有写权限。");
        }

        var maxRows = tool.MaxRows ?? SonnetDbMcpResults.DefaultToolRowLimit;
        SqlStatement executable = statement;
        var canTruncate = false;
        if (statement is SelectStatement selectStatement)
        {
            executable = SonnetDbMcpResults.ApplyToolRowLimit(selectStatement, maxRows, out canTruncate);
        }

        object? executionResult;
        try
        {
            if (statement is CreateDatabaseStatement)
            {
                if (!context.CanUseControlPlane)
                {
                    throw new InvalidOperationException("当前凭据没有控制面权限，无法直接创建数据库。");
                }

                executionResult = SqlExecutor.ExecuteControlPlaneStatement(statement, _controlPlane);
            }
            else
            {
                var database = RequireToolDatabase(context, tool, "execute_sql", isWrite ? DatabasePermission.Write : DatabasePermission.Read);
                executionResult = SqlExecutor.ExecuteStatement(database, executable);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            throw new SqlExecutionException(sql, "execute", ex.Message, ex);
        }

        IReadOnlyList<string>? columns = null;
        IReadOnlyList<IReadOnlyList<JsonElementValue>>? rows = null;
        int? returnedRows = null;
        int? rowsAffected = null;
        var truncated = false;

        switch (executionResult)
        {
            case SelectExecutionResult selectResult:
                var (rowList, isTruncated) = SonnetDbMcpResults.SliceRows(selectResult, maxRows, canTruncate);
                columns = selectResult.Columns;
                rows = rowList;
                returnedRows = rowList.Count;
                truncated = isTruncated;
                break;
            case InsertExecutionResult insertResult:
                rowsAffected = insertResult.RowsInserted;
                break;
            case DeleteExecutionResult deleteResult:
                rowsAffected = deleteResult.TombstonesAdded;
                break;
            case RowsAffectedExecutionResult affectedResult:
                rowsAffected = affectedResult.RowsAffected;
                break;
            case MeasurementSchema schema:
                rowsAffected = schema.Columns.Count;
                break;
            case TableSchema schema:
                rowsAffected = schema.Columns.Count;
                break;
            case int affected when statement is CreateDatabaseStatement:
                rowsAffected = affected;
                break;
        }

        var payload = new McpExecuteSqlResult(
            Database: databaseName,
            StatementType: statementType,
            Sql: sql.Trim(),
            Measurement: measurement,
            RowsAffected: rowsAffected,
            Columns: columns,
            Rows: rows,
            ReturnedRows: returnedRows,
            Truncated: truncated);
        return SerializeToolResult(payload, ServerJsonContext.Default.McpExecuteSqlResult);
    }

    private Tsdb RequireToolDatabase(
        CopilotLocalToolContext context,
        CopilotToolInvocation tool,
        string toolName,
        DatabasePermission requiredPermission)
    {
        var databaseName = ResolveToolDatabaseName(context, tool);
        if (!HasDatabasePermission(context, databaseName, requiredPermission))
        {
            throw new InvalidOperationException($"当前凭据对数据库 '{databaseName}' 没有 {requiredPermission.ToString().ToLowerInvariant()} 权限。");
        }

        return TryResolveToolDatabase(context, tool)
            ?? throw new InvalidOperationException($"工具 {toolName} 需要数据库上下文，但数据库 '{databaseName}' 当前不存在或不可用。");
    }

    private Tsdb? TryResolveToolDatabase(CopilotLocalToolContext context, CopilotToolInvocation tool)
    {
        if (!string.IsNullOrWhiteSpace(tool.Database))
        {
            if (context.Database is not null &&
                string.Equals(context.DatabaseName, tool.Database, StringComparison.OrdinalIgnoreCase))
            {
                return context.Database;
            }

            return _registry.TryGet(tool.Database, out var explicitDatabase) ? explicitDatabase : null;
        }

        return context.Database;
    }

    private static string ResolveToolDatabaseName(
        CopilotLocalToolContext context,
        CopilotToolInvocation tool,
        SqlStatement? statement = null)
    {
        if (!string.IsNullOrWhiteSpace(tool.Database))
        {
            return tool.Database.Trim();
        }

        if (statement is CreateDatabaseStatement createDatabase)
        {
            return createDatabase.DatabaseName;
        }

        return context.DatabaseName;
    }

    private static bool HasDatabasePermission(
        CopilotLocalToolContext context,
        string database,
        DatabasePermission required)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            return false;
        }

        var permission = DatabaseAccessEvaluator.GetEffectivePermission(
            context.HttpContext,
            context.GrantsStore,
            database);
        return DatabaseAccessEvaluator.HasPermission(permission, required);
    }

    private static CopilotToolInvocation ParseToolInvocation(CopilotCloudToolCallEvent tool)
    {
        var name = tool.Name.Trim().ToLowerInvariant();
        string? database = null;
        string? measurement = null;
        string? sql = null;
        int? maxRows = tool.MaxRows;
        int? n = null;

        if (tool.Arguments.ValueKind == JsonValueKind.Object)
        {
            database = GetString(tool.Arguments, "database");
            measurement = GetString(tool.Arguments, "measurement") ?? GetString(tool.Arguments, "name");
            sql = GetString(tool.Arguments, "sql");
            maxRows ??= GetInt(tool.Arguments, "maxRows");
            n = GetInt(tool.Arguments, "n");
        }

        return new CopilotToolInvocation(
            name,
            maxRows,
            n,
            string.IsNullOrWhiteSpace(measurement) ? null : measurement.Trim(),
            string.IsNullOrWhiteSpace(sql) ? null : sql.Trim(),
            string.IsNullOrWhiteSpace(database) ? null : database.Trim());
    }

    private static string? GetString(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static SqlStatement ParseSql(string sql)
    {
        try
        {
            return SqlParser.Parse(sql);
        }
        catch (SqlParseException ex)
        {
            throw new SqlExecutionException(sql, "parse", ex.Message, ex);
        }
    }

    private static (string StatementType, string? Measurement, bool IsWrite) DescribeStatement(SqlStatement statement)
        => statement switch
        {
            CreateDatabaseStatement createDatabase => ("create_database", createDatabase.DatabaseName, true),
            CreateMeasurementStatement create => ("create_measurement", create.Name, true),
            InsertStatement insert => ("insert", insert.Measurement, true),
            DeleteStatement delete => ("delete", delete.Measurement, true),
            CreateTableStatement createTable => ("create_table", createTable.Name, true),
            DropMeasurementStatement dropMeasurement => ("drop_measurement", dropMeasurement.Name, true),
            DropTableStatement dropTable => ("drop_table", dropTable.Name, true),
            UpdateStatement update => ("update", update.TableName, true),
            SelectStatement select => ("select", select.Measurement, false),
            ShowMeasurementsStatement => ("show_measurements", null, false),
            ShowTablesStatement => ("show_tables", null, false),
            DescribeMeasurementStatement describe => ("describe_measurement", describe.Name, false),
            DescribeTableStatement describeTable => ("describe_table", describeTable.Name, false),
            _ => ("unknown", null, false)
        };

    private static bool IsDraftableStatement(SqlStatement statement)
        => statement is CreateDatabaseStatement
            or CreateMeasurementStatement
            or CreateTableStatement
            or DropMeasurementStatement
            or DropTableStatement
            or InsertStatement
            or UpdateStatement
            or DeleteStatement
            or SelectStatement
            or ShowMeasurementsStatement
            or ShowTablesStatement
            or DescribeMeasurementStatement
            or DescribeTableStatement;

    private static bool IsReadOnlyStatement(SqlStatement statement)
        => statement is SelectStatement
            or ShowMeasurementsStatement
            or ShowTablesStatement
            or DescribeMeasurementStatement
            or DescribeTableStatement
            or ExplainStatement;

    private static string GetReadOnlyStatementType(SqlStatement statement)
        => statement switch
        {
            SelectStatement => "select",
            ShowMeasurementsStatement => "show_measurements",
            ShowTablesStatement => "show_tables",
            DescribeMeasurementStatement => "describe_measurement",
            DescribeTableStatement => "describe_table",
            ExplainStatement => "explain",
            _ => "unknown"
        };

    private static string SerializeToolResult<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Serialize(value, typeInfo);

    private static string CreateErrorJson(string errorCode, string phase, string message, string? sql)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("error", errorCode);
            writer.WriteString("phase", phase);
            writer.WriteString("message", message);
            if (!string.IsNullOrWhiteSpace(sql))
            {
                writer.WriteString("sql", sql);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}

internal sealed record CopilotLocalToolContext(
    HttpContext HttpContext,
    GrantsStore GrantsStore,
    string DatabaseName,
    Tsdb? Database,
    IReadOnlyList<string> VisibleDatabases,
    bool AllowWrite,
    bool CanUseControlPlane);

internal sealed record CopilotLocalToolResult(
    bool Ok,
    JsonElement Content,
    string? ErrorCode,
    string? ErrorMessage,
    string ResultJson)
{
    public static CopilotLocalToolResult Success(JsonElement content, string resultJson)
        => new(true, content, null, null, resultJson);

    public static CopilotLocalToolResult Failure(
        string errorCode,
        string errorMessage,
        string resultJson)
    {
        using var document = JsonDocument.Parse(resultJson);
        return new(false, document.RootElement.Clone(), errorCode, errorMessage, resultJson);
    }
}
