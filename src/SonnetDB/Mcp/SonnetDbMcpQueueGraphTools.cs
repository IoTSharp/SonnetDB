using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SonnetDB.Auth;
using SonnetDB.Endpoints;
using SonnetDB.Graphs;
using SonnetDB.Json;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetMQ;

namespace SonnetDB.Mcp;

internal sealed partial class SonnetDbMcpTools
{
    /// <summary>列出绑定数据库限定的实例 MQ 主题；不暴露其它数据库或无数据库前缀主题。</summary>
    [Description("列出绑定数据库限定的实例 MQ 主题；不暴露其它数据库或无数据库前缀主题。")]
    [McpServerTool(Name = "list_mq_topics", Title = "list_mq_topics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelNamesResult))]
    public static Task<CallToolResult> ListMqTopics(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        SonnetMqStore mq, [Range(1, 1000)] int? maxRows = null, string? after = null, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            var prefix = context.GetDatabaseName() + ".";
            return ModelNames(context, "mq", mq.ListTopicStats().Where(stats => stats.Topic.StartsWith(prefix, StringComparison.Ordinal))
                .Select(stats => stats.Topic[prefix.Length..]), maxRows, after, "instance");
        }, ServerJsonContext.Default.McpModelNamesResult, cancellationToken);

    /// <summary>按 offset 只读查看数据库限定 MQ 消息，不创建组、不确认或推进消费状态。</summary>
    [Description("按 offset 只读查看数据库限定 MQ 消息，不创建组、不确认或推进消费状态。")]
    [McpServerTool(Name = "read_mq", Title = "read_mq", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpMqReadResult))]
    public static Task<CallToolResult> ReadMq(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        SonnetMqStore mq, string topic, long offset = 0, [Range(1, 1000)] int? maxRows = null, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            ValidateModelResource(topic);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            var limit = SonnetDbMcpResults.NormalizeToolRowLimit(maxRows);
            var qualified = SonnetDbEndpoints.QualifyMqTopic(context.GetDatabaseName(), topic);
            var messages = new List<McpMqMessage>();
            var next = offset;
            var totalBytes = 0L;
            var truncated = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var index = 0; index <= limit; index++)
            {
                operationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed > TimeSpan.FromSeconds(10))
                    throw new OperationCanceledException();
                var page = mq.Pull(qualified, next, 1);
                if (page.Count == 0)
                    break;
                if (index == limit)
                {
                    truncated = true;
                    break;
                }
                var message = page[0];
                totalBytes += message.Payload.Length;
                if (totalBytes > MaxModelValueBytes * 2)
                    throw new ArgumentException("消息页超过内容预算；请减小 maxRows。");
                messages.Add(new(message.Offset, message.TimestampUtc, EncodeModelBytes(message.Payload), message.Headers));
                next = checked(message.Offset + 1);
            }
            return new McpMqReadResult(context.GetDatabaseName(), topic, messages, next, truncated);
        }, ServerJsonContext.Default.McpMqReadResult, cancellationToken);

    /// <summary>读取数据库限定实例 MQ 主题统计与消费者 offset；不改变消费状态。</summary>
    [Description("读取数据库限定实例 MQ 主题统计与消费者 offset；不改变消费状态。")]
    [McpServerTool(Name = "get_mq_stats", Title = "get_mq_stats", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpMqStatsResult))]
    public static Task<CallToolResult> GetMqStats(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        SonnetMqStore mq, string topic, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            ValidateModelResource(topic);
            var stats = mq.GetStats(SonnetDbEndpoints.QualifyMqTopic(context.GetDatabaseName(), topic));
            if (stats.ConsumerOffsets.Count > SonnetDbMcpResults.MaxToolRowLimit)
                throw new ArgumentException("消费者组超过1000个；请使用原生管理接口。");
            return new McpMqStatsResult(context.GetDatabaseName(), topic, stats.MessageCount, stats.NextOffset, stats.ConsumerOffsets);
        }, ServerJsonContext.Default.McpMqStatsResult, cancellationToken);

    /// <summary>向绑定数据库限定的实例 MQ 主题发布一条 Base64 消息，最多64 KiB；需 Write 权限，不自动重试。</summary>
    [Description("向绑定数据库限定的实例 MQ 主题发布一条 Base64 消息，最多64 KiB；需 Write 权限，不自动重试。")]
    [McpServerTool(Name = "publish_mq", Title = "publish_mq", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> PublishMq(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        SonnetMqStore mq, string topic, string payloadBase64, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, true, operationToken =>
        {
            ValidateModelResource(topic);
            var payload = DecodeModelBytes(payloadBase64);
            var offset = mq.Publish(SonnetDbEndpoints.QualifyMqTopic(context.GetDatabaseName(), topic), payload);
            return new McpModelWriteResult(context.GetDatabaseName(), "mq", topic, Version: offset);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

    /// <summary>确认实例 MQ 中绑定数据库主题的消费者 offset；需 Write 权限，可能推进组状态。</summary>
    [Description("确认实例 MQ 中绑定数据库主题的消费者 offset；需 Write 权限，可能推进组状态。")]
    [McpServerTool(Name = "ack_mq", Title = "ack_mq", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> AckMq(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        SonnetMqStore mq, string topic, string consumerGroup, long offset, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, true, operationToken =>
        {
            ValidateModelResource(topic);
            ValidateModelText(consumerGroup, nameof(consumerGroup));
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            var next = mq.Ack(SonnetDbEndpoints.QualifyMqTopic(context.GetDatabaseName(), topic), consumerGroup, offset);
            return new McpModelWriteResult(context.GetDatabaseName(), "mq", topic, Version: next);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

    /// <summary>列出绑定数据库关系表的目录原名，支持有界分页。</summary>
    [Description("列出绑定数据库关系表的目录原名，支持有界分页。")]
    [McpServerTool(Name = "list_tables", Title = "list_tables", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelNamesResult))]
    public static Task<CallToolResult> ListTables(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        [Range(1, 1000)] int? maxRows = null, string? after = null, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            return ModelNames(context, "relational", context.GetDatabase().Tables.Catalog.Snapshot().Select(table => table.Name), maxRows, after);
        }, ServerJsonContext.Default.McpModelNamesResult, cancellationToken);

    /// <summary>列出绑定数据库原生图名称，支持有界分页；Graph Beta 合同保持不变。</summary>
    [Description("列出绑定数据库原生图名称，支持有界分页；Graph Beta 合同保持不变。")]
    [McpServerTool(Name = "list_graphs", Title = "list_graphs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelNamesResult))]
    public static Task<CallToolResult> ListGraphs(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        [Range(1, 1000)] int? maxRows = null, string? after = null, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            return ModelNames(context, "graph", context.GetDatabase().Graphs.Catalog.Snapshot().Select(graph => graph.Name), maxRows, after);
        }, ServerJsonContext.Default.McpModelNamesResult, cancellationToken);

    /// <summary>读取原生图的一个顶点、标签、属性和元素版本。</summary>
    [Description("读取原生图的一个顶点、标签、属性和元素版本。")]
    [McpServerTool(Name = "get_graph_vertex", Title = "get_graph_vertex", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpGraphVertexResult))]
    public static Task<CallToolResult> GetGraphVertex(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string graph, long vertexId, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            ValidateModelResource(graph);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vertexId);
            using var read = context.GetDatabase().Graphs.Open(graph).BeginRead();
            var vertex = read.GetVertex(new GraphElementId(vertexId));
            return new McpGraphVertexResult(context.GetDatabaseName(), graph, vertex is not null,
                vertex is null ? null : SonnetDbEndpoints.ToDto(vertex));
        }, ServerJsonContext.Default.McpGraphVertexResult, cancellationToken);

    /// <summary>有界读取原生图顶点的一跳出边或入边；支持最多1000条结果。</summary>
    [Description("有界读取原生图顶点的一跳出边或入边；支持最多1000条结果。")]
    [McpServerTool(Name = "expand_graph", Title = "expand_graph", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpGraphExpandResult))]
    public static Task<CallToolResult> ExpandGraph(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string graph, long vertexId, bool incoming = false, [Range(1, 1000)] int? maxRows = null, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            ValidateModelResource(graph);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vertexId);
            var limit = SonnetDbMcpResults.NormalizeToolRowLimit(maxRows);
            using var read = context.GetDatabase().Graphs.Open(graph).BeginRead();
            using var cursor = read.Expand(new GraphElementId(vertexId),
                incoming ? GraphDirection.Incoming : GraphDirection.Outgoing, options: new GraphCursorOptions
                {
                    PageSize = limit + 1,
                    MaxResults = limit + 1,
                    MaxPageBytes = MaxModelResponseBytes,
                });
            var page = cursor.ReadNextPage(operationToken);
            return new McpGraphExpandResult(context.GetDatabaseName(), graph,
                page.Take(limit).Select(SonnetDbEndpoints.ToDto).ToArray(), page.Count > limit || !cursor.IsExhausted);
        }, ServerJsonContext.Default.McpGraphExpandResult, cancellationToken);

    /// <summary>按元素版本写入一个图顶点；需 Write 权限和非空 requestId，未知提交先回读。</summary>
    [Description("按元素版本写入一个图顶点；需 Write 权限和非空 requestId，未知提交先回读。")]
    [McpServerTool(Name = "upsert_graph_vertex", Title = "upsert_graph_vertex", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> UpsertGraphVertex(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string graph, GraphUpsertVertexRequest request, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, true, operationToken =>
        {
            ValidateModelResource(graph);
            ArgumentNullException.ThrowIfNull(request);
            if (request.RequestId == Guid.Empty)
                throw new ArgumentException("requestId 不能为空。");
            ValidateGraphInput(request.Labels, request.Properties, request.UniquePropertyIds, operationToken);
            _ = EncodeBoundedModel(request, ServerJsonContext.Default.GraphUpsertVertexRequest, MaxModelValueBytes);
            var transaction = context.GetDatabase().Graphs.Open(graph).BeginTransaction(request.RequestId);
            transaction.UpsertVertex(new GraphElementId(request.Id), request.ExpectedElementVersion,
                SonnetDbEndpoints.ToLabels(request.Labels ?? []), SonnetDbEndpoints.ToProperties(request.Properties ?? []), request.UniquePropertyIds ?? []);
            var result = transaction.Commit(operationToken);
            return new McpModelWriteResult(context.GetDatabaseName(), "graph", graph, Version: result.Sequence, IsDuplicate: result.IsDuplicate);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

    /// <summary>按元素版本删除一个图顶点；需 Write 权限和非空 requestId。</summary>
    [Description("按元素版本删除一个图顶点；需 Write 权限和非空 requestId。")]
    [McpServerTool(Name = "delete_graph_vertex", Title = "delete_graph_vertex", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> DeleteGraphVertex(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string graph, long id, long expectedElementVersion, Guid requestId, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, true, operationToken =>
        {
            ValidateModelResource(graph);
            if (requestId == Guid.Empty)
                throw new ArgumentException("requestId 不能为空。");
            var transaction = context.GetDatabase().Graphs.Open(graph).BeginTransaction(requestId);
            transaction.DeleteVertex(new GraphElementId(id), expectedElementVersion);
            var result = transaction.Commit(operationToken);
            return new McpModelWriteResult(context.GetDatabaseName(), "graph", graph, Version: result.Sequence, IsDuplicate: result.IsDuplicate);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

    /// <summary>按元素版本写入一个图边；需 Write 权限和非空 requestId，未知提交先回读。</summary>
    [Description("按元素版本写入一个图边；需 Write 权限和非空 requestId，未知提交先回读。")]
    [McpServerTool(Name = "upsert_graph_edge", Title = "upsert_graph_edge", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> UpsertGraphEdge(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string graph, GraphUpsertEdgeRequest request, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, true, operationToken =>
        {
            ValidateModelResource(graph);
            ArgumentNullException.ThrowIfNull(request);
            if (request.RequestId == Guid.Empty)
                throw new ArgumentException("requestId 不能为空。");
            ValidateGraphInput(null, request.Properties, request.UniquePropertyIds, operationToken);
            _ = EncodeBoundedModel(request, ServerJsonContext.Default.GraphUpsertEdgeRequest, MaxModelValueBytes);
            var transaction = context.GetDatabase().Graphs.Open(graph).BeginTransaction(request.RequestId);
            transaction.UpsertEdge(new GraphElementId(request.Id), request.ExpectedElementVersion, new GraphElementId(request.SourceId),
                new GraphElementId(request.TargetId), new LabelId(request.LabelId), SonnetDbEndpoints.ToProperties(request.Properties ?? []), request.UniquePropertyIds ?? []);
            var result = transaction.Commit(operationToken);
            return new McpModelWriteResult(context.GetDatabaseName(), "graph", graph, Version: result.Sequence, IsDuplicate: result.IsDuplicate);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

    /// <summary>按元素版本删除一个图边；需 Write 权限和非空 requestId。</summary>
    [Description("按元素版本删除一个图边；需 Write 权限和非空 requestId。")]
    [McpServerTool(Name = "delete_graph_edge", Title = "delete_graph_edge", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> DeleteGraphEdge(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string graph, long id, long expectedElementVersion, Guid requestId, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, true, operationToken =>
        {
            ValidateModelResource(graph);
            if (requestId == Guid.Empty)
                throw new ArgumentException("requestId 不能为空。");
            var transaction = context.GetDatabase().Graphs.Open(graph).BeginTransaction(requestId);
            transaction.DeleteEdge(new GraphElementId(id), expectedElementVersion);
            var result = transaction.Commit(operationToken);
            return new McpModelWriteResult(context.GetDatabaseName(), "graph", graph, Version: result.Sequence, IsDuplicate: result.IsDuplicate);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

    /// <summary>执行单条受限数据面写 SQL；需 Write 权限。支持基础建表/measurement/文档集合/索引/图及无 RETURNING 的 DML，拒绝控制面、事务、多语句和 INSERT SELECT。</summary>
    [Description("执行单条受限数据面写 SQL；需 Write 权限。支持基础建表/measurement/文档集合/索引/图及无 RETURNING 的 DML，拒绝控制面、事务、多语句和 INSERT SELECT。")]
    [McpServerTool(Name = "execute_sql", Title = "execute_sql", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> ExecuteSql(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string sql, CancellationToken cancellationToken = default)
        => RunModelToolAsync(context, grants, true, operationToken =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);
            if (Encoding.UTF8.GetByteCount(sql) > MaxModelValueBytes)
                throw new ArgumentException("SQL超过64 KiB输入预算。", nameof(sql));
            var statement = SqlParser.Parse(sql);
            var allowed = statement is CreateMeasurementStatement or CreateTableStatement { IsTemporary: false, ModbusBinding: null } or CreateDocumentCollectionStatement
                or CreateFullTextIndexStatement or CreateDocumentVectorIndexStatement or CreateGraphStatement
                or InsertStatement { ReturningColumns.Count: 0, Query: null, Rows.Count: <= 1000 }
                or UpdateStatement { ReturningColumns.Count: 0 }
                or DeleteStatement { ReturningColumns.Count: 0 };
            if (!allowed)
                throw new ArgumentException("execute_sql 不支持此语句；只读查询请使用 query_sql，控制面/事务/多语句/RETURNING 不支持。");
            var result = SqlExecutor.ExecuteStatement(context.GetDatabase(), context.GetDatabaseName(), statement,
                controlPlane: null, transaction: null, new SqlExecutionOptions { CancellationToken = operationToken });
            long? affected = result switch
            {
                RowsAffectedExecutionResult rows => rows.RowsAffected,
                InsertExecutionResult insert => insert.RowsInserted,
                _ => null,
            };
            return Task.FromResult(new McpModelWriteResult(context.GetDatabaseName(), "sql", "sql", RowsAffected: affected));
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

}
