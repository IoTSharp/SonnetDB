namespace SonnetDB.Documents;

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
/// 混合文档批量写操作类型。
/// </summary>
public enum DocumentBulkWriteOperationType
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
/// <param name="Id">insert/replace 的文档 ID，或 update/delete 的可选 ID 等值条件。</param>
/// <param name="Json">insert/replace 的 JSON 文档。</param>
/// <param name="Filter">update/delete 的过滤条件；与 <paramref name="Id"/> 同时提供时按 AND 合并。</param>
/// <param name="Update">局部更新操作符。</param>
/// <param name="Upsert">未匹配 update/replace 时是否插入文档。</param>
/// <param name="UpsertId">upsert 新文档 ID；为空时尝试从 ID 或过滤条件推断。</param>
/// <param name="ExpectedVersion">可选的预期文档版本。</param>
public sealed record DocumentBulkWriteOperation(
    DocumentBulkWriteOperationType Type,
    string? Id = null,
    string? Json = null,
    DocumentFilter? Filter = null,
    DocumentUpdate? Update = null,
    bool Upsert = false,
    string? UpsertId = null,
    long? ExpectedVersion = null);

/// <summary>
/// 混合批量写单项状态常量。
/// </summary>
public static class DocumentBulkWriteItemStatuses
{
    /// <summary>操作已成功提交。</summary>
    public const string Succeeded = "succeeded";

    /// <summary>操作合法但没有匹配或没有产生内容变化。</summary>
    public const string NoOp = "no_op";

    /// <summary>操作校验或执行失败。</summary>
    public const string Failed = "failed";

    /// <summary>有序批次因其他项失败而没有提交。</summary>
    public const string NotAttempted = "not_attempted";
}

/// <summary>
/// 混合批量写的单项结果。
/// </summary>
/// <param name="Index">原始请求中的零基序号。</param>
/// <param name="Operation">稳定操作名称。</param>
/// <param name="Id">操作目标或 upsert 后的文档 ID。</param>
/// <param name="Status">单项状态。</param>
/// <param name="Inserted">插入数量。</param>
/// <param name="Matched">匹配数量。</param>
/// <param name="Modified">修改数量。</param>
/// <param name="Deleted">删除数量。</param>
/// <param name="UpsertedId">发生 upsert 时的新文档 ID。</param>
/// <param name="Error">当前项的错误或警告。</param>
public sealed record DocumentBulkWriteItemResult(
    int Index,
    string Operation,
    string? Id,
    string Status,
    int Inserted = 0,
    int Matched = 0,
    int Modified = 0,
    int Deleted = 0,
    string? UpsertedId = null,
    DocumentWriteError? Error = null);

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
/// 单个文档写入请求。
/// </summary>
/// <param name="Id">文档 ID。</param>
/// <param name="Json">JSON 文档文本。</param>
/// <param name="ExpectedVersion">可选的预期文档版本；不匹配时返回 write_conflict。</param>
public sealed record DocumentWriteRequest(
    string Id,
    string Json,
    long? ExpectedVersion = null);

/// <summary>
/// 文档批量写中的单项错误。
/// </summary>
/// <param name="Index">原始批量请求中的零基序号。</param>
/// <param name="Id">发生错误的文档 ID；请求 ID 无效时为 null。</param>
/// <param name="Code">稳定错误码。</param>
/// <param name="Message">面向调用方的错误说明。</param>
/// <param name="Severity">错误或警告级别。</param>
public sealed record DocumentWriteError(
    int Index,
    string? Id,
    string Code,
    string Message,
    string Severity = DocumentWriteErrorSeverity.Error);

/// <summary>
/// 文档写入执行结果。
/// </summary>
public sealed class DocumentWriteResult
{
    /// <summary>
    /// 初始化文档写入执行结果。
    /// </summary>
    /// <param name="inserted">插入文档数。</param>
    /// <param name="matched">匹配到的已有文档数。</param>
    /// <param name="modified">实际修改的已有文档数。</param>
    /// <param name="deleted">删除文档数。</param>
    /// <param name="errors">批量写中的单项错误。</param>
    /// <param name="items">混合批量写逐项结果。</param>
    /// <param name="requestId">调用方幂等请求 ID。</param>
    /// <param name="replayed">是否重放持久化结果。</param>
    /// <param name="committed">成功项是否已提交。</param>
    public DocumentWriteResult(
        int inserted = 0,
        int matched = 0,
        int modified = 0,
        int deleted = 0,
        IReadOnlyList<DocumentWriteError>? errors = null,
        IReadOnlyList<DocumentBulkWriteItemResult>? items = null,
        string? requestId = null,
        bool replayed = false,
        bool committed = true)
    {
        Inserted = inserted;
        Matched = matched;
        Modified = modified;
        Deleted = deleted;
        Errors = errors ?? Array.Empty<DocumentWriteError>();
        Items = items ?? Array.Empty<DocumentBulkWriteItemResult>();
        RequestId = requestId;
        Replayed = replayed;
        Committed = committed;
    }

    /// <summary>
    /// 创建旧式同类批量写结果，保留既有二进制构造入口。
    /// </summary>
    /// <param name="inserted">插入文档数。</param>
    /// <param name="matched">匹配到的已有文档数。</param>
    /// <param name="modified">实际修改的已有文档数。</param>
    /// <param name="deleted">删除文档数。</param>
    /// <param name="errors">批量写中的单项错误。</param>
    public DocumentWriteResult(
        int inserted,
        int matched,
        int modified,
        int deleted,
        IReadOnlyList<DocumentWriteError>? errors)
        : this(inserted, matched, modified, deleted, errors, null, null, false, true)
    {
    }

    /// <summary>插入文档数。</summary>
    public int Inserted { get; }

    /// <summary>匹配到的已有文档数。</summary>
    public int Matched { get; }

    /// <summary>实际修改的已有文档数。</summary>
    public int Modified { get; }

    /// <summary>删除文档数。</summary>
    public int Deleted { get; }

    /// <summary>批量写中的单项错误。</summary>
    public IReadOnlyList<DocumentWriteError> Errors { get; }

    /// <summary>混合批量写的逐项结果；旧式同类批量写可为空。</summary>
    public IReadOnlyList<DocumentBulkWriteItemResult> Items { get; }

    /// <summary>调用方提供的幂等请求 ID。</summary>
    public string? RequestId { get; }

    /// <summary>是否从持久化幂等日志直接重放结果。</summary>
    public bool Replayed { get; }

    /// <summary>本结果中的成功项是否作为一个 collection 内原子批次提交。</summary>
    public bool Committed { get; }

    /// <summary>是否包含单项写入错误。</summary>
    public bool HasErrors => Errors.Any(static error => string.Equals(error.Severity, DocumentWriteErrorSeverity.Error, StringComparison.Ordinal));

    /// <summary>是否包含单项写入警告。</summary>
    public bool HasWarnings => Errors.Any(static error => string.Equals(error.Severity, DocumentWriteErrorSeverity.Warning, StringComparison.Ordinal));

    internal int Affected => Inserted + Modified + Deleted;

    internal DocumentUpdateResult ToUpdateResult() => new(Matched, Modified, Inserted);
}
