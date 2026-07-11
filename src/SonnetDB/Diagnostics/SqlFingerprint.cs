using System.Security.Cryptography;
using System.Text;
using SonnetDB.Sql;

namespace SonnetDB.Diagnostics;

/// <summary>
/// 把 SQL 归一化为不含字面量的稳定形状，并生成可跨进程比较的短指纹。
/// </summary>
internal static class SqlFingerprint
{
    /// <summary>
    /// 移除注释、统一关键字和标识符大小写，并把值与参数替换为 <c>?</c>。
    /// 解析失败的语句统一归入 <c>&lt;unparsed&gt;</c>，避免诊断聚合泄露字面量。
    /// </summary>
    /// <param name="sql">原始 SQL。</param>
    /// <returns>归一化 SQL。</returns>
    public static string Normalize(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        try
        {
            var tokens = SqlLexer.Tokenize(sql);
            var normalized = new StringBuilder(sql.Length);
            foreach (var token in tokens)
            {
                if (token.Kind == TokenKind.EndOfFile)
                    break;

                if (normalized.Length > 0)
                    normalized.Append(' ');
                normalized.Append(NormalizeToken(token));
            }

            return normalized.Length == 0 ? "<empty>" : normalized.ToString();
        }
        catch (Exception ex) when (ex is SqlParseException or OverflowException or FormatException)
        {
            return "<unparsed>";
        }
    }

    /// <summary>
    /// 计算归一化 SQL 的 SHA-256 前 64 位十六进制指纹。
    /// </summary>
    /// <param name="normalizedSql">归一化 SQL。</param>
    /// <returns>16 个小写十六进制字符。</returns>
    public static string Compute(string normalizedSql)
    {
        ArgumentNullException.ThrowIfNull(normalizedSql);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSql), hash);
        return Convert.ToHexStringLower(hash[..8]);
    }

    private static string NormalizeToken(Token token)
    {
        if ((int)token.Kind >= (int)TokenKind.KeywordCreate)
            return token.Kind is TokenKind.KeywordTrue or TokenKind.KeywordFalse
                ? "?"
                : token.Text.ToUpperInvariant();

        return token.Kind switch
        {
            TokenKind.IntegerLiteral or
            TokenKind.FloatLiteral or
            TokenKind.StringLiteral or
            TokenKind.DurationLiteral or
            TokenKind.Parameter => "?",
            // SonnetDB 标识符区分大小写，只统一关键字，不折叠对象名。
            TokenKind.IdentifierLiteral => token.Text,
            _ => token.Text,
        };
    }
}
