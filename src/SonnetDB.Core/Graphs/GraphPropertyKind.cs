namespace SonnetDB.Graphs;

/// <summary>
/// 图属性值的持久化标量类型。
/// </summary>
public enum GraphPropertyKind : byte
{
    /// <summary>空值。</summary>
    Null = 0,

    /// <summary>64 位有符号整数。</summary>
    Int64 = 1,

    /// <summary>64 位 IEEE 754 浮点数。</summary>
    Float64 = 2,

    /// <summary>布尔值。</summary>
    Boolean = 3,

    /// <summary>UTF-8 字符串。</summary>
    String = 4,

    /// <summary>精确到毫秒的 UTC 时间。</summary>
    DateTime = 5,

    /// <summary>二进制值。</summary>
    Blob = 6,

    /// <summary>保留原始文本的合法 JSON 值。</summary>
    Json = 7,
}
