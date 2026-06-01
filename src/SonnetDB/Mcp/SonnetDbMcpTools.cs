using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SonnetDB.Auth;
using SonnetDB.Copilot;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Query.Functions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Mcp;

/// <summary>
/// SonnetDB 服务端的只读 MCP tools。
/// </summary>
[McpServerToolType]
internal sealed class SonnetDbMcpTools
{
    /// <summary>
    /// 列出当前凭据可见的数据库集合。
    /// </summary>
    [McpServerTool(
        Name = "list_databases",
        Title = "List Databases",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    public static CallToolResult ListDatabases(
        SonnetDbMcpContextAccessor contextAccessor,
        TsdbRegistry registry,
        GrantsStore grantsStore)
    {
        try
        {
            var context = contextAccessor.GetHttpContext();
            var currentDatabase = contextAccessor.GetDatabaseName();
            var visibleDatabases = DatabaseAccessEvaluator.GetVisibleDatabases(
                context,
                grantsStore,
                registry.ListDatabases());

            var payload = new McpDatabaseListResult(currentDatabase, visibleDatabases);
            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpDatabaseListResult);
        }
        catch (Exception ex)
        {
            return SonnetDbMcpResults.Error(ex.Message);
        }
    }

    /// <summary>
    /// 执行只读 SQL 查询。仅允许 <c>SELECT</c> / <c>SHOW MEASUREMENTS</c> / <c>SHOW TABLES</c> /
    /// <c>DESCRIBE [MEASUREMENT|TABLE]</c>，并自动限制最大返回行数。
    /// </summary>
    [McpServerTool(
        Name = "query_sql",
        Title = "Query SQL",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    public static CallToolResult QuerySql(
        string sql,
        int? maxRows,
        SonnetDbMcpContextAccessor contextAccessor)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);
            var databaseName = contextAccessor.GetDatabaseName();
            var tsdb = contextAccessor.GetDatabase();
            var normalizedLimit = SonnetDbMcpResults.NormalizeToolRowLimit(maxRows);
            var statement = SqlParser.Parse(sql);

            if (!IsReadOnlyMcpStatement(statement))
                return SonnetDbMcpResults.Error(
                    "query_sql 仅支持 SELECT、SHOW MEASUREMENTS / SHOW TABLES、DESCRIBE [MEASUREMENT|TABLE] 与 EXPLAIN。");

            SqlStatement executable = statement;
            var canTruncate = false;
            if (statement is SelectStatement select)
                executable = SonnetDbMcpResults.ApplyToolRowLimit(select, normalizedLimit, out canTruncate);

            var executionResult = SqlExecutor.ExecuteStatement(tsdb, executable);
            if (executionResult is not SelectExecutionResult selectResult)
                return SonnetDbMcpResults.Error("只读 SQL 未返回结果集。");

            var (rows, truncated) = SonnetDbMcpResults.SliceRows(selectResult, normalizedLimit, canTruncate);
            var payload = new McpSqlQueryResult(
                databaseName,
                StatementType: GetStatementType(statement),
                selectResult.Columns,
                rows,
                rows.Count,
                truncated);

            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpSqlQueryResult);
        }
        catch (Exception ex)
        {
            return SonnetDbMcpResults.Error(ex.Message);
        }
    }

    /// <summary>
    /// 列出当前数据库的全部 measurement 名称。
    /// </summary>
    [McpServerTool(
        Name = "list_measurements",
        Title = "List Measurements",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    public static CallToolResult ListMeasurements(
        int? maxRows,
        SonnetDbMcpContextAccessor contextAccessor,
        SonnetDbMcpSchemaCache schemaCache)
    {
        try
        {
            var databaseName = contextAccessor.GetDatabaseName();
            var tsdb = contextAccessor.GetDatabase();
            var normalizedLimit = SonnetDbMcpResults.NormalizeToolRowLimit(maxRows);
            var measurements = schemaCache.GetMeasurements(databaseName, tsdb);
            var names = new List<string>(Math.Min(measurements.Count, normalizedLimit));
            for (int i = 0; i < measurements.Count && i < normalizedLimit; i++)
                names.Add(measurements[i]);

            var payload = new McpMeasurementListResult(
                databaseName,
                names,
                Truncated: measurements.Count > normalizedLimit);

            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpMeasurementListResult);
        }
        catch (Exception ex)
        {
            return SonnetDbMcpResults.Error(ex.Message);
        }
    }

    /// <summary>
    /// 描述指定 measurement 的列结构。
    /// </summary>
    [McpServerTool(
        Name = "describe_measurement",
        Title = "Describe Measurement",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    public static CallToolResult DescribeMeasurement(
        string name,
        SonnetDbMcpContextAccessor contextAccessor,
        SonnetDbMcpSchemaCache schemaCache)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var databaseName = contextAccessor.GetDatabaseName();
            var tsdb = contextAccessor.GetDatabase();
            var payload = schemaCache.GetMeasurementSchema(databaseName, name, tsdb);
            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpMeasurementSchemaResult);
        }
        catch (Exception ex)
        {
            return SonnetDbMcpResults.Error(ex.Message);
        }
    }

    /// <summary>
    /// 返回指定 measurement 的少量示例行。
    /// </summary>
    [McpServerTool(
        Name = "sample_rows",
        Title = "Sample Rows",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    public static CallToolResult SampleRows(
        string measurement,
        int? n,
        SonnetDbMcpContextAccessor contextAccessor)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(measurement);

            var databaseName = contextAccessor.GetDatabaseName();
            var tsdb = contextAccessor.GetDatabase();
            var normalizedLimit = SonnetDbMcpResults.NormalizeSampleRowLimit(n);

            var statement = new SelectStatement(
                Projections: [new SelectItem(StarExpression.Instance, Alias: null)],
                Measurement: measurement,
                Where: null,
                GroupBy: [],
                TableValuedFunction: null,
                Pagination: new PaginationSpec(0, checked(normalizedLimit + 1)));

            var executionResult = SqlExecutor.ExecuteStatement(tsdb, statement);
            if (executionResult is not SelectExecutionResult selectResult)
                return SonnetDbMcpResults.Error("sample_rows 未返回结果集。");

            var (rows, truncated) = SonnetDbMcpResults.SliceRows(selectResult, normalizedLimit, canTruncate: true);
            var payload = new McpSampleRowsResult(
                Database: databaseName,
                Measurement: measurement,
                RequestedRows: normalizedLimit,
                Columns: selectResult.Columns,
                Rows: rows,
                ReturnedRows: rows.Count,
                Truncated: truncated);

            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpSampleRowsResult);
        }
        catch (Exception ex)
        {
            return SonnetDbMcpResults.Error(ex.Message);
        }
    }

    /// <summary>
    /// 估算一条只读 SQL 将扫描的段数与行数。
    /// </summary>
    [McpServerTool(
        Name = "explain_sql",
        Title = "Explain SQL",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    public static CallToolResult ExplainSql(
        string sql,
        SonnetDbMcpContextAccessor contextAccessor,
        SonnetDbMcpExplainSqlService explainSqlService)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);

            var databaseName = contextAccessor.GetDatabaseName();
            var tsdb = contextAccessor.GetDatabase();
            var statement = SqlParser.Parse(sql);

            if (!IsReadOnlyMcpStatement(statement))
            {
                return SonnetDbMcpResults.Error(
                    "explain_sql 仅支持 SELECT、SHOW MEASUREMENTS / SHOW TABLES 与 DESCRIBE [MEASUREMENT|TABLE]。");
            }

            var payload = explainSqlService.Explain(databaseName, tsdb, statement);
            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpExplainSqlResult);
        }
        catch (Exception ex)
        {
            return SonnetDbMcpResults.Error(ex.Message);
        }
    }

    private static string GetStatementType(SqlStatement statement) => statement switch
    {
        SelectStatement => "select",
        ShowMeasurementsStatement => "show_measurements",
        ShowTablesStatement => "show_tables",
        DescribeMeasurementStatement => "describe_measurement",
        DescribeTableStatement => "describe_table",
        ExplainStatement => "explain",
        _ => "unknown",
    };

    private static bool IsReadOnlyMcpStatement(SqlStatement statement)
        => statement is SelectStatement
            or ShowMeasurementsStatement
            or ShowTablesStatement
            or DescribeMeasurementStatement
            or DescribeTableStatement
            or ExplainStatement;

    /// <summary>
    /// 在 Copilot 知识库 <c>__copilot__.docs</c> 上做向量召回（PR #64）。
    /// 仅当 Copilot 启用且 embedding provider 已就绪时可用。
    /// </summary>
    [McpServerTool(
        Name = "docs_search",
        Title = "Search Copilot Docs",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    public static async Task<CallToolResult> DocsSearchAsync(
        string query,
        int? k,
        DocsSearchService docsSearchService,
        CancellationToken cancellationToken)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(query);
            var requested = k is null or <= 0 ? 5 : Math.Min(k.Value, 50);

            var hits = await docsSearchService.SearchAsync(query, requested, cancellationToken).ConfigureAwait(false);
            var payload = new McpDocsSearchResult(
                Query: query,
                Requested: requested,
                Hits: hits
                    .Select(static h => new McpDocsSearchHit(h.Source, h.Title, h.Section, h.Content, h.Score))
                    .ToArray());

            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpDocsSearchResult);
        }
        catch (Exception ex)
        {
            return SonnetDbMcpResults.Error(ex.Message);
        }
    }

    /// <summary>
    /// 在 Copilot 技能库 <c>__copilot__.skills</c> 上做向量召回（PR #65）。
    /// 返回 top-K 技能的元数据（不含完整 body），由调用方决定是否进一步 <c>skill_load</c>。
    /// </summary>
    [McpServerTool(
        Name = "skill_search",
        Title = "Search Copilot Skills",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    public static async Task<CallToolResult> SkillSearchAsync(
        string query,
        int? k,
        SkillSearchService skillSearchService,
        CancellationToken cancellationToken)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(query);
            var requested = k is null or <= 0 ? 5 : Math.Min(k.Value, 50);

            var hits = await skillSearchService.SearchAsync(query, requested, cancellationToken).ConfigureAwait(false);
            var payload = new McpSkillSearchResult(
                Query: query,
                Requested: requested,
                Hits: hits
                    .Select(static h => new McpSkillSearchHit(h.Name, h.Description, h.Triggers, h.RequiresTools, h.Score))
                    .ToArray());

            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpSkillSearchResult);
        }
        catch (Exception ex)
        {
            return SonnetDbMcpResults.Error(ex.Message);
        }
    }

    /// <summary>
    /// 按名称加载完整的 Copilot 技能 markdown body，供调用方插入到对话上下文中（PR #65）。
    /// </summary>
    [McpServerTool(
        Name = "skill_load",
        Title = "Load Copilot Skill",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    public static CallToolResult SkillLoad(
        string name,
        SkillRegistry skillRegistry)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var skill = skillRegistry.Load(name);
            if (skill is null)
                return SonnetDbMcpResults.Error($"未找到技能 '{name}'。");

            var payload = new McpSkillLoadResult(
                skill.Name,
                skill.Description,
                skill.Triggers,
                skill.RequiresTools,
                skill.Body,
                skill.Source);
            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpSkillLoadResult);
        }
        catch (Exception ex)
        {
            return SonnetDbMcpResults.Error(ex.Message);
        }
    }
}
