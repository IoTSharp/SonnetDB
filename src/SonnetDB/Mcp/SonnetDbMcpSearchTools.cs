using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SonnetDB.Auth;
using SonnetDB.Json;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Mcp;

internal sealed partial class SonnetDbMcpTools
{
    /// <summary>描述文档集合的全文和向量索引，帮助调用方选择搜索参数。</summary>
    [Description("返回文档集合的全文/向量索引名称、字段路径、维度和度量，不构建索引。")]
    [McpServerTool(Name = "describe_collection", Title = "describe_collection", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpCollectionSchemaResult))]
    public static Task<CallToolResult> DescribeCollection(SonnetDbMcpContextAccessor context, GrantsStore grants, string collection, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            ValidateModelResource(collection);
            var schema = context.GetDatabase().Documents.Catalog.TryGet(collection)
                ?? throw new InvalidOperationException("文档集合不存在。");
            var indexes = schema.FullTextIndexes.Select(index => new McpCollectionSearchIndex(index.Name, "fulltext", index.Fields, null, null))
                .Concat(schema.VectorIndexes.Select(index => new McpCollectionSearchIndex(index.Name, "vector", [index.Path], index.Dimensions, index.Metric.ToString().ToLowerInvariant())))
                .Take(SonnetDbMcpResults.MaxToolRowLimit + 1).ToArray();
            if (indexes.Length > SonnetDbMcpResults.MaxToolRowLimit)
                throw new ArgumentException("搜索索引超过1000个；请使用原生管理接口。");
            return new McpCollectionSchemaResult(context.GetDatabaseName(), collection, indexes);
        }, ServerJsonContext.Default.McpCollectionSchemaResult, cancellationToken);

    /// <summary>使用现有全文索引搜索文档，返回文档 ID，正文另行读取。</summary>
    [Description("在文档集合的现有全文索引上搜索；返回有界文档ID，可用get_document读取正文。")]
    [McpServerTool(Name = "search_fulltext", Title = "search_fulltext", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpSqlQueryResult))]
    public static Task<CallToolResult> SearchFulltext(SonnetDbMcpContextAccessor context, GrantsStore grants,
        string collection, string index, string path, string query, [Range(1, 1000)] int? maxRows = null, CancellationToken cancellationToken = default)
        => RunModelToolAsync(context, grants, false, operationToken =>
        {
            ValidateModelResource(collection);
            ValidateModelResource(index);
            var limit = SonnetDbMcpResults.NormalizeToolRowLimit(maxRows);
            var sql = "SELECT id FROM " + QuotedIdentifier(collection) + " WHERE match(" + QuotedIdentifier(index) + ", "
                + QuotedLiteral(path) + ", " + QuotedLiteral(query) + ", " + (limit + 1).ToString(CultureInfo.InvariantCulture) + ")";
            return Task.FromResult(ExecuteModelSearch(context, sql, limit, operationToken));
        }, ServerJsonContext.Default.McpSqlQueryResult, cancellationToken);

    /// <summary>按显式向量在文档集合中搜索，返回前 k 个文档 ID。</summary>
    [Description("按显式float向量搜索文档，最多4096维、k为1..1000；不调用embedding provider，不写入索引。")]
    [McpServerTool(Name = "search_vector", Title = "search_vector", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpSqlQueryResult))]
    public static Task<CallToolResult> SearchVector(SonnetDbMcpContextAccessor context, GrantsStore grants,
        string collection, string path, float[] vector, [Range(1, 1000)] int k = 5, string metric = "cosine", CancellationToken cancellationToken = default)
        => RunModelToolAsync(context, grants, false, operationToken =>
        {
            ValidateModelResource(collection);
            ArgumentNullException.ThrowIfNull(vector);
            if (vector.Length is < 1 or > 4096 || vector.Any(value => !float.IsFinite(value)))
                throw new ArgumentException("向量必须包含1..4096个有限float值。", nameof(vector));
            if (k is < 1 or > 1000)
                throw new ArgumentOutOfRangeException(nameof(k));
            if (metric is not ("cosine" or "l2" or "dot"))
                throw new ArgumentException("metric必须为cosine、l2或dot。", nameof(metric));
            var literal = "[" + string.Join(",", vector.Select(value => value.ToString("R", CultureInfo.InvariantCulture))) + "]";
            var sql = "SELECT id FROM vector_search(source => " + QuotedIdentifier(collection)
                + ", vector_field => " + QuotedLiteral(path) + ", vector => " + literal
                + ", k => " + k.ToString(CultureInfo.InvariantCulture) + ", metric => " + QuotedLiteral(metric) + ")";
            return Task.FromResult(ExecuteModelSearch(context, sql, k, operationToken));
        }, ServerJsonContext.Default.McpSqlQueryResult, cancellationToken);

    private static McpSqlQueryResult ExecuteModelSearch(SonnetDbMcpContextAccessor context, string sql, int limit, CancellationToken operationToken)
    {
        var statement = (SelectStatement)SqlParser.Parse(sql);
        var bounded = SonnetDbMcpResults.ApplyToolRowLimit(statement, limit, out var canTruncate);
        var result = SqlExecutor.ExecuteStatement(context.GetDatabase(), context.GetDatabaseName(), bounded,
            controlPlane: null, transaction: null, new SqlExecutionOptions { CancellationToken = operationToken });
        if (result is not SelectExecutionResult selected)
            throw new InvalidOperationException("搜索没有返回结果集。");
        var (rows, truncated) = SonnetDbMcpResults.SliceRows(selected, limit, canTruncate);
        return new(context.GetDatabaseName(), "select", selected.Columns, rows, rows.Count, truncated);
    }
}
