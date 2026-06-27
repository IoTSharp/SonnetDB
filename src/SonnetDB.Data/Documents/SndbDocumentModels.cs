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
    int Skip = 0);

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
    int Skip = 0);

internal sealed record DocumentItemResponse(string Id, JsonElement Document, long Version);

internal sealed record DocumentFindResponse(
    string Collection,
    IReadOnlyList<DocumentItemResponse> Documents,
    int Count,
    int? Limit,
    int Skip);

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
