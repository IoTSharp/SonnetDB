using System.Text.Json;
using System.Text.Json.Serialization;

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
/// 文档写入错误码常量。
/// </summary>
public static class SndbDocumentWriteErrorCodes
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
public static class SndbDocumentWriteErrorSeverity
{
    /// <summary>会阻止写入的错误。</summary>
    public const string Error = "error";

    /// <summary>不会阻止写入的警告。</summary>
    public const string Warning = "warning";
}

/// <summary>
/// SonnetDB 文档局部更新操作符集合。
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
public sealed record SndbDocumentUpdate(
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
    int Deleted,
    IReadOnlyList<SndbDocumentWriteError>? Errors = null)
{
    /// <summary>是否包含批量单项错误。</summary>
    public bool HasErrors => Errors?.Any(static error => string.Equals(error.Severity, SndbDocumentWriteErrorSeverity.Error, StringComparison.Ordinal)) == true;

    /// <summary>是否包含批量单项警告。</summary>
    public bool HasWarnings => Errors?.Any(static error => string.Equals(error.Severity, SndbDocumentWriteErrorSeverity.Warning, StringComparison.Ordinal)) == true;
}

/// <summary>
/// 文档批量写中的单项错误。
/// </summary>
/// <param name="Index">原始批量请求中的零基序号。</param>
/// <param name="Id">发生错误的文档 ID；请求 ID 无效时为 null。</param>
/// <param name="Code">稳定错误码。</param>
/// <param name="Message">面向调用方的错误说明。</param>
/// <param name="Severity">错误或警告级别。</param>
public sealed record SndbDocumentWriteError(
    int Index,
    string? Id,
    string Code,
    string Message,
    string Severity = SndbDocumentWriteErrorSeverity.Error);

/// <summary>
/// SonnetDB 文档集合 validator。
/// </summary>
/// <param name="Rules">字段校验规则。</param>
/// <param name="ValidationAction">校验失败动作：error 或 warn。</param>
public sealed record SndbDocumentValidator(
    IReadOnlyList<SndbDocumentValidatorRule> Rules,
    string ValidationAction = "error");

/// <summary>
/// SonnetDB 文档集合 validator 字段规则。
/// </summary>
/// <param name="Path">JSON path。</param>
/// <param name="Required">字段是否必填。</param>
/// <param name="Type">单个允许类型。</param>
/// <param name="Types">多个允许类型。</param>
/// <param name="Minimum">数值下界。</param>
/// <param name="Maximum">数值上界。</param>
/// <param name="Enum">允许的枚举值。</param>
/// <param name="Pattern">字符串正则表达式。</param>
public sealed record SndbDocumentValidatorRule(
    string Path,
    bool Required = false,
    string? Type = null,
    IReadOnlyList<string>? Types = null,
    double? Minimum = null,
    double? Maximum = null,
    IReadOnlyList<JsonElement>? Enum = null,
    string? Pattern = null);

/// <summary>
/// SonnetDB 文档集合 validator 操作响应。
/// </summary>
/// <param name="Collection">集合名。</param>
/// <param name="Status">updated / dropped / missing。</param>
/// <param name="Validator">当前 validator；删除后为空。</param>
public sealed record SndbDocumentValidatorResponse(
    string Collection,
    string Status,
    SndbDocumentValidator? Validator = null);

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

/// <summary>
/// 文档聚合管线阶段。每个阶段对象只能设置一个 `$xxx` 属性。
/// </summary>
/// <param name="Match">`$match` 阶段。</param>
/// <param name="Project">`$project` 阶段。</param>
/// <param name="Group">`$group` 阶段。</param>
/// <param name="Sort">`$sort` 阶段。</param>
/// <param name="Limit">`$limit` 阶段。</param>
/// <param name="Skip">`$skip` 阶段。</param>
/// <param name="Unwind">`$unwind` 阶段。</param>
/// <param name="Count">`$count` 阶段输出字段名。</param>
/// <param name="Distinct">`$distinct` 等价阶段。</param>
public sealed record SndbDocumentAggregateStage(
    [property: JsonPropertyName("$match")] SndbDocumentFilter? Match = null,
    [property: JsonPropertyName("$project")] IReadOnlyList<SndbDocumentProjection>? Project = null,
    [property: JsonPropertyName("$group")] SndbDocumentAggregateGroup? Group = null,
    [property: JsonPropertyName("$sort")] IReadOnlyList<SndbDocumentSort>? Sort = null,
    [property: JsonPropertyName("$limit")] int? Limit = null,
    [property: JsonPropertyName("$skip")] int? Skip = null,
    [property: JsonPropertyName("$unwind")] SndbDocumentAggregateUnwind? Unwind = null,
    [property: JsonPropertyName("$count")] string? Count = null,
    [property: JsonPropertyName("$distinct")] SndbDocumentAggregateDistinct? Distinct = null);

/// <summary>
/// 文档 `$group` 阶段定义。
/// </summary>
/// <param name="Keys">分组键；为空时表示全局分组。</param>
/// <param name="Accumulators">聚合函数定义。</param>
public sealed record SndbDocumentAggregateGroup(
    IReadOnlyList<SndbDocumentAggregateGroupKey>? Keys = null,
    IReadOnlyList<SndbDocumentAggregateAccumulator>? Accumulators = null);

/// <summary>
/// 文档 `$group` 分组键。
/// </summary>
/// <param name="Name">输出字段名。</param>
/// <param name="Path">输入字段路径，可为 `_id` / `id` / `document` / `json` 或 JSON path。</param>
public sealed record SndbDocumentAggregateGroupKey(string Name, string Path);

/// <summary>
/// 文档 `$group` 聚合函数。
/// </summary>
/// <param name="Name">输出字段名。</param>
/// <param name="Op">函数名：count/sum/avg/min/max/first/last/distinct。</param>
/// <param name="Path">输入字段路径；count 可不传。</param>
public sealed record SndbDocumentAggregateAccumulator(string Name, string Op, string? Path = null);

/// <summary>
/// 文档 `$unwind` 阶段定义。
/// </summary>
/// <param name="Path">要展开的数组字段路径。</param>
/// <param name="Name">可选输出别名；为空时替换原字段。</param>
/// <param name="PreserveNullAndEmptyArrays">字段缺失、null 或空数组时是否保留原文档。</param>
public sealed record SndbDocumentAggregateUnwind(
    string Path,
    string? Name = null,
    bool PreserveNullAndEmptyArrays = false);

/// <summary>
/// 文档 `$distinct` 等价阶段定义。
/// </summary>
/// <param name="Path">去重字段路径。</param>
/// <param name="Name">输出字段名。</param>
/// <param name="Limit">最多返回的去重值数量。</param>
public sealed record SndbDocumentAggregateDistinct(
    string Path,
    string Name = "value",
    int? Limit = null);

/// <summary>
/// 文档聚合管线结果。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Documents">聚合输出的紧凑 JSON 文档。</param>
/// <param name="Count">输出文档数量。</param>
public sealed record SndbDocumentAggregateResult(
    string Collection,
    IReadOnlyList<string> Documents,
    int Count);

internal sealed record DocumentCollectionCreateRequest(
    bool IfNotExists = true,
    SndbDocumentValidator? Validator = null);

internal sealed record DocumentCollectionOperationResponse(string Collection, string Status);

internal sealed record DocumentWriteItem(string Id, JsonElement Document);

internal sealed record DocumentInsertManyRequest(
    IReadOnlyList<DocumentWriteItem> Documents,
    bool Ordered = true);

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

internal sealed record DocumentUpdateOneRequest(
    string? Id = null,
    JsonElement? Document = null,
    SndbDocumentFilter? Filter = null,
    SndbDocumentUpdate? Update = null,
    bool Upsert = false,
    string? UpsertId = null);

internal sealed record DocumentUpdateManyRequest(
    IReadOnlyList<DocumentWriteItem>? Documents = null,
    SndbDocumentFilter? Filter = null,
    SndbDocumentUpdate? Update = null,
    bool Upsert = false,
    string? UpsertId = null,
    bool Ordered = true);

internal sealed record DocumentDeleteOneRequest(string Id);

internal sealed record DocumentDeleteManyRequest(IReadOnlyList<string> Ids, bool Ordered = true);

internal sealed record DocumentWriteErrorResponse(
    int Index,
    string? Id,
    string Code,
    string Message,
    string Severity = SndbDocumentWriteErrorSeverity.Error);

internal sealed record DocumentWriteResponse(
    string Collection,
    int Inserted = 0,
    int Matched = 0,
    int Modified = 0,
    int Deleted = 0,
    IReadOnlyList<DocumentWriteErrorResponse>? Errors = null);

internal sealed record DocumentValidatorResponse(
    string Collection,
    string Status,
    SndbDocumentValidator? Validator = null);

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

internal sealed record DocumentAggregateRequest(IReadOnlyList<SndbDocumentAggregateStage> Pipeline);

internal sealed record DocumentAggregateResponse(
    string Collection,
    IReadOnlyList<JsonElement> Documents,
    int Count);

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
