namespace SonnetDB.Documents;

/// <summary>
/// Document Store 游标错误码。
/// </summary>
public static class DocumentCursorErrorCodes
{
    /// <summary>游标 token 格式、版本或签名无效。</summary>
    public const string InvalidToken = "document_cursor_invalid";

    /// <summary>游标不属于当前集合或查询形状。</summary>
    public const string QueryMismatch = "document_cursor_mismatch";

    /// <summary>游标已超过有效期。</summary>
    public const string Expired = "document_cursor_expired";

    /// <summary>游标绑定的集合快照已发生变化。</summary>
    public const string SnapshotStale = "document_cursor_stale";

    /// <summary>同一客户端游标正在执行另一个读取。</summary>
    public const string ConcurrentRead = "document_cursor_concurrent_read";

    /// <summary>
    /// 判断错误码是否属于 Document Store 游标错误。
    /// </summary>
    /// <param name="code">待判断的错误码。</param>
    /// <returns>属于已知游标错误时返回 <c>true</c>。</returns>
    public static bool IsCursorError(string? code)
        => code is InvalidToken or QueryMismatch or Expired or SnapshotStale or ConcurrentRead;
}

/// <summary>
/// Document Store 游标无法继续读取时抛出的异常。
/// </summary>
public sealed class DocumentCursorException : InvalidOperationException
{
    /// <summary>
    /// 创建游标异常。
    /// </summary>
    /// <param name="code">稳定的机器可读错误码。</param>
    /// <param name="message">面向调用方的错误说明。</param>
    public DocumentCursorException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>
    /// 创建包含内部异常的游标异常。
    /// </summary>
    /// <param name="code">稳定的机器可读错误码。</param>
    /// <param name="message">面向调用方的错误说明。</param>
    /// <param name="innerException">导致游标失败的内部异常。</param>
    public DocumentCursorException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(innerException);
        Code = code;
    }

    /// <summary>稳定的机器可读错误码。</summary>
    public string Code { get; }
}
