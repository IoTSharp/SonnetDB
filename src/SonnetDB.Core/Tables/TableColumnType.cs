namespace SonnetDB.Tables;

/// <summary>
/// 关系表列的数据类型。
/// </summary>
public enum TableColumnType : byte
{
    /// <summary>64 位有符号整数。</summary>
    Int64 = 1,

    /// <summary>64 位双精度浮点数。</summary>
    Float64 = 2,

    /// <summary>布尔值。</summary>
    Boolean = 3,

    /// <summary>UTF-8 字符串。</summary>
    String = 4,

    /// <summary>UTC 时间戳，按 Unix 毫秒持久化。</summary>
    DateTime = 5,

    /// <summary>二进制大对象。</summary>
    Blob = 6,

    /// <summary>JSON 文本。</summary>
    Json = 7,

    /// <summary>固定精度十进制数，使用 System.Decimal 精确保存。</summary>
    Decimal = 8,

    /// <summary>仅时间值，使用 TimeOnly ticks 精确保存。</summary>
    Time = 9,

    /// <summary>带符号时长，使用 TimeSpan 的 100 纳秒 ticks 完整保存。</summary>
    Duration = 10,

    /// <summary>原生 UTC 时刻，按完整 100 纳秒 ticks 保存并返回 DateTimeOffset。</summary>
    DateTimeOffset = 11,
}
