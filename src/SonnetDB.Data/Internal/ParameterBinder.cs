using System.Globalization;
using System.Text;

using SonnetDB.Model;

namespace SonnetDB.Data.Internal;

/// <summary>
/// 把 <c>@name</c> / <c>:name</c> 占位符按字面量安全替换到 SQL 文本中。
/// 已通过状态机跳过字符串字面量与双引号标识符内的内容。
/// </summary>
internal static class ParameterBinder
{
    public static string Bind(string sql, SndbParameterCollection parameters)
    {
        if (sql.IndexOf('@') < 0 && sql.IndexOf(':') < 0)
            return sql;

        var byName = new Dictionary<string, SndbParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parameters.Items)
        {
            var n = SndbParameterCollection.NormalizeName(p.ParameterName);
            if (string.IsNullOrEmpty(n))
                throw new InvalidOperationException("参数名不能为空。");
            byName[n] = p;
        }

        var sb = new StringBuilder(sql.Length + 16);
        int i = 0;
        while (i < sql.Length)
        {
            char ch = sql[i];

            // 跳过 '...' 字符串字面量（' 内的 '' 视为转义）
            if (ch == '\'')
            {
                sb.Append(ch); i++;
                while (i < sql.Length)
                {
                    char c = sql[i++];
                    sb.Append(c);
                    if (c == '\'')
                    {
                        if (i < sql.Length && sql[i] == '\'') { sb.Append(sql[i++]); continue; }
                        break;
                    }
                }
                continue;
            }

            // 跳过 "..." 双引号标识符
            if (ch == '"')
            {
                sb.Append(ch); i++;
                while (i < sql.Length)
                {
                    char c = sql[i++];
                    sb.Append(c);
                    if (c == '"') break;
                }
                continue;
            }

            // 跳过单行注释 -- ...
            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') sb.Append(sql[i++]);
                continue;
            }

            if ((ch == '@' || ch == ':') && i + 1 < sql.Length && IsIdentStart(sql[i + 1]))
            {
                int start = i + 1;
                int end = start;
                while (end < sql.Length && IsIdentPart(sql[end])) end++;
                string name = sql.Substring(start, end - start);
                if (!byName.TryGetValue(name, out var p))
                    throw new InvalidOperationException($"未提供参数 '{ch}{name}' 的值。");
                sb.Append(FormatLiteral(p.Value));
                i = end;
                continue;
            }

            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    internal static string FormatLiteral(object? value)
    {
        if (value is null || value is DBNull)
            return "NULL";

        return value switch
        {
            string s => "'" + s.Replace("'", "''") + "'",
            bool b => b ? "true" : "false",
            byte u8 => u8.ToString(CultureInfo.InvariantCulture),
            short i16 => i16.ToString(CultureInfo.InvariantCulture),
            int i32 => i32.ToString(CultureInfo.InvariantCulture),
            long i64 => i64.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            byte[] bytes => "'" + Convert.ToBase64String(bytes) + "'",
            Guid guid => "'" + guid.ToString("D", CultureInfo.InvariantCulture) + "'",
            DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : dt).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            GeoPoint p => string.Create(CultureInfo.InvariantCulture, $"POINT({p.Lat:R}, {p.Lon:R})"),
            _ => throw new NotSupportedException(
                $"不支持的参数类型 '{value.GetType().FullName}'。"),
        };
    }
}
