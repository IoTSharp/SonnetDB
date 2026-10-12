using System.Numerics;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Tables;

/// <summary>DDL 使用的无损 Decimal 转换和声明预检；不改变普通 DML 的转换合同。</summary>
internal static class DecimalDeclarationValidator
{
    private static readonly BigInteger _maximumCoefficient = (BigInteger.One << 96) - 1;

    internal static decimal ConvertExact(object value, string columnName)
        => value switch
        {
            decimal number => number,
            string text when text.Length <= 1024 => SqlScalarOperations.ParseExactDecimal(text.Trim()),
            double number => ConvertBinaryExact(number, columnName),
            float number => ConvertBinaryExact(number, columnName),
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture),
            _ => throw Invalid(columnName, "输入不能无损转换为 Decimal"),
        };

    internal static void Validate(decimal value, byte precision, byte scale, string columnName)
    {
        if (precision is < 1 or > 38 || scale > precision)
            throw Invalid(columnName, "精度/小数位声明无效");

        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        BigInteger coefficient = (uint)bits[0]
            | (BigInteger)(uint)bits[1] << 32
            | (BigInteger)(uint)bits[2] << 64;
        int sourceScale = (bits[3] >> 16) & 0xff;
        if (sourceScale > scale)
        {
            coefficient = BigInteger.DivRem(coefficient, BigInteger.Pow(10, sourceScale - scale), out var remainder);
            if (!remainder.IsZero)
                throw Invalid(columnName, "已有值或默认值需要舍入才能符合小数位声明");
        }
        else
        {
            coefficient *= BigInteger.Pow(10, scale - sourceScale);
        }

        if (coefficient >= BigInteger.Pow(10, precision))
            throw Invalid(columnName, "已有值或默认值超出精度声明");
    }

    private static decimal ConvertBinaryExact(double value, string columnName)
    {
        ulong bits = BitConverter.DoubleToUInt64Bits(value);
        int exponentBits = (int)((bits >> 52) & 0x7ff);
        if (exponentBits == 0x7ff)
            throw Invalid(columnName, "非有限数值不能转换为 Decimal");
        ulong significand = bits & 0x000f_ffff_ffff_ffff;
        if (exponentBits != 0)
            significand |= 1UL << 52;
        if (significand == 0)
            return decimal.Zero;

        int exponent = exponentBits == 0 ? -1074 : exponentBits - 1023 - 52;
        int scale = 0;
        BigInteger coefficient;
        if (exponent < 0)
        {
            int cancelled = Math.Min(BitOperations.TrailingZeroCount(significand), -exponent);
            significand >>= cancelled;
            scale = -exponent - cancelled;
            if (scale > 28)
                throw Invalid(columnName, "二进制数值不能在 Decimal 值域内无损表示");
            coefficient = significand * BigInteger.Pow(5, scale);
        }
        else
        {
            coefficient = (BigInteger)significand << exponent;
        }

        if (coefficient > _maximumCoefficient)
            throw Invalid(columnName, "数值超出 Decimal 实际值域");
        return new decimal(
            unchecked((int)(uint)(coefficient & uint.MaxValue)),
            unchecked((int)(uint)((coefficient >> 32) & uint.MaxValue)),
            unchecked((int)(uint)((coefficient >> 64) & uint.MaxValue)),
            (bits >> 63) != 0,
            (byte)scale);
    }

    private static InvalidOperationException Invalid(string columnName, string reason)
        => new($"DECIMAL 列 '{columnName}' 的 DDL 预检失败：{reason}；schema 和已有行保持不变。");
}
