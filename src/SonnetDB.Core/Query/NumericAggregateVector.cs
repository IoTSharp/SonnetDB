using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SonnetDB.Storage.Format;

namespace SonnetDB.Query;

/// <summary>
/// 数值聚合的向量化辅助路径。仅使用 <see cref="Vector{T}"/> 与安全 span API，
/// 当硬件不支持或数据不满足 SIMD 前置条件时由调用方回退到标量实现。
/// </summary>
internal static class NumericAggregateVector
{
    public static bool IsSupported => BitConverter.IsLittleEndian && System.Numerics.Vector.IsHardwareAccelerated;

    public static NumericAggregateVectorResult AggregateScalar(
        FieldType fieldType,
        ReadOnlySpan<byte> valuePayload,
        int start,
        int end)
    {
        if (start >= end)
            return NumericAggregateVectorResult.Empty;

        long count = end - start;
        double sum = 0d;
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;

        for (int i = start; i < end; i++)
        {
            double value = ReadNumericValue(fieldType, valuePayload, i);
            sum += value;
            if (value < min) min = value;
            if (value > max) max = value;
        }

        return new NumericAggregateVectorResult(count, sum, min, max);
    }

    public static NumericAggregateVectorResult Aggregate(
        FieldType fieldType,
        ReadOnlySpan<byte> valuePayload,
        int start,
        int end,
        bool useSimd)
    {
        if (useSimd && TryAggregateSimd(fieldType, valuePayload, start, end, out var result))
            return result;

        return AggregateScalar(fieldType, valuePayload, start, end);
    }

    public static bool TryAggregateSimd(
        FieldType fieldType,
        ReadOnlySpan<byte> valuePayload,
        int start,
        int end,
        out NumericAggregateVectorResult result)
    {
        result = NumericAggregateVectorResult.Empty;

        if (!IsSupported || start >= end)
            return false;

        return fieldType switch
        {
            FieldType.Float64 => TryAggregateFloat64(valuePayload, start, end, out result),
            FieldType.Int64 => TryAggregateInt64(valuePayload, start, end, out result),
            _ => false,
        };
    }

    private static bool TryAggregateFloat64(
        ReadOnlySpan<byte> valuePayload,
        int start,
        int end,
        out NumericAggregateVectorResult result)
    {
        result = NumericAggregateVectorResult.Empty;

        ReadOnlySpan<double> values = MemoryMarshal.Cast<byte, double>(valuePayload);
        int length = end - start;
        if (length < Vector<double>.Count)
            return false;

        ReadOnlySpan<double> slice = values.Slice(start, length);
        var sumVector = Vector<double>.Zero;
        var minVector = new Vector<double>(double.PositiveInfinity);
        var maxVector = new Vector<double>(double.NegativeInfinity);

        int i = 0;
        int vectorWidth = Vector<double>.Count;
        int vectorEnd = length - vectorWidth + 1;
        for (; i < vectorEnd; i += vectorWidth)
        {
            var current = new Vector<double>(slice.Slice(i, vectorWidth));
            if (!System.Numerics.Vector.EqualsAll(current, current))
                return false;

            sumVector += current;
            minVector = System.Numerics.Vector.Min(minVector, current);
            maxVector = System.Numerics.Vector.Max(maxVector, current);
        }

        double sum = 0d;
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        for (int lane = 0; lane < vectorWidth; lane++)
        {
            double sumValue = sumVector[lane];
            double minValue = minVector[lane];
            double maxValue = maxVector[lane];

            sum += sumValue;
            if (minValue < min) min = minValue;
            if (maxValue > max) max = maxValue;
        }

        for (; i < length; i++)
        {
            double value = slice[i];
            if (double.IsNaN(value))
                return false;

            sum += value;
            if (value < min) min = value;
            if (value > max) max = value;
        }

        result = new NumericAggregateVectorResult(length, sum, min, max);
        return true;
    }

    private static bool TryAggregateInt64(
        ReadOnlySpan<byte> valuePayload,
        int start,
        int end,
        out NumericAggregateVectorResult result)
    {
        result = NumericAggregateVectorResult.Empty;

        ReadOnlySpan<long> values = MemoryMarshal.Cast<byte, long>(valuePayload);
        int length = end - start;
        if (length < Vector<long>.Count)
            return false;

        ReadOnlySpan<long> slice = values.Slice(start, length);
        var sumVector = Vector<double>.Zero;
        var minVector = new Vector<long>(long.MaxValue);
        var maxVector = new Vector<long>(long.MinValue);

        int i = 0;
        int vectorWidth = Vector<long>.Count;
        int vectorEnd = length - vectorWidth + 1;
        for (; i < vectorEnd; i += vectorWidth)
        {
            var current = new Vector<long>(slice.Slice(i, vectorWidth));
            sumVector += System.Numerics.Vector.ConvertToDouble(current);
            minVector = System.Numerics.Vector.Min(minVector, current);
            maxVector = System.Numerics.Vector.Max(maxVector, current);
        }

        double sum = 0d;
        long min = long.MaxValue;
        long max = long.MinValue;
        for (int lane = 0; lane < vectorWidth; lane++)
        {
            sum += sumVector[lane];
            long minValue = minVector[lane];
            long maxValue = maxVector[lane];
            if (minValue < min) min = minValue;
            if (maxValue > max) max = maxValue;
        }

        for (; i < length; i++)
        {
            long value = slice[i];
            sum += value;
            if (value < min) min = value;
            if (value > max) max = value;
        }

        result = new NumericAggregateVectorResult(length, sum, min, max);
        return true;
    }

    private static double ReadNumericValue(
        FieldType fieldType,
        ReadOnlySpan<byte> valuePayload,
        int index)
    {
        return fieldType switch
        {
            FieldType.Float64 => BinaryPrimitives.ReadDoubleLittleEndian(valuePayload.Slice(index * 8, 8)),
            FieldType.Int64 => (double)BinaryPrimitives.ReadInt64LittleEndian(valuePayload.Slice(index * 8, 8)),
            FieldType.Boolean => valuePayload[index] != 0 ? 1.0 : 0.0,
            _ => throw new NotSupportedException(
                $"字段类型 {fieldType} 不支持数值聚合。仅支持 Float64 / Int64 / Boolean 字段。"),
        };
    }
}

internal readonly record struct NumericAggregateVectorResult(
    long Count,
    double Sum,
    double Min,
    double Max)
{
    public static NumericAggregateVectorResult Empty { get; } =
        new(0L, 0d, double.PositiveInfinity, double.NegativeInfinity);
}
