namespace SonnetDB.Tables;

/// <summary>关系日期在旧毫秒格式与精确 ticks 格式之间的无损边界。</summary>
internal static class TableDateTimeValues
{
    internal static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    internal static long ToPreciseTicks(object value)
        => value is DateTime dateTime ? NormalizeUtc(dateTime).Ticks
            : throw new InvalidOperationException("DATETIME2 需要原生 DateTime 值；其它类型须先显式 CAST。");

    internal static long ToLegacyMilliseconds(object value)
    {
        if (value is long milliseconds)
            return milliseconds;
        DateTime normalized = value switch
        {
            DateTime dateTime => NormalizeUtc(dateTime),
            DateTimeOffset offset => offset.UtcDateTime,
            _ => throw new InvalidOperationException($"无法把 {value.GetType().Name} 转换为 DATETIME。"),
        };
        if (normalized.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new InvalidOperationException("DATETIME 仅支持毫秒精度；请使用 DATETIME2。");
        return new DateTimeOffset(normalized).ToUnixTimeMilliseconds();
    }
}
