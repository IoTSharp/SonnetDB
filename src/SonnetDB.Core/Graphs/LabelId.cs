using System.Globalization;

namespace SonnetDB.Graphs;

/// <summary>
/// Graph Catalog 分配的稳定标签标识符。
/// </summary>
public readonly struct LabelId : IEquatable<LabelId>, IComparable<LabelId>
{
    /// <summary>
    /// 使用正整数初始化标签标识符。
    /// </summary>
    /// <param name="value">大于零的标签标识值。</param>
    public LabelId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    /// <summary>标签标识值。</summary>
    public int Value { get; }

    /// <inheritdoc />
    public int CompareTo(LabelId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public bool Equals(LabelId other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LabelId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value;

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>比较两个标签标识符是否相等。</summary>
    public static bool operator ==(LabelId left, LabelId right) => left.Equals(right);

    /// <summary>比较两个标签标识符是否不相等。</summary>
    public static bool operator !=(LabelId left, LabelId right) => !left.Equals(right);
}
