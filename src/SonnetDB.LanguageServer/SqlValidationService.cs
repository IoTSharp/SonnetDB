using SonnetDB.Sql;

namespace SonnetDB.LanguageServer;

/// <summary>
/// 使用 SonnetDB 核心解析器检查 SQL 词法与语法错误。
/// </summary>
public static class SqlValidationService
{
    /// <summary>
    /// 解析一段 SQL 脚本并返回首个阻断性诊断；合法脚本返回空集合。
    /// </summary>
    /// <param name="text">待检查的 SQL 脚本文本。</param>
    /// <returns>按源码位置排序的诊断集合。</returns>
    public static IReadOnlyList<LanguageDiagnostic> Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            _ = SqlParser.ParseScript(text);
            return [];
        }
        catch (SqlParseException exception)
        {
            int offset = Math.Clamp(exception.Position, 0, text.Length);
            int length = offset < text.Length ? 1 : 0;
            return [new LanguageDiagnostic(DiagnosticSeverity.Error, exception.Message, offset, length)];
        }
    }
}
