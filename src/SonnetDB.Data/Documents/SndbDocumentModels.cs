using System.Text.Json;

namespace SonnetDB.Data.Documents;

/// <summary>
/// SonnetDB 文档集合中的一条 JSON 文档。
/// </summary>
/// <param name="Id">文档 ID。</param>
/// <param name="Json">规范化后的 JSON 文本。</param>
/// <param name="Version">底层 KV 版本号。</param>
public sealed record SndbDocument(string Id, string Json, long Version);

/// <summary>
/// 文档查询选项。第一版仅支持按 ID / ID 列表或集合顺序扫描。
/// </summary>
/// <param name="Id">可选单文档 ID。</param>
/// <param name="Ids">可选文档 ID 列表。</param>
/// <param name="Limit">扫描时最多返回的文档数。</param>
/// <param name="Skip">扫描时跳过的文档数。</param>
public sealed record SndbDocumentFindOptions(
    string? Id = null,
    IReadOnlyList<string>? Ids = null,
    int? Limit = null,
    int Skip = 0,
    SndbDocumentFilter? Filter = null,
    IReadOnlyList<SndbDocumentProjection>? Projection = null,
    IReadOnlyList<SndbDocumentSort>? Sort = null,
    string? ContinuationToken = null);

/// <summary>
/// 文档分页查询结果。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Documents">当前页文档。</param>
/// <param name="ContinuationToken">下一页 continuation token；没有更多数据时为 null。</param>
/// <param name="HasMore">是否还有下一页。</param>
/// <param name="BatchSize">本次请求采用的 batch size。</param>
/// <param name="SnapshotVersion">创建 token 时绑定的只读快照版本。</param>
/// <param name="CursorExpiresAtUtc">token 的 UTC 过期时间；没有下一页时为 null。</param>
public sealed record SndbDocumentPage(
    string Collection,
    IReadOnlyList<SndbDocument> Documents,
    string? ContinuationToken,
    bool HasMore,
    int BatchSize,
    long? SnapshotVersion,
    DateTimeOffset? CursorExpiresAtUtc);

/// <summary>
/// 文档客户端过滤表达式。
/// </summary>
/// <param name="Path">JSON path；也可传 <c>_id</c>、<c>id</c>、<c>document</c>。</param>
/// <param name="Op">操作符：eq/ne/gt/gte/lt/lte/in/nin/exists/contains。</param>
/// <param name="Value">比较值。</param>
/// <param name="And">AND 子表达式列表。</param>
/// <param name="Or">OR 子表达式列表。</param>
/// <param name="Not">NOT 子表达式。</param>
public sealed record SndbDocumentFilter(
    string? Path = null,
    string? Op = null,
    JsonElement? Value = null,
    IReadOnlyList<SndbDocumentFilter>? And = null,
    IReadOnlyList<SndbDocumentFilter>? Or = null,
    SndbDocumentFilter? Not = null);

/// <summary>
/// 文档客户端投影字段。
/// </summary>
/// <param name="Name">输出字段名；为空时从 path 推断。</param>
/// <param name="Path">JSON path；也可传 <c>_id</c>、<c>id</c>、<c>document</c>。</param>
public sealed record SndbDocumentProjection(string? Name = null, string? Path = null);

/// <summary>
/// 文档客户端排序字段。
/// </summary>
/// <param name="Path">JSON path；也可传 <c>_id</c>、<c>id</c>、<c>document</c>。</param>
/// <param name="Descending">是否降序。</param>
public sealed record SndbDocumentSort(string Path, bool Descending = false);

/// <summary>
/// 文档写操作结果。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Inserted">插入或覆盖写入数量。</param>
/// <param name="Matched">更新匹配数量。</param>
/// <param name="Modified">实际替换数量。</param>
/// <param name="Deleted">删除数量。</param>
public sealed record SndbDocumentWriteResult(
    string Collection,
    int Inserted,
    int Matched,
    int Modified,
    int Deleted);

/// <summary>
/// 文档 distinct 查询结果。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Path">JSON path 表达式。</param>
/// <param name="Values">distinct 值列表。</param>
public sealed record SndbDocumentDistinctResult(
    string Collection,
    string Path,
    IReadOnlyList<object?> Values);

internal sealed record DocumentCollectionCreateRequest(bool IfNotExists = true);

internal sealed record DocumentCollectionOperationResponse(string Collection, string Status);

internal sealed record DocumentWriteItem(string Id, JsonElement Document);

internal sealed record DocumentInsertManyRequest(IReadOnlyList<DocumentWriteItem> Documents);

internal sealed record DocumentFindRequest(
    string? Id = null,
    IReadOnlyList<string>? Ids = null,
    int? Limit = null,
    int Skip = 0,
    SndbDocumentFilter? Filter = null,
    IReadOnlyList<SndbDocumentProjection>? Projection = null,
    IReadOnlyList<SndbDocumentSort>? Sort = null,
    string? ContinuationToken = null);

internal sealed record DocumentItemResponse(string Id, JsonElement Document, long Version);

internal sealed record DocumentFindResponse(
    string Collection,
    IReadOnlyList<DocumentItemResponse> Documents,
    int Count,
    int? Limit,
    int Skip,
    string? ContinuationToken = null,
    bool HasMore = false,
    int? BatchSize = null,
    long? SnapshotVersion = null,
    DateTimeOffset? CursorExpiresAtUtc = null);

internal sealed record DocumentFindOneResponse(
    string Collection,
    bool Found,
    DocumentItemResponse? Document);

internal sealed record DocumentUpdateOneRequest(string Id, JsonElement Document);

internal sealed record DocumentUpdateManyRequest(IReadOnlyList<DocumentWriteItem> Documents);

internal sealed record DocumentDeleteOneRequest(string Id);

internal sealed record DocumentDeleteManyRequest(IReadOnlyList<string> Ids);

internal sealed record DocumentWriteResponse(
    string Collection,
    int Inserted = 0,
    int Matched = 0,
    int Modified = 0,
    int Deleted = 0);

internal sealed record DocumentCountRequest(IReadOnlyList<string>? Ids = null);

internal sealed record DocumentCountResponse(string Collection, long Count);

internal sealed record DocumentDistinctRequest(
    string Path,
    IReadOnlyList<string>? Ids = null,
    int? Limit = null);

internal sealed record DocumentDistinctResponse(
    string Collection,
    string Path,
    IReadOnlyList<JsonElementValue> Values);

internal sealed record JsonElementValue(
    ScalarKind Kind,
    string? StringValue = null,
    long? IntegerValue = null,
    double? DoubleValue = null,
    bool? BooleanValue = null);

internal enum ScalarKind
{
    Null = 0,
    String,
    Integer,
    Double,
    Boolean,
}
