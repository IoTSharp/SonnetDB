using SonnetDB.Catalog;
using SonnetDB.Model;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;

namespace SonnetDB.Query.Functions.Aggregates;

/// <summary>
/// 扩展聚合函数公共基类：固定 <see cref="IAggregateFunction.LegacyAggregator"/> 为 <c>null</c>，
/// 派生类只需实现累加器创建。
/// </summary>
internal abstract class ExtendedAggregateFunction : IAggregateFunction
{
    public abstract string Name { get; }

    public Aggregator? LegacyAggregator => null;

    public virtual AggregateFieldTypes AcceptedFieldTypes
        => AggregateFieldTypes.Numeric | AggregateFieldTypes.Boolean;

    public abstract string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema);

    public abstract IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema);

    IAggregateAccumulator? IAggregateFunction.CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema) => CreateAccumulator(call, schema);
}

// ── stddev / variance ────────────────────────────────────────────────────

internal sealed class StddevFunction : ExtendedAggregateFunction
{
    public override string Name => "stddev";

    public override string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
        => ExtendedAggregateBinder.ResolveSingleNumericField(call, schema, Name);

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema) => new StddevAccumulator();
}

internal sealed class VarianceFunction : ExtendedAggregateFunction
{
    public override string Name => "variance";

    public override string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
        => ExtendedAggregateBinder.ResolveSingleNumericField(call, schema, Name);

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema) => new VarianceAccumulator();
}

internal sealed class StddevAccumulator : IAggregateAccumulator
{
    private readonly WelfordAccumulator _state = new();

    public long Count => _state.Count;

    public void Add(double value) => _state.Add(value);

    public void Merge(IAggregateAccumulator other)
    {
        if (other is not StddevAccumulator s)
            throw new ArgumentException($"Cannot merge {other.GetType().Name} into StddevAccumulator.", nameof(other));
        _state.Merge(s._state);
    }

    public object? Finalize() => Count >= 2 ? _state.SampleStdDev : null;
}

internal sealed class VarianceAccumulator : IAggregateAccumulator
{
    private readonly WelfordAccumulator _state = new();

    public long Count => _state.Count;

    public void Add(double value) => _state.Add(value);

    public void Merge(IAggregateAccumulator other)
    {
        if (other is not VarianceAccumulator v)
            throw new ArgumentException($"Cannot merge {other.GetType().Name} into VarianceAccumulator.", nameof(other));
        _state.Merge(v._state);
    }

    public object? Finalize() => Count >= 2 ? _state.SampleVariance : null;
}

// ── spread ───────────────────────────────────────────────────────────────

internal sealed class SpreadFunction : ExtendedAggregateFunction
{
    public override string Name => "spread";

    public override string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
        => ExtendedAggregateBinder.ResolveSingleNumericField(call, schema, Name);

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema) => new SpreadAccumulator();
}

internal sealed class SpreadAccumulator : IAggregateAccumulator
{
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;

    public long Count { get; private set; }

    public void Add(double value)
    {
        if (double.IsNaN(value)) return;
        Count++;
        if (value < _min) _min = value;
        if (value > _max) _max = value;
    }

    public void Merge(IAggregateAccumulator other)
    {
        if (other is not SpreadAccumulator s)
            throw new ArgumentException($"Cannot merge {other.GetType().Name} into SpreadAccumulator.", nameof(other));
        if (s.Count == 0) return;
        Count += s.Count;
        if (s._min < _min) _min = s._min;
        if (s._max > _max) _max = s._max;
    }

    public object? Finalize() => Count == 0 ? null : (object)(_max - _min);
}

// ── mode ─────────────────────────────────────────────────────────────────

internal sealed class ModeFunction : ExtendedAggregateFunction
{
    public override string Name => "mode";

    public override AggregateFieldTypes AcceptedFieldTypes => AggregateFieldTypes.Categorical;

    public override string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
        => ExtendedAggregateBinder.ResolveSingleCategoricalField(call, schema, Name);

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema)
    {
        string fieldName = ExtendedAggregateBinder.ResolveSingleCategoricalField(call, schema, Name);
        return new ModeAccumulator(schema.TryGetColumn(fieldName)!.DataType);
    }
}

internal sealed class ModeAccumulator : IAggregateAccumulator
{
    private readonly FieldType _fieldType;
    private readonly Dictionary<double, long> _numericCounts = new();
    private readonly Dictionary<FieldValue, long> _categoricalCounts = new();

    public ModeAccumulator() : this(FieldType.Float64)
    {
    }

    public ModeAccumulator(FieldType fieldType)
    {
        if (!AggregateFieldTypes.Categorical.Supports(fieldType))
            throw new ArgumentOutOfRangeException(nameof(fieldType), fieldType, "mode 不支持该字段类型。");
        _fieldType = fieldType;
    }

    public long Count { get; private set; }

    public void Add(double value)
    {
        if (double.IsNaN(value)) return;
        Count++;
        ref long slot = ref System.Runtime.InteropServices.CollectionsMarshal
            .GetValueRefOrAddDefault(_numericCounts, value, out _);
        slot++;
    }

    public void Add(FieldValue value)
    {
        if (_fieldType is not (FieldType.String or FieldType.Boolean))
        {
            if (!value.TryGetNumeric(out double numeric))
                throw new InvalidOperationException($"mode 不支持 {value.Type} 参数。");
            Add(numeric);
            return;
        }

        if (value.Type != _fieldType)
            throw new InvalidOperationException($"mode 期望 {_fieldType} 参数，实际为 {value.Type}。");

        Count++;
        ref long slot = ref System.Runtime.InteropServices.CollectionsMarshal
            .GetValueRefOrAddDefault(_categoricalCounts, value, out _);
        slot++;
    }

    public void Add(long timestampMs, FieldValue value) => Add(value);

    public void Merge(IAggregateAccumulator other)
    {
        if (other is not ModeAccumulator m)
            throw new ArgumentException($"Cannot merge {other.GetType().Name} into ModeAccumulator.", nameof(other));
        if (m._fieldType != _fieldType)
            throw new ArgumentException("Cannot merge mode accumulators with different field types.", nameof(other));
        foreach (var (k, v) in m._numericCounts)
        {
            ref long slot = ref System.Runtime.InteropServices.CollectionsMarshal
                .GetValueRefOrAddDefault(_numericCounts, k, out _);
            slot += v;
        }
        foreach (var (k, v) in m._categoricalCounts)
        {
            ref long slot = ref System.Runtime.InteropServices.CollectionsMarshal
                .GetValueRefOrAddDefault(_categoricalCounts, k, out _);
            slot += v;
        }
        Count += m.Count;
    }

    public object? Finalize()
    {
        if (Count == 0) return null;
        if (_fieldType is FieldType.String or FieldType.Boolean)
            return FinalizeCategorical();

        long bestCount = 0;
        double bestValue = 0;
        foreach (var (k, v) in _numericCounts)
        {
            if (v > bestCount || (v == bestCount && k < bestValue))
            {
                bestCount = v;
                bestValue = k;
            }
        }
        return bestValue;
    }

    private object FinalizeCategorical()
    {
        long bestCount = 0;
        FieldValue bestValue = default;
        foreach (var (value, count) in _categoricalCounts)
        {
            if (count > bestCount || (count == bestCount && Compare(value, bestValue) < 0))
            {
                bestCount = count;
                bestValue = value;
            }
        }

        return _fieldType == FieldType.String ? bestValue.AsString() : bestValue.AsBool();
    }

    private int Compare(FieldValue left, FieldValue right)
        => _fieldType == FieldType.String
            ? string.Compare(left.AsString(), right.AsString(), StringComparison.Ordinal)
            : left.AsBool().CompareTo(right.AsBool());
}

// ── percentile / median / pXX ────────────────────────────────────────────

internal sealed class PercentileFunction : ExtendedAggregateFunction
{
    public override string Name => "percentile";

    public override string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
    {
        var (field, _) = ExtendedAggregateBinder.ResolveFieldAndNumeric(call, schema, Name);
        return field;
    }

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema)
    {
        var (_, q) = ExtendedAggregateBinder.ResolveFieldAndNumeric(call, schema, Name);
        if (q <= 0 || q > 100)
            throw new InvalidOperationException(
                $"percentile(...) 第二个参数 q 必须落在 (0, 100] 区间，实际 {q}。");
        return new PercentileAccumulator(q / 100.0);
    }
}

internal sealed class FixedPercentileFunction : ExtendedAggregateFunction
{
    private readonly double _q;
    public override string Name { get; }

    public FixedPercentileFunction(string name, double q)
    {
        Name = name;
        _q = q;
    }

    public override string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
        => ExtendedAggregateBinder.ResolveSingleNumericField(call, schema, Name);

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema) => new PercentileAccumulator(_q);
}

internal sealed class PercentileAccumulator : IAggregateAccumulator
{
    private readonly double _q;
    private readonly TDigest _digest = new();

    public PercentileAccumulator(double q)
    {
        if (q <= 0 || q > 1)
            throw new ArgumentOutOfRangeException(nameof(q));
        _q = q;
    }

    public long Count => _digest.Count;

    public void Add(double value) => _digest.Add(value);

    public void Merge(IAggregateAccumulator other)
    {
        if (other is not PercentileAccumulator p)
            throw new ArgumentException($"Cannot merge {other.GetType().Name} into PercentileAccumulator.", nameof(other));
        _digest.Merge(p._digest);
    }

    internal void MergeDigest(TDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        _digest.Merge(digest);
    }

    public object? Finalize() => Count == 0 ? null : (object)_digest.Quantile(_q);
}

// ── tdigest_agg ──────────────────────────────────────────────────────────

internal sealed class TDigestAggFunction : ExtendedAggregateFunction
{
    public override string Name => "tdigest_agg";

    public override string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
        => ExtendedAggregateBinder.ResolveSingleNumericField(call, schema, Name);

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema) => new TDigestAggAccumulator();
}

internal sealed class TDigestAggAccumulator : IAggregateAccumulator
{
    private readonly TDigest _digest = new();

    public long Count => _digest.Count;

    public void Add(double value) => _digest.Add(value);

    public void Merge(IAggregateAccumulator other)
    {
        if (other is not TDigestAggAccumulator t)
            throw new ArgumentException($"Cannot merge {other.GetType().Name} into TDigestAggAccumulator.", nameof(other));
        _digest.Merge(t._digest);
    }

    internal void MergeDigest(TDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        _digest.Merge(digest);
    }

    public object? Finalize() => Count == 0 ? null : (object)_digest.ToJson();
}

// ── distinct_count ───────────────────────────────────────────────────────

internal sealed class DistinctCountFunction : ExtendedAggregateFunction
{
    public override string Name => "distinct_count";

    public override AggregateFieldTypes AcceptedFieldTypes => AggregateFieldTypes.Categorical;

    public override string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
        => ExtendedAggregateBinder.ResolveSingleCategoricalField(call, schema, Name);

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema)
    {
        string fieldName = ExtendedAggregateBinder.ResolveSingleCategoricalField(call, schema, Name);
        return new DistinctCountAccumulator(schema.TryGetColumn(fieldName)!.DataType);
    }
}

internal sealed class DistinctCountAccumulator : IAggregateAccumulator
{
    private readonly FieldType _fieldType;
    private readonly HyperLogLog _hll = new();

    public DistinctCountAccumulator() : this(FieldType.Float64)
    {
    }

    public DistinctCountAccumulator(FieldType fieldType)
    {
        if (!AggregateFieldTypes.Categorical.Supports(fieldType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(fieldType), fieldType, "distinct_count 不支持该字段类型。");
        }
        _fieldType = fieldType;
    }

    public long Count { get; private set; }

    public void Add(double value)
    {
        if (double.IsNaN(value)) return;
        Count++;
        _hll.Add(value);
    }

    public void Add(FieldValue value)
    {
        if (_fieldType == FieldType.String)
        {
            if (value.Type != FieldType.String)
                throw new InvalidOperationException($"distinct_count 期望 String 参数，实际为 {value.Type}。");
            Count++;
            _hll.Add(value.AsString());
            return;
        }

        if (!value.TryGetNumeric(out double numeric))
            throw new InvalidOperationException($"distinct_count 不支持 {value.Type} 参数。");
        Add(numeric);
    }

    public void Add(long timestampMs, FieldValue value) => Add(value);

    public void Merge(IAggregateAccumulator other)
    {
        if (other is not DistinctCountAccumulator d)
            throw new ArgumentException($"Cannot merge {other.GetType().Name} into DistinctCountAccumulator.", nameof(other));
        if (d._fieldType != _fieldType)
        {
            throw new ArgumentException(
                "Cannot merge distinct_count accumulators with different field types.", nameof(other));
        }
        _hll.Merge(d._hll);
        Count += d.Count;
    }

    internal void MergeSketch(HyperLogLog hll, long count)
    {
        ArgumentNullException.ThrowIfNull(hll);
        _hll.Merge(hll);
        Count += count;
    }

    public object? Finalize() => Count == 0 ? (object)0L : _hll.Estimate();
}

// ── histogram ────────────────────────────────────────────────────────────

internal sealed class HistogramFunction : ExtendedAggregateFunction
{
    public override string Name => "histogram";

    public override string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
    {
        var (field, _) = ExtendedAggregateBinder.ResolveFieldAndNumeric(call, schema, Name);
        return field;
    }

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema)
    {
        var (_, binWidth) = ExtendedAggregateBinder.ResolveFieldAndNumeric(call, schema, Name);
        if (binWidth <= 0 || double.IsNaN(binWidth) || double.IsInfinity(binWidth))
            throw new InvalidOperationException(
                $"histogram(...) 第二个参数 bin_width 必须为正有限数，实际 {binWidth}。");
        return new HistogramAccumulator(binWidth);
    }
}

internal sealed class HistogramAccumulator : IAggregateAccumulator
{
    private readonly double _binWidth;
    private readonly Dictionary<long, long> _bins = new();

    public HistogramAccumulator(double binWidth) => _binWidth = binWidth;

    public long Count { get; private set; }

    public void Add(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return;
        Count++;
        long binIndex = (long)Math.Floor(value / _binWidth);
        ref long slot = ref System.Runtime.InteropServices.CollectionsMarshal
            .GetValueRefOrAddDefault(_bins, binIndex, out _);
        slot++;
    }

    public void Merge(IAggregateAccumulator other)
    {
        if (other is not HistogramAccumulator h)
            throw new ArgumentException($"Cannot merge {other.GetType().Name} into HistogramAccumulator.", nameof(other));
        if (h._binWidth != _binWidth)
            throw new ArgumentException("Cannot merge histograms with different bin widths.", nameof(other));
        foreach (var (k, v) in h._bins)
        {
            ref long slot = ref System.Runtime.InteropServices.CollectionsMarshal
                .GetValueRefOrAddDefault(_bins, k, out _);
            slot += v;
        }
        Count += h.Count;
    }

    public object? Finalize()
    {
        if (Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        bool first = true;
        foreach (var (k, v) in _bins.OrderBy(static kv => kv.Key))
        {
            if (!first) sb.Append(',');
            first = false;
            double lo = k * _binWidth;
            double hi = lo + _binWidth;
            sb.Append("\"[")
              .Append(lo.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
              .Append(',')
              .Append(hi.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
              .Append(")\":")
              .Append(v);
        }
        sb.Append('}');
        return sb.ToString();
    }
}

// ── centroid ───────────────────────────────────────────────────────────────

internal sealed class CentroidFunction : ExtendedAggregateFunction
{
    public override string Name => "centroid";

    public override AggregateFieldTypes AcceptedFieldTypes => AggregateFieldTypes.Vector;

    public override string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
        => ExtendedAggregateBinder.ResolveSingleVectorField(call, schema, Name);

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema) => new CentroidAccumulator();
}

internal sealed class CentroidAccumulator : IAggregateAccumulator
{
    private double[]? _sums;

    public long Count { get; private set; }

    public void Add(double value)
        => throw new InvalidOperationException("centroid(...) 需要 VECTOR 参数。");

    public void Add(ReadOnlyMemory<float> vector)
    {
        var span = vector.Span;
        if (span.Length == 0)
            throw new InvalidOperationException("centroid(...) 不接受空向量。");

        if (_sums is null)
        {
            _sums = new double[span.Length];
        }
        else if (_sums.Length != span.Length)
        {
            throw new InvalidOperationException(
                $"centroid(...) 向量维度不一致：期望 {_sums.Length}，实际 {span.Length}。");
        }

        for (int i = 0; i < span.Length; i++)
            _sums[i] += span[i];

        Count++;
    }

    public void Merge(IAggregateAccumulator other)
    {
        if (other is not CentroidAccumulator c)
            throw new ArgumentException($"Cannot merge {other.GetType().Name} into CentroidAccumulator.", nameof(other));
        if (c.Count == 0)
            return;
        if (c._sums is null)
            throw new InvalidOperationException("待合并的 centroid 累加器缺少向量状态。");

        if (_sums is null)
        {
            _sums = c._sums.ToArray();
            Count = c.Count;
            return;
        }

        if (_sums.Length != c._sums.Length)
        {
            throw new InvalidOperationException(
                $"centroid(...) 向量维度不一致：期望 {_sums.Length}，实际 {c._sums.Length}。");
        }

        for (int i = 0; i < _sums.Length; i++)
            _sums[i] += c._sums[i];

        Count += c.Count;
    }

    public object? Finalize()
    {
        if (Count == 0 || _sums is null)
            return null;

        var result = new float[_sums.Length];
        for (int i = 0; i < _sums.Length; i++)
            result[i] = (float)(_sums[i] / Count);
        return result;
    }
}

// ?? trajectory geo aggregates ?????????????????????????????????????????????

internal abstract class TrajectoryAggregateFunction : ExtendedAggregateFunction
{
    protected TrajectoryAggregateFunction(string name) => Name = name;

    public override string Name { get; }

    public override AggregateFieldTypes AcceptedFieldTypes => AggregateFieldTypes.GeoPoint;

    public override string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
        => ExtendedAggregateBinder.ResolveSingleGeoPointField(call, schema, Name);
}

internal sealed class TrajectoryLengthFunction : TrajectoryAggregateFunction
{
    public TrajectoryLengthFunction() : base("trajectory_length") { }

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema) => new TrajectoryLengthAccumulator();
}

internal sealed class TrajectoryCentroidFunction : TrajectoryAggregateFunction
{
    public TrajectoryCentroidFunction() : base("trajectory_centroid") { }

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema) => new TrajectoryCentroidAccumulator();
}

internal sealed class TrajectoryBboxFunction : TrajectoryAggregateFunction
{
    public TrajectoryBboxFunction() : base("trajectory_bbox") { }

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema) => new TrajectoryBboxAccumulator();
}

internal abstract class TrajectorySpeedFunction : ExtendedAggregateFunction
{
    protected TrajectorySpeedFunction(string name) => Name = name;

    public override string Name { get; }

    public override AggregateFieldTypes AcceptedFieldTypes => AggregateFieldTypes.GeoPoint;

    public override string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
        => ExtendedAggregateBinder.ResolveGeoPointFieldAndTime(call, schema, Name);
}

internal sealed class TrajectorySpeedMaxFunction : TrajectorySpeedFunction
{
    public TrajectorySpeedMaxFunction() : base("trajectory_speed_max") { }

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema) => new TrajectorySpeedAccumulator(TrajectorySpeedMode.Max);
}

internal sealed class TrajectorySpeedAvgFunction : TrajectorySpeedFunction
{
    public TrajectorySpeedAvgFunction() : base("trajectory_speed_avg") { }

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema) => new TrajectorySpeedAccumulator(TrajectorySpeedMode.Avg);
}

internal sealed class TrajectorySpeedP95Function : TrajectorySpeedFunction
{
    public TrajectorySpeedP95Function() : base("trajectory_speed_p95") { }

    public override IAggregateAccumulator CreateAccumulator(
        FunctionCallExpression call, MeasurementSchema schema) => new TrajectorySpeedAccumulator(TrajectorySpeedMode.P95);
}

internal abstract class TrajectoryAccumulatorBase : IAggregateAccumulator
{
    protected GeoPoint? FirstPoint { get; private set; }
    protected long FirstTimestamp { get; private set; }
    protected GeoPoint? LastPoint { get; private set; }
    protected long LastTimestamp { get; private set; }

    public long Count { get; protected set; }

    public void Add(double value)
        => throw new InvalidOperationException("?????? GEOPOINT ???");

    public void Add(GeoPoint geoPoint) => Add(0, geoPoint);

    public virtual void Add(long timestampMs, GeoPoint geoPoint)
    {
        if (Count == 0)
        {
            FirstPoint = geoPoint;
            FirstTimestamp = timestampMs;
        }

        LastPoint = geoPoint;
        LastTimestamp = timestampMs;
        Count++;
    }

    protected void MergeEndpoints(TrajectoryAccumulatorBase other)
    {
        if (other.Count == 0)
            return;
        if (Count == 0 || other.FirstTimestamp < FirstTimestamp)
        {
            FirstPoint = other.FirstPoint;
            FirstTimestamp = other.FirstTimestamp;
        }
        if (Count == 0 || other.LastTimestamp > LastTimestamp)
        {
            LastPoint = other.LastPoint;
            LastTimestamp = other.LastTimestamp;
        }
    }

    protected static double DistanceMeters(GeoPoint left, GeoPoint right)
    {
        const double EarthRadiusMeters = 6_371_008.8d;
        double lat1 = DegreesToRadians(left.Lat);
        double lat2 = DegreesToRadians(right.Lat);
        double dLat = DegreesToRadians(right.Lat - left.Lat);
        double dLon = DegreesToRadians(right.Lon - left.Lon);
        double sinLat = Math.Sin(dLat / 2d);
        double sinLon = Math.Sin(dLon / 2d);
        double a = sinLat * sinLat + Math.Cos(lat1) * Math.Cos(lat2) * sinLon * sinLon;
        double c = 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
        return EarthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    public abstract void Merge(IAggregateAccumulator other);

    public abstract object? Finalize();
}

internal sealed class TrajectoryLengthAccumulator : TrajectoryAccumulatorBase
{
    private double _lengthMeters;

    public override void Add(long timestampMs, GeoPoint geoPoint)
    {
        if (LastPoint is { } last)
            _lengthMeters += DistanceMeters(last, geoPoint);
        base.Add(timestampMs, geoPoint);
    }

    public override void Merge(IAggregateAccumulator other)
    {
        if (other is not TrajectoryLengthAccumulator t)
            throw new ArgumentException($"Cannot merge {other.GetType().Name} into TrajectoryLengthAccumulator.", nameof(other));
        if (t.Count == 0)
            return;
        if (Count > 0 && LastPoint is { } last && t.FirstPoint is { } first)
            _lengthMeters += DistanceMeters(last, first);
        _lengthMeters += t._lengthMeters;
        MergeEndpoints(t);
        Count += t.Count;
    }

    public override object? Finalize() => Count == 0 ? null : _lengthMeters;
}

internal sealed class TrajectoryCentroidAccumulator : TrajectoryAccumulatorBase
{
    private double _latSum;
    private double _lonSum;

    public override void Add(long timestampMs, GeoPoint geoPoint)
    {
        _latSum += geoPoint.Lat;
        _lonSum += geoPoint.Lon;
        base.Add(timestampMs, geoPoint);
    }

    public override void Merge(IAggregateAccumulator other)
    {
        if (other is not TrajectoryCentroidAccumulator t)
            throw new ArgumentException($"Cannot merge {other.GetType().Name} into TrajectoryCentroidAccumulator.", nameof(other));
        if (t.Count == 0)
            return;
        _latSum += t._latSum;
        _lonSum += t._lonSum;
        MergeEndpoints(t);
        Count += t.Count;
    }

    public override object? Finalize()
        => Count == 0 ? null : GeoPoint.Create(_latSum / Count, _lonSum / Count);
}

internal sealed class TrajectoryBboxAccumulator : TrajectoryAccumulatorBase
{
    private double _minLat = double.PositiveInfinity;
    private double _minLon = double.PositiveInfinity;
    private double _maxLat = double.NegativeInfinity;
    private double _maxLon = double.NegativeInfinity;

    public override void Add(long timestampMs, GeoPoint geoPoint)
    {
        if (geoPoint.Lat < _minLat) _minLat = geoPoint.Lat;
        if (geoPoint.Lon < _minLon) _minLon = geoPoint.Lon;
        if (geoPoint.Lat > _maxLat) _maxLat = geoPoint.Lat;
        if (geoPoint.Lon > _maxLon) _maxLon = geoPoint.Lon;
        base.Add(timestampMs, geoPoint);
    }

    public override void Merge(IAggregateAccumulator other)
    {
        if (other is not TrajectoryBboxAccumulator t)
            throw new ArgumentException($"Cannot merge {other.GetType().Name} into TrajectoryBboxAccumulator.", nameof(other));
        if (t.Count == 0)
            return;
        if (t._minLat < _minLat) _minLat = t._minLat;
        if (t._minLon < _minLon) _minLon = t._minLon;
        if (t._maxLat > _maxLat) _maxLat = t._maxLat;
        if (t._maxLon > _maxLon) _maxLon = t._maxLon;
        MergeEndpoints(t);
        Count += t.Count;
    }

    public override object? Finalize()
    {
        if (Count == 0)
            return null;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{{\"min_lat\":{_minLat:R},\"min_lon\":{_minLon:R},\"max_lat\":{_maxLat:R},\"max_lon\":{_maxLon:R}}}");
    }
}

internal enum TrajectorySpeedMode
{
    Max,
    Avg,
    P95,
}

internal sealed class TrajectorySpeedAccumulator : TrajectoryAccumulatorBase
{
    private readonly TrajectorySpeedMode _mode;
    private readonly List<double> _speeds = [];
    private double _sum;
    private double _max = double.NegativeInfinity;

    public TrajectorySpeedAccumulator(TrajectorySpeedMode mode) => _mode = mode;

    public override void Add(long timestampMs, GeoPoint geoPoint)
    {
        if (LastPoint is { } last)
        {
            long elapsedMs = timestampMs - LastTimestamp;
            if (elapsedMs > 0)
            {
                double speed = DistanceMeters(last, geoPoint) / (elapsedMs / 1000d);
                _speeds.Add(speed);
                _sum += speed;
                if (speed > _max) _max = speed;
            }
        }
        base.Add(timestampMs, geoPoint);
    }

    public override void Merge(IAggregateAccumulator other)
    {
        if (other is not TrajectorySpeedAccumulator t)
            throw new ArgumentException($"Cannot merge {other.GetType().Name} into TrajectorySpeedAccumulator.", nameof(other));
        if (t._mode != _mode)
            throw new ArgumentException("Cannot merge trajectory speed accumulators with different modes.", nameof(other));
        if (t.Count == 0)
            return;
        if (Count > 0 && LastPoint is { } last && t.FirstPoint is { } first)
        {
            long elapsedMs = t.FirstTimestamp - LastTimestamp;
            if (elapsedMs > 0)
            {
                double speed = DistanceMeters(last, first) / (elapsedMs / 1000d);
                _speeds.Add(speed);
                _sum += speed;
                if (speed > _max) _max = speed;
            }
        }
        _speeds.AddRange(t._speeds);
        _sum += t._sum;
        if (t._max > _max) _max = t._max;
        MergeEndpoints(t);
        Count += t.Count;
    }

    public override object? Finalize()
    {
        if (_speeds.Count == 0)
            return null;
        return _mode switch
        {
            TrajectorySpeedMode.Max => _max,
            TrajectorySpeedMode.Avg => _sum / _speeds.Count,
            TrajectorySpeedMode.P95 => Percentile(0.95d),
            _ => throw new InvalidOperationException($"???????????? {_mode}?"),
        };
    }

    private double Percentile(double q)
    {
        _speeds.Sort();
        double rank = q * (_speeds.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi)
            return _speeds[lo];
        double weight = rank - lo;
        return _speeds[lo] * (1d - weight) + _speeds[hi] * weight;
    }
}
