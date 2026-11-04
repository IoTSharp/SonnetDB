using SonnetDB.Documents;
using SonnetDB.Graphs;

namespace SonnetDB.Mcp;

/// <summary>文档集合搜索索引的可发现元数据。</summary>
internal sealed record McpCollectionSearchIndex(string Name, string Kind, IReadOnlyList<string> Paths, int? Dimensions, string? Metric);

/// <summary>文档集合的搜索索引发现结果。</summary>
internal sealed record McpCollectionSchemaResult(string Database, string Collection, IReadOnlyList<McpCollectionSearchIndex> Indexes) : McpContractResult;

/// <summary>模型资源名称的有界分页结果。</summary>
internal sealed record McpModelNamesResult(string Database, string Model, IReadOnlyList<string> Names, bool Truncated, string? NextCursor, string StorageScope = "database") : McpContractResult;

/// <summary>单个键值及其版本和到期时间。</summary>
internal sealed record McpKvValueResult(string Database, string Keyspace, string Key, bool Found, byte[]? ValueBase64, long? Version, DateTimeOffset? ExpiresAtUtc) : McpContractResult;

/// <summary>键值扫描中的单条记录。</summary>
internal sealed record McpKvEntry(string Key, byte[] ValueBase64, long Version, DateTimeOffset? ExpiresAtUtc);

/// <summary>有界键值前缀扫描结果。</summary>
internal sealed record McpKvScanResult(string Database, string Keyspace, IReadOnlyList<McpKvEntry> Entries, bool Truncated, string? NextCursor) : McpContractResult;

/// <summary>写入确认；不表示多模型事务或未知写入可重试。</summary>
internal sealed record McpModelWriteResult(string Database, string Model, string Resource, long? Version = null, long? RowsAffected = null, bool? Removed = null, string? VersionId = null, bool? IsDuplicate = null) : McpContractResult;

/// <summary>对象的精简元数据。</summary>
internal sealed record McpObjectInfo(string Key, long SizeBytes, string ContentType, string ETag, string VersionId);

/// <summary>对象列表分页结果。</summary>
internal sealed record McpObjectListResult(string Database, string Bucket, IReadOnlyList<McpObjectInfo> Objects, bool Truncated, string? NextCursor) : McpContractResult;

/// <summary>对象内容的有界范围读取结果。</summary>
internal sealed record McpObjectReadResult(string Database, string Bucket, string Key, bool Found, byte[]? ValueBase64, long Offset, long TotalLength, bool Truncated, string? VersionId) : McpContractResult;

/// <summary>文档读取或分页结果。</summary>
internal sealed record McpDocumentReadResult(string Database, string Collection, IReadOnlyList<DocumentRow> Documents, bool Truncated, string? NextCursor) : McpContractResult;

/// <summary>队列中的一条只读消息，不含内部数据库限定名称。</summary>
internal sealed record McpMqMessage(long Offset, DateTimeOffset TimestampUtc, byte[] PayloadBase64, IReadOnlyDictionary<string, string>? Headers);

/// <summary>按 offset 读取消息，不创建消费者组或确认消息。</summary>
internal sealed record McpMqReadResult(string Database, string Topic, IReadOnlyList<McpMqMessage> Messages, long NextOffset, bool Truncated, string StorageScope = "instance") : McpContractResult;

/// <summary>数据库限定主题的统计信息。</summary>
internal sealed record McpMqStatsResult(string Database, string Topic, long MessageCount, long NextOffset, IReadOnlyDictionary<string, long> ConsumerOffsets, string StorageScope = "instance") : McpContractResult;

/// <summary>图顶点读取结果。</summary>
internal sealed record McpGraphVertexResult(string Database, string Graph, bool Found, GraphVertexDto? Vertex) : McpContractResult;

/// <summary>图的一跳邻接结果。</summary>
internal sealed record McpGraphExpandResult(string Database, string Graph, IReadOnlyList<GraphExpansionDto> Edges, bool Truncated) : McpContractResult;
