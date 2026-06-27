using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SonnetDB.Documents;

internal static class DocumentAggregationExecutor
{
    public static DocumentAggregationResult Execute(
        IReadOnlyList<DocumentRow> rows,
        DocumentAggregationPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(pipeline);
        ValidatePipeline(pipeline);

        var working = rows.Select(static row => new AggregateRow(row.Id, row.Json, row.Version)).ToList();
        foreach (var stage in pipeline.Stages)
        {
            working = stage switch
            {
                DocumentMatchStage match => ApplyMatch(working, match.Filter),
                DocumentProjectStage project => ApplyProject(working, project.Projection),
                DocumentGroupStage group => ApplyGroup(working, group),
                DocumentSortStage sort => ApplySort(working, sort.Sort),
                DocumentLimitStage limit => ApplyLimit(working, limit.Limit),
                DocumentSkipStage skip => ApplySkip(working, skip.Skip),
                DocumentUnwindStage unwind => ApplyUnwind(working, unwind),
                DocumentCountStage count => ApplyCount(working, count.Name),
                DocumentDistinctStage distinct => ApplyDistinct(working, distinct),
                _ => throw new InvalidOperationException($"不支持的文档聚合阶段 '{stage.GetType().Name}'。"),
            };
        }

        return new DocumentAggregationResult(working.Select(static row => row.Json).ToArray());
    }

    private static void ValidatePipeline(DocumentAggregationPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline.Stages);
        foreach (var stage in pipeline.Stages)
        {
            ArgumentNullException.ThrowIfNull(stage);
            switch (stage)
            {
                case DocumentLimitStage { Limit: <= 0 }:
                    throw new ArgumentOutOfRangeException(nameof(pipeline), "$limit 必须大于 0。");
                case DocumentSkipStage { Skip: < 0 }:
                    throw new ArgumentOutOfRangeException(nameof(pipeline), "$skip 不能为负数。");
                case DocumentDistinctStage { Limit: <= 0 }:
                    throw new ArgumentOutOfRangeException(nameof(pipeline), "$distinct.limit 必须大于 0。");
            }
        }
    }

    private static List<AggregateRow> ApplyMatch(List<AggregateRow> rows, DocumentFilter filter)
        => rows
            .Where(row => DocumentQueryPlanner.Matches(filter, row.ToDocumentRow()))
            .ToList();

    private static List<AggregateRow> ApplyProject(List<AggregateRow> rows, DocumentProjection projection)
    {
        if (projection.Fields.Count == 0)
            return rows;

        var projected = new List<AggregateRow>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            projected.Add(row with
            {
                Id = row.Id ?? $"project:{i.ToString(CultureInfo.InvariantCulture)}",
                Json = ProjectJson(row, projection),
            });
        }

        return projected;
    }

    private static List<AggregateRow> ApplyGroup(List<AggregateRow> rows, DocumentGroupStage stage)
    {
        var groups = new Dictionary<string, GroupState>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var keyValues = stage.Keys.Select(key => ReadValueOrNull(row, key.Field)).ToArray();
            string key = BuildGroupKey(keyValues);
            if (!groups.TryGetValue(key, out var state))
            {
                state = new GroupState(keyValues, stage.Accumulators.Select(static acc => new AccumulatorState(acc)).ToArray());
                groups.Add(key, state);
            }

            foreach (var accumulator in state.Accumulators)
                accumulator.Add(row);
        }

        var output = new List<AggregateRow>(groups.Count);
        int ordinal = 0;
        foreach (var state in groups.Values)
        {
            var root = new JsonObject();
            for (int i = 0; i < stage.Keys.Count; i++)
                root[stage.Keys[i].Name] = ToJsonNode(state.KeyValues[i]);

            foreach (var accumulator in state.Accumulators)
                root[accumulator.Definition.Name] = ToJsonNode(accumulator.GetValue());

            output.Add(new AggregateRow(
                $"group:{ordinal.ToString(CultureInfo.InvariantCulture)}",
                Normalize(root),
                Version: 0));
            ordinal++;
        }

        return output;
    }

    private static List<AggregateRow> ApplySort(List<AggregateRow> rows, IReadOnlyList<DocumentSort> sort)
    {
        if (rows.Count <= 1 || sort.Count == 0)
            return rows;

        return rows.OrderBy(static row => row, new AggregateRowComparer(sort)).ToList();
    }

    private static List<AggregateRow> ApplyLimit(List<AggregateRow> rows, int limit)
        => rows.Count <= limit ? rows : rows.Take(limit).ToList();

    private static List<AggregateRow> ApplySkip(List<AggregateRow> rows, int skip)
        => skip == 0 ? rows : rows.Skip(skip).ToList();

    private static List<AggregateRow> ApplyUnwind(List<AggregateRow> rows, DocumentUnwindStage stage)
    {
        var output = new List<AggregateRow>();
        foreach (var row in rows)
        {
            using var document = JsonDocument.Parse(row.Json);
            if (!TryResolveElement(document.RootElement, stage.Field, out var element))
            {
                if (stage.PreserveNullAndEmptyArrays)
                    output.Add(row);
                continue;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                int length = element.GetArrayLength();
                if (length == 0)
                {
                    if (stage.PreserveNullAndEmptyArrays)
                        output.Add(row);
                    continue;
                }

                int index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    output.Add(row with
                    {
                        Id = $"{row.Id ?? "row"}:{index.ToString(CultureInfo.InvariantCulture)}",
                        Json = WriteUnwindValue(row.Json, stage, item),
                    });
                    index++;
                }

                continue;
            }

            output.Add(row with { Json = WriteUnwindValue(row.Json, stage, element) });
        }

        return output;
    }

    private static List<AggregateRow> ApplyCount(List<AggregateRow> rows, string name)
    {
        string outputName = string.IsNullOrWhiteSpace(name) ? "count" : name;
        return
        [
            new AggregateRow(
                "count",
                WriteSingleFieldJson(outputName, rows.Count),
                Version: 0),
        ];
    }

    private static List<AggregateRow> ApplyDistinct(List<AggregateRow> rows, DocumentDistinctStage stage)
    {
        int take = stage.Limit ?? int.MaxValue;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<AggregateRow>();
        foreach (var row in rows)
        {
            object? value = ReadValueOrNull(row, stage.Field);
            string key = BuildValueKey(value);
            if (!seen.Add(key))
                continue;

            output.Add(new AggregateRow(
                $"distinct:{output.Count.ToString(CultureInfo.InvariantCulture)}",
                WriteSingleFieldJson(string.IsNullOrWhiteSpace(stage.Name) ? "value" : stage.Name, value),
                Version: 0));
            if (output.Count >= take)
                break;
        }

        return output;
    }

    private static object? ReadValueOrNull(AggregateRow row, DocumentFieldRef field)
        => DocumentQueryPlanner.TryGetFieldValue(row.ToDocumentRow(), field, out var value) ? value : null;

    private static string ProjectJson(AggregateRow row, DocumentProjection projection)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in projection.Fields)
            {
                if (field.Field.Kind == DocumentFieldKind.Id)
                {
                    writer.WritePropertyName(field.Name);
                    writer.WriteStringValue(row.Id);
                    continue;
                }

                if (TryGetFieldElement(row, field.Field, out var owner, out var element))
                {
                    writer.WritePropertyName(field.Name);
                    using (owner)
                        element.WriteTo(writer);
                    continue;
                }

                if (DocumentQueryPlanner.TryGetFieldValue(row.ToDocumentRow(), field.Field, out object? value))
                {
                    writer.WritePropertyName(field.Name);
                    WriteJsonValue(writer, value);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool TryGetFieldElement(
        AggregateRow row,
        DocumentFieldRef field,
        out JsonDocument owner,
        out JsonElement element)
    {
        owner = null!;
        element = default;
        try
        {
            owner = JsonDocument.Parse(row.Json);
            if (TryResolveElement(owner.RootElement, field, out element))
                return true;

            owner.Dispose();
            owner = null!;
            return false;
        }
        catch (JsonException)
        {
            owner?.Dispose();
            owner = null!;
            return false;
        }
    }

    private static bool TryResolveElement(JsonElement root, DocumentFieldRef field, out JsonElement element)
    {
        element = default;
        if (field.Kind == DocumentFieldKind.Document)
        {
            element = root;
            return true;
        }

        if (field.Kind != DocumentFieldKind.JsonPath || field.Path is null)
            return false;

        return JsonPathEvaluator.TryResolve(root, JsonPath.Parse(field.Path), out element);
    }

    private static string WriteUnwindValue(string json, DocumentUnwindStage stage, JsonElement value)
    {
        JsonNode? root = JsonNode.Parse(json);
        JsonNode? node = JsonNode.Parse(value.GetRawText());
        if (!string.IsNullOrWhiteSpace(stage.Name))
        {
            if (root is not JsonObject obj)
                obj = new JsonObject { ["document"] = root?.DeepClone() };
            obj[stage.Name] = node;
            return Normalize(obj);
        }

        if (stage.Field.Kind == DocumentFieldKind.Document)
            return Normalize(node);
        if (stage.Field.Kind != DocumentFieldKind.JsonPath || stage.Field.Path is null)
            throw new InvalidOperationException("$unwind 不支持 _id 字段。");

        SetValue(ref root, JsonPath.Parse(stage.Field.Path), node);
        return Normalize(root);
    }

    private static void SetValue(ref JsonNode? root, JsonPath path, JsonNode? value)
    {
        if (path.Segments.Count == 0)
        {
            root = value;
            return;
        }

        root ??= new JsonObject();
        JsonNode parent = EnsureParent(ref root, path);
        var last = path.Segments[^1];
        if (last.Kind == JsonPathSegmentKind.Property)
        {
            if (parent is not JsonObject obj)
                throw new InvalidOperationException($"JSON path '{path.Text}' 的父节点不是对象。");
            obj[last.PropertyName!] = value;
            return;
        }

        if (parent is not JsonArray array)
            throw new InvalidOperationException($"JSON path '{path.Text}' 的父节点不是数组。");
        while (array.Count <= last.ArrayIndex)
            array.Add(null);
        array[last.ArrayIndex] = value;
    }

    private static JsonNode EnsureParent(ref JsonNode root, JsonPath path)
    {
        JsonNode current = root;
        for (int i = 0; i < path.Segments.Count - 1; i++)
        {
            var segment = path.Segments[i];
            var next = path.Segments[i + 1];
            if (segment.Kind == JsonPathSegmentKind.Property)
            {
                if (current is not JsonObject obj)
                    throw new InvalidOperationException($"JSON path '{path.Text}' 的父节点不是对象。");
                if (!obj.TryGetPropertyValue(segment.PropertyName!, out var child) || child is null)
                {
                    child = CreateContainer(next);
                    obj[segment.PropertyName!] = child;
                }

                current = child;
                continue;
            }

            if (current is not JsonArray array)
                throw new InvalidOperationException($"JSON path '{path.Text}' 的父节点不是数组。");
            while (array.Count <= segment.ArrayIndex)
                array.Add(null);
            current = array[segment.ArrayIndex] ?? CreateContainer(next);
            array[segment.ArrayIndex] = current;
        }

        return current;
    }

    private static JsonNode CreateContainer(JsonPathSegment nextSegment)
        => nextSegment.Kind == JsonPathSegmentKind.ArrayIndex ? new JsonArray() : new JsonObject();

    private static string WriteSingleFieldJson(string name, object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(name);
            WriteJsonValue(writer, value);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong ulongValue:
                writer.WriteNumberValue(ulongValue);
                break;
            case float or double:
                writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case decimal decimalValue:
                writer.WriteNumberValue(decimalValue);
                break;
            case IReadOnlyList<object?> values:
                writer.WriteStartArray();
                foreach (var item in values)
                    WriteJsonValue(writer, item);
                writer.WriteEndArray();
                break;
            case string text when TryWriteRawJsonValue(writer, text):
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static bool TryWriteRawJsonValue(Utf8JsonWriter writer, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        char first = text.TrimStart()[0];
        if (first != '{' && first != '[')
            return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            document.RootElement.WriteTo(writer);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonNode? ToJsonNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case bool boolean:
                return JsonValue.Create(boolean);
            case byte or sbyte or short or ushort or int or uint or long:
                return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case ulong ulongValue:
                return JsonValue.Create(ulongValue);
            case float or double:
                return JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case decimal decimalValue:
                return JsonValue.Create(decimalValue);
            case string text when TryParseJsonNode(text, out var node):
                return node;
            case string text:
                return JsonValue.Create(text);
            case IReadOnlyList<object?> values:
                var array = new JsonArray();
                foreach (var item in values)
                    array.Add(ToJsonNode(item));
                return array;
            default:
                return JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture));
        }
    }

    private static bool TryParseJsonNode(string text, out JsonNode? node)
    {
        node = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        char first = text.TrimStart()[0];
        if (first != '{' && first != '[')
            return false;

        try
        {
            node = JsonNode.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Normalize(JsonNode? node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            (node ?? JsonValue.Create((string?)null))!.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildGroupKey(IReadOnlyList<object?> values)
        => string.Join('\u001f', values.Select(BuildValueKey));

    private static string BuildValueKey(object? value)
    {
        value = NormalizeValue(value);
        if (value is null)
            return "null:";
        if (IsNumeric(value))
            return "number:" + Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        return value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static object? NormalizeValue(object? value)
        => value is JsonElement element ? JsonPathEvaluator.ConvertElement(element) : value;

    private static int CompareValues(object? left, object? right)
    {
        left = NormalizeValue(left);
        right = NormalizeValue(right);
        if (left is null && right is null)
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;
        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDecimal(left, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture));
        if (left is string leftString && right is string rightString)
            return string.Compare(leftString, rightString, StringComparison.Ordinal);
        if (left is bool leftBool && right is bool rightBool)
            return leftBool.CompareTo(rightBool);

        return string.Compare(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private static bool IsNumeric(object value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private sealed record AggregateRow(string? Id, string Json, long Version)
    {
        public DocumentRow ToDocumentRow() => new(Id ?? string.Empty, Json, Version);
    }

    private sealed record GroupState(object?[] KeyValues, AccumulatorState[] Accumulators);

    private sealed class AccumulatorState
    {
        private long _count;
        private long _valueCount;
        private decimal _sum;
        private object? _min;
        private object? _max;
        private object? _first;
        private object? _last;
        private bool _hasFirst;
        private readonly List<object?> _distinct = [];
        private readonly HashSet<string> _distinctKeys = new(StringComparer.Ordinal);

        public AccumulatorState(DocumentAggregationAccumulator definition)
        {
            Definition = definition;
        }

        public DocumentAggregationAccumulator Definition { get; }

        public void Add(AggregateRow row)
        {
            _count++;
            object? value = Definition.Field is null ? null : ReadValueOrNull(row, Definition.Field);
            switch (Definition.Operator)
            {
                case DocumentAggregationAccumulatorOperator.Count:
                    break;

                case DocumentAggregationAccumulatorOperator.Sum:
                case DocumentAggregationAccumulatorOperator.Average:
                    if (value is not null && IsNumeric(value))
                    {
                        _sum += Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                        _valueCount++;
                    }
                    break;

                case DocumentAggregationAccumulatorOperator.Min:
                    if (value is not null && (_valueCount++ == 0 || CompareValues(value, _min) < 0))
                        _min = value;
                    break;

                case DocumentAggregationAccumulatorOperator.Max:
                    if (value is not null && (_valueCount++ == 0 || CompareValues(value, _max) > 0))
                        _max = value;
                    break;

                case DocumentAggregationAccumulatorOperator.First:
                    if (!_hasFirst)
                    {
                        _first = value;
                        _hasFirst = true;
                    }
                    break;

                case DocumentAggregationAccumulatorOperator.Last:
                    _last = value;
                    break;

                case DocumentAggregationAccumulatorOperator.Distinct:
                    string key = BuildValueKey(value);
                    if (_distinctKeys.Add(key))
                        _distinct.Add(value);
                    break;

                default:
                    throw new InvalidOperationException($"不支持的文档聚合函数 '{Definition.Operator}'。");
            }
        }

        public object? GetValue()
            => Definition.Operator switch
            {
                DocumentAggregationAccumulatorOperator.Count => _count,
                DocumentAggregationAccumulatorOperator.Sum => NormalizeNumber(_sum),
                DocumentAggregationAccumulatorOperator.Average => _valueCount == 0 ? null : (double)(_sum / _valueCount),
                DocumentAggregationAccumulatorOperator.Min => _min,
                DocumentAggregationAccumulatorOperator.Max => _max,
                DocumentAggregationAccumulatorOperator.First => _first,
                DocumentAggregationAccumulatorOperator.Last => _last,
                DocumentAggregationAccumulatorOperator.Distinct => _distinct,
                _ => throw new InvalidOperationException($"不支持的文档聚合函数 '{Definition.Operator}'。"),
            };

        private static object NormalizeNumber(decimal value)
            => decimal.Truncate(value) == value
                && value <= long.MaxValue
                && value >= long.MinValue
                    ? decimal.ToInt64(value)
                    : value;
    }

    private sealed class AggregateRowComparer : IComparer<AggregateRow>
    {
        private readonly IReadOnlyList<DocumentSort> _sort;

        public AggregateRowComparer(IReadOnlyList<DocumentSort> sort)
        {
            _sort = sort;
        }

        public int Compare(AggregateRow? x, AggregateRow? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            foreach (var sort in _sort)
            {
                object? left = ReadValueOrNull(x, sort.Field);
                object? right = ReadValueOrNull(y, sort.Field);
                int cmp = CompareValues(left, right);
                if (cmp != 0)
                    return sort.Descending ? -cmp : cmp;
            }

            return string.Compare(x.Id, y.Id, StringComparison.Ordinal);
        }
    }
}
