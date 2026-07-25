using System.Globalization;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 统一实现 SQL 标量数值运算，保证各数据模型使用相同的 NULL、类型、溢出与除零语义。
/// </summary>
internal static class SqlScalarOperations
{
    /// <summary>
    /// 计算二元算术表达式；整数加减乘模保留 Int64，除法与含浮点操作数的运算返回 Float64。
    /// </summary>
    /// <param name="operatorValue">待执行的算术运算符。</param>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>运算结果；任一操作数为 NULL 时返回 NULL。</returns>
    internal static object? EvaluateArithmetic(SqlBinaryOperator operatorValue, object? left, object? right)
    {
        if (left is null || right is null)
            return null;

        if (!IsNumeric(left) || !IsNumeric(right))
        {
            throw new InvalidOperationException(
                $"运算符 {OperatorText(operatorValue)} 只支持数值操作数，不执行隐式字符串拼接或字符串转数值。");
        }

        if (operatorValue is SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo && IsZero(right))
            throw new InvalidOperationException($"运算符 {OperatorText(operatorValue)} 的除数不能为 0。");

        if (operatorValue != SqlBinaryOperator.Divide && IsIntegral(left) && IsIntegral(right))
            return EvaluateIntegral(operatorValue, ToInt64(left), ToInt64(right));

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
        if (!IsNumeric(value))
            throw new InvalidOperationException("一元负号只支持数值操作数。");

        try
        {
            if (IsIntegral(value))
                return checked(-ToInt64(value));
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
        => IsIntegral(value) ? ToInt64(value) == 0L : ToDouble(value) == 0d;

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
        _ => value.ToString(),
    };
}
