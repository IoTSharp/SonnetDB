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
/// <param name="Filter">可选递归过滤表达式。</param>
/// <param name="Projection">可选投影字段。</param>
/// <param name="Sort">可选排序字段。</param>
/// <param name="ContinuationToken">可选 continuation token。</param>
/// <param name="Collation">字符串过滤与排序校对模式：ordinal 或 ordinal_ignore_case。</param>
[method: JsonConstructor]
public sealed record DocumentFindRequest(
    string? Id = null,
    IReadOnlyList<string>? Ids = null,
    int? Limit = null,
    int Skip = 0,
    DocumentFilterContract? Filter = null,
    IReadOnlyList<DocumentProjectionContract>? Projection = null,
    IReadOnlyList<DocumentSortContract>? Sort = null,
    string? ContinuationToken = null,
    string? Collation = null)
{
    /// <summary>使用默认 ordinal collation 创建查询，保留旧版构造入口。</summary>
    public DocumentFindRequest(
        string? Id,
        IReadOnlyList<string>? Ids,
        int? Limit,
        int Skip,
        DocumentFilterContract? Filter,
        IReadOnlyList<DocumentProjectionContract>? Projection,
        IReadOnlyList<DocumentSortContract>? Sort,
        string? ContinuationToken)
        : this(Id, Ids, Limit, Skip, Filter, Projection, Sort, ContinuationToken, null)
    {
    }

    /// <summary>按旧版八字段形态解构查询。</summary>
    public void Deconstruct(
        out string? Id,
        out IReadOnlyList<string>? Ids,
        out int? Limit,
        out int Skip,
        out DocumentFilterContract? Filter,
        out IReadOnlyList<DocumentProjectionContract>? Projection,
        out IReadOnlyList<DocumentSortContract>? Sort,
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
/// Document API 过滤表达式。
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
public sealed record DocumentFilterContract(
    string? Path = null,
    string? Op = null,
    JsonElement? Value = null,
    IReadOnlyList<DocumentFilterContract>? And = null,
    IReadOnlyList<DocumentFilterContract>? Or = null,
    DocumentFilterContract? Not = null,
    DocumentFilterContract? ElemMatch = null,
    string? RegexOptions = null)
{
    /// <summary>创建不含 M32 扩展操作数的过滤器，保留旧版构造入口。</summary>
    public DocumentFilterContract(
        string? Path,
        string? Op,
        JsonElement? Value,
        IReadOnlyList<DocumentFilterContract>? And,
        IReadOnlyList<DocumentFilterContract>? Or,
        DocumentFilterContract? Not)
        : this(Path, Op, Value, And, Or, Not, null, null)
    {
    }

    /// <summary>按旧版六字段形态解构过滤器。</summary>
    public void Deconstruct(
        out string? Path,
        out string? Op,
        out JsonElement? Value,
        out IReadOnlyList<DocumentFilterContract>? And,
        out IReadOnlyList<DocumentFilterContract>? Or,
        out DocumentFilterContract? Not)
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
/// <param name="Mul">对应 $mul。</param>
/// <param name="Pop">对应 $pop；值为 -1 时移除首项，1 时移除末项。</param>
[method: JsonConstructor]
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
    IReadOnlyDictionary<string, JsonElement>? CurrentDate = null,
    IReadOnlyDictionary<string, JsonElement>? Mul = null,
    IReadOnlyDictionary<string, JsonElement>? Pop = null)
{
    /// <summary>创建不含 <c>$mul</c>/<c>$pop</c> 的更新，保留旧版构造入口。</summary>
    public DocumentUpdateContract(
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
/// 原子查找并局部更新单条文档的请求。
/// </summary>
/// <param name="Id">可选文档 ID 等值条件。</param>
/// <param name="Filter">附加过滤条件；与 <paramref name="Id"/> 同时提供时按 AND 合并。</param>
/// <param name="Update">局部更新操作符。</param>
/// <param name="Upsert">未匹配时是否插入新文档。</param>
/// <param name="UpsertId">upsert 文档 ID；为空时尝试从 ID 或过滤条件推断。</param>
/// <param name="ReturnDocument">返回 before 或 after 文档。</param>
public sealed record DocumentFindOneAndUpdateRequest(
    string? Id,
    DocumentFilterContract? Filter,
    DocumentUpdateContract Update,
    bool Upsert = false,
    string? UpsertId = null,
    string ReturnDocument = "before");

/// <summary>
/// 原子查找并局部更新单条文档的响应。
/// </summary>
/// <param name="Collection">文档集合名称。</param>
/// <param name="Found">是否返回了 before/after 文档。</param>
/// <param name="Document">按请求选择返回的文档；未匹配或 before-upsert 时为空。</param>
/// <param name="Inserted">upsert 插入数量。</param>
/// <param name="Matched">匹配数量。</param>
/// <param name="Modified">实际修改数量。</param>
/// <param name="Errors">validator 或写入错误。</param>
public sealed record DocumentFindOneAndUpdateResponse(
    string Collection,
    bool Found,
    DocumentItemResponse? Document,
    int Inserted,
    int Matched,
    int Modified,
    IReadOnlyList<DocumentWriteErrorResponse>? Errors = null);

/// <summary>
/// 混合批量写中的一个 insert/replace/update/delete 操作。
/// </summary>
/// <param name="Type">insertOne、replaceOne、updateOne、updateMany、deleteOne 或 deleteMany。</param>
/// <param name="Id">insert/replace 的 ID，或 update/delete 的可选 ID 等值条件。</param>
/// <param name="Document">insert/replace 的 JSON 文档。</param>
/// <param name="Filter">update/delete 的过滤条件。</param>
/// <param name="Update">局部更新操作符。</param>
/// <param name="Upsert">update/replace 未匹配时是否插入。</param>
/// <param name="UpsertId">upsert 文档 ID。</param>
/// <param name="ExpectedVersion">replace 的可选预期版本。</param>
public sealed record DocumentBulkWriteOperationContract(
    string Type,
    string? Id = null,
    JsonElement? Document = null,
    DocumentFilterContract? Filter = null,
    DocumentUpdateContract? Update = null,
    bool Upsert = false,
    string? UpsertId = null,
    long? ExpectedVersion = null);

/// <summary>
/// 混合批量写请求。
/// </summary>
/// <param name="Operations">按请求顺序执行的操作，最多 1000 项。</param>
/// <param name="Ordered">为 true 时任一错误使整批不提交；为 false 时提交全部有效项。</param>
/// <param name="RequestId">可选幂等键；同一集合内重试相同请求时返回首次持久化结果。</param>
public sealed record DocumentBulkWriteRequest(
    IReadOnlyList<DocumentBulkWriteOperationContract> Operations,
    bool Ordered = true,
    string? RequestId = null);

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
public sealed record DocumentBulkWriteItemResponse(
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

/// <summary>
/// 混合批量写响应。
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
public sealed record DocumentBulkWriteResponse(
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

/// <summary>
/// 局部更新预览请求，不修改集合状态。
/// </summary>
/// <param name="Filter">待更新文档过滤条件。</param>
/// <param name="Update">局部更新操作符。</param>
/// <param name="Many">是否预览多条匹配文档。</param>
/// <param name="Limit">最多返回的预览数量，范围 1~100。</param>
/// <param name="Upsert">未匹配时是否生成 upsert 预览。</param>
/// <param name="UpsertId">upsert 文档 ID。</param>
public sealed record DocumentUpdatePreviewRequest(
    DocumentFilterContract? Filter,
    DocumentUpdateContract Update,
    bool Many = false,
    int Limit = 20,
    bool Upsert = false,
    string? UpsertId = null);

/// <summary>一条局部更新前后对照。</summary>
public sealed record DocumentUpdatePreviewItemResponse(
    string Id,
    long Version,
    JsonElement? Before,
    JsonElement After,
    bool IsUpsert,
    bool Changed);

/// <summary>局部更新预览响应。</summary>
public sealed record DocumentUpdatePreviewResponse(
    string Collection,
    int Matched,
    int Changed,
    IReadOnlyList<DocumentUpdatePreviewItemResponse> Documents);

/// <summary>Document partial index 过滤条件。</summary>
public sealed record DocumentIndexPartialFilterContract(
    string Path,
    string Operator,
    string? ValueScalar = null);

/// <summary>
/// 创建 Document JSON path 索引的请求。
/// </summary>
/// <param name="Name">索引名称。</param>
/// <param name="Paths">显式字段 path，或 wildcard 的单个 subtree root path。</param>
/// <param name="IsUnique">是否为唯一索引。</param>
/// <param name="IsSparse">是否跳过 null 或缺失值。</param>
/// <param name="PartialFilter">可选 partial index 过滤条件。</param>
/// <param name="TtlPath">可选 TTL 时间字段 path。</param>
/// <param name="TtlSeconds">TTL 保留秒数。</param>
/// <param name="Kind">索引类型：path 或 wildcard。</param>
[method: JsonConstructor]
public sealed record DocumentIndexCreateRequest(
    string Name,
    IReadOnlyList<string> Paths,
    bool IsUnique = false,
    bool IsSparse = false,
    DocumentIndexPartialFilterContract? PartialFilter = null,
    string? TtlPath = null,
    long? TtlSeconds = null,
    string Kind = "path")
{
    /// <summary>使用普通 path 索引创建请求，保留旧版构造入口。</summary>
    public DocumentIndexCreateRequest(
        string Name,
        IReadOnlyList<string> Paths,
        bool IsUnique,
        bool IsSparse,
        DocumentIndexPartialFilterContract? PartialFilter,
        string? TtlPath,
        long? TtlSeconds)
        : this(Name, Paths, IsUnique, IsSparse, PartialFilter, TtlPath, TtlSeconds, "path")
    {
    }

    /// <summary>按旧版字段形态解构请求，忽略新增的索引类型。</summary>
    public void Deconstruct(
        out string Name,
        out IReadOnlyList<string> Paths,
        out bool IsUnique,
        out bool IsSparse,
        out DocumentIndexPartialFilterContract? PartialFilter,
        out string? TtlPath,
        out long? TtlSeconds)
    {
        Name = this.Name;
        Paths = this.Paths;
        IsUnique = this.IsUnique;
        IsSparse = this.IsSparse;
        PartialFilter = this.PartialFilter;
        TtlPath = this.TtlPath;
        TtlSeconds = this.TtlSeconds;
    }
}

/// <summary>Document 索引生命周期操作响应。</summary>
public sealed record DocumentIndexOperationResponse(
    string Collection,
    string Index,
    string Status,
    IReadOnlyList<string>? Paths = null);

/// <summary>单个 Document 索引一致性明细。</summary>
public sealed record DocumentIndexConsistencyItemResponse(
    string Index,
    bool IsConsistent,
    int ExpectedEntries,
    int ActualEntries,
    int MissingEntries,
    int OrphanEntries);

/// <summary>Document 索引一致性校验响应。</summary>
public sealed record DocumentIndexConsistencyResponse(
    string Collection,
    int DocumentCount,
    bool IsConsistent,
    IReadOnlyList<DocumentIndexConsistencyItemResponse> Indexes);

/// <summary>
/// Document collection 变更订阅读取请求。
/// </summary>
/// <param name="ResumeToken">上次响应返回的续传 token。</param>
/// <param name="StartAt">无 token 时的起点：beginning 或 now。</param>
/// <param name="Limit">本批最多返回的匹配事件数。</param>
/// <param name="Operations">可选 insert/update/delete 过滤。</param>
/// <param name="DocumentId">可选文档 ID 过滤。</param>
public sealed record DocumentChangeFeedRequest(
    string? ResumeToken = null,
    string StartAt = "now",
    int Limit = 100,
    IReadOnlyList<string>? Operations = null,
    string? DocumentId = null);

/// <summary>一条 Document collection 变更事件。</summary>
public sealed record DocumentChangeFeedItemResponse(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string Operation,
    string DocumentId,
    long DocumentVersion,
    JsonElement? Before,
    JsonElement? After,
    bool PayloadTruncated);

/// <summary>Document collection 变更订阅读取响应。</summary>
public sealed record DocumentChangeFeedResponse(
    string Collection,
    IReadOnlyList<DocumentChangeFeedItemResponse> Changes,
    string ResumeToken,
    bool HasMore,
    long LatestSequence,
    long? OldestAvailableSequence,
    DateTimeOffset ResumeTokenExpiresAtUtc);

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
/// <param name="ComputedFields">`$project` 阶段的可选计算字段。</param>
[method: JsonConstructor]
public sealed record DocumentAggregateStageContract(
    [property: JsonPropertyName("$match")] DocumentFilterContract? Match = null,
    [property: JsonPropertyName("$project")] IReadOnlyList<DocumentProjectionContract>? Project = null,
    [property: JsonPropertyName("$group")] DocumentAggregateGroupContract? Group = null,
    [property: JsonPropertyName("$sort")] IReadOnlyList<DocumentSortContract>? Sort = null,
    [property: JsonPropertyName("$limit")] int? Limit = null,
    [property: JsonPropertyName("$skip")] int? Skip = null,
    [property: JsonPropertyName("$unwind")] DocumentAggregateUnwindContract? Unwind = null,
    [property: JsonPropertyName("$count")] string? Count = null,
    [property: JsonPropertyName("$distinct")] DocumentAggregateDistinctContract? Distinct = null,
    IReadOnlyList<DocumentAggregateComputedFieldContract>? ComputedFields = null)
{
    /// <summary>使用既有九种阶段属性创建聚合阶段，保留旧版构造入口。</summary>
    public DocumentAggregateStageContract(
        DocumentFilterContract? Match,
        IReadOnlyList<DocumentProjectionContract>? Project,
        DocumentAggregateGroupContract? Group,
        IReadOnlyList<DocumentSortContract>? Sort,
        int? Limit,
        int? Skip,
        DocumentAggregateUnwindContract? Unwind,
        string? Count,
        DocumentAggregateDistinctContract? Distinct)
        : this(Match, Project, Group, Sort, Limit, Skip, Unwind, Count, Distinct, null)
    {
    }

    /// <summary>按旧版九字段形态解构阶段，忽略新增的计算字段。</summary>
    public void Deconstruct(
        out DocumentFilterContract? Match,
        out IReadOnlyList<DocumentProjectionContract>? Project,
        out DocumentAggregateGroupContract? Group,
        out IReadOnlyList<DocumentSortContract>? Sort,
        out int? Limit,
        out int? Skip,
        out DocumentAggregateUnwindContract? Unwind,
        out string? Count,
        out DocumentAggregateDistinctContract? Distinct)
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
public sealed record DocumentAggregateComputedFieldContract(
    string Name,
    DocumentAggregateExpressionContract Expression);

/// <summary>SonnetDB-native 聚合表达式。</summary>
/// <param name="Op">field、literal、add、subtract、multiply、divide、concat、if_null 或 cond。</param>
/// <param name="Path">field 表达式读取的字段 path。</param>
/// <param name="Value">literal 表达式的 JSON 值。</param>
/// <param name="Arguments">运算表达式的有序参数。</param>
public sealed record DocumentAggregateExpressionContract(
    string Op,
    string? Path = null,
    JsonElement? Value = null,
    IReadOnlyList<DocumentAggregateExpressionContract>? Arguments = null);

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
/// <param name="Expression">可选分组表达式；设置时优先于 path。</param>
[method: JsonConstructor]
public sealed record DocumentAggregateGroupKeyContract(
    string Name,
    string Path,
    DocumentAggregateExpressionContract? Expression = null)
{
    /// <summary>使用字段 path 创建分组键，保留旧版构造入口。</summary>
    /// <param name="Name">输出字段名。</param>
    /// <param name="Path">输入字段路径。</param>
    public DocumentAggregateGroupKeyContract(string Name, string Path)
        : this(Name, Path, null)
    {
    }

    /// <summary>创建使用表达式的分组键。</summary>
    /// <param name="name">输出字段名。</param>
    /// <param name="expression">分组表达式。</param>
    /// <returns>使用指定表达式的分组键。</returns>
    public static DocumentAggregateGroupKeyContract FromExpression(
        string name,
        DocumentAggregateExpressionContract expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new DocumentAggregateGroupKeyContract(name, string.Empty, expression);
    }

    /// <summary>按旧版字段形态解构分组键。</summary>
    public void Deconstruct(out string Name, out string Path)
    {
        Name = this.Name;
        Path = this.Path;
    }
}

/// <summary>
/// `$group` 聚合函数。
/// </summary>
/// <param name="Name">输出字段名。</param>
/// <param name="Op">函数名：count/sum/avg/min/max/first/last/distinct/push/addToSet。</param>
/// <param name="Path">输入字段路径；count 可不传。</param>
/// <param name="Expression">可选输入表达式；设置时优先于 path。</param>
[method: JsonConstructor]
public sealed record DocumentAggregateAccumulatorContract(
    string Name,
    string Op,
    string? Path = null,
    DocumentAggregateExpressionContract? Expression = null)
{
    /// <summary>使用字段 path 创建聚合函数，保留旧版构造入口。</summary>
    public DocumentAggregateAccumulatorContract(string Name, string Op, string? Path)
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
/// `$unwind` 阶段定义。
/// </summary>
/// <param name="Path">要展开的数组字段路径。</param>
/// <param name="Name">可选输出别名；为空时替换原字段。</param>
/// <param name="PreserveNullAndEmptyArrays">字段缺失、null 或空数组时是否保留原文档。</param>
/// <param name="IncludeArrayIndex">可选数组下标输出字段名。</param>
[method: JsonConstructor]
public sealed record DocumentAggregateUnwindContract(
    string Path,
    string? Name = null,
    bool PreserveNullAndEmptyArrays = false,
    string? IncludeArrayIndex = null)
{
    /// <summary>创建不输出数组下标的 unwind，保留旧版构造入口。</summary>
    public DocumentAggregateUnwindContract(
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
