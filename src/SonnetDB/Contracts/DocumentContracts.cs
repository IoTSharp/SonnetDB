using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonnetDB.Contracts;

/// <summary>
/// 创建文档集合的请求体。
/// </summary>
/// <param name="IfNotExists">集合已存在时是否直接返回 existing 状态。</param>
/// <param name="Validator">可选集合 validator。</param>
public sealed record DocumentCollectionCreateRequest(
    bool IfNotExists = true,
    DocumentValidatorContract? Validator = null);

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
/// 文档写入错误码常量。
/// </summary>
public static class DocumentWriteErrorCodes
{
    /// <summary>文档 ID 或唯一索引键已存在。</summary>
    public const string DuplicateKey = "duplicate_key";

    /// <summary>写入内容未通过参数、JSON 或更新操作校验。</summary>
    public const string ValidationFailed = "validation_failed";

    /// <summary>调用方声明的预期版本与当前文档版本不一致。</summary>
    public const string WriteConflict = "write_conflict";

    /// <summary>文档或派生索引项超过底层存储允许的大小。</summary>
    public const string DocumentTooLarge = "document_too_large";
}

/// <summary>
/// 文档写入错误或警告级别。
/// </summary>
public static class DocumentWriteErrorSeverity
{
    /// <summary>会阻止写入的错误。</summary>
    public const string Error = "error";

    /// <summary>不会阻止写入的警告。</summary>
    public const string Warning = "warning";
}

/// <summary>
/// 批量写入 JSON 文档请求。
/// </summary>
/// <param name="Documents">要写入的文档列表。</param>
public sealed record DocumentInsertManyRequest(
    IReadOnlyList<DocumentWriteItem> Documents,
    bool Ordered = true);

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
    IReadOnlyList<DocumentSortContract>? Sort = null,
    string? ContinuationToken = null);

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
    int Skip,
    string? ContinuationToken = null,
    bool HasMore = false,
    int? BatchSize = null,
    long? SnapshotVersion = null,
    DateTimeOffset? CursorExpiresAtUtc = null);

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
/// 文档局部更新操作符请求体。
/// </summary>
/// <param name="Set">对应 $set。</param>
/// <param name="Unset">对应 $unset。</param>
/// <param name="Inc">对应 $inc。</param>
/// <param name="Min">对应 $min。</param>
/// <param name="Max">对应 $max。</param>
/// <param name="Rename">对应 $rename。</param>
/// <param name="Push">对应 $push。</param>
/// <param name="Pull">对应 $pull。</param>
/// <param name="AddToSet">对应 $addToSet。</param>
/// <param name="CurrentDate">对应 $currentDate。</param>
public sealed record DocumentUpdateContract(
    IReadOnlyDictionary<string, JsonElement>? Set = null,
    IReadOnlyDictionary<string, JsonElement>? Unset = null,
    IReadOnlyDictionary<string, JsonElement>? Inc = null,
    IReadOnlyDictionary<string, JsonElement>? Min = null,
    IReadOnlyDictionary<string, JsonElement>? Max = null,
    IReadOnlyDictionary<string, string>? Rename = null,
    IReadOnlyDictionary<string, JsonElement>? Push = null,
    IReadOnlyDictionary<string, JsonElement>? Pull = null,
    IReadOnlyDictionary<string, JsonElement>? AddToSet = null,
    IReadOnlyDictionary<string, JsonElement>? CurrentDate = null);

/// <summary>
/// 单文档整体替换或局部更新请求。
/// </summary>
/// <param name="Id">文档 ID；局部更新时可与 <paramref name="Filter"/> 合并。</param>
/// <param name="Document">整体替换时新的 JSON 文档主体。</param>
/// <param name="Filter">局部更新时使用的过滤条件。</param>
/// <param name="Update">局部更新操作符；为空时保持整体替换语义。</param>
/// <param name="Upsert">局部更新未匹配时是否插入新文档。</param>
/// <param name="UpsertId">upsert 插入的新文档 ID；为空时从 <paramref name="Id"/> 或过滤条件推断。</param>
public sealed record DocumentUpdateOneRequest(
    string? Id = null,
    JsonElement? Document = null,
    DocumentFilterContract? Filter = null,
    DocumentUpdateContract? Update = null,
    bool Upsert = false,
    string? UpsertId = null);

/// <summary>
/// 批量整体替换或局部更新文档请求。
/// </summary>
/// <param name="Documents">整体替换时要替换的文档列表。</param>
/// <param name="Filter">局部更新时使用的过滤条件。</param>
/// <param name="Update">局部更新操作符；为空时保持整体替换语义。</param>
/// <param name="Upsert">局部更新未匹配时是否插入新文档。</param>
/// <param name="UpsertId">upsert 插入的新文档 ID；为空时从过滤条件推断。</param>
public sealed record DocumentUpdateManyRequest(
    IReadOnlyList<DocumentWriteItem>? Documents = null,
    DocumentFilterContract? Filter = null,
    DocumentUpdateContract? Update = null,
    bool Upsert = false,
    string? UpsertId = null,
    bool Ordered = true);

/// <summary>
/// 单文档删除请求。
/// </summary>
/// <param name="Id">文档 ID。</param>
public sealed record DocumentDeleteOneRequest(string Id);

/// <summary>
/// 批量删除文档请求。
/// </summary>
/// <param name="Ids">要删除的文档 ID 列表。</param>
public sealed record DocumentDeleteManyRequest(IReadOnlyList<string> Ids, bool Ordered = true);

/// <summary>
/// 文档批量写中的单项错误。
/// </summary>
/// <param name="Index">原始批量请求中的零基序号。</param>
/// <param name="Id">发生错误的文档 ID；请求 ID 无效时为 null。</param>
/// <param name="Code">稳定错误码。</param>
/// <param name="Message">面向调用方的错误说明。</param>
/// <param name="Severity">错误或警告级别。</param>
public sealed record DocumentWriteErrorResponse(
    int Index,
    string? Id,
    string Code,
    string Message,
    string Severity = DocumentWriteErrorSeverity.Error);

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
    int Deleted = 0,
    IReadOnlyList<DocumentWriteErrorResponse>? Errors = null);

/// <summary>
/// 文档集合 validator 请求体。
/// </summary>
/// <param name="Rules">字段校验规则。</param>
/// <param name="ValidationAction">校验失败动作：error 或 warn。</param>
public sealed record DocumentValidatorContract(
    IReadOnlyList<DocumentValidatorRuleContract> Rules,
    string ValidationAction = "error");

/// <summary>
/// 文档集合 validator 字段规则。
/// </summary>
/// <param name="Path">JSON path。</param>
/// <param name="Required">字段是否必填。</param>
/// <param name="Type">单个允许类型。</param>
/// <param name="Types">多个允许类型。</param>
/// <param name="Minimum">数值下界。</param>
/// <param name="Maximum">数值上界。</param>
/// <param name="Enum">允许的枚举值。</param>
/// <param name="Pattern">字符串正则表达式。</param>
public sealed record DocumentValidatorRuleContract(
    string Path,
    bool Required = false,
    string? Type = null,
    IReadOnlyList<string>? Types = null,
    double? Minimum = null,
    double? Maximum = null,
    IReadOnlyList<JsonElement>? Enum = null,
    string? Pattern = null);

/// <summary>
/// 文档集合 validator 操作响应。
/// </summary>
/// <param name="Collection">集合名。</param>
/// <param name="Status">updated / dropped / missing。</param>
/// <param name="Validator">当前 validator；删除后为空。</param>
public sealed record DocumentValidatorResponse(
    string Collection,
    string Status,
    DocumentValidatorContract? Validator = null);

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

/// <summary>
/// 文档聚合管线请求。
/// </summary>
/// <param name="Pipeline">按顺序执行的聚合阶段。</param>
public sealed record DocumentAggregateRequest(IReadOnlyList<DocumentAggregateStageContract> Pipeline);

/// <summary>
/// Document API 聚合阶段。每个阶段对象只能设置一个 `$xxx` 属性。
/// </summary>
/// <param name="Match">`$match` 阶段，复用 find 过滤表达式。</param>
/// <param name="Project">`$project` 阶段，复用 find 投影字段。</param>
/// <param name="Group">`$group` 阶段。</param>
/// <param name="Sort">`$sort` 阶段，复用 find 排序字段。</param>
/// <param name="Limit">`$limit` 阶段。</param>
/// <param name="Skip">`$skip` 阶段。</param>
/// <param name="Unwind">`$unwind` 阶段。</param>
/// <param name="Count">`$count` 阶段输出字段名。</param>
/// <param name="Distinct">`$distinct` 等价阶段。</param>
public sealed record DocumentAggregateStageContract(
    [property: JsonPropertyName("$match")] DocumentFilterContract? Match = null,
    [property: JsonPropertyName("$project")] IReadOnlyList<DocumentProjectionContract>? Project = null,
    [property: JsonPropertyName("$group")] DocumentAggregateGroupContract? Group = null,
    [property: JsonPropertyName("$sort")] IReadOnlyList<DocumentSortContract>? Sort = null,
    [property: JsonPropertyName("$limit")] int? Limit = null,
    [property: JsonPropertyName("$skip")] int? Skip = null,
    [property: JsonPropertyName("$unwind")] DocumentAggregateUnwindContract? Unwind = null,
    [property: JsonPropertyName("$count")] string? Count = null,
    [property: JsonPropertyName("$distinct")] DocumentAggregateDistinctContract? Distinct = null);

/// <summary>
/// `$group` 阶段定义。
/// </summary>
/// <param name="Keys">分组键；为空时表示全局分组。</param>
/// <param name="Accumulators">聚合函数定义。</param>
public sealed record DocumentAggregateGroupContract(
    IReadOnlyList<DocumentAggregateGroupKeyContract>? Keys = null,
    IReadOnlyList<DocumentAggregateAccumulatorContract>? Accumulators = null);

/// <summary>
/// `$group` 分组键。
/// </summary>
/// <param name="Name">输出字段名。</param>
/// <param name="Path">输入字段路径，可为 `_id` / `id` / `document` / `json` 或 JSON path。</param>
public sealed record DocumentAggregateGroupKeyContract(string Name, string Path);

/// <summary>
/// `$group` 聚合函数。
/// </summary>
/// <param name="Name">输出字段名。</param>
/// <param name="Op">函数名：count/sum/avg/min/max/first/last/distinct。</param>
/// <param name="Path">输入字段路径；count 可不传。</param>
public sealed record DocumentAggregateAccumulatorContract(string Name, string Op, string? Path = null);

/// <summary>
/// `$unwind` 阶段定义。
/// </summary>
/// <param name="Path">要展开的数组字段路径。</param>
/// <param name="Name">可选输出别名；为空时替换原字段。</param>
/// <param name="PreserveNullAndEmptyArrays">字段缺失、null 或空数组时是否保留原文档。</param>
public sealed record DocumentAggregateUnwindContract(
    string Path,
    string? Name = null,
    bool PreserveNullAndEmptyArrays = false);

/// <summary>
/// `$distinct` 等价阶段定义。
/// </summary>
/// <param name="Path">去重字段路径。</param>
/// <param name="Name">输出字段名。</param>
/// <param name="Limit">最多返回的去重值数量。</param>
public sealed record DocumentAggregateDistinctContract(
    string Path,
    string Name = "value",
    int? Limit = null);

/// <summary>
/// 文档聚合管线响应。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Documents">聚合输出文档。</param>
/// <param name="Count">输出文档数量。</param>
public sealed record DocumentAggregateResponse(
    string Collection,
    IReadOnlyList<JsonElement> Documents,
    int Count);
