using System.Globalization;
using System.Text;

using SonnetDB.Model;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;

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

        // 远程协议发送内联字面量，须在日期等类型被通用格式化为整数之前验证删除上限。
        if (sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
            && SqlLexer.Tokenize(sql)[0].Kind == TokenKind.KeywordDelete
            && SqlParser.Parse(sql) is DeleteStatement { RowLimit: ParameterExpression { Name: { } limitName } }
            && byName.TryGetValue(limitName, out var limit))
        {
            if (limit.Value is not (byte or sbyte or short or ushort or int or uint or long or ulong)
                || limit.Value is ulong unsignedValue && unsignedValue > long.MaxValue)
                throw new InvalidOperationException("DELETE LIMIT 参数必须是非负 Int64 范围内的整数。");
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
                sb.Append(FormatLiteral(GetBindingValue(p)));
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

    /// <summary>显式 DbType.Time 沿用一天内钟面合同；默认 TimeSpan 使用完整时长。</summary>
    internal static object? GetBindingValue(SndbParameter parameter)
        => parameter.DbType == System.Data.DbType.Time && parameter.Value is TimeSpan span
            ? TimeOnly.FromTimeSpan(span) : parameter.Value;

    internal static string FormatLiteral(object? value)
    {
        if (value is null || value is DBNull)
            return "NULL";

        // 保留枚举底层整数类型，复用相同的无损格式化与范围限制。
        if (value is Enum enumValue)
            value = Convert.ChangeType(enumValue, enumValue.GetTypeCode(), CultureInfo.InvariantCulture);

        return value switch
        {
            string s => "'" + s.Replace("'", "''") + "'",
            bool b => b ? "true" : "false",
            byte u8 => u8.ToString(CultureInfo.InvariantCulture),
            sbyte i8 => i8.ToString(CultureInfo.InvariantCulture),
            short i16 => i16.ToString(CultureInfo.InvariantCulture),
            ushort u16 => u16.ToString(CultureInfo.InvariantCulture),
            int i32 => i32.ToString(CultureInfo.InvariantCulture),
            uint u32 => u32.ToString(CultureInfo.InvariantCulture),
            long i64 => i64.ToString(CultureInfo.InvariantCulture),
            ulong u64 when u64 <= long.MaxValue => u64.ToString(CultureInfo.InvariantCulture),
            float f => "FLOAT '" + f.ToString("R", CultureInfo.InvariantCulture) + "'",
            double d => "FLOAT '" + d.ToString("R", CultureInfo.InvariantCulture) + "'",
            decimal m => "DECIMAL '" + m.ToString(CultureInfo.InvariantCulture) + "'",
            byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
            Guid guid => "'" + guid.ToString("D", CultureInfo.InvariantCulture) + "'",
            DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : dt).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            TimeOnly time => "'" + time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "'",
            TimeSpan span => "CAST(" + span.Ticks.ToString(CultureInfo.InvariantCulture) + " AS DURATION)",
            GeoPoint p => string.Create(CultureInfo.InvariantCulture, $"POINT({p.Lat:R}, {p.Lon:R})"),
            float[] vector => FormatVectorLiteral(vector),
            Memory<float> vector => FormatVectorLiteral(vector.Span),
            ReadOnlyMemory<float> vector => FormatVectorLiteral(vector.Span),
            _ => throw new NotSupportedException(
                $"不支持的参数类型 '{value.GetType().FullName}'。"),
        };
    }

    private static string FormatVectorLiteral(ReadOnlySpan<float> vector)
    {
        if (vector.Length == 0)
            throw new ArgumentException("VECTOR 参数不能为空。", nameof(vector));

        var builder = new StringBuilder(vector.Length * 8 + 2);
        builder.Append('[');
        for (int i = 0; i < vector.Length; i++)
        {
            if (!float.IsFinite(vector[i]))
                throw new ArgumentException("VECTOR 参数只能包含有限浮点数。", nameof(vector));
            if (i != 0)
                builder.Append(", ");
            builder.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }
        builder.Append(']');
        return builder.ToString();
    }
}
