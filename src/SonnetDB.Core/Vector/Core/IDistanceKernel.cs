using System.Numerics;

namespace SonnetDB.Vector.Core;

/// <summary>
/// 向量距离计算内核接口，泛型化到具体数值类型。
/// 支持 fp32（float）/ fp16 / bf16 / int8 等精度，
/// 通过 .NET 10 通用泛型数学 <see cref="IFloatingPointIeee754{T}"/> 统一实现。
/// </summary>
/// <typeparam name="T">数值精度类型，必须满足 IFloatingPointIeee754。</typeparam>
/// <remarks>
/// fp32 SIMD 实现见 <c>SonnetDB.Vector.Compute.FloatDistanceKernel</c>。
/// 通用 scalar 实现见 <c>SonnetDB.Vector.Compute.GenericFloatDistanceKernel{T}</c>。
/// TODO(M11): 扩展 Half（fp16）/ SByte（int8）量化精度实现。
/// </remarks>
public interface IDistanceKernel<T>
    where T : unmanaged, IFloatingPointIeee754<T>
{
    /// <summary>
    /// 计算两个向量的 L2 距离平方。
    /// </summary>
    /// <param name="a">向量 A。</param>
    /// <param name="b">向量 B（长度须与 A 相同）。</param>
    /// <returns>L2 距离平方（非负值）。</returns>
    T ComputeL2Squared(ReadOnlySpan<T> a, ReadOnlySpan<T> b);

    /// <summary>
    /// 计算两个向量的余弦距离（1 - 余弦相似度）。
    /// </summary>
    T ComputeCosine(ReadOnlySpan<T> a, ReadOnlySpan<T> b);

    /// <summary>
    /// 计算两个向量的内积（点积）。
    /// </summary>
    T ComputeInnerProduct(ReadOnlySpan<T> a, ReadOnlySpan<T> b);
}
