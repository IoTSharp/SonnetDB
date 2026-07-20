using System.Globalization;

namespace SonnetDB.Query.Functions;

/// <summary>
/// SonnetDB SQL 日期时间标量函数的共享实现。
/// </summary>
internal static class SqlDateTimeFunctions
{
    internal static object CurrentDateTime(IReadOnlyList<object?> arguments)
        => DateTime.Now;

    internal static object CurrentUtcDateTime(IReadOnlyList<object?> arguments)
        => DateTime.UtcNow;

    internal static object CurrentDateTimeOffset(IReadOnlyList<object?> arguments)
        => DateTimeOffset.Now;

    internal static object CurrentUtcDateTimeOffset(IReadOnlyList<object?> arguments)
        => DateTimeOffset.UtcNow;

    internal static object? DateOnly(IReadOnlyList<object?> arguments)
    {
        return arguments[0] switch
        {
            null => null,
            DateTime value => value.Date,
            DateTimeOffset value => value.Date,
            long unixMilliseconds => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime.Date,
            var value => throw InvalidTemporalArgument("date_only", value),
        };
    }

    internal static object? DatePart(IReadOnlyList<object?> arguments)
    {
        var valueArgument = arguments[1];
        if (valueArgument is null)
            return null;

        string part = RequirePart(arguments[0], "date_part");
        DateTime value = ToDateTime(valueArgument, "date_part");
        return part switch
        {
            "year" => value.Year,
            "quarter" => ((value.Month - 1) / 3) + 1,
            "month" => value.Month,
            "day" => value.Day,
            "day_of_year" or "dayofyear" => value.DayOfYear,
            "day_of_week" or "dayofweek" => (int)value.DayOfWeek,
            "hour" => value.Hour,
            "minute" => value.Minute,
            "second" => value.Second,
            "millisecond" => value.Millisecond,
            "microsecond" => value.Microsecond,
            "nanosecond" => value.Nanosecond,
            _ => throw new InvalidOperationException($"函数 date_part 不支持日期分量 '{part}'。"),
        };
    }

    internal static object? DateAdd(IReadOnlyList<object?> arguments)
    {
        var valueArgument = arguments[0];
        var amount = arguments[1];
        if (valueArgument is null || amount is null)
            return null;

        string part = RequirePart(arguments[2], "date_add");
        return valueArgument switch
        {
            DateTime value => Add(value, amount, part),
            DateTimeOffset value => Add(value, amount, part),
            long unixMilliseconds => Add(
                DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime,
                amount,
                part),
            var value => throw InvalidTemporalArgument("date_add", value),
        };
    }

    internal static object? DateAddDateTime(IReadOnlyList<object?> arguments)
    {
        var valueArgument = arguments[0];
        var amount = arguments[1];
        if (valueArgument is null || amount is null)
            return null;

        string part = RequirePart(arguments[2], "date_add_datetime");
        DateTime value = valueArgument switch
        {
            DateTime dateTime => dateTime,
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
            long unixMilliseconds => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime,
            var invalid => throw InvalidTemporalArgument("date_add_datetime", invalid),
        };
        return Add(value, amount, part);
    }

    internal static object? DateAddDateTimeOffset(IReadOnlyList<object?> arguments)
    {
        var valueArgument = arguments[0];
        var amount = arguments[1];
        if (valueArgument is null || amount is null)
            return null;

        string part = RequirePart(arguments[2], "date_add_datetime_offset");
        DateTimeOffset value = valueArgument switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime),
            long unixMilliseconds => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds),
            var invalid => throw InvalidTemporalArgument("date_add_datetime_offset", invalid),
        };
        return Add(value, amount, part);
    }

    internal static object? ToUnixTimeMilliseconds(IReadOnlyList<object?> arguments)
    {
        return arguments[0] switch
        {
            null => null,
            long unixMilliseconds => unixMilliseconds,
            DateTime value => new DateTimeOffset(
                value.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                    : value).ToUnixTimeMilliseconds(),
            DateTimeOffset value => value.ToUnixTimeMilliseconds(),
            var value => throw InvalidTemporalArgument("to_unix_milliseconds", value),
        };
    }

    internal static object? ToUnixTimeSeconds(IReadOnlyList<object?> arguments)
    {
        return arguments[0] switch
        {
            null => null,
            long unixMilliseconds => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).ToUnixTimeSeconds(),
            DateTime value => new DateTimeOffset(
                value.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                    : value).ToUnixTimeSeconds(),
            DateTimeOffset value => value.ToUnixTimeSeconds(),
            var value => throw InvalidTemporalArgument("to_unix_seconds", value),
        };
    }

    internal static object? ToDateTime(IReadOnlyList<object?> arguments)
    {
        return arguments[0] switch
        {
            null => null,
            DateTime value => DateTime.SpecifyKind(value, DateTimeKind.Unspecified),
            DateTimeOffset value => value.DateTime,
            long unixMilliseconds => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).DateTime,
            var value => throw InvalidTemporalArgument("to_datetime", value),
        };
    }

    internal static object? ToUtcDateTime(IReadOnlyList<object?> arguments)
    {
        return arguments[0] switch
        {
            null => null,
            DateTime value => NormalizeUtc(value),
            DateTimeOffset value => value.UtcDateTime,
            long unixMilliseconds => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime,
            var value => throw InvalidTemporalArgument("to_utc_datetime", value),
        };
    }

    internal static object? ToLocalDateTime(IReadOnlyList<object?> arguments)
    {
        return arguments[0] switch
        {
            null => null,
            DateTime value => NormalizeUtc(value).ToLocalTime(),
            DateTimeOffset value => value.LocalDateTime,
            long unixMilliseconds => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).LocalDateTime,
            var value => throw InvalidTemporalArgument("to_local_datetime", value),
        };
    }

    private static DateTime Add(DateTime value, object amount, string part)
        => part switch
        {
            "year" => value.AddYears(RequireInt32(amount, "date_add")),
            "month" => value.AddMonths(RequireInt32(amount, "date_add")),
            "day" => value.AddDays(RequireDouble(amount, "date_add")),
            "hour" => value.AddHours(RequireDouble(amount, "date_add")),
            "minute" => value.AddMinutes(RequireDouble(amount, "date_add")),
            "second" => value.AddSeconds(RequireDouble(amount, "date_add")),
            "millisecond" => value.AddMilliseconds(RequireDouble(amount, "date_add")),
            "microsecond" => value.AddMicroseconds(RequireDouble(amount, "date_add")),
            "tick" => value.AddTicks(RequireInt64(amount, "date_add")),
            _ => throw new InvalidOperationException($"函数 date_add 不支持日期分量 '{part}'。"),
        };

    private static DateTimeOffset Add(DateTimeOffset value, object amount, string part)
        => part switch
        {
            "year" => value.AddYears(RequireInt32(amount, "date_add")),
            "month" => value.AddMonths(RequireInt32(amount, "date_add")),
            "day" => value.AddDays(RequireDouble(amount, "date_add")),
            "hour" => value.AddHours(RequireDouble(amount, "date_add")),
            "minute" => value.AddMinutes(RequireDouble(amount, "date_add")),
            "second" => value.AddSeconds(RequireDouble(amount, "date_add")),
            "millisecond" => value.AddMilliseconds(RequireDouble(amount, "date_add")),
            "microsecond" => value.AddMicroseconds(RequireDouble(amount, "date_add")),
            "tick" => value.AddTicks(RequireInt64(amount, "date_add")),
            _ => throw new InvalidOperationException($"函数 date_add 不支持日期分量 '{part}'。"),
        };

    private static DateTime ToDateTime(object value, string functionName)
        => value switch
        {
            DateTime dateTime => dateTime,
            DateTimeOffset dateTimeOffset => dateTimeOffset.DateTime,
            long unixMilliseconds => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime,
            _ => throw InvalidTemporalArgument(functionName, value),
        };

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

    private static string RequirePart(object? value, string functionName)
    {
        if (value is not string part || string.IsNullOrWhiteSpace(part))
            throw new InvalidOperationException($"函数 {functionName} 的日期分量必须是非空字符串。");

        return part.Trim().Replace('-', '_').ToLowerInvariant();
    }

    private static double RequireDouble(object value, string functionName)
    {
        return value switch
        {
            byte number => number,
            sbyte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            float number => number,
            double number => number,
            decimal number => (double)number,
            _ => throw new InvalidOperationException($"函数 {functionName} 的增量必须是数值。"),
        };
    }

    private static int RequireInt32(object value, string functionName)
        => checked((int)RequireInt64(value, functionName));

    private static long RequireInt64(object value, string functionName)
    {
        if (value is byte or sbyte or short or ushort or int or uint or long)
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);

        if (value is ulong unsigned)
            return checked((long)unsigned);

        if (value is decimal decimalNumber
            && decimalNumber == decimal.Truncate(decimalNumber))
        {
            return checked((long)decimalNumber);
        }

        double number = RequireDouble(value, functionName);
        if (!double.IsFinite(number) || number != Math.Truncate(number))
            throw new InvalidOperationException($"函数 {functionName} 的 year、month 和 tick 增量必须是整数。");
        return checked((long)number);
    }

    private static InvalidOperationException InvalidTemporalArgument(string functionName, object value)
        => new($"函数 {functionName} 需要 DATETIME 或 Unix 毫秒参数，实际为 {value.GetType().Name}。");
}
