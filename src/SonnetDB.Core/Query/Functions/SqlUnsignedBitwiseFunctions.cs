namespace SonnetDB.Query.Functions;

/// <summary>使用 UInt64 运算并以既有 Decimal 无损承载完整无符号位运算结果。</summary>
internal static class SqlUnsignedBitwiseFunctions
{
    /// <summary>计算两个非负整数的 64 位无符号按位与，任一 SQL NULL 传播。</summary>
    internal static object? BitAnd(IReadOnlyList<object?> arguments)
        => Evaluate(arguments, "bit_and_uint64", and: true);

    /// <summary>计算两个非负整数的 64 位无符号按位或，任一 SQL NULL 传播。</summary>
    internal static object? BitOr(IReadOnlyList<object?> arguments)
        => Evaluate(arguments, "bit_or_uint64", and: false);

    private static object? Evaluate(IReadOnlyList<object?> arguments, string name, bool and)
    {
        if (arguments[0] is null or DBNull || arguments[1] is null or DBNull)
            return null;

        ulong left = ReadUnsignedInteger(arguments[0]!, name);
        ulong right = ReadUnsignedInteger(arguments[1]!, name);
        // Decimal 能精确承载 UInt64 全值域；转换前已校验范围及整数性，禁止 Double 或 Int64 中转。
        return (decimal)(and ? left & right : left | right);
    }

    private static ulong ReadUnsignedInteger(object value, string name) => value switch
    {
        byte integer => integer,
        sbyte integer when integer >= 0 => (ulong)integer,
        ushort integer => integer,
        short integer when integer >= 0 => (ulong)integer,
        uint integer => integer,
        int integer when integer >= 0 => (ulong)integer,
        ulong integer => integer,
        long integer when integer >= 0 => (ulong)integer,
        decimal integer when integer >= 0m && integer <= ulong.MaxValue
            && decimal.Truncate(integer) == integer => (ulong)integer,
        _ => throw new InvalidOperationException(
            $"函数 {name} 只接受 0..18446744073709551615 范围内的整数或整数值 DECIMAL；不接受浮点、字符串、小数、负值或溢出。"),
    };
}
