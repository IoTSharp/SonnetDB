using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SonnetDB.Auth;
using SonnetDB.Json;
using SonnetDB.ObjectStorage;

namespace SonnetDB.Mcp;

internal sealed partial class SonnetDbMcpTools
{
    /// <summary>列出绑定数据库的可见 KV keyspace，支持有界名称分页。</summary>
    [Description("列出绑定数据库的可见 KV keyspace，支持有界名称分页。")]
    [McpServerTool(Name = "list_keyspaces", Title = "list_keyspaces", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelNamesResult))]
    public static Task<CallToolResult> ListKeyspaces(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        [Description("最大返回条数，默认100，范围1..1000。"), Range(1, 1000)] int? maxRows = null, [Description("上页返回的名称游标。")] string? after = null, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            return ModelNames(context, "kv", context.GetDatabase().Keyspaces.List(), maxRows, after);
        }, ServerJsonContext.Default.McpModelNamesResult, cancellationToken);

    /// <summary>读取已有 keyspace 的键值、版本和到期时间；内容以 Base64 返回，最多64 KiB。</summary>
    [Description("读取已有 keyspace 的键值、版本和到期时间；内容以 Base64 返回，最多64 KiB。")]
    [McpServerTool(Name = "get_kv", Title = "get_kv", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpKvValueResult))]
    public static Task<CallToolResult> GetKv(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string keyspace, string key, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            ValidateModelText(key, nameof(key));
            var entry = OpenExistingKeyspace(context, keyspace).GetEntryBounded(Encoding.UTF8.GetBytes(key), MaxModelValueBytes, operationToken);
            return new McpKvValueResult(context.GetDatabaseName(), keyspace, key, entry is not null,
                entry is null ? null : EncodeModelBytes(entry.Value.Span), entry?.Version, entry?.ExpiresAtUtc);
        }, ServerJsonContext.Default.McpKvValueResult, cancellationToken);

    /// <summary>有界扫描已有 keyspace 的 UTF-8 键前缀，不创建缺失 keyspace。</summary>
    [Description("有界扫描已有 keyspace 的 UTF-8 键前缀，不创建缺失 keyspace。")]
    [McpServerTool(Name = "scan_kv", Title = "scan_kv", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpKvScanResult))]
    public static Task<CallToolResult> ScanKv(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string keyspace, string prefix = "", [Description("最大返回条数，默认100，范围1..1000。"), Range(1, 1000)] int? maxRows = null,
        string? afterKeyBase64 = null, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            ValidateModelText(prefix, nameof(prefix), allowEmpty: true);
            var limit = SonnetDbMcpResults.NormalizeToolRowLimit(maxRows);
            var prefixBytes = Encoding.UTF8.GetBytes(prefix);
            var after = afterKeyBase64 is null ? [] : DecodeModelBytes(afterKeyBase64);
            if (after.Length > MaxModelTextChars * 3 || after.Length != 0 && !after.AsSpan().StartsWith(prefixBytes))
                throw new ArgumentException("游标必须属于相同 key 前缀。", nameof(afterKeyBase64));
            var page = OpenExistingKeyspace(context, keyspace).ScanPrefixBounded(prefixBytes, after, limit,
                MaxModelValueBytes, MaxModelValueBytes + MaxModelTextChars * 3, operationToken);
            var rows = new List<McpKvEntry>();
            foreach (var entry in page.Entries)
            {
                operationToken.ThrowIfCancellationRequested();
                rows.Add(new(Encoding.UTF8.GetString(entry.Key.Span), EncodeModelBytes(entry.Value.Span), entry.Version, entry.ExpiresAtUtc));
            }
            return new McpKvScanResult(context.GetDatabaseName(), keyspace, rows, page.Truncated,
                page.NextKey is null ? null : Convert.ToBase64String(page.NextKey));
        }, ServerJsonContext.Default.McpKvScanResult, cancellationToken);

    /// <summary>写入一个 KV 键，允许创建 keyspace；需 Write 权限，Base64 内容最多64 KiB。</summary>
    [Description("写入一个 KV 键，允许创建 keyspace；需 Write 权限，Base64 内容最多64 KiB。")]
    [McpServerTool(Name = "put_kv", Title = "put_kv", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> PutKv(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string keyspace, string key, string valueBase64, DateTimeOffset? expiresAtUtc = null, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, true, operationToken =>
        {
            ValidateModelResource(keyspace);
            ValidateModelText(key, nameof(key));
            var bytes = DecodeModelBytes(valueBase64);
            var version = context.GetDatabase().Keyspaces.Open(keyspace).Put(key, bytes, expiresAtUtc);
            return new McpModelWriteResult(context.GetDatabaseName(), "kv", keyspace, Version: version);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

    /// <summary>删除已有 keyspace 中的一个键；需 Write 权限。</summary>
    [Description("删除已有 keyspace 中的一个键；需 Write 权限。")]
    [McpServerTool(Name = "delete_kv", Title = "delete_kv", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> DeleteKv(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string keyspace, string key, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, true, operationToken =>
        {
            ValidateModelText(key, nameof(key));
            var removed = OpenExistingKeyspace(context, keyspace).Delete(key);
            return new McpModelWriteResult(context.GetDatabaseName(), "kv", keyspace, Removed: removed);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

    /// <summary>列出绑定数据库的对象存储桶名称，支持有界分页。</summary>
    [Description("列出绑定数据库的对象存储桶名称，支持有界分页。")]
    [McpServerTool(Name = "list_buckets", Title = "list_buckets", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelNamesResult))]
    public static Task<CallToolResult> ListBuckets(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        [Description("最大返回条数，默认100，范围1..1000。"), Range(1, 1000)] int? maxRows = null, [Description("上页返回的名称游标。")] string? after = null, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            return ModelNames(context, "object", new SndbObjectStore(context.GetDatabase()).ListBuckets().Select(bucket => bucket.Name), maxRows, after);
        }, ServerJsonContext.Default.McpModelNamesResult, cancellationToken);

    /// <summary>按桶、前缀和不透明游标分页列出对象元数据；不读取对象内容。</summary>
    [Description("按桶、前缀和不透明游标分页列出对象元数据；不读取对象内容。")]
    [McpServerTool(Name = "list_objects", Title = "list_objects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpObjectListResult))]
    public static Task<CallToolResult> ListObjects(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string bucket, string prefix = "", [Description("最大返回条数，默认100，范围1..1000。"), Range(1, 1000)] int? maxRows = null, string? continuationToken = null, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            ValidateModelResource(bucket);
            ValidateModelText(prefix, nameof(prefix), allowEmpty: true);
            if (continuationToken is not null)
                ValidateModelText(continuationToken, nameof(continuationToken));
            var limit = SonnetDbMcpResults.NormalizeToolRowLimit(maxRows);
            var page = new SndbObjectStore(context.GetDatabase()).ListObjects(bucket, prefix, limit, continuationToken);
            return new McpObjectListResult(context.GetDatabaseName(), bucket,
                page.Objects.Select(info => new McpObjectInfo(info.Key, info.SizeBytes, info.ContentType, info.ETag, info.VersionId)).ToArray(),
                page.IsTruncated, page.NextContinuationToken);
        }, ServerJsonContext.Default.McpObjectListResult, cancellationToken);

    /// <summary>按字节范围读取对象，最多64 KiB；返回 Base64、版本和截断标识。</summary>
    [Description("按字节范围读取对象，最多64 KiB；返回 Base64、版本和截断标识。")]
    [McpServerTool(Name = "get_object", Title = "get_object", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpObjectReadResult))]
    public static Task<CallToolResult> GetObject(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string bucket, string key, long offset = 0, [Range(1, MaxModelValueBytes)] int maxBytes = MaxModelValueBytes, CancellationToken cancellationToken = default)
        => RunModelToolAsync(context, grants, false, async operationToken =>
        {
            ValidateModelResource(bucket);
            ValidateModelText(key, nameof(key));
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (maxBytes is < 1 or > MaxModelValueBytes)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            var store = new SndbObjectStore(context.GetDatabase());
            var info = store.HeadObject(bucket, key);
            if (info is null)
                return new McpObjectReadResult(context.GetDatabaseName(), bucket, key, false, null, offset, 0, false, null);
            if (offset > info.SizeBytes)
                throw new ArgumentOutOfRangeException(nameof(offset));
            var length = (int)Math.Min(maxBytes, info.SizeBytes - offset);
            if (length == 0)
                return new McpObjectReadResult(context.GetDatabaseName(), bucket, key, true, [], offset, info.SizeBytes, false, info.VersionId);
            var read = store.OpenRead(bucket, key, new SndbObjectRange(offset, length), info.VersionId)
                ?? throw new InvalidOperationException("对象版本已不可读。");
            await using var content = read.Content;
            var bytes = new byte[length];
            await content.ReadExactlyAsync(bytes, operationToken).ConfigureAwait(false);
            return new McpObjectReadResult(context.GetDatabaseName(), bucket, key, true, bytes,
                offset, info.SizeBytes, offset + length < info.SizeBytes, info.VersionId);
        }, ServerJsonContext.Default.McpObjectReadResult, cancellationToken);

    /// <summary>创建绑定数据库的对象存储桶；需 Write 权限。</summary>
    [Description("创建绑定数据库的对象存储桶；需 Write 权限。")]
    [McpServerTool(Name = "create_bucket", Title = "create_bucket", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> CreateBucket(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string bucket, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, true, operationToken =>
        {
            ValidateModelResource(bucket);
            new SndbObjectStore(context.GetDatabase()).CreateBucket(bucket);
            return new McpModelWriteResult(context.GetDatabaseName(), "object", bucket);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

    /// <summary>删除空对象存储桶；有对象的桶由原生合同拒绝，需 Write 权限。</summary>
    [Description("删除空对象存储桶；有对象的桶由原生合同拒绝，需 Write 权限。")]
    [McpServerTool(Name = "delete_bucket", Title = "delete_bucket", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> DeleteBucket(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string bucket, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, true, operationToken =>
        {
            ValidateModelResource(bucket);
            var removed = new SndbObjectStore(context.GetDatabase()).DeleteBucket(bucket);
            return new McpModelWriteResult(context.GetDatabaseName(), "object", bucket, Removed: removed);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

    /// <summary>写入一个对象版本；需 Write 权限，Base64 内容最多64 KiB，未知结果先回读。</summary>
    [Description("写入一个对象版本；需 Write 权限，Base64 内容最多64 KiB，未知结果先回读。")]
    [McpServerTool(Name = "put_object", Title = "put_object", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> PutObject(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string bucket, string key, string valueBase64, string contentType = "application/octet-stream", CancellationToken cancellationToken = default)
        => RunModelToolAsync(context, grants, true, async operationToken =>
        {
            ValidateModelResource(bucket);
            ValidateModelText(key, nameof(key));
            ValidateModelText(contentType, nameof(contentType));
            var bytes = DecodeModelBytes(valueBase64);
            using var stream = new MemoryStream(bytes, writable: false);
            var info = await new SndbObjectStore(context.GetDatabase()).PutObjectAsync(bucket, key, stream,
                contentType, cancellationToken: operationToken).ConfigureAwait(false);
            return new McpModelWriteResult(context.GetDatabaseName(), "object", bucket + "/" + key, VersionId: info.VersionId);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

    /// <summary>按原生版本/删除标记语义删除对象；需 Write 权限。</summary>
    [Description("按原生版本/删除标记语义删除对象；需 Write 权限。")]
    [McpServerTool(Name = "delete_object", Title = "delete_object", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> DeleteObject(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string bucket, string key, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, true, operationToken =>
        {
            ValidateModelResource(bucket);
            ValidateModelText(key, nameof(key));
            var info = new SndbObjectStore(context.GetDatabase()).DeleteObject(bucket, key);
            return new McpModelWriteResult(context.GetDatabaseName(), "object", bucket + "/" + key, VersionId: info.VersionId);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

    /// <summary>列出绑定数据库的文档集合名称；全文/向量文档数据使用相同集合。</summary>
    [Description("列出绑定数据库的文档集合名称；全文/向量文档数据使用相同集合。")]
    [McpServerTool(Name = "list_collections", Title = "list_collections", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelNamesResult))]
    public static Task<CallToolResult> ListCollections(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        [Description("最大返回条数，默认100，范围1..1000。"), Range(1, 1000)] int? maxRows = null, [Description("上页返回的名称游标。")] string? after = null, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            return ModelNames(context, "document", context.GetDatabase().Documents.Catalog.Snapshot().Select(schema => schema.Name), maxRows, after);
        }, ServerJsonContext.Default.McpModelNamesResult, cancellationToken);

    /// <summary>按 ID 读取文档和版本；单条 JSON 最多64 KiB。</summary>
    [Description("按 ID 读取文档和版本；单条 JSON 最多64 KiB。")]
    [McpServerTool(Name = "get_document", Title = "get_document", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpDocumentReadResult))]
    public static Task<CallToolResult> GetDocument(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string collection, string id, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            ValidateModelResource(collection);
            ValidateModelText(id, nameof(id));
            var document = context.GetDatabase().Documents.Open(collection).GetBounded(id, MaxModelValueBytes, operationToken);
            return new McpDocumentReadResult(context.GetDatabaseName(), collection, document is null ? [] : [document], false, null);
        }, ServerJsonContext.Default.McpDocumentReadResult, cancellationToken);

    /// <summary>按条数与原始 JSON 字节预算分页读取文档，返回真实继续位置。</summary>
    [Description("按条数与原始JSON字节预算分页读取文档；超大单文档明确报错，字节截页可用nextCursor继续。")]
    [McpServerTool(Name = "scan_documents", Title = "scan_documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpDocumentReadResult))]
    public static Task<CallToolResult> ScanDocuments(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string collection, [Description("最大返回条数，默认100，范围1..1000。"), Range(1, 1000)] int? maxRows = null, string? afterId = null, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, false, operationToken =>
        {
            ValidateModelResource(collection);
            if (afterId is not null)
                ValidateModelText(afterId, nameof(afterId));
            var limit = SonnetDbMcpResults.NormalizeToolRowLimit(maxRows);
            var page = context.GetDatabase().Documents.Open(collection).ScanAfterBounded(afterId, limit,
                MaxModelValueBytes, MaxModelValueBytes + MaxModelTextChars * 3, operationToken);
            return new McpDocumentReadResult(context.GetDatabaseName(), collection, page.Documents, page.Truncated, page.NextId);
        }, ServerJsonContext.Default.McpDocumentReadResult, cancellationToken);

    /// <summary>按 ID 写入已有集合的 JSON 文档并维护原生索引；需 Write 权限，最多64 KiB。</summary>
    [Description("按 ID 写入已有集合的 JSON 文档并维护原生索引；需 Write 权限，最多64 KiB。")]
    [McpServerTool(Name = "put_document", Title = "put_document", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> PutDocument(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string collection, string id, string json, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, true, operationToken =>
        {
            ValidateModelResource(collection);
            ValidateModelText(id, nameof(id));
            ArgumentNullException.ThrowIfNull(json);
            if (Encoding.UTF8.GetByteCount(json) > MaxModelValueBytes)
                throw new ArgumentException("文档超过64 KiB输入预算。", nameof(json));
            context.GetDatabase().Documents.Open(collection).Upsert(id, json);
            return new McpModelWriteResult(context.GetDatabaseName(), "document", collection);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

    /// <summary>删除已有集合中的一个文档并维护索引；需 Write 权限。</summary>
    [Description("删除已有集合中的一个文档并维护索引；需 Write 权限。")]
    [McpServerTool(Name = "delete_document", Title = "delete_document", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(McpModelWriteResult))]
    public static Task<CallToolResult> DeleteDocument(
        SonnetDbMcpContextAccessor context, GrantsStore grants,
        string collection, string id, CancellationToken cancellationToken = default)
        => RunModelTool(context, grants, true, operationToken =>
        {
            ValidateModelResource(collection);
            ValidateModelText(id, nameof(id));
            var removed = context.GetDatabase().Documents.Open(collection).Delete(id);
            return new McpModelWriteResult(context.GetDatabaseName(), "document", collection, Removed: removed);
        }, ServerJsonContext.Default.McpModelWriteResult, cancellationToken);

}
