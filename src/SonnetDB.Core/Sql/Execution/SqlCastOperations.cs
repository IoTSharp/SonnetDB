using System.Globalization;
using System.Text;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>统一实现 SQL CAST 的显式类型转换语义。</summary>
internal static class SqlCastOperations
{
    /// <summary>将执行期值转换为指定 SQL 类型；NULL 保持 NULL。</summary>
    internal static object? Convert(object? value, SqlDataType targetType)
    {
        if (value is null)
            return null;

        try
        {
            return targetType switch
            {
                SqlDataType.Int64 => ToInt64(value),
                SqlDataType.Float64 => ToFloat64(value),
                SqlDataType.Decimal => ToDecimal(value),
                SqlDataType.Boolean => ToBoolean(value),
                SqlDataType.String => ToStringValue(value),
                SqlDataType.DateTime => ToDateTime(value),
                SqlDataType.Time => ToTime(value),
                SqlDataType.Blob => ToBlob(value),
                SqlDataType.Json => ToJson(value),
                SqlDataType.Vector or SqlDataType.GeoPoint => throw new NotSupportedException(
                    $"CAST 当前不支持目标类型 {targetType}。"),
                _ => throw new NotSupportedException($"CAST 当前不支持目标类型 {targetType}。"),
            };
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"无法将值 '{FormatValue(value)}' 转换为 {targetType}。", exception);
        }
    }

    private static long ToInt64(object value)
    {
        if (value is bool boolean)
            return boolean ? 1L : 0L;
        if (value is string text)
        {
            if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                throw new InvalidOperationException($"字符串 '{text}' 不是有效的 Int64。");
            return parsed;
        }
        if (value is DateTime dateTime)
            return new DateTimeOffset(NormalizeUtc(dateTime)).ToUnixTimeMilliseconds();
        if (value is DateTimeOffset dateTimeOffset)
            return dateTimeOffset.ToUnixTimeMilliseconds();
        if (value is decimal decimalValue)
            return checked((long)decimal.Truncate(decimalValue));
        if (value is double doubleValue)
        {
            if (!double.IsFinite(doubleValue) || doubleValue < long.MinValue || doubleValue >= 9.223372036854776E18)
                throw new InvalidOperationException("浮点值超出 Int64 范围或不是有限值。");
            return checked((long)doubleValue);
        }
        if (value is float floatValue)
        {
            if (!float.IsFinite(floatValue) || floatValue < long.MinValue || floatValue >= 9.223372E18f)
                throw new InvalidOperationException("浮点值超出 Int64 范围或不是有限值。");
            return checked((long)floatValue);
        }
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong)
            return checked(System.Convert.ToInt64(value, CultureInfo.InvariantCulture));
        throw new InvalidOperationException($"值类型 {value.GetType().Name} 不能转换为 Int64。");
    }

    private static double ToFloat64(object value)
    {
        if (value is string text)
        {
            if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                || !double.IsFinite(parsed))
                throw new InvalidOperationException($"字符串 '{text}' 不是有效的有限 Float64。");
            return parsed;
        }
        if (value is bool boolean)
            return boolean ? 1d : 0d;
        double converted = value switch
        {
            DateTime dateTime => new DateTimeOffset(NormalizeUtc(dateTime)).ToUnixTimeMilliseconds(),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUnixTimeMilliseconds(),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"值类型 {value.GetType().Name} 不能转换为 Float64。"),
        };
        if (!double.IsFinite(converted))
            throw new InvalidOperationException("转换结果不是有限 Float64。");
        return converted;
    }

    private static decimal ToDecimal(object value)
    {
        if (value is string text)
        {
            if (!decimal.TryParse(text.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out decimal parsed))
            {
                throw new InvalidOperationException($"字符串 '{text}' 不是有效的 DECIMAL。");
            }

            return parsed;
        }

        if (value is bool boolean)
            return boolean ? 1m : 0m;
        if (value is DateTime dateTime)
            return new DateTimeOffset(NormalizeUtc(dateTime)).ToUnixTimeMilliseconds();
        if (value is DateTimeOffset dateTimeOffset)
            return dateTimeOffset.ToUnixTimeMilliseconds();
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal)
        {
            if (value is float floatValue && !float.IsFinite(floatValue)
                || value is double doubleValue && !double.IsFinite(doubleValue))
            {
                throw new InvalidOperationException("浮点值不是有限的 DECIMAL 输入。");
            }

            return System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException($"值类型 {value.GetType().Name} 不能转换为 DECIMAL。");
    }

    private static bool ToBoolean(object value)
    {
        if (value is bool boolean)
            return boolean;
        if (value is string text)
        {
            if (bool.TryParse(text.Trim(), out bool parsed))
                return parsed;
            if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
                return integer switch
                {
                    0 => false,
                    1 => true,
                    _ => throw new InvalidOperationException($"字符串 '{text}' 不是有效的 BOOL。"),
                };
            throw new InvalidOperationException($"字符串 '{text}' 不是有效的 BOOL。");
        }
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong)
        {
            long integer = System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return integer switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidOperationException($"整数值 {integer} 不是有效的 BOOL。"),
            };
        }
        if (value is float or double or decimal)
        {
            double number = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return number switch
            {
                0d => false,
                1d => true,
                _ => throw new InvalidOperationException($"数值 {number.ToString(CultureInfo.InvariantCulture)} 不是有效的 BOOL。"),
            };
        }
        throw new InvalidOperationException($"值类型 {value.GetType().Name} 不能转换为 BOOL。");
    }

    private static string ToStringValue(object value) => value switch
    {
        string text => text,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        DateTime dateTime => NormalizeUtc(dateTime).ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    private static DateTime ToDateTime(object value)
    {
        if (value is DateTime dateTime)
            return NormalizeUtc(dateTime);
        if (value is DateTimeOffset dateTimeOffset)
            return dateTimeOffset.UtcDateTime;
        if (value is string text
            && DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed.UtcDateTime;
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong)
            return DateTimeOffset.FromUnixTimeMilliseconds(System.Convert.ToInt64(value, CultureInfo.InvariantCulture)).UtcDateTime;
        throw new InvalidOperationException($"值 '{FormatValue(value)}' 不是有效的 DATETIME。");
    }

    private static TimeOnly ToTime(object value)
    {
        if (value is TimeOnly time)
            return time;
        if (value is TimeSpan span && span >= TimeSpan.Zero && span < TimeSpan.FromDays(1))
            return TimeOnly.FromTimeSpan(span);
        if (value is string text
            && TimeOnly.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"值 '{FormatValue(value)}' 不是有效的 TIME。仅支持 00:00:00 至 23:59:59.9999999。");
    }

    private static byte[] ToBlob(object value) => value switch
    {
        byte[] bytes => bytes.ToArray(),
        string text => Encoding.UTF8.GetBytes(text),
        _ => throw new InvalidOperationException($"值类型 {value.GetType().Name} 不能转换为 BLOB。"),
    };

    private static string ToJson(object value) => value switch
    {
        string text => text,
        _ => throw new InvalidOperationException($"值类型 {value.GetType().Name} 不能转换为 JSON；JSON CAST 目前仅接受字符串。"),
    };

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

    private static string FormatValue(object value)
        => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty
            : value.ToString() ?? string.Empty;
}
