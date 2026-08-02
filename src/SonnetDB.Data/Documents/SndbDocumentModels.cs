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
/// 文档查询选项，支持 ID、filter、projection、sort 与 continuation token 分页。
/// </summary>
/// <param name="Id">可选单文档 ID。</param>
/// <param name="Ids">可选文档 ID 列表。</param>
/// <param name="Limit">扫描时最多返回的文档数。</param>
/// <param name="Skip">扫描时跳过的文档数。</param>
/// <param name="Filter">可选递归过滤表达式。</param>
/// <param name="Projection">可选投影字段。</param>
/// <param name="Sort">可选排序字段。</param>
/// <param name="ContinuationToken">可选 continuation token。</param>
/// <param name="Collation">字符串过滤与排序使用的基础校对模式。</param>
[method: JsonConstructor]
public sealed record SndbDocumentFindOptions(
    string? Id = null,
    IReadOnlyList<string>? Ids = null,
    int? Limit = null,
    int Skip = 0,
    SndbDocumentFilter? Filter = null,
    IReadOnlyList<SndbDocumentProjection>? Projection = null,
    IReadOnlyList<SndbDocumentSort>? Sort = null,
    string? ContinuationToken = null,
    SndbDocumentCollation Collation = SndbDocumentCollation.Ordinal)
{
    /// <summary>使用 ordinal 校对创建查询选项，保留旧版构造入口。</summary>
    public SndbDocumentFindOptions(
        string? Id,
        IReadOnlyList<string>? Ids,
        int? Limit,
        int Skip,
        SndbDocumentFilter? Filter,
        IReadOnlyList<SndbDocumentProjection>? Projection,
        IReadOnlyList<SndbDocumentSort>? Sort,
        string? ContinuationToken)
        : this(Id, Ids, Limit, Skip, Filter, Projection, Sort, ContinuationToken, SndbDocumentCollation.Ordinal)
    {
    }

    /// <summary>按旧版八字段形态解构查询选项。</summary>
    public void Deconstruct(
        out string? Id,
        out IReadOnlyList<string>? Ids,
        out int? Limit,
        out int Skip,
        out SndbDocumentFilter? Filter,
        out IReadOnlyList<SndbDocumentProjection>? Projection,
        out IReadOnlyList<SndbDocumentSort>? Sort,
        out string? ContinuationToken)
    {
        Id = this.Id;
        Ids = this.Ids;
        Limit = this.Limit;
        Skip = this.Skip;
        Filter = this.Filter;
        Projection = this.Projection;
        Sort = this.Sort;
        ContinuationToken = this.ContinuationToken;
    }
}

/// <summary>
/// 文档查询支持的基础字符串校对模式。
/// </summary>
public enum SndbDocumentCollation
{
    /// <summary>按 Unicode 码元进行大小写敏感比较。</summary>
    Ordinal,

    /// <summary>按 Unicode 码元进行大小写不敏感比较。</summary>
    OrdinalIgnoreCase,
}

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
/// <param name="Op">操作符：eq/ne/gt/gte/lt/lte/in/nin/exists/contains/elemMatch/regex/type/size/all。</param>
/// <param name="Value">比较值。</param>
/// <param name="And">AND 子表达式列表。</param>
/// <param name="Or">OR 子表达式列表。</param>
/// <param name="Not">NOT 子表达式。</param>
/// <param name="ElemMatch">$elemMatch 的元素级子过滤表达式。</param>
/// <param name="RegexOptions">$regex 标志，支持 i/c/m/s/x。</param>
[method: JsonConstructor]
public sealed record SndbDocumentFilter(
    string? Path = null,
    string? Op = null,
    JsonElement? Value = null,
    IReadOnlyList<SndbDocumentFilter>? And = null,
    IReadOnlyList<SndbDocumentFilter>? Or = null,
    SndbDocumentFilter? Not = null,
    SndbDocumentFilter? ElemMatch = null,
    string? RegexOptions = null)
{
    /// <summary>创建不含 M32 扩展字段的过滤表达式，保留旧版构造入口。</summary>
    public SndbDocumentFilter(
        string? Path,
        string? Op,
        JsonElement? Value,
        IReadOnlyList<SndbDocumentFilter>? And,
        IReadOnlyList<SndbDocumentFilter>? Or,
        SndbDocumentFilter? Not)
        : this(Path, Op, Value, And, Or, Not, null, null)
    {
    }

    /// <summary>按旧版六字段形态解构过滤表达式。</summary>
    public void Deconstruct(
        out string? Path,
        out string? Op,
        out JsonElement? Value,
        out IReadOnlyList<SndbDocumentFilter>? And,
        out IReadOnlyList<SndbDocumentFilter>? Or,
        out SndbDocumentFilter? Not)
    {
        Path = this.Path;
        Op = this.Op;
        Value = this.Value;
        And = this.And;
        Or = this.Or;
        Not = this.Not;
    }
}

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

    /// <summary>原子批次超过底层 WAL 或内存预算，未提交任何变更。</summary>
    public const string BatchTooLarge = "batch_too_large";

    /// <summary>同一幂等键被用于不同的批量请求。</summary>
    public const string IdempotencyConflict = "idempotency_conflict";

    /// <summary>有序批次因前序错误而未执行当前项。</summary>
    public const string NotAttempted = "not_attempted";
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
/// <param name="Mul">对应 $mul。</param>
/// <param name="Pop">对应 $pop；值为 -1 时移除首项，1 时移除末项。</param>
[method: JsonConstructor]
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
    IReadOnlyDictionary<string, JsonElement>? CurrentDate = null,
    IReadOnlyDictionary<string, JsonElement>? Mul = null,
    IReadOnlyDictionary<string, JsonElement>? Pop = null)
{
    /// <summary>使用既有十种操作符创建更新，保留旧版构造入口。</summary>
    public SndbDocumentUpdate(
        IReadOnlyDictionary<string, JsonElement>? Set,
        IReadOnlyDictionary<string, JsonElement>? Unset,
        IReadOnlyDictionary<string, JsonElement>? Inc,
        IReadOnlyDictionary<string, JsonElement>? Min,
        IReadOnlyDictionary<string, JsonElement>? Max,
        IReadOnlyDictionary<string, string>? Rename,
        IReadOnlyDictionary<string, JsonElement>? Push,
        IReadOnlyDictionary<string, JsonElement>? Pull,
        IReadOnlyDictionary<string, JsonElement>? AddToSet,
        IReadOnlyDictionary<string, JsonElement>? CurrentDate)
        : this(Set, Unset, Inc, Min, Max, Rename, Push, Pull, AddToSet, CurrentDate, null, null)
    {
    }

    /// <summary>按旧版十字段形态解构更新。</summary>
    public void Deconstruct(
        out IReadOnlyDictionary<string, JsonElement>? Set,
        out IReadOnlyDictionary<string, JsonElement>? Unset,
        out IReadOnlyDictionary<string, JsonElement>? Inc,
        out IReadOnlyDictionary<string, JsonElement>? Min,
        out IReadOnlyDictionary<string, JsonElement>? Max,
        out IReadOnlyDictionary<string, string>? Rename,
        out IReadOnlyDictionary<string, JsonElement>? Push,
        out IReadOnlyDictionary<string, JsonElement>? Pull,
        out IReadOnlyDictionary<string, JsonElement>? AddToSet,
        out IReadOnlyDictionary<string, JsonElement>? CurrentDate)
    {
        Set = this.Set;
        Unset = this.Unset;
        Inc = this.Inc;
        Min = this.Min;
        Max = this.Max;
        Rename = this.Rename;
        Push = this.Push;
        Pull = this.Pull;
        AddToSet = this.AddToSet;
        CurrentDate = this.CurrentDate;
    }
}

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
/// <c>findOneAndUpdate</c> 返回更新前或更新后文档的模式。
/// </summary>
public enum SndbDocumentReturnDocument
{
    /// <summary>返回更新前文档；upsert 时为空。</summary>
    Before,

    /// <summary>返回更新后或 upsert 新建的文档。</summary>
    After,
}

/// <summary>
/// 原子查找并更新单条文档的选项。
/// </summary>
/// <param name="Update">局部更新操作符。</param>
/// <param name="Id">可选文档 ID 等值条件。</param>
/// <param name="Filter">附加过滤条件；与 <paramref name="Id"/> 同时提供时按 AND 合并。</param>
/// <param name="Upsert">未匹配时是否插入新文档。</param>
/// <param name="UpsertId">upsert 文档 ID。</param>
/// <param name="ReturnDocument">返回更新前或更新后文档。</param>
public sealed record SndbDocumentFindOneAndUpdateOptions(
    SndbDocumentUpdate Update,
    string? Id = null,
    SndbDocumentFilter? Filter = null,
    bool Upsert = false,
    string? UpsertId = null,
    SndbDocumentReturnDocument ReturnDocument = SndbDocumentReturnDocument.Before);

/// <summary>
/// 原子查找并更新单条文档的结果。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Document">按选项返回的 before/after 文档。</param>
/// <param name="Inserted">upsert 插入数量。</param>
/// <param name="Matched">匹配数量。</param>
/// <param name="Modified">实际修改数量。</param>
/// <param name="Errors">validator 或写入错误。</param>
public sealed record SndbDocumentFindOneAndUpdateResult(
    string Collection,
    SndbDocument? Document,
    int Inserted,
    int Matched,
    int Modified,
    IReadOnlyList<SndbDocumentWriteError>? Errors = null)
{
    /// <summary>是否包含会阻止写入的错误。</summary>
    public bool HasErrors => Errors?.Any(static error => string.Equals(
        error.Severity,
        SndbDocumentWriteErrorSeverity.Error,
        StringComparison.Ordinal)) == true;
}

/// <summary>
/// 混合文档批量写操作类型。
/// </summary>
public enum SndbDocumentBulkWriteOperationType
{
    /// <summary>插入单条文档。</summary>
    InsertOne,

    /// <summary>整体替换单条文档。</summary>
    ReplaceOne,

    /// <summary>局部更新第一条匹配文档。</summary>
    UpdateOne,

    /// <summary>局部更新全部匹配文档。</summary>
    UpdateMany,

    /// <summary>删除第一条匹配文档。</summary>
    DeleteOne,

    /// <summary>删除全部匹配文档。</summary>
    DeleteMany,
}

/// <summary>
/// 混合文档批量写中的一个操作。
/// </summary>
/// <param name="Type">操作类型。</param>
/// <param name="Id">insert/replace 的 ID，或 update/delete 的可选 ID 等值条件。</param>
/// <param name="Json">insert/replace 的 JSON 文档文本。</param>
/// <param name="Filter">update/delete 的过滤条件。</param>
/// <param name="Update">局部更新操作符。</param>
/// <param name="Upsert">update/replace 未匹配时是否插入。</param>
/// <param name="UpsertId">upsert 文档 ID。</param>
/// <param name="ExpectedVersion">replace 的可选预期版本。</param>
public sealed record SndbDocumentBulkWriteOperation(
    SndbDocumentBulkWriteOperationType Type,
    string? Id = null,
    string? Json = null,
    SndbDocumentFilter? Filter = null,
    SndbDocumentUpdate? Update = null,
    bool Upsert = false,
    string? UpsertId = null,
    long? ExpectedVersion = null);

/// <summary>
/// 混合批量写的单项结果。
/// </summary>
/// <param name="Index">原始请求中的零基序号。</param>
/// <param name="Operation">稳定操作名称。</param>
/// <param name="Id">目标或 upsert 后的文档 ID。</param>
/// <param name="Status">succeeded、no_op、failed 或 not_attempted。</param>
/// <param name="Inserted">插入数量。</param>
/// <param name="Matched">匹配数量。</param>
/// <param name="Modified">修改数量。</param>
/// <param name="Deleted">删除数量。</param>
/// <param name="UpsertedId">发生 upsert 时的新文档 ID。</param>
/// <param name="Error">当前项错误或警告。</param>
public sealed record SndbDocumentBulkWriteItemResult(
    int Index,
    string Operation,
    string? Id,
    string Status,
    int Inserted = 0,
    int Matched = 0,
    int Modified = 0,
    int Deleted = 0,
    string? UpsertedId = null,
    SndbDocumentWriteError? Error = null);

/// <summary>
/// 混合批量写结果。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Inserted">插入总数。</param>
/// <param name="Matched">匹配总数。</param>
/// <param name="Modified">修改总数。</param>
/// <param name="Deleted">删除总数。</param>
/// <param name="Items">按请求序号排列的逐项结果。</param>
/// <param name="Errors">批次错误与警告。</param>
/// <param name="RequestId">调用方幂等键。</param>
/// <param name="Replayed">是否直接重放首次持久化结果。</param>
/// <param name="Committed">成功项是否已作为一个 collection 内原子批次提交。</param>
public sealed record SndbDocumentBulkWriteResult(
    string Collection,
    int Inserted,
    int Matched,
    int Modified,
    int Deleted,
    IReadOnlyList<SndbDocumentBulkWriteItemResult> Items,
    IReadOnlyList<SndbDocumentWriteError>? Errors = null,
    string? RequestId = null,
    bool Replayed = false,
    bool Committed = true)
{
    /// <summary>是否包含会阻止写入的错误。</summary>
    public bool HasErrors => Errors?.Any(static error => string.Equals(
        error.Severity,
        SndbDocumentWriteErrorSeverity.Error,
        StringComparison.Ordinal)) == true;
}

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
/// <param name="ComputedFields">`$project` 阶段的可选计算字段。</param>
[method: JsonConstructor]
public sealed record SndbDocumentAggregateStage(
    [property: JsonPropertyName("$match")] SndbDocumentFilter? Match = null,
    [property: JsonPropertyName("$project")] IReadOnlyList<SndbDocumentProjection>? Project = null,
    [property: JsonPropertyName("$group")] SndbDocumentAggregateGroup? Group = null,
    [property: JsonPropertyName("$sort")] IReadOnlyList<SndbDocumentSort>? Sort = null,
    [property: JsonPropertyName("$limit")] int? Limit = null,
    [property: JsonPropertyName("$skip")] int? Skip = null,
    [property: JsonPropertyName("$unwind")] SndbDocumentAggregateUnwind? Unwind = null,
    [property: JsonPropertyName("$count")] string? Count = null,
    [property: JsonPropertyName("$distinct")] SndbDocumentAggregateDistinct? Distinct = null,
    IReadOnlyList<SndbDocumentAggregateComputedField>? ComputedFields = null)
{
    /// <summary>使用既有九种阶段属性创建聚合阶段，保留旧版构造入口。</summary>
    public SndbDocumentAggregateStage(
        SndbDocumentFilter? Match,
        IReadOnlyList<SndbDocumentProjection>? Project,
        SndbDocumentAggregateGroup? Group,
        IReadOnlyList<SndbDocumentSort>? Sort,
        int? Limit,
        int? Skip,
        SndbDocumentAggregateUnwind? Unwind,
        string? Count,
        SndbDocumentAggregateDistinct? Distinct)
        : this(Match, Project, Group, Sort, Limit, Skip, Unwind, Count, Distinct, null)
    {
    }

    /// <summary>按旧版九字段形态解构阶段，忽略新增的计算字段。</summary>
    public void Deconstruct(
        out SndbDocumentFilter? Match,
        out IReadOnlyList<SndbDocumentProjection>? Project,
        out SndbDocumentAggregateGroup? Group,
        out IReadOnlyList<SndbDocumentSort>? Sort,
        out int? Limit,
        out int? Skip,
        out SndbDocumentAggregateUnwind? Unwind,
        out string? Count,
        out SndbDocumentAggregateDistinct? Distinct)
    {
        Match = this.Match;
        Project = this.Project;
        Group = this.Group;
        Sort = this.Sort;
        Limit = this.Limit;
        Skip = this.Skip;
        Unwind = this.Unwind;
        Count = this.Count;
        Distinct = this.Distinct;
    }
}

/// <summary>`$project` 阶段的一个计算字段。</summary>
/// <param name="Name">输出字段名。</param>
/// <param name="Expression">字段值表达式。</param>
public sealed record SndbDocumentAggregateComputedField(
    string Name,
    SndbDocumentAggregateExpression Expression);

/// <summary>SonnetDB-native 聚合表达式。</summary>
/// <param name="Op">field、literal、add、subtract、multiply、divide、concat、if_null 或 cond。</param>
/// <param name="Path">field 表达式读取的字段 path。</param>
/// <param name="Value">literal 表达式的 JSON 值。</param>
/// <param name="Arguments">运算表达式的有序参数。</param>
public sealed record SndbDocumentAggregateExpression(
    string Op,
    string? Path = null,
    JsonElement? Value = null,
    IReadOnlyList<SndbDocumentAggregateExpression>? Arguments = null);

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
/// <param name="Expression">可选分组表达式；设置时优先于 path。</param>
[method: JsonConstructor]
public sealed record SndbDocumentAggregateGroupKey(
    string Name,
    string Path,
    SndbDocumentAggregateExpression? Expression = null)
{
    /// <summary>使用字段 path 创建分组键，保留旧版构造入口。</summary>
    /// <param name="Name">输出字段名。</param>
    /// <param name="Path">输入字段路径。</param>
    public SndbDocumentAggregateGroupKey(string Name, string Path)
        : this(Name, Path, null)
    {
    }

    /// <summary>创建使用表达式的分组键。</summary>
    /// <param name="name">输出字段名。</param>
    /// <param name="expression">分组表达式。</param>
    /// <returns>使用指定表达式的分组键。</returns>
    public static SndbDocumentAggregateGroupKey FromExpression(
        string name,
        SndbDocumentAggregateExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new SndbDocumentAggregateGroupKey(name, string.Empty, expression);
    }

    /// <summary>按旧版字段形态解构分组键。</summary>
    public void Deconstruct(out string Name, out string Path)
    {
        Name = this.Name;
        Path = this.Path;
    }
}

/// <summary>
/// 文档 `$group` 聚合函数。
/// </summary>
/// <param name="Name">输出字段名。</param>
/// <param name="Op">函数名：count/sum/avg/min/max/first/last/distinct/push/addToSet。</param>
/// <param name="Path">输入字段路径；count 可不传。</param>
/// <param name="Expression">可选输入表达式；设置时优先于 path。</param>
[method: JsonConstructor]
public sealed record SndbDocumentAggregateAccumulator(
    string Name,
    string Op,
    string? Path = null,
    SndbDocumentAggregateExpression? Expression = null)
{
    /// <summary>使用字段 path 创建聚合函数，保留旧版构造入口。</summary>
    public SndbDocumentAggregateAccumulator(string Name, string Op, string? Path)
        : this(Name, Op, Path, null)
    {
    }

    /// <summary>按旧版字段形态解构聚合函数。</summary>
    public void Deconstruct(out string Name, out string Op, out string? Path)
    {
        Name = this.Name;
        Op = this.Op;
        Path = this.Path;
    }
}

/// <summary>
/// 文档 `$unwind` 阶段定义。
/// </summary>
/// <param name="Path">要展开的数组字段路径。</param>
/// <param name="Name">可选输出别名；为空时替换原字段。</param>
/// <param name="PreserveNullAndEmptyArrays">字段缺失、null 或空数组时是否保留原文档。</param>
/// <param name="IncludeArrayIndex">可选数组下标输出字段名。</param>
[method: JsonConstructor]
public sealed record SndbDocumentAggregateUnwind(
    string Path,
    string? Name = null,
    bool PreserveNullAndEmptyArrays = false,
    string? IncludeArrayIndex = null)
{
    /// <summary>创建不输出数组下标的 unwind，保留旧版构造入口。</summary>
    public SndbDocumentAggregateUnwind(
        string Path,
        string? Name,
        bool PreserveNullAndEmptyArrays)
        : this(Path, Name, PreserveNullAndEmptyArrays, null)
    {
    }

    /// <summary>按旧版字段形态解构 unwind。</summary>
    public void Deconstruct(
        out string Path,
        out string? Name,
        out bool PreserveNullAndEmptyArrays)
    {
        Path = this.Path;
        Name = this.Name;
        PreserveNullAndEmptyArrays = this.PreserveNullAndEmptyArrays;
    }
}

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
    string? ContinuationToken = null,
    string? Collation = null);

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

internal sealed record DocumentFindOneAndUpdateRequest(
    string? Id,
    SndbDocumentFilter? Filter,
    SndbDocumentUpdate Update,
    bool Upsert = false,
    string? UpsertId = null,
    string ReturnDocument = "before");

internal sealed record DocumentFindOneAndUpdateResponse(
    string Collection,
    bool Found,
    DocumentItemResponse? Document,
    int Inserted,
    int Matched,
    int Modified,
    IReadOnlyList<DocumentWriteErrorResponse>? Errors = null);

internal sealed record DocumentBulkWriteOperationContract(
    string Type,
    string? Id = null,
    JsonElement? Document = null,
    SndbDocumentFilter? Filter = null,
    SndbDocumentUpdate? Update = null,
    bool Upsert = false,
    string? UpsertId = null,
    long? ExpectedVersion = null);

internal sealed record DocumentBulkWriteRequest(
    IReadOnlyList<DocumentBulkWriteOperationContract> Operations,
    bool Ordered = true,
    string? RequestId = null);

internal sealed record DocumentBulkWriteItemResponse(
    int Index,
    string Operation,
    string? Id,
    string Status,
    int Inserted = 0,
    int Matched = 0,
    int Modified = 0,
    int Deleted = 0,
    string? UpsertedId = null,
    DocumentWriteErrorResponse? Error = null);

internal sealed record DocumentBulkWriteResponse(
    string Collection,
    int Inserted,
    int Matched,
    int Modified,
    int Deleted,
    IReadOnlyList<DocumentBulkWriteItemResponse> Items,
    IReadOnlyList<DocumentWriteErrorResponse>? Errors = null,
    string? RequestId = null,
    bool Replayed = false,
    bool Committed = true);

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
