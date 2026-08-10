using System.Globalization;

namespace SonnetDB.Graphs;

/// <summary>
/// 图顶点或边的稳定 64 位内部标识符。
/// </summary>
public readonly struct GraphElementId : IEquatable<GraphElementId>, IComparable<GraphElementId>
{
    /// <summary>
    /// 使用正整数初始化图元素标识符。
    /// </summary>
    /// <param name="value">大于零的内部标识值。</param>
    public GraphElementId(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    /// <summary>内部标识值。</summary>
    public long Value { get; }

    /// <inheritdoc />
    public int CompareTo(GraphElementId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public bool Equals(GraphElementId other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GraphElementId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>比较两个图元素标识符是否相等。</summary>
    public static bool operator ==(GraphElementId left, GraphElementId right) => left.Equals(right);

    /// <summary>比较两个图元素标识符是否不相等。</summary>
    public static bool operator !=(GraphElementId left, GraphElementId right) => !left.Equals(right);

    /// <summary>判断左侧图元素标识符是否小于右侧。</summary>
    public static bool operator <(GraphElementId left, GraphElementId right) => left.Value < right.Value;

    /// <summary>判断左侧图元素标识符是否大于右侧。</summary>
    public static bool operator >(GraphElementId left, GraphElementId right) => left.Value > right.Value;
}
