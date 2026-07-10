using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Query.Functions.Aggregates;

/// <summary>
/// first/last 选择器累加器；按时间戳选择并保留原始字段类型。
/// </summary>
internal sealed class SelectorAccumulator : IAggregateAccumulator
{
    private readonly bool _selectFirst;
    private bool _hasValue;
    private long _selectedTimestamp;
    private FieldValue _selectedValue;

    public SelectorAccumulator(bool selectFirst) => _selectFirst = selectFirst;

    public long Count { get; private set; }

    public void Add(double value) => Add(Count, FieldValue.FromDouble(value));

    public void Add(FieldValue value) => Add(Count, value);

    public void Add(long timestampMs, double value)
        => Add(timestampMs, FieldValue.FromDouble(value));

    public void Add(long timestampMs, FieldValue value)
    {
        Count++;
        if (!_hasValue || IsPreferred(timestampMs))
        {
            _hasValue = true;
            _selectedTimestamp = timestampMs;
            _selectedValue = value;
        }
    }

    public void Merge(IAggregateAccumulator other)
    {
        if (other is not SelectorAccumulator selector || selector._selectFirst != _selectFirst)
            throw new ArgumentException("只能合并相同方向的 selector 累加器。", nameof(other));

        if (selector._hasValue && (!_hasValue || IsPreferred(selector._selectedTimestamp)))
        {
            _hasValue = true;
            _selectedTimestamp = selector._selectedTimestamp;
            _selectedValue = selector._selectedValue;
        }
        Count += selector.Count;
    }

    public object? Finalize() => _hasValue ? Unbox(_selectedValue) : null;

    private bool IsPreferred(long timestampMs)
        => _selectFirst ? timestampMs < _selectedTimestamp : timestampMs > _selectedTimestamp;

    private static object Unbox(FieldValue value) => value.Type switch
    {
        FieldType.Float64 => value.AsDouble(),
        FieldType.Int64 => value.AsLong(),
        FieldType.Boolean => value.AsBool(),
        FieldType.String => value.AsString(),
        FieldType.Vector => value.AsVector().ToArray(),
        FieldType.GeoPoint => value.AsGeoPoint(),
        _ => throw new InvalidOperationException($"selector 不支持 {value.Type} 参数。"),
    };
}

/// <summary>
/// 字符串/布尔 min/max 累加器；字符串比较固定使用 Ordinal。
/// </summary>
internal sealed class CategoricalMinMaxAccumulator : IAggregateAccumulator
{
    private readonly FieldType _fieldType;
    private readonly bool _selectMinimum;
    private bool _hasValue;
    private FieldValue _selectedValue;

    public CategoricalMinMaxAccumulator(FieldType fieldType, bool selectMinimum)
    {
        if (fieldType is not (FieldType.String or FieldType.Boolean))
        {
            throw new ArgumentOutOfRangeException(
                nameof(fieldType), fieldType, "分类 min/max 仅支持 String 或 Boolean。");
        }
        _fieldType = fieldType;
        _selectMinimum = selectMinimum;
    }

    public long Count { get; private set; }

    public void Add(double value)
    {
        if (_fieldType != FieldType.Boolean)
            throw new InvalidOperationException("字符串 min/max 需要 String 参数。");
        Add(FieldValue.FromBool(value != 0));
    }

    public void Add(FieldValue value)
    {
        if (value.Type != _fieldType)
            throw new InvalidOperationException($"min/max 期望 {_fieldType} 参数，实际为 {value.Type}。");

        Count++;
        if (!_hasValue || IsPreferred(value))
        {
            _hasValue = true;
            _selectedValue = value;
        }
    }

    public void Add(long timestampMs, FieldValue value) => Add(value);

    public void Merge(IAggregateAccumulator other)
    {
        if (other is not CategoricalMinMaxAccumulator accumulator
            || accumulator._fieldType != _fieldType
            || accumulator._selectMinimum != _selectMinimum)
        {
            throw new ArgumentException("只能合并字段类型和比较方向相同的 min/max 累加器。", nameof(other));
        }

        if (accumulator._hasValue && (!_hasValue || IsPreferred(accumulator._selectedValue)))
        {
            _hasValue = true;
            _selectedValue = accumulator._selectedValue;
        }
        Count += accumulator.Count;
    }

    public object? Finalize()
    {
        if (!_hasValue)
            return null;
        return _fieldType == FieldType.String ? _selectedValue.AsString() : _selectedValue.AsBool();
    }

    private bool IsPreferred(FieldValue value)
    {
        int comparison = _fieldType == FieldType.String
            ? string.Compare(value.AsString(), _selectedValue.AsString(), StringComparison.Ordinal)
            : value.AsBool().CompareTo(_selectedValue.AsBool());
        return _selectMinimum ? comparison < 0 : comparison > 0;
    }
}
