namespace SonnetDB.Sql;

/// <summary>
/// SQL 词法或语法分析阶段抛出的异常。
/// </summary>
public sealed class SqlParseException : Exception
{
    /// <summary>SQL 解析失败的稳定错误码。</summary>
    public const string DefaultErrorCode = "sql_parse_error";

    /// <summary>SQL 解析阶段的稳定操作名。</summary>
    public const string DefaultOperation = "parse";

    /// <summary>错误在源 SQL 中的字符索引（0-based）。</summary>
    public int Position { get; }

    /// <summary>稳定机器可读错误码。</summary>
    public string Code { get; }

    /// <summary>产生错误的稳定 SQL 操作阶段。</summary>
    public string Operation { get; }

    /// <summary>不包含 SQL 参数或行内容的可行动提示。</summary>
    public string? Hint { get; }

    /// <summary>构造一个新的 <see cref="SqlParseException"/>。</summary>
    /// <param name="message">错误消息（中文）。</param>
    /// <param name="position">在源 SQL 中的字符索引。</param>
    public SqlParseException(string message, int position)
        : base($"{message}（位置 {position}）")
    {
        // 保留历史构造器对 position 和 message 的接受范围；新增诊断字段只扩展可观察信息。
        Position = position;
        Code = DefaultErrorCode;
        Operation = DefaultOperation;
        Hint = SqlErrorHints.ForParseMessage(message ?? string.Empty);
    }

    /// <summary>
    /// 构造包含稳定错误合同的 SQL 解析异常。
    /// </summary>
    /// <param name="message">错误消息（中文）。</param>
    /// <param name="position">在源 SQL 中的字符索引。</param>
    /// <param name="code">稳定机器可读错误码。</param>
    /// <param name="operation">产生错误的 SQL 操作阶段。</param>
    /// <param name="hint">不包含 SQL 参数或行内容的可行动提示；为空时按消息生成通用提示。</param>
    public SqlParseException(
        string message,
        int position,
        string code,
        string operation,
        string? hint)
        : base($"{message}（位置 {position}）")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Position = position;
        Code = code;
        Operation = operation;
        Hint = string.IsNullOrWhiteSpace(hint)
            ? SqlErrorHints.ForParseMessage(message)
            : hint;
    }
}

/// <summary>
/// SQL 解析和执行错误使用的稳定错误码。
/// </summary>
public static class SqlErrorCodes
{
    /// <summary>SQL 词法或语法解析失败。</summary>
    public const string Parse = SqlParseException.DefaultErrorCode;

    /// <summary>SQL 执行阶段失败。</summary>
    public const string Execution = "sql_execution_error";

    /// <summary>SQL 执行被调用方取消。</summary>
    public const string Cancelled = "sql_cancelled";

    /// <summary>SQL 执行超过时间预算。</summary>
    public const string Timeout = "sql_timeout";

    /// <summary>SELECT 锁定读未提供行锁语义。</summary>
    public const string LockingReadUnsupported = "sql_locking_read_unsupported";
}

/// <summary>
/// SQL 解析失败时生成不泄露请求内容的提示。
/// </summary>
internal static class SqlErrorHints
{
    /// <summary>按解析消息选择可行动提示。</summary>
    public static string ForParseMessage(string message)
    {
        if (message.Contains("未闭合", StringComparison.Ordinal))
            return "补齐引号、括号或注释后重试。";

        if (message.Contains("无法识别", StringComparison.Ordinal)
            || message.Contains("非法", StringComparison.Ordinal)
            || message.Contains("无效", StringComparison.Ordinal))
            return "检查当前位置的字符、字面量格式和标点。";

        if (message.StartsWith("期望", StringComparison.Ordinal))
            return "检查关键字、标识符和子句顺序。";

        if (message.Contains("不支持", StringComparison.Ordinal))
            return "改用当前 SQL 合同支持的语法。";

        if (message.Contains("至少", StringComparison.Ordinal)
            || message.Contains("必须", StringComparison.Ordinal)
            || message.Contains("缺少", StringComparison.Ordinal))
            return "补充缺少的参数或值，并确认其类型。";

        if (message.Contains("重复", StringComparison.Ordinal))
            return "删除重复项，或使用别名区分输出列。";

        return "检查 SQL 语法和当前语句支持范围。";
    }
}

/// <summary>
/// 可跨嵌入式、ADO.NET 和远程传输复用的 SQL 错误结果。
/// </summary>
public sealed class SqlErrorInfo
{
    /// <summary>创建 SQL 错误结果。</summary>
    /// <param name="code">稳定机器可读错误码。</param>
    /// <param name="message">与既有异常兼容的错误消息。</param>
    /// <param name="operation">失败的 SQL 操作阶段或操作名。</param>
    /// <param name="position">SQL 字符位置（0-based）；执行错误通常为空。</param>
    /// <param name="hint">不包含请求内容的可行动提示。</param>
    public SqlErrorInfo(
        string code,
        string message,
        string operation,
        int? position = null,
        string? hint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (position is < 0)
            throw new ArgumentOutOfRangeException(nameof(position));

        Code = code;
        Message = message;
        Operation = operation;
        Position = position;
        Hint = hint;
    }

    /// <summary>稳定机器可读错误码。</summary>
    public string Code { get; }

    /// <summary>面向调用方的错误消息。</summary>
    public string Message { get; }

    /// <summary>失败的 SQL 操作阶段或操作名。</summary>
    public string Operation { get; }

    /// <summary>SQL 字符位置（0-based）；执行错误通常为空。</summary>
    public int? Position { get; }

    /// <summary>不包含请求内容的可行动提示。</summary>
    public string? Hint { get; }
}
