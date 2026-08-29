using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using SonnetDB.Contracts;
using SonnetDB.Json;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Mcp;

/// <summary>
/// SonnetDB MCP typed contract 的版本与输入预算。
/// </summary>
internal static class SonnetDbMcpContract
{
    public const string Version = "1.0";
}

/// <summary>
/// MCP 工具的稳定错误码。
/// </summary>
internal static class SonnetDbMcpErrorCodes
{
    public const string InvalidArgument = "invalid_argument";
    public const string InvalidSql = "invalid_sql";
    public const string ReadOnlyViolation = "read_only_violation";
    public const string MeasurementNotFound = "measurement_not_found";
    public const string SkillNotFound = "skill_not_found";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string RequestCancelled = "request_cancelled";
    public const string OperationFailed = "operation_failed";
}

/// <summary>
/// MCP typed contract 返回体的共同版本字段。
/// </summary>
internal abstract record McpContractResult
{
    /// <summary>
    /// 当前返回体遵循的 MCP 合同版本。
    /// </summary>
    public string ContractVersion => SonnetDbMcpContract.Version;
}

/// <summary>
/// MCP 工具失败时写入文本 content 的稳定 JSON 返回体。
/// </summary>
internal sealed record McpToolErrorResult(string Code, string Message, bool Retryable) : McpContractResult;

/// <summary>
/// MCP 端点返回的 measurement 列描述。
/// </summary>
internal sealed record McpMeasurementColumnResult(string Name, string ColumnType, string DataType);

/// <summary>
/// MCP tool <c>list_measurements</c> 的返回体。
/// </summary>
internal sealed record McpMeasurementListResult(string Database, IReadOnlyList<string> Measurements, bool Truncated)
    : McpContractResult;

/// <summary>
/// MCP tool <c>list_databases</c> 的返回体。
/// </summary>
internal sealed record McpDatabaseListResult(string CurrentDatabase, IReadOnlyList<string> Databases)
    : McpContractResult;

/// <summary>
/// MCP tool/resource 的 measurement schema 返回体。
/// </summary>
internal sealed record McpMeasurementSchemaResult(
    string Database,
    string Measurement,
    IReadOnlyList<McpMeasurementColumnResult> Columns) : McpContractResult;

/// <summary>
/// MCP tool <c>query_sql</c> 的返回体。
/// </summary>
internal sealed record McpSqlQueryResult(
    string Database,
    string StatementType,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<JsonElementValue>> Rows,
    int ReturnedRows,
    bool Truncated) : McpContractResult;

/// <summary>
/// 数据库统计资源返回体。
/// </summary>
internal sealed record McpDatabaseStatsResult(
    string Database,
    int MeasurementCount,
    int SegmentCount,
    long MemTablePointCount,
    long NextSegmentId,
    long CheckpointLsn) : McpContractResult;

/// <summary>
/// MCP tool <c>docs_search</c> 的单条命中（PR #64）。
/// </summary>
internal sealed record McpDocsSearchHit(
    string Source,
    string Title,
    string Section,
    string Content,
    double Score);

/// <summary>
/// MCP tool <c>docs_search</c> 的返回体（PR #64）。
/// </summary>
internal sealed record McpDocsSearchResult(
    string Query,
    int Requested,
    IReadOnlyList<McpDocsSearchHit> Hits) : McpContractResult;

/// <summary>
/// MCP tool <c>skill_search</c> 的单条命中（PR #65）。
/// </summary>
internal sealed record McpSkillSearchHit(
    string Name,
    string Description,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> RequiresTools,
    double Score);

/// <summary>
/// MCP tool <c>skill_search</c> 的返回体（PR #65）。
/// </summary>
internal sealed record McpSkillSearchResult(
    string Query,
    int Requested,
    IReadOnlyList<McpSkillSearchHit> Hits) : McpContractResult;

/// <summary>
/// MCP tool <c>skill_load</c> 的返回体（PR #65）。
/// </summary>
internal sealed record McpSkillLoadResult(
    string Name,
    string Description,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> RequiresTools,
    string Body,
    string Source) : McpContractResult;

/// <summary>
/// MCP tool <c>sample_rows</c> 的返回体。
/// </summary>
internal sealed record McpSampleRowsResult(
    string Database,
    string Measurement,
    int RequestedRows,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<JsonElementValue>> Rows,
    int ReturnedRows,
    bool Truncated) : McpContractResult;

/// <summary>
/// MCP tool <c>explain_sql</c> 的返回体。
/// </summary>
internal sealed record McpExplainSqlResult(
    string Database,
    string StatementType,
    string? Measurement,
    int MatchedSeriesCount,
    int EstimatedSegmentCount,
    int EstimatedBlockCount,
    long EstimatedScannedRows,
    long EstimatedMemTableRows,
    long EstimatedSegmentRows,
    bool HasTimeFilter,
    int TagFilterCount) : McpContractResult;

/// <summary>
/// Copilot <c>draft_sql</c> 工具的返回体：仅做语法/语义校验，不执行写入，
/// 用于让 LLM 与最终回答阶段把可执行的 SQL 安全呈现给用户。
/// </summary>
/// <param name="Database">目标数据库名。</param>
/// <param name="StatementType">语句类型（<c>create_measurement</c>/<c>insert</c>/<c>delete</c>/<c>select</c>/<c>show_measurements</c>/<c>describe_measurement</c>）。</param>
/// <param name="Sql">规范化后的 SQL 文本。</param>
/// <param name="Measurement">语句涉及的 measurement 名称（无法推断时为 <c>null</c>）。</param>
/// <param name="IsWrite">是否为写入类语句（CREATE/INSERT/DELETE）。</param>
/// <param name="MeasurementExists">写入类语句涉及的 measurement 是否已经存在；非写入或不可判定时为 <c>null</c>。</param>
/// <param name="Notes">附加提示（例如安全建议、需要的权限等）。</param>
internal sealed record McpDraftSqlResult(
    string Database,
    string StatementType,
    string Sql,
    string? Measurement,
    bool IsWrite,
    bool? MeasurementExists,
    IReadOnlyList<string> Notes);

/// <summary>
/// Copilot <c>execute_sql</c> 工具的返回体：实际执行单条 SQL，
/// 写入类语句要求当前调用方拥有写权限。
/// </summary>
/// <param name="Database">目标数据库名。</param>
/// <param name="StatementType">语句类型。</param>
/// <param name="Sql">实际执行的 SQL 文本。</param>
/// <param name="Measurement">语句涉及的 measurement 名称。</param>
/// <param name="RowsAffected">写入类语句的影响行数（INSERT 行数 / DELETE 墓碑数 / CREATE 列数；不适用时为 <c>null</c>）。</param>
/// <param name="Columns">SELECT 类语句返回的列名（不适用时为 <c>null</c>）。</param>
/// <param name="Rows">SELECT 类语句返回的行数据（不适用时为 <c>null</c>）。</param>
/// <param name="ReturnedRows">实际返回的行数（不适用时为 <c>null</c>）。</param>
/// <param name="Truncated">SELECT 结果是否被工具的 <c>maxRows</c> 截断。</param>
internal sealed record McpExecuteSqlResult(
    string Database,
    string StatementType,
    string Sql,
    string? Measurement,
    int? RowsAffected,
    IReadOnlyList<string>? Columns,
    IReadOnlyList<IReadOnlyList<JsonElementValue>>? Rows,
    int? ReturnedRows,
    bool Truncated);

/// <summary>
/// MCP 工具/资源的辅助方法。
/// </summary>
internal static class SonnetDbMcpResults
{
    public const int DefaultToolRowLimit = 100;
    public const int MaxToolRowLimit = 1000;
    public const int ResourceRowLimit = 500;
    public const int DefaultSampleRowLimit = 5;
    public const int MaxSampleRowLimit = 100;

    /// <summary>
    /// 校验并规范化工具的最大返回行数。
    /// </summary>
    public static int NormalizeToolRowLimit(int? requestedLimit)
    {
        return NormalizePositiveLimit(requestedLimit, DefaultToolRowLimit, MaxToolRowLimit, "maxRows");
    }

    /// <summary>
    /// 校验并规范化抽样工具的最大返回行数。
    /// </summary>
    public static int NormalizeSampleRowLimit(int? requestedLimit)
    {
        return NormalizePositiveLimit(requestedLimit, DefaultSampleRowLimit, MaxSampleRowLimit, "n");
    }

    /// <summary>
    /// 在 AST 层对 SELECT 施加最大返回行数限制，并保留一行用于判断截断。
    /// </summary>
    public static SelectStatement ApplyToolRowLimit(SelectStatement statement, int maxRows, out bool canTruncate)
    {
        var probeFetch = checked(maxRows + 1);
        if (statement.Pagination is null)
        {
            canTruncate = true;
            return statement with { Pagination = new PaginationSpec(0, probeFetch) };
        }

        if (statement.Pagination.Fetch is null || statement.Pagination.Fetch.Value > maxRows)
        {
            canTruncate = true;
            return statement with { Pagination = statement.Pagination with { Fetch = probeFetch } };
        }

        canTruncate = false;
        return statement;
    }

    /// <summary>
    /// 把查询结果裁剪到指定最大行数。
    /// </summary>
    public static (IReadOnlyList<IReadOnlyList<JsonElementValue>> Rows, bool Truncated) SliceRows(
        SelectExecutionResult result,
        int maxRows,
        bool canTruncate)
    {
        var truncated = canTruncate && result.Rows.Count > maxRows;
        var take = truncated ? maxRows : result.Rows.Count;
        var rows = new List<IReadOnlyList<JsonElementValue>>(take);
        for (int i = 0; i < take; i++)
        {
            var row = result.Rows[i];
            var converted = new JsonElementValue[row.Count];
            for (int c = 0; c < row.Count; c++)
                converted[c] = ToJsonElementValue(row[c]);
            rows.Add(converted);
        }

        return (rows, truncated);
    }

    /// <summary>
    /// 生成成功的 MCP tool 返回值，同时附带文本和 structured content。
    /// </summary>
    public static CallToolResult Success<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        var structured = JsonSerializer.SerializeToElement(value, typeInfo);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = json,
                },
            ],
            StructuredContent = structured,
            IsError = false,
        };
    }

    /// <summary>
    /// 生成失败的 MCP tool 返回值。
    /// </summary>
    public static CallToolResult Error(string code, string message, bool retryable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var payload = new McpToolErrorResult(code, message, retryable);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = message,
                },
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(payload, ServerJsonContext.Default.McpToolErrorResult),
                },
            ],
            IsError = true,
        };
    }

    /// <summary>
    /// 校验来自 MCP 调用方的必填文本和字符预算。
    /// </summary>
    public static void ValidateRequiredText(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
    }

    /// <summary>
    /// 生成 JSON 文本资源。
    /// </summary>
    public static TextResourceContents Resource<T>(string uri, T value, JsonTypeInfo<T> typeInfo)
        => new()
        {
            Uri = uri,
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(value, typeInfo),
        };

    /// <summary>
    /// 把执行结果中的标量值转换成 MCP 友好的 JSON 标量包装。
    /// </summary>
    public static JsonElementValue ToJsonElementValue(object? value) => value switch
    {
        null => new JsonElementValue(ScalarKind.Null),
        string text => new JsonElementValue(ScalarKind.String, StringValue: text),
        bool boolean => new JsonElementValue(ScalarKind.Boolean, BooleanValue: boolean),
        byte number => new JsonElementValue(ScalarKind.Integer, IntegerValue: number),
        sbyte number => new JsonElementValue(ScalarKind.Integer, IntegerValue: number),
        short number => new JsonElementValue(ScalarKind.Integer, IntegerValue: number),
        ushort number => new JsonElementValue(ScalarKind.Integer, IntegerValue: number),
        int number => new JsonElementValue(ScalarKind.Integer, IntegerValue: number),
        uint number => new JsonElementValue(ScalarKind.Integer, IntegerValue: number),
        long number => new JsonElementValue(ScalarKind.Integer, IntegerValue: number),
        ulong number when number <= long.MaxValue => new JsonElementValue(ScalarKind.Integer, IntegerValue: (long)number),
        float number => new JsonElementValue(ScalarKind.Double, DoubleValue: number),
        double number => new JsonElementValue(ScalarKind.Double, DoubleValue: number),
        decimal number => new JsonElementValue(ScalarKind.Double, DoubleValue: (double)number),
        _ => new JsonElementValue(ScalarKind.String, StringValue: value.ToString()),
    };

    private static int NormalizePositiveLimit(int? requestedLimit, int defaultLimit, int maxLimit, string parameterName)
    {
        var limit = requestedLimit ?? defaultLimit;
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(parameterName, limit, $"{parameterName} 必须大于 0。");
        if (limit > maxLimit)
            throw new ArgumentOutOfRangeException(parameterName, limit, $"{parameterName} 不能超过 {maxLimit}。");
        return limit;
    }
}
