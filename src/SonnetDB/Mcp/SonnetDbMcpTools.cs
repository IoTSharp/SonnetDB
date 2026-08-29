using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
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
    [Description("列出当前凭据可见的数据库。返回 MCP typed contract v1 结构，且不会泄露未授权数据库。")]
    [McpServerTool(
        Name = "list_databases",
        Title = "List Databases",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDatabaseListResult))]
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
        catch (Exception)
        {
            return SonnetDbMcpResults.Error(
                SonnetDbMcpErrorCodes.OperationFailed,
                "list_databases 执行失败。");
        }
    }

    /// <summary>
    /// 执行只读 SQL 查询。仅允许 <c>SELECT</c>、只读 <c>SHOW</c> / <c>DESCRIBE</c> 与
    /// <c>EXPLAIN</c>，并自动限制最大返回行数。
    /// </summary>
    [Description("执行一条只读 SonnetDB SQL，并在 AST 层强制结果行上限。写语句和控制面语句会被拒绝。")]
    [McpServerTool(
        Name = "query_sql",
        Title = "Query SQL",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpSqlQueryResult))]
    public static CallToolResult QuerySql(
        [Description("只读 SonnetDB SQL。")]
        string sql,
        SonnetDbMcpContextAccessor contextAccessor,
        [Description("最大返回行数，范围 1..1000；省略时为 100。")]
        [Range(1, SonnetDbMcpResults.MaxToolRowLimit)] int? maxRows = null)
    {
        try
        {
            SonnetDbMcpResults.ValidateRequiredText(sql, nameof(sql));
            var databaseName = contextAccessor.GetDatabaseName();
            var tsdb = contextAccessor.GetDatabase();
            var normalizedLimit = SonnetDbMcpResults.NormalizeToolRowLimit(maxRows);
            var statement = SqlParser.Parse(sql);

            if (!IsReadOnlyMcpStatement(statement))
                return SonnetDbMcpResults.Error(
                    SonnetDbMcpErrorCodes.ReadOnlyViolation,
                    "query_sql 仅支持 SELECT、SHOW MEASUREMENTS / TABLES / VIEWS / MATERIALIZED VIEWS、对应 DESCRIBE 与 EXPLAIN。");

            SqlStatement executable = statement;
            var canTruncate = false;
            if (statement is SelectStatement select)
                executable = SonnetDbMcpResults.ApplyToolRowLimit(select, normalizedLimit, out canTruncate);

            var executionResult = SqlExecutor.ExecuteStatement(tsdb, executable);
            if (executionResult is not SelectExecutionResult selectResult)
                return SonnetDbMcpResults.Error(
                    SonnetDbMcpErrorCodes.OperationFailed,
                    "只读 SQL 未返回结果集。");

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
        catch (SqlParseException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.InvalidSql, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.InvalidArgument, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.OperationFailed, ex.Message);
        }
        catch (Exception)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.OperationFailed, "query_sql 执行失败。");
        }
    }

    /// <summary>
    /// 列出当前数据库的全部 measurement 名称。
    /// </summary>
    [Description("列出当前 MCP endpoint 所绑定数据库中的 measurement 名称，结果有界且按 schema 快照返回。")]
    [McpServerTool(
        Name = "list_measurements",
        Title = "List Measurements",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpMeasurementListResult))]
    public static CallToolResult ListMeasurements(
        SonnetDbMcpContextAccessor contextAccessor,
        SonnetDbMcpSchemaCache schemaCache,
        [Description("最大返回 measurement 数量，范围 1..1000；省略时为 100。")]
        [Range(1, SonnetDbMcpResults.MaxToolRowLimit)] int? maxRows = null)
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
        catch (ArgumentException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception)
        {
            return SonnetDbMcpResults.Error(
                SonnetDbMcpErrorCodes.OperationFailed,
                "list_measurements 执行失败。");
        }
    }

    /// <summary>
    /// 描述指定 measurement 的列结构。
    /// </summary>
    [Description("返回指定 measurement 的 TAG/FIELD 列名与数据类型。")]
    [McpServerTool(
        Name = "describe_measurement",
        Title = "Describe Measurement",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpMeasurementSchemaResult))]
    public static CallToolResult DescribeMeasurement(
        [Description("measurement 名称。")]
        string name,
        SonnetDbMcpContextAccessor contextAccessor,
        SonnetDbMcpSchemaCache schemaCache)
    {
        try
        {
            SonnetDbMcpResults.ValidateRequiredText(name, nameof(name));
            var databaseName = contextAccessor.GetDatabaseName();
            var tsdb = contextAccessor.GetDatabase();
            var payload = schemaCache.GetMeasurementSchema(databaseName, name, tsdb);
            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpMeasurementSchemaResult);
        }
        catch (ArgumentException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.InvalidArgument, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.MeasurementNotFound, ex.Message);
        }
        catch (Exception)
        {
            return SonnetDbMcpResults.Error(
                SonnetDbMcpErrorCodes.OperationFailed,
                "describe_measurement 执行失败。");
        }
    }

    /// <summary>
    /// 返回指定 measurement 的少量示例行。
    /// </summary>
    [Description("返回指定 measurement 的少量有界样例行，用于在生成查询前确认实际列和值类型。")]
    [McpServerTool(
        Name = "sample_rows",
        Title = "Sample Rows",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpSampleRowsResult))]
    public static CallToolResult SampleRows(
        [Description("measurement 名称。")]
        string measurement,
        SonnetDbMcpContextAccessor contextAccessor,
        [Description("样例行数，范围 1..100；省略时为 5。")]
        [Range(1, SonnetDbMcpResults.MaxSampleRowLimit)] int? n = null)
    {
        try
        {
            SonnetDbMcpResults.ValidateRequiredText(
                measurement,
                nameof(measurement));

            var databaseName = contextAccessor.GetDatabaseName();
            var tsdb = contextAccessor.GetDatabase();
            var normalizedLimit = SonnetDbMcpResults.NormalizeSampleRowLimit(n);
            if (tsdb.Measurements.TryGet(measurement) is null)
            {
                return SonnetDbMcpResults.Error(
                    SonnetDbMcpErrorCodes.MeasurementNotFound,
                    $"measurement '{measurement}' 不存在。");
            }

            var statement = new SelectStatement(
                Projections: [new SelectItem(StarExpression.Instance, Alias: null)],
                Measurement: measurement,
                Where: null,
                GroupBy: [],
                TableValuedFunction: null,
                Pagination: new PaginationSpec(0, checked(normalizedLimit + 1)));

            var executionResult = SqlExecutor.ExecuteStatement(tsdb, statement);
            if (executionResult is not SelectExecutionResult selectResult)
                return SonnetDbMcpResults.Error(
                    SonnetDbMcpErrorCodes.OperationFailed,
                    "sample_rows 未返回结果集。");

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
        catch (ArgumentException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.InvalidArgument, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.OperationFailed, ex.Message);
        }
        catch (Exception)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.OperationFailed, "sample_rows 执行失败。");
        }
    }

    /// <summary>
    /// 估算一条只读 SQL 将扫描的段数与行数。
    /// </summary>
    [Description("解释一条只读 SonnetDB SQL 的访问范围和估算扫描成本，不执行该 SQL。")]
    [McpServerTool(
        Name = "explain_sql",
        Title = "Explain SQL",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpExplainSqlResult))]
    public static CallToolResult ExplainSql(
        [Description("待解释的只读 SonnetDB SQL。")]
        string sql,
        SonnetDbMcpContextAccessor contextAccessor,
        SonnetDbMcpExplainSqlService explainSqlService)
    {
        try
        {
            SonnetDbMcpResults.ValidateRequiredText(sql, nameof(sql));

            var databaseName = contextAccessor.GetDatabaseName();
            var tsdb = contextAccessor.GetDatabase();
            var statement = SqlParser.Parse(sql);

            if (!IsReadOnlyMcpStatement(statement))
            {
                return SonnetDbMcpResults.Error(
                    SonnetDbMcpErrorCodes.ReadOnlyViolation,
                    "explain_sql 仅支持 SELECT、SHOW MEASUREMENTS / SHOW TABLES / SHOW VIEWS 与 DESCRIBE [MEASUREMENT|TABLE|VIEW]。");
            }

            var payload = explainSqlService.Explain(databaseName, tsdb, statement);
            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpExplainSqlResult);
        }
        catch (SqlParseException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.InvalidSql, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.InvalidArgument, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.OperationFailed, ex.Message);
        }
        catch (Exception)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.OperationFailed, "explain_sql 执行失败。");
        }
    }

    private static string GetStatementType(SqlStatement statement) => statement switch
    {
        SelectStatement => "select",
        ShowMeasurementsStatement => "show_measurements",
        ShowTablesStatement => "show_tables",
        ShowViewsStatement => "show_views",
        ShowMaterializedViewsStatement => "show_materialized_views",
        DescribeMeasurementStatement => "describe_measurement",
        DescribeTableStatement => "describe_table",
        DescribeViewStatement => "describe_view",
        DescribeMaterializedViewStatement => "describe_materialized_view",
        ExplainStatement => "explain",
        _ => "unknown",
    };

    private static bool IsReadOnlyMcpStatement(SqlStatement statement)
        => statement is SelectStatement
            or ShowMeasurementsStatement
            or ShowTablesStatement
            or ShowViewsStatement
            or ShowMaterializedViewsStatement
            or DescribeMeasurementStatement
            or DescribeTableStatement
            or DescribeViewStatement
            or DescribeMaterializedViewStatement
            or ExplainStatement;

    /// <summary>
    /// 在 Copilot 知识库 <c>__copilot__.docs</c> 上做向量召回（PR #64）。
    /// 仅当 Copilot 启用且 embedding provider 已就绪时可用。
    /// </summary>
    [Description("在 SonnetDB 文档知识库中检索与问题相关的有界片段；需要已就绪的 embedding provider。")]
    [McpServerTool(
        Name = "docs_search",
        Title = "Search Copilot Docs",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDocsSearchResult))]
    public static async Task<CallToolResult> DocsSearchAsync(
        [Description("检索问题。")]
        string query,
        DocsSearchService docsSearchService,
        CancellationToken cancellationToken,
        [Description("建议返回命中数为 1..50；省略或不大于 0 时为 5，大于 50 时按 50 处理。")]
        int? k = null)
    {
        try
        {
            SonnetDbMcpResults.ValidateRequiredText(
                query,
                nameof(query));
            var requested = NormalizeSearchLimit(k);

            var hits = await docsSearchService.SearchAsync(query, requested, cancellationToken).ConfigureAwait(false);
            var payload = new McpDocsSearchResult(
                Query: query,
                Requested: requested,
                Hits: hits
                    .Select(static h => new McpDocsSearchHit(h.Source, h.Title, h.Section, h.Content, h.Score))
                    .ToArray());

            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpDocsSearchResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SonnetDbMcpResults.Error(
                SonnetDbMcpErrorCodes.RequestCancelled,
                "docs_search 已取消。",
                retryable: true);
        }
        catch (ArgumentException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return SonnetDbMcpResults.Error(
                SonnetDbMcpErrorCodes.ProviderUnavailable,
                "docs_search 的 embedding provider 当前不可用。",
                retryable: true);
        }
        catch (Exception)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.OperationFailed, "docs_search 执行失败。");
        }
    }

    /// <summary>
    /// 在 Copilot 技能库 <c>__copilot__.skills</c> 上做向量召回（PR #65）。
    /// 返回 top-K 技能的元数据（不含完整 body），由调用方决定是否进一步 <c>skill_load</c>。
    /// </summary>
    [Description("在 SonnetDB Copilot 技能库中检索技能元数据；需要已就绪的 embedding provider。")]
    [McpServerTool(
        Name = "skill_search",
        Title = "Search Copilot Skills",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpSkillSearchResult))]
    public static async Task<CallToolResult> SkillSearchAsync(
        [Description("检索问题。")]
        string query,
        SkillSearchService skillSearchService,
        CancellationToken cancellationToken,
        [Description("建议返回命中数为 1..50；省略或不大于 0 时为 5，大于 50 时按 50 处理。")]
        int? k = null)
    {
        try
        {
            SonnetDbMcpResults.ValidateRequiredText(
                query,
                nameof(query));
            var requested = NormalizeSearchLimit(k);

            var hits = await skillSearchService.SearchAsync(query, requested, cancellationToken).ConfigureAwait(false);
            var payload = new McpSkillSearchResult(
                Query: query,
                Requested: requested,
                Hits: hits
                    .Select(static h => new McpSkillSearchHit(h.Name, h.Description, h.Triggers, h.RequiresTools, h.Score))
                    .ToArray());

            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpSkillSearchResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SonnetDbMcpResults.Error(
                SonnetDbMcpErrorCodes.RequestCancelled,
                "skill_search 已取消。",
                retryable: true);
        }
        catch (ArgumentException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return SonnetDbMcpResults.Error(
                SonnetDbMcpErrorCodes.ProviderUnavailable,
                "skill_search 的 embedding provider 当前不可用。",
                retryable: true);
        }
        catch (Exception)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.OperationFailed, "skill_search 执行失败。");
        }
    }

    /// <summary>
    /// 按名称加载完整的 Copilot 技能 markdown body，供调用方插入到对话上下文中（PR #65）。
    /// </summary>
    [Description("按精确名称加载一个 SonnetDB Copilot 技能的完整 Markdown 正文。")]
    [McpServerTool(
        Name = "skill_load",
        Title = "Load Copilot Skill",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpSkillLoadResult))]
    public static CallToolResult SkillLoad(
        [Description("技能名称。")]
        string name,
        SkillRegistry skillRegistry)
    {
        try
        {
            SonnetDbMcpResults.ValidateRequiredText(name, nameof(name));
            var skill = skillRegistry.Load(name);
            if (skill is null)
            {
                return SonnetDbMcpResults.Error(
                    SonnetDbMcpErrorCodes.SkillNotFound,
                    $"未找到技能 '{name}'。");
            }

            var payload = new McpSkillLoadResult(
                skill.Name,
                skill.Description,
                skill.Triggers,
                skill.RequiresTools,
                skill.Body,
                skill.Source);
            return SonnetDbMcpResults.Success(payload, ServerJsonContext.Default.McpSkillLoadResult);
        }
        catch (ArgumentException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.OperationFailed, "skill_load 执行失败。");
        }
    }

    internal static int NormalizeSearchLimit(int? requested)
    {
        return requested is null or <= 0 ? 5 : Math.Min(requested.Value, 50);
    }
}
