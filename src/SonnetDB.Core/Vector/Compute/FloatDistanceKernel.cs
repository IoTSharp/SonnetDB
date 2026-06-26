using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using SonnetDB.Vector.Core;

namespace SonnetDB.Vector.Compute;

/// <summary>
/// 通用浮点距离内核，基于 .NET 10 通用泛型数学
/// （<see cref="IFloatingPointIeee754{T}"/>）实现，
/// 支持 fp32（<see cref="float"/>）、fp64（<see cref="double"/>）
/// 以及未来的 fp16（<see cref="Half"/>）等任意 IEEE 754 浮点精度。
/// </summary>
/// <typeparam name="T">数值精度类型。</typeparam>
/// <remarks>
/// 对于 fp32 推荐直接使用 <see cref="FloatDistanceKernel"/>，
/// 它会走 <see cref="TensorPrimitives"/> + <see cref="Vector{T}"/> 的 SIMD 快路径。
/// 本通用实现走 scalar，主要用于 fp16 / 量化等暂未硬件加速的精度。
/// </remarks>
public sealed class GenericFloatDistanceKernel<T> : IDistanceKernel<T>
    where T : unmanaged, IFloatingPointIeee754<T>
{
    /// <summary>共享的内核实例。</summary>
    public static readonly GenericFloatDistanceKernel<T> Instance = new();

    /// <inheritdoc />
    public T ComputeL2Squared(ReadOnlySpan<T> a, ReadOnlySpan<T> b)
    {
        EnsureSameLength(a, b);
        T sum = T.Zero;
        for (int i = 0; i < a.Length; i++)
        {
            T diff = a[i] - b[i];
            sum += diff * diff;
        }
        return sum;
    }

    /// <inheritdoc />
    public T ComputeCosine(ReadOnlySpan<T> a, ReadOnlySpan<T> b)
    {
        EnsureSameLength(a, b);
        if (a.Length == 0)
        {
            return T.One;
        }

        T dot = T.Zero, na = T.Zero, nb = T.Zero;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        T denom = T.Sqrt(na) * T.Sqrt(nb);
        if (denom <= T.Epsilon)
        {
            return T.One;
        }

        T sim = dot / denom;
        if (sim > T.One) { sim = T.One; }
        else if (sim < -T.One) { sim = -T.One; }
        return T.One - sim;
    }

    /// <inheritdoc />
    public T ComputeInnerProduct(ReadOnlySpan<T> a, ReadOnlySpan<T> b)
    {
        EnsureSameLength(a, b);
        T sum = T.Zero;
        for (int i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureSameLength(ReadOnlySpan<T> a, ReadOnlySpan<T> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException(
                $"向量维度不匹配：a.Length={a.Length}, b.Length={b.Length}。");
        }
    }
}

/// <summary>
/// fp32 距离内核的 SIMD 加速实现。基于 <see cref="Distance"/> 静态类，
/// 在所有支持的平台（x64 AVX-512 / AVX2 / SSE，ARM64 NEON）上自动启用硬件加速。
/// </summary>
public sealed class FloatDistanceKernel : IDistanceKernel<float>
{
    /// <summary>共享内核实例（无状态，可安全并发使用）。</summary>
    public static readonly FloatDistanceKernel Instance = new();

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ComputeL2Squared(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => Distance.L2Squared(a, b);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ComputeCosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => Distance.Cosine(a, b);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ComputeInnerProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => Distance.InnerProduct(a, b);
}
