using System.Text.Json;

namespace SonnetDB.Contracts;

/// <summary>
/// 创建文档集合的请求体。
/// </summary>
/// <param name="IfNotExists">集合已存在时是否直接返回 existing 状态。</param>
public sealed record DocumentCollectionCreateRequest(bool IfNotExists = true);

/// <summary>
/// 文档集合生命周期操作响应。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Status">操作结果，例如 <c>created</c> / <c>exists</c> / <c>dropped</c> / <c>missing</c>。</param>
public sealed record DocumentCollectionOperationResponse(string Collection, string Status);

/// <summary>
/// 写入或替换的一条 JSON 文档。
/// </summary>
/// <param name="Id">文档 ID。</param>
/// <param name="Document">JSON 文档主体。</param>
public sealed record DocumentWriteItem(string Id, JsonElement Document);

/// <summary>
/// 批量写入 JSON 文档请求。
/// </summary>
/// <param name="Documents">要写入的文档列表。</param>
public sealed record DocumentInsertManyRequest(IReadOnlyList<DocumentWriteItem> Documents);

/// <summary>
/// 文档查询请求。第一版仅支持按 ID / ID 列表或集合顺序扫描。
/// </summary>
/// <param name="Id">可选单文档 ID。</param>
/// <param name="Ids">可选文档 ID 列表。</param>
/// <param name="Limit">扫描时最多返回的文档数。</param>
/// <param name="Skip">扫描时跳过的文档数。</param>
public sealed record DocumentFindRequest(
    string? Id = null,
    IReadOnlyList<string>? Ids = null,
    int? Limit = null,
    int Skip = 0,
    DocumentFilterContract? Filter = null,
    IReadOnlyList<DocumentProjectionContract>? Projection = null,
    IReadOnlyList<DocumentSortContract>? Sort = null);

/// <summary>
/// Document API 过滤表达式。
/// </summary>
/// <param name="Path">JSON path；也可传 <c>_id</c>、<c>id</c>、<c>document</c>。</param>
/// <param name="Op">操作符：eq/ne/gt/gte/lt/lte/in/nin/exists/contains。</param>
/// <param name="Value">比较值。</param>
/// <param name="And">AND 子表达式列表。</param>
/// <param name="Or">OR 子表达式列表。</param>
/// <param name="Not">NOT 子表达式。</param>
public sealed record DocumentFilterContract(
    string? Path = null,
    string? Op = null,
    JsonElement? Value = null,
    IReadOnlyList<DocumentFilterContract>? And = null,
    IReadOnlyList<DocumentFilterContract>? Or = null,
    DocumentFilterContract? Not = null);

/// <summary>
/// Document API 投影字段。
/// </summary>
/// <param name="Name">输出字段名；为空时从 path 推断。</param>
/// <param name="Path">JSON path；也可传 <c>_id</c>、<c>id</c>、<c>document</c>。</param>
public sealed record DocumentProjectionContract(string? Name = null, string? Path = null);

/// <summary>
/// Document API 排序字段。
/// </summary>
/// <param name="Path">JSON path；也可传 <c>_id</c>、<c>id</c>、<c>document</c>。</param>
/// <param name="Descending">是否降序。</param>
public sealed record DocumentSortContract(string Path, bool Descending = false);

/// <summary>
/// HTTP API 返回的一条 JSON 文档。
/// </summary>
/// <param name="Id">文档 ID。</param>
/// <param name="Document">JSON 文档主体。</param>
/// <param name="Version">底层 KV 版本号。</param>
public sealed record DocumentItemResponse(string Id, JsonElement Document, long Version);

/// <summary>
/// 文档查询响应。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Documents">命中的文档列表。</param>
/// <param name="Count">本次响应返回的文档数量。</param>
/// <param name="Limit">请求携带的 limit。</param>
/// <param name="Skip">请求携带的 skip。</param>
public sealed record DocumentFindResponse(
    string Collection,
    IReadOnlyList<DocumentItemResponse> Documents,
    int Count,
    int? Limit,
    int Skip);

/// <summary>
/// 单文档查询响应。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Found">是否找到文档。</param>
/// <param name="Document">找到时返回的文档。</param>
public sealed record DocumentFindOneResponse(
    string Collection,
    bool Found,
    DocumentItemResponse? Document);

/// <summary>
/// 单文档整体替换请求。
/// </summary>
/// <param name="Id">文档 ID。</param>
/// <param name="Document">新的 JSON 文档主体。</param>
public sealed record DocumentUpdateOneRequest(string Id, JsonElement Document);

/// <summary>
/// 批量整体替换文档请求。第一版不执行 upsert，仅替换已存在文档。
/// </summary>
/// <param name="Documents">要替换的文档列表。</param>
public sealed record DocumentUpdateManyRequest(IReadOnlyList<DocumentWriteItem> Documents);

/// <summary>
/// 单文档删除请求。
/// </summary>
/// <param name="Id">文档 ID。</param>
public sealed record DocumentDeleteOneRequest(string Id);

/// <summary>
/// 批量删除文档请求。
/// </summary>
/// <param name="Ids">要删除的文档 ID 列表。</param>
public sealed record DocumentDeleteManyRequest(IReadOnlyList<string> Ids);

/// <summary>
/// 文档写操作响应。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Inserted">插入或覆盖写入数量。</param>
/// <param name="Matched">更新匹配数量。</param>
/// <param name="Modified">实际替换数量。</param>
/// <param name="Deleted">删除数量。</param>
public sealed record DocumentWriteResponse(
    string Collection,
    int Inserted = 0,
    int Matched = 0,
    int Modified = 0,
    int Deleted = 0);

/// <summary>
/// 文档计数请求。
/// </summary>
/// <param name="Ids">可选文档 ID 列表；为空时统计整个集合。</param>
public sealed record DocumentCountRequest(IReadOnlyList<string>? Ids = null);

/// <summary>
/// 文档计数响应。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Count">文档数量。</param>
public sealed record DocumentCountResponse(string Collection, long Count);

/// <summary>
/// JSON path distinct 请求。
/// </summary>
/// <param name="Path">JSON path 表达式。</param>
/// <param name="Ids">可选文档 ID 列表；为空时扫描整个集合。</param>
/// <param name="Limit">最多返回的 distinct 值数量。</param>
public sealed record DocumentDistinctRequest(
    string Path,
    IReadOnlyList<string>? Ids = null,
    int? Limit = null);

/// <summary>
/// JSON path distinct 响应。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Path">JSON path 表达式。</param>
/// <param name="Values">distinct 标量值列表。</param>
public sealed record DocumentDistinctResponse(
    string Collection,
    string Path,
    IReadOnlyList<JsonElementValue> Values);
