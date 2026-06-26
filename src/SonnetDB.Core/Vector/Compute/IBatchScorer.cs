using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Compute;

/// <summary>
/// 批量打分内核（M14）：对一个查询向量与数据集中所有行向量批量计算距离/相似度，
/// 用于将硬件加速（GPU / DirectML / CUDA / ONNX Runtime）以可插拔方式注入到 CPU 路径之外。
/// </summary>
/// <remarks>
/// <para>
/// 默认实现 <see cref="CpuTensorPrimitivesScorer"/> 内嵌在 <c>SonnetDB.Vector.Core</c>，
/// 与 <see cref="Distance"/> 在 scalar 路径下 bit-identical。
/// </para>
/// <para>
/// 加速实现（如 <c>OnnxRuntimeScorer</c>）位于独立 NuGet 包 <c>SonnetDB.Vector.Acceleration.*</c>，
/// 不进入 <c>SonnetDB.Vector.Core</c>，以保持核心库零第三方运行时依赖。
/// </para>
/// <para>
/// 实现需保证：
/// <list type="bullet">
///   <item>线程安全：同一实例可被多个查询并发调用。</item>
///   <item>分数语义与 <see cref="Distance.Compute(ReadOnlySpan{float}, ReadOnlySpan{float}, Metric)"/> 一致
///         （L2 返回平方、Cosine 返回 1-cos、InnerProduct 返回点积、DotProduct 返回 1-dot）。</item>
///   <item>不分配（hot-path 调用应零分配）。</item>
/// </list>
/// </para>
/// </remarks>
public interface IBatchScorer
{
    /// <summary>
    /// 对 <paramref name="query"/> 与 <paramref name="dataset"/> 中每一行（行优先、维度由
    /// <c>dataset.Length / scores.Length</c> 推导）批量计算 <paramref name="metric"/> 度量分数。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="dataset">数据集，按行优先连续存储；长度须为 <c>scores.Length × query.Length</c>。</param>
    /// <param name="scores">输出分数缓冲；长度即数据集行数。</param>
    /// <param name="metric">距离/相似度度量。</param>
    /// <exception cref="ArgumentException">当 <paramref name="dataset"/> 长度与 <paramref name="query"/>、<paramref name="scores"/> 不一致时抛出。</exception>
    /// <exception cref="NotSupportedException">当实现不支持指定 <paramref name="metric"/> 时抛出。</exception>
    void Score(
        ReadOnlySpan<float> query,
        ReadOnlySpan<float> dataset,
        Span<float> scores,
        Metric metric);
}
