namespace SonnetDB.Exceptions;

/// <summary>SQL 参数类型不在 SonnetDB 的可表示范围内。</summary>
public sealed class SndbParameterTypeException : Exception
{
    /// <summary>BigInteger 必须由应用层显式转换时使用的稳定错误码。</summary>
    public const string BigIntegerUnsupportedCode = "big_integer_unsupported";

    /// <summary>机器可读的稳定错误码。</summary>
    public string Code { get; }

    /// <summary>构造参数类型异常。</summary>
    /// <param name="code">机器可读的稳定错误码。</param>
    /// <param name="message">不包含参数值的错误说明。</param>
    public SndbParameterTypeException(string code, string message) : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
    }
}
