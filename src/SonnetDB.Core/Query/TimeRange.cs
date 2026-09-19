namespace SonnetDB.Query;

/// <summary>
/// 闭区间时间范围 [FromInclusive, ToInclusive]，单位与 <see cref="SonnetDB.Model.DataPoint.Timestamp"/> 一致（Unix 毫秒）。
/// </summary>
public readonly struct TimeRange : IEquatable<TimeRange>
{
    /// <summary>区间起点（含）。</summary>
    public long FromInclusive { get; }

    /// <summary>区间终点（含）。</summary>
    public long ToInclusive { get; }

    /// <summary>
    /// 构造时间范围 [<paramref name="fromInclusive"/>, <paramref name="toInclusive"/>]。
    /// </summary>
    /// <param name="fromInclusive">起点时间戳（含）。</param>
    /// <param name="toInclusive">终点时间戳（含）。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fromInclusive"/> 大于 <paramref name="toInclusive"/> 时抛出。</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public TimeRange(long fromInclusive, long toInclusive)
    {
        if (fromInclusive > toInclusive)
            throw new ArgumentOutOfRangeException(nameof(fromInclusive),
                $"fromInclusive ({fromInclusive}) 不能大于 toInclusive ({toInclusive})。");
        FromInclusive = fromInclusive;
        ToInclusive = toInclusive;
    }

    /// <summary>覆盖全部时间范围 [long.MinValue, long.MaxValue]。</summary>
    public static TimeRange All => new(long.MinValue, long.MaxValue);

    /// <summary>
    /// 从 <paramref name="from"/> 到时间轴末端：[<paramref name="from"/>, long.MaxValue]。
    /// </summary>
    /// <param name="from">起点时间戳（含）。</param>
    public static TimeRange From(long from) => new(from, long.MaxValue);

    /// <summary>
    /// 从时间轴起始到 <paramref name="to"/>：[long.MinValue, <paramref name="to"/>]。
    /// </summary>
    /// <param name="to">终点时间戳（含）。</param>
    public static TimeRange Until(long to) => new(long.MinValue, to);

    /// <summary>
    /// 判断时间戳 <paramref name="timestamp"/> 是否在范围内（含边界）。
    /// </summary>
    /// <param name="timestamp">待检查的时间戳。</param>
    /// <returns>在范围内返回 <c>true</c>，否则返回 <c>false</c>。</returns>
    public bool Contains(long timestamp)
        => timestamp >= FromInclusive && timestamp <= ToInclusive;

    /// <summary>
    /// 判断区间 [<paramref name="min"/>, <paramref name="max"/>] 是否与当前范围重叠。
    /// </summary>
    /// <param name="min">外部区间最小值（含）。</param>
    /// <param name="max">外部区间最大值（含）。</param>
    /// <returns>有重叠返回 <c>true</c>，否则返回 <c>false</c>。</returns>
    public bool Overlaps(long min, long max)
        => min <= ToInclusive && max >= FromInclusive;

    /// <inheritdoc/>
    public bool Equals(TimeRange other)
        => FromInclusive == other.FromInclusive && ToInclusive == other.ToInclusive;

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj is TimeRange other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(FromInclusive, ToInclusive);

    /// <summary>相等运算符。</summary>
    public static bool operator ==(TimeRange left, TimeRange right) => left.Equals(right);

    /// <summary>不等运算符。</summary>
    public static bool operator !=(TimeRange left, TimeRange right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString()
        => $"[{FromInclusive}, {ToInclusive}]";
}
