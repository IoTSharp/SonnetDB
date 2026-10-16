using System.Diagnostics;
using System.Globalization;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 统一实现 SQL 标量数值运算，保证各数据模型使用相同的 NULL、类型、溢出与除零语义。
/// </summary>
internal static class SqlScalarOperations
{
    /// <summary>在 System.Decimal 的真实值域内精确解析，拒绝自动舍入；输入最多 1024 字符，耗时最多一秒。</summary>
    internal static decimal ParseExactDecimal(string text)
    {
        long started = Stopwatch.GetTimestamp();
        string expected = NormalizeDecimal(text, started);
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
            || !string.Equals(expected, NormalizeDecimal(value.ToString(CultureInfo.InvariantCulture), started), StringComparison.Ordinal))
            throw new InvalidOperationException("DECIMAL 数值超出 System.Decimal 可无损表示的范围或精度。");
        return value;
    }

    /// <summary>只在明确的 DECIMAL 上下文中使用原始数值文本；显式 FLOAT 和 CLR Double 不改类型。</summary>
    internal static SqlExpression InDecimalContext(SqlExpression expression)
    {
        long started = Stopwatch.GetTimestamp();
        int visited = 0;
        return Rewrite(expression, 0);

        SqlExpression Rewrite(SqlExpression node, int depth)
        {
            if (++visited > 4096 || depth > 64 || Stopwatch.GetElapsedTime(started).TotalSeconds > 1)
                throw new InvalidOperationException("DECIMAL 表达式超出有界解析预算。");
            SqlQueryResources.Current?.CancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case LiteralExpression { Kind: SqlLiteralKind.Float, NumericText: not null } literal:
                    return LiteralExpression.Decimal(ParseExactDecimal(literal.NumericText));
                case UnaryExpression { Operator: SqlUnaryOperator.Negate } unary:
                    var operand = Rewrite(unary.Operand, depth + 1);
                    return ReferenceEquals(operand, unary.Operand) ? unary : unary with { Operand = operand };
                case BinaryExpression binary when IsDecimalArithmetic(binary.Operator):
                    var left = Rewrite(binary.Left, depth + 1);
                    var right = Rewrite(binary.Right, depth + 1);
                    return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                        ? binary : binary with { Left = left, Right = right };
                case CastExpression { TargetType: SqlDataType.Decimal } cast:
                    var converted = Rewrite(cast.Operand, depth + 1);
                    return ReferenceEquals(converted, cast.Operand) ? cast : cast with { Operand = converted };
                default:
                    return node;
            }
        }
    }

    /// <summary>只重新求值纯数值常量；动态列、UDF 与子查询保留首次执行结果，最多 8192 节点、一秒。</summary>
    internal static object? ExactDecimalConstant(SqlExpression expression, object? originalValue)
    {
        long started = Stopwatch.GetTimestamp();
        int visited = 0;
        if (!IsPureNumber(expression, 0))
            return originalValue;
        SqlExpression exact = InDecimalContext(expression);
        return ReferenceEquals(exact, expression) ? originalValue : EvaluateNumber(exact, 0);

        void CheckBudget(int depth)
        {
            if (++visited > 8192 || depth > 64 || Stopwatch.GetElapsedTime(started).TotalSeconds > 1)
                throw new InvalidOperationException("DECIMAL 常量超出有界解析预算。");
            SqlQueryResources.Current?.CancellationToken.ThrowIfCancellationRequested();
        }

        bool IsPureNumber(SqlExpression node, int depth)
        {
            CheckBudget(depth);
            return node switch
            {
                LiteralExpression { Kind: SqlLiteralKind.Integer or SqlLiteralKind.Float or SqlLiteralKind.Decimal } => true,
                UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => IsPureNumber(unary.Operand, depth + 1),
                BinaryExpression binary when IsDecimalArithmetic(binary.Operator) =>
                    IsPureNumber(binary.Left, depth + 1) && IsPureNumber(binary.Right, depth + 1),
                CastExpression { TargetType: SqlDataType.Decimal } cast => IsPureNumber(cast.Operand, depth + 1),
                _ => false,
            };
        }

        object? EvaluateNumber(SqlExpression node, int depth)
        {
            CheckBudget(depth);
            return node switch
            {
                LiteralExpression { Kind: SqlLiteralKind.Integer } literal => literal.IntegerValue,
                LiteralExpression { Kind: SqlLiteralKind.Float } literal => literal.FloatValue,
                LiteralExpression { Kind: SqlLiteralKind.Decimal } literal => literal.DecimalValue,
                UnaryExpression unary => Negate(EvaluateNumber(unary.Operand, depth + 1)),
                BinaryExpression binary => EvaluateArithmetic(binary.Operator,
                    EvaluateNumber(binary.Left, depth + 1), EvaluateNumber(binary.Right, depth + 1)),
                CastExpression cast => SqlCastOperations.Convert(EvaluateNumber(cast.Operand, depth + 1), cast.TargetType),
                _ => throw new InvalidOperationException("DECIMAL 常量包含动态表达式。"),
            };
        }
    }

    private static bool IsDecimalArithmetic(SqlBinaryOperator value)
        => value is SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Multiply
            or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo;

    private static string NormalizeDecimal(string text, long started)
    {
        if (text.Length is < 1 or > 1024)
            throw new InvalidOperationException("DECIMAL 数值文本必须在 1..1024 字符的解析预算内。");
        int exponentIndex = text.IndexOfAny(['e', 'E']);
        int exponent = 0;
        if (exponentIndex >= 0
            && (!int.TryParse(text.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent) || exponent is < -4096 or > 4096))
            throw new InvalidOperationException("DECIMAL 指数超出有界解析预算。");
        ReadOnlySpan<char> mantissa = exponentIndex >= 0 ? text.AsSpan(0, exponentIndex) : text.AsSpan();
        bool negative = mantissa.Length > 0 && mantissa[0] == '-';
        int offset = mantissa.Length > 0 && mantissa[0] is '-' or '+' ? 1 : 0;
        Span<char> digits = stackalloc char[1024];
        int count = 0;
        int fractional = 0;
        bool point = false;
        for (int index = offset; index < mantissa.Length && index < 1024; index++)
        {
            if ((index & 31) == 0)
            {
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 1)
                    throw new InvalidOperationException("DECIMAL 文本解析超时。");
                SqlQueryResources.Current?.CancellationToken.ThrowIfCancellationRequested();
            }
            char digit = mantissa[index];
            if (digit == '.' && !point)
            {
                point = true;
                continue;
            }
            if (digit is < '0' or > '9')
                throw new InvalidOperationException("DECIMAL 文本包含无效数值字符。");
            digits[count++] = digit;
            if (point)
                fractional++;
        }
        if (count == 0)
            throw new InvalidOperationException("DECIMAL 文本缺少数字。");
        int first = 0;
        for (int round = 0; round < count && first < count && digits[first] == '0'
            && Stopwatch.GetElapsedTime(started).TotalSeconds < 1; round++)
            first++;
        if (first == count)
            return "0";
        int end = count;
        for (int round = 0; round < count && end > first && digits[end - 1] == '0'
            && Stopwatch.GetElapsedTime(started).TotalSeconds < 1; round++)
            end--;
        if (Stopwatch.GetElapsedTime(started).TotalSeconds >= 1)
            throw new InvalidOperationException("DECIMAL 文本解析超时。");
        int power = exponent - fractional + count - end;
        return string.Concat(negative ? "-" : string.Empty, new string(digits[first..end]), "E", power.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 计算二元算术表达式；整数加减乘模与按位运算保留 Int64，DECIMAL 输入保持 Decimal，
    /// 其它含浮点操作数的运算返回 Float64。
    /// </summary>
    /// <param name="operatorValue">待执行的算术运算符。</param>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>运算结果；任一操作数为 NULL 时返回 NULL。</returns>
    internal static object? EvaluateArithmetic(SqlBinaryOperator operatorValue, object? left, object? right)
    {
        if (left is null || right is null)
            return null;

        if (operatorValue == SqlBinaryOperator.Subtract && left is DateTime leftDate && right is DateTime rightDate)
            return leftDate - rightDate;
        if (operatorValue == SqlBinaryOperator.Subtract && left is DateTimeOffset leftOffset && right is DateTimeOffset rightOffset)
            return TimeSpan.FromTicks(leftOffset.UtcTicks - rightOffset.UtcTicks);
        if (left is TimeSpan leftDuration && right is TimeSpan rightDuration)
        {
            try
            {
                return operatorValue switch
                {
                    SqlBinaryOperator.Add => leftDuration + rightDuration,
                    SqlBinaryOperator.Subtract => leftDuration - rightDuration,
                    _ => throw new InvalidOperationException("DURATION 只支持时长加减。"),
                };
            }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException("DURATION 运算超出 Int64 ticks 范围。", exception);
            }
        }

        if (operatorValue is SqlBinaryOperator.BitwiseAnd or SqlBinaryOperator.BitwiseOr)
        {
            if (!IsIntegral(left) || !IsIntegral(right))
            {
                throw new InvalidOperationException(
                    $"运算符 {OperatorText(operatorValue)} 只支持整数操作数。");
            }

            long leftInteger = ToInt64(left);
            long rightInteger = ToInt64(right);
            return operatorValue == SqlBinaryOperator.BitwiseAnd
                ? leftInteger & rightInteger
                : leftInteger | rightInteger;
        }

        if (!IsNumeric(left) || !IsNumeric(right))
        {
            throw new InvalidOperationException(
                $"运算符 {OperatorText(operatorValue)} 只支持数值操作数，不执行隐式字符串拼接或字符串转数值。");
        }

        if (operatorValue is SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo && IsZero(right))
            throw new InvalidOperationException($"运算符 {OperatorText(operatorValue)} 的除数不能为 0。");

        if (operatorValue != SqlBinaryOperator.Divide && IsIntegral(left) && IsIntegral(right))
            return EvaluateIntegral(operatorValue, ToInt64(left), ToInt64(right));

        if (left is decimal || right is decimal)
            return EvaluateDecimal(
                operatorValue,
                Convert.ToDecimal(left, CultureInfo.InvariantCulture),
                Convert.ToDecimal(right, CultureInfo.InvariantCulture));

        double leftValue = ToDouble(left);
        double rightValue = ToDouble(right);
        return operatorValue switch
        {
            SqlBinaryOperator.Add => leftValue + rightValue,
            SqlBinaryOperator.Subtract => leftValue - rightValue,
            SqlBinaryOperator.Multiply => leftValue * rightValue,
            SqlBinaryOperator.Divide => leftValue / rightValue,
            SqlBinaryOperator.Modulo => leftValue % rightValue,
            _ => throw new InvalidOperationException($"不支持的算术运算符 {operatorValue}。"),
        };
    }

    /// <summary>
    /// 计算一元负号；整数保持 Int64，浮点值保持 Float64，NULL 继续传播。
    /// </summary>
    /// <param name="value">待取负的值。</param>
    /// <returns>取负结果，或 NULL。</returns>
    internal static object? Negate(object? value)
    {
        if (value is null)
            return null;
        if (value is TimeSpan duration)
        {
            try { return duration.Negate(); }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException("DURATION 取负超出 Int64 ticks 范围。", exception);
            }
        }
        if (!IsNumeric(value))
            throw new InvalidOperationException("一元负号只支持数值操作数。");

        try
        {
            if (IsIntegral(value))
                return checked(-ToInt64(value));
            if (value is decimal decimalValue)
                return checked(-decimalValue);
            return -ToDouble(value);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException("一元负号的 Int64 运算发生溢出。", ex);
        }
    }

    /// <summary>
    /// 执行两个 Int64 操作数的运算，并把数值越界转换为稳定的 SQL 执行错误。
    /// </summary>
    private static object EvaluateIntegral(SqlBinaryOperator operatorValue, long left, long right)
    {
        try
        {
            return operatorValue switch
            {
                SqlBinaryOperator.Add => checked(left + right),
                SqlBinaryOperator.Subtract => checked(left - right),
                SqlBinaryOperator.Multiply => checked(left * right),
                SqlBinaryOperator.Modulo => checked(left % right),
                _ => throw new InvalidOperationException($"不支持的整数算术运算符 {operatorValue}。"),
            };
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                $"运算符 {OperatorText(operatorValue)} 的 Int64 运算发生溢出。", ex);
        }
    }

    private static decimal EvaluateDecimal(SqlBinaryOperator operatorValue, decimal left, decimal right)
    {
        try
        {
            return operatorValue switch
            {
                SqlBinaryOperator.Add => checked(left + right),
                SqlBinaryOperator.Subtract => checked(left - right),
                SqlBinaryOperator.Multiply => checked(left * right),
                SqlBinaryOperator.Divide => right == 0m
                    ? throw new InvalidOperationException("运算符 / 的除数不能为 0。")
                    : left / right,
                SqlBinaryOperator.Modulo => right == 0m
                    ? throw new InvalidOperationException("运算符 % 的除数不能为 0。")
                    : left % right,
                _ => throw new InvalidOperationException($"不支持的算术运算符 {operatorValue}。"),
            };
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException("DECIMAL 运算发生溢出。", exception);
        }
    }

    /// <summary>
    /// 判断运行时值是否属于 SQL 数值类型。
    /// </summary>
    private static bool IsNumeric(object value) => value is
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    /// <summary>
    /// 判断运行时值是否属于可保持为 Int64 的整数类型。
    /// </summary>
    private static bool IsIntegral(object value) => value is
        byte or sbyte or short or ushort or int or uint or long or ulong;

    /// <summary>
    /// 把整数操作数转换为 Int64，并拒绝超出 Int64 范围的无符号值。
    /// </summary>
    private static long ToInt64(object value)
    {
        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException("整数操作数超出 Int64 可表示范围。", ex);
        }
    }

    /// <summary>
    /// 把已验证的数值操作数转换为 Float64。
    /// </summary>
    private static double ToDouble(object value)
        => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// 判断数值操作数是否为零，供除法与取模在计算前统一拒绝零除数。
    /// </summary>
    private static bool IsZero(object value)
        => value is decimal decimalValue
            ? decimalValue == 0m
            : IsIntegral(value) ? ToInt64(value) == 0L : ToDouble(value) == 0d;

    /// <summary>
    /// 返回面向错误消息的 SQL 运算符文本。
    /// </summary>
    private static string OperatorText(SqlBinaryOperator value) => value switch
    {
        SqlBinaryOperator.Add => "+",
        SqlBinaryOperator.Subtract => "-",
        SqlBinaryOperator.Multiply => "*",
        SqlBinaryOperator.Divide => "/",
        SqlBinaryOperator.Modulo => "%",
        SqlBinaryOperator.BitwiseAnd => "&",
        SqlBinaryOperator.BitwiseOr => "|",
        _ => value.ToString(),
    };
}
