using SonnetDB.Sql.Execution;

namespace SonnetDB.Query.Functions;

/// <summary>使用明确的 ASCII 语法解析无符号整数，并以现有 Decimal 无损承载结果。</summary>
internal static class SqlUnsignedIntegerFunctions
{
    /// <summary>解析最多 1024 字符的 UInt64 文本；首尾 ASCII 空白及一个前导加号可选，NULL 传播。</summary>
    internal static object? ParseUInt64(IReadOnlyList<object?> arguments)
    {
        if (arguments[0] is null or DBNull)
            return null;
        if (arguments[0] is not string text)
            throw new InvalidOperationException("函数 parse_uint64 只接受 STRING 或 NULL，不执行隐式数值转换。");
        if (text.Length is < 1 or > 1024)
            throw new InvalidOperationException("函数 parse_uint64 的文本长度必须在 1..1024 字符的解析预算内。");

        SqlQueryResources.Current?.CancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<char> digits = text.AsSpan();
        int start = 0;
        int end = digits.Length;
        while (start < end && IsAsciiWhiteSpace(digits[start]))
            start++;
        while (end > start && IsAsciiWhiteSpace(digits[end - 1]))
            end--;
        if (start < end && digits[start] == '+')
            start++;
        if (start == end)
            throw new InvalidOperationException("函数 parse_uint64 的文本必须包含至少一个 ASCII 十进制数字。");

        ulong value = 0;
        for (int index = start; index < end; index++)
        {
            if ((index & 63) == 0)
                SqlQueryResources.Current?.CancellationToken.ThrowIfCancellationRequested();
            char character = digits[index];
            if (character is < '0' or > '9')
                throw new InvalidOperationException("函数 parse_uint64 只接受首尾 ASCII 空白、可选前导加号及 ASCII 十进制数字；不接受负号、小数或指数。");
            uint digit = (uint)(character - '0');
            if (value > (ulong.MaxValue - digit) / 10)
                throw new InvalidOperationException("函数 parse_uint64 的数值必须在 0..18446744073709551615 范围内。");
            value = value * 10 + digit;
        }

        // UInt64 全值域可由 scale=0 的 Decimal 精确承载，不经过 Double 或有符号 Int64。
        return (decimal)value;
    }

    private static bool IsAsciiWhiteSpace(char value) => value is ' ' or '\t' or '\r' or '\n' or '\v' or '\f';
}
