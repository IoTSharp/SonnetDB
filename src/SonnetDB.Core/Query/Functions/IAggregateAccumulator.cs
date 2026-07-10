using SonnetDB.Model;

namespace SonnetDB.Query.Functions;

/// <summary>
/// 可合并聚合状态。Tier 2 起的扩展聚合函数（PR #52+）通过实现该接口接入查询管线，
/// 由调用方按桶维护一个累加器实例，逐点调用 <see cref="Add(long, FieldValue)"/>，
/// 在桶切换时调用 <see cref="Finalize"/> 输出结果。
/// <para>
/// <see cref="Merge"/> 用于跨段或跨分片合并：实现必须满足结合律（A+B == B+A）
/// 与幂等性（多次合并同一份只读快照不会改变结果）。
/// </para>
/// </summary>
public interface IAggregateAccumulator
{
    /// <summary>已累加的数据点数（不含被丢弃的 NaN / null）。</summary>
    long Count { get; }

    /// <summary>累加单个数值数据点。</summary>
    /// <param name="value">数据点值；NaN 由实现决定是否丢弃。</param>
    void Add(double value);

    /// <summary>
    /// 累加保留原始类型的字段值。
    /// <para>默认实现把数值与布尔转换为 <see cref="double"/>，并把向量、地理点分派给专用重载。</para>
    /// </summary>
    /// <param name="value">字段值。</param>
    void Add(FieldValue value)
    {
        if (value.Type == Storage.Format.FieldType.Vector)
        {
            Add(value.AsVector());
            return;
        }
        if (value.Type == Storage.Format.FieldType.GeoPoint)
        {
            Add(value.AsGeoPoint());
            return;
        }
        if (value.TryGetNumeric(out double numeric))
        {
            Add(numeric);
            return;
        }

        throw new InvalidOperationException($"该聚合函数不支持 {value.Type} 参数。");
    }

    /// <summary>累加单个向量数据点。</summary>
    /// <param name="vector">向量数据；维度必须与当前累加器状态一致。</param>
    void Add(ReadOnlyMemory<float> vector)
        => throw new InvalidOperationException("该聚合函数不支持 VECTOR 参数。");

    /// <summary>
    /// 携带时间戳的累加重载；多数累加器忽略时间戳，按 <see cref="Add(double)"/> 处理。
    /// 时间相关的累加器（如 <c>pid</c>）需要重写以使用 <paramref name="timestampMs"/>。
    /// </summary>
    /// <param name="timestampMs">数据点时间戳（毫秒）。</param>
    /// <param name="value">数据点值。</param>
    void Add(long timestampMs, double value) => Add(value);

    /// <summary>
    /// 携带时间戳并保留原始字段类型的累加重载。
    /// </summary>
    /// <param name="timestampMs">数据点时间戳（毫秒）。</param>
    /// <param name="value">字段值。</param>
    void Add(long timestampMs, FieldValue value)
    {
        if (value.Type == Storage.Format.FieldType.Vector)
        {
            Add(timestampMs, value.AsVector());
            return;
        }
        if (value.Type == Storage.Format.FieldType.GeoPoint)
        {
            Add(timestampMs, value.AsGeoPoint());
            return;
        }
        if (value.TryGetNumeric(out double numeric))
        {
            Add(timestampMs, numeric);
            return;
        }

        Add(value);
    }

    /// <summary>
    /// 携带时间戳的向量累加重载；多数向量累加器忽略时间戳，按 <see cref="Add(ReadOnlyMemory{float})"/> 处理。
    /// </summary>
    /// <param name="timestampMs">数据点时间戳（毫秒）。</param>
    /// <param name="vector">向量数据点。</param>
    void Add(long timestampMs, ReadOnlyMemory<float> vector) => Add(vector);

    /// <summary>累加单个地理点。</summary>
    /// <param name="geoPoint">WGS84 地理点。</param>
    void Add(GeoPoint geoPoint)
        => throw new InvalidOperationException("该聚合函数不支持 GEOPOINT 参数。");

    /// <summary>
    /// 携带时间戳的地理点累加重载；默认忽略时间戳。
    /// </summary>
    /// <param name="timestampMs">数据点时间戳（毫秒）。</param>
    /// <param name="geoPoint">WGS84 地理点。</param>
    void Add(long timestampMs, GeoPoint geoPoint) => Add(geoPoint);

    /// <summary>合并另一个同类型累加器的状态。</summary>
    /// <param name="other">需要合并的另一份累加器。</param>
    /// <exception cref="ArgumentException">类型不匹配时抛出。</exception>
    void Merge(IAggregateAccumulator other);

    /// <summary>
    /// 输出最终结果；不修改累加器内部状态，可重复调用。
    /// <para>结果类型由具体聚合决定：标量聚合返回 <see cref="double"/> / <see cref="long"/> 等盒装值；
    /// 复合结果（如 histogram / tdigest_agg）返回字符串或字节数组。</para>
    /// <para>累加器为空（<see cref="Count"/> == 0）时，多数实现返回 <c>null</c>。</para>
    /// </summary>
    object? Finalize();
}
