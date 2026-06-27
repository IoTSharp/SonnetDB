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
    public DocumentWriteResult(
        int inserted = 0,
        int matched = 0,
        int modified = 0,
        int deleted = 0,
        IReadOnlyList<DocumentWriteError>? errors = null)
    {
        Inserted = inserted;
        Matched = matched;
        Modified = modified;
        Deleted = deleted;
        Errors = errors ?? Array.Empty<DocumentWriteError>();
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

    /// <summary>是否包含单项写入错误。</summary>
    public bool HasErrors => Errors.Any(static error => string.Equals(error.Severity, DocumentWriteErrorSeverity.Error, StringComparison.Ordinal));

    /// <summary>是否包含单项写入警告。</summary>
    public bool HasWarnings => Errors.Any(static error => string.Equals(error.Severity, DocumentWriteErrorSeverity.Warning, StringComparison.Ordinal));

    internal int Affected => Inserted + Modified + Deleted;

    internal DocumentUpdateResult ToUpdateResult() => new(Matched, Modified, Inserted);
}
