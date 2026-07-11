using System.Globalization;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 统一 SQL 执行器的标量相等与排序语义。
/// </summary>
internal static class SqlScalarComparer
{
    /// <summary>
    /// 判断两个 SQL 标量值是否相等。
    /// </summary>
    public static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return leftBytes.AsSpan().SequenceEqual(rightBytes);

        if (TryCompareTemporal(left, right, out var temporalComparison))
            return temporalComparison == 0;

        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .Equals(Convert.ToDouble(right, CultureInfo.InvariantCulture));

        return Equals(left, right);
    }

    /// <summary>
    /// 比较两个 SQL 标量值；任一值为 <see langword="null"/> 时返回 <see langword="null"/>。
    /// </summary>
    public static int? Compare(object? left, object? right)
    {
        if (left is null || right is null)
            return null;

        if (TryCompareTemporal(left, right, out var temporalComparison))
            return temporalComparison;

        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));

        if (left is string leftString && right is string rightString)
            return string.Compare(leftString, rightString, StringComparison.Ordinal);

        if (left is bool leftBool && right is bool rightBool)
            return leftBool.CompareTo(rightBool);

        throw new InvalidOperationException($"无法比较 {left.GetType().Name} 与 {right.GetType().Name}。");
    }

    private static bool TryCompareTemporal(object left, object right, out int comparison)
    {
        var leftIsTemporal = TryGetUnixTimeMilliseconds(left, out var leftMilliseconds);
        var rightIsTemporal = TryGetUnixTimeMilliseconds(right, out var rightMilliseconds);

        if (leftIsTemporal && rightIsTemporal)
        {
            comparison = leftMilliseconds.CompareTo(rightMilliseconds);
            return true;
        }

        if (leftIsTemporal && right is long rightUnixMilliseconds)
        {
            comparison = leftMilliseconds.CompareTo(rightUnixMilliseconds);
            return true;
        }

        if (rightIsTemporal && left is long leftUnixMilliseconds)
        {
            comparison = leftUnixMilliseconds.CompareTo(rightMilliseconds);
            return true;
        }

        comparison = 0;
        return false;
    }

    private static bool TryGetUnixTimeMilliseconds(object value, out long milliseconds)
    {
        switch (value)
        {
            case DateTime dateTime:
                var utc = dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime.ToUniversalTime();
                milliseconds = new DateTimeOffset(utc).ToUnixTimeMilliseconds();
                return true;
            case DateTimeOffset dateTimeOffset:
                milliseconds = dateTimeOffset.ToUnixTimeMilliseconds();
                return true;
            default:
                milliseconds = 0;
                return false;
        }
    }

    private static bool IsNumeric(object value) => value is
        byte or sbyte or
        short or ushort or
        int or uint or
        long or ulong or
        float or double or decimal;
}
