namespace SonnetDB.Exceptions;

/// <summary>独立序列违反持久取号合同时抛出的异常。</summary>
/// <param name="code">稳定错误码。</param>
/// <param name="message">中文诊断。</param>
public sealed class SequenceException(string code, string message) : Exception(message)
{
    /// <summary>序列不存在。</summary>
    public const string NotFound = "sequence_not_found";
    /// <summary>序列名称已存在。</summary>
    public const string AlreadyExists = "sequence_already_exists";
    /// <summary>序列耗尽 Int64 范围。</summary>
    public const string Exhausted = "sequence_exhausted";
    /// <summary>定义、引用或调用形式无效。</summary>
    public const string InvalidDefinition = "sequence_invalid_definition";
    /// <summary>持久化结果未知，必须重开恢复。</summary>
    public const string RecoveryRequired = "sequence_recovery_required";
    /// <summary>稳定机器错误码。</summary>
    public string Code { get; } = code;
}
