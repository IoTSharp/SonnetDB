namespace SonnetDB.Sql;

/// <summary>
/// SQL 词法 token：包含类型、源文本片段以及（按需）已解析的字面量值。
/// </summary>
/// <param name="Kind">Token 类别。</param>
/// <param name="Text">源 SQL 中的 lexeme（对字符串字面量为去引号 + 反转义后的内容；对 duration 为去除单位的数字部分）。</param>
/// <param name="Position">在源 SQL 字符串中的起始字符索引（0-based）。</param>
/// <param name="IntegerValue">当 <see cref="Kind"/> 为 <see cref="TokenKind.IntegerLiteral"/> 或 <see cref="TokenKind.DurationLiteral"/> 时承载的整数值（duration 已转换为毫秒）。</param>
/// <param name="DoubleValue">当 <see cref="Kind"/> 为 <see cref="TokenKind.FloatLiteral"/> 时承载的浮点值。</param>
public readonly record struct Token(
    TokenKind Kind,
    string Text,
    int Position,
    long IntegerValue = 0,
    double DoubleValue = 0)
{
    /// <summary>返回便于诊断的 token 描述。</summary>
    public override string ToString() => Kind switch
    {
        TokenKind.IdentifierLiteral => $"Ident({Text})",
        TokenKind.IntegerLiteral => $"Int({IntegerValue})",
        TokenKind.Int64MinMagnitudeLiteral => $"IntMagnitude({Text})",
        TokenKind.FloatLiteral => $"Float({DoubleValue})",
        TokenKind.StringLiteral => $"String('{Text}')",
        TokenKind.DurationLiteral => $"Duration({IntegerValue}ms)",
        _ => Kind.ToString(),
    };
}
