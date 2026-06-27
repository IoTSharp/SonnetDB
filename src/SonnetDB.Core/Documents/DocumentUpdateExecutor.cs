using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SonnetDB.Documents;

internal static class DocumentUpdateExecutor
{
    public static string Apply(string json, DocumentUpdate update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(update);
        Validate(update);

        JsonNode? root = JsonNode.Parse(json);
        ApplyRenames(ref root, update.Rename);
        ApplySet(ref root, update.Set);
        ApplyUnset(ref root, update.Unset);
        ApplyInc(ref root, update.Inc);
        ApplyMin(ref root, update.Min);
        ApplyMax(ref root, update.Max);
        ApplyPush(ref root, update.Push);
        ApplyPull(ref root, update.Pull);
        ApplyAddToSet(ref root, update.AddToSet);
        ApplyCurrentDate(ref root, update.CurrentDate);
        return Normalize(root);
    }

    public static string BuildUpsertSeed(DocumentFilter? filter)
    {
        var root = new JsonObject();
        foreach (var leaf in FlattenAnd(filter))
        {
            if (leaf is not DocumentFieldFilter
                {
                    Field.Kind: DocumentFieldKind.JsonPath,
                    Field.Path: not null,
                    Operator: DocumentFilterOperator.Equal,
                    Value: var value,
                } fieldFilter)
            {
                continue;
            }

            var path = JsonPath.Parse(fieldFilter.Field.Path!);
            if (path.Segments.Count == 0 || path.Segments[0].Kind != JsonPathSegmentKind.Property)
                continue;

            JsonNode? node = ToJsonNode(value);
            JsonNode? rootNode = root;
            SetValue(ref rootNode, path, node);
            root = (JsonObject)rootNode!;
        }

        return Normalize(root);
    }

    public static string? TryInferUpsertId(DocumentFilter? filter)
    {
        foreach (var leaf in FlattenAnd(filter))
        {
            if (leaf is DocumentFieldFilter
                {
                    Field.Kind: DocumentFieldKind.Id,
                    Operator: DocumentFilterOperator.Equal,
                    Value: string id,
                })
            {
                return id;
            }
        }

        return null;
    }

    private static void Validate(DocumentUpdate update)
    {
        var paths = new List<UpdatePath>();
        AddPaths(paths, "$set", update.Set?.Keys);
        AddPaths(paths, "$unset", update.Unset?.Keys);
        AddPaths(paths, "$inc", update.Inc?.Keys);
        AddPaths(paths, "$min", update.Min?.Keys);
        AddPaths(paths, "$max", update.Max?.Keys);
        AddPaths(paths, "$push", update.Push?.Keys);
        AddPaths(paths, "$pull", update.Pull?.Keys);
        AddPaths(paths, "$addToSet", update.AddToSet?.Keys);
        AddPaths(paths, "$currentDate", update.CurrentDate?.Keys);

        if (update.Rename is not null)
        {
            foreach (var pair in update.Rename)
            {
                var source = AddPath(paths, "$rename", pair.Key);
                var target = AddPath(paths, "$rename", pair.Value);
                if (source.Path.Segments.Count == 0 || target.Path.Segments.Count == 0)
                    throw new InvalidOperationException("$rename 不支持根路径 '$'。");
                if (HasPrefixConflict(source.Path, target.Path))
                    throw new InvalidOperationException($"$rename 路径 '{source.Path.Text}' 与 '{target.Path.Text}' 冲突。");
            }
        }

        if (paths.Count == 0)
            throw new InvalidOperationException("document update 至少需要一个局部更新操作符。");

        for (int i = 0; i < paths.Count; i++)
        {
            for (int j = i + 1; j < paths.Count; j++)
            {
                if (HasPrefixConflict(paths[i].Path, paths[j].Path))
                {
                    throw new InvalidOperationException(
                        $"document update 路径冲突：{paths[i].Operator} {paths[i].Path.Text} 与 {paths[j].Operator} {paths[j].Path.Text}。");
                }
            }
        }
    }

    private static void AddPaths(List<UpdatePath> paths, string op, IEnumerable<string>? source)
    {
        if (source is null)
            return;

        foreach (string path in source)
            AddPath(paths, op, path);
    }

    private static UpdatePath AddPath(List<UpdatePath> paths, string op, string pathText)
    {
        var path = JsonPath.Parse(pathText);
        if (path.Segments.Count == 0 && op != "$set" && op != "$min" && op != "$max")
            throw new InvalidOperationException($"{op} 不支持根路径 '$'。");

        var updatePath = new UpdatePath(op, path);
        paths.Add(updatePath);
        return updatePath;
    }

    private static bool HasPrefixConflict(JsonPath left, JsonPath right)
    {
        int common = Math.Min(left.Segments.Count, right.Segments.Count);
        for (int i = 0; i < common; i++)
        {
            if (!SegmentEquals(left.Segments[i], right.Segments[i]))
                return false;
        }

        return true;
    }

    private static bool SegmentEquals(JsonPathSegment left, JsonPathSegment right)
        => left.Kind == right.Kind
            && left.ArrayIndex == right.ArrayIndex
            && string.Equals(left.PropertyName, right.PropertyName, StringComparison.Ordinal);

    private static void ApplyRenames(ref JsonNode? root, IReadOnlyDictionary<string, string>? rename)
    {
        if (rename is null)
            return;

        foreach (var pair in rename)
        {
            var source = JsonPath.Parse(pair.Key);
            if (!TryGetValue(root, source, out var value))
                continue;

            RemoveValue(ref root, source);
            SetValue(ref root, JsonPath.Parse(pair.Value), value?.DeepClone());
        }
    }

    private static void ApplySet(ref JsonNode? root, IReadOnlyDictionary<string, JsonElement>? set)
    {
        if (set is null)
            return;

        foreach (var pair in set)
            SetValue(ref root, JsonPath.Parse(pair.Key), CloneElement(pair.Value));
    }

    private static void ApplyUnset(ref JsonNode? root, IReadOnlyDictionary<string, JsonElement>? unset)
    {
        if (unset is null)
            return;

        foreach (string path in unset.Keys)
            RemoveValue(ref root, JsonPath.Parse(path));
    }

    private static void ApplyInc(ref JsonNode? root, IReadOnlyDictionary<string, JsonElement>? inc)
    {
        if (inc is null)
            return;

        foreach (var pair in inc)
        {
            var path = JsonPath.Parse(pair.Key);
            var delta = ReadNumber(pair.Value, "$inc", path);
            if (!TryGetValue(root, path, out var existing))
            {
                SetValue(ref root, path, NumberToJsonNode(delta));
                continue;
            }

            var current = ReadNumber(existing, "$inc", path);
            SetValue(ref root, path, NumberToJsonNode(current.Add(delta)));
        }
    }

    private static void ApplyMin(ref JsonNode? root, IReadOnlyDictionary<string, JsonElement>? min)
    {
        if (min is null)
            return;

        foreach (var pair in min)
            ApplyCompareAndSet(ref root, "$min", JsonPath.Parse(pair.Key), pair.Value, replaceWhenComparisonSign: 1);
    }

    private static void ApplyMax(ref JsonNode? root, IReadOnlyDictionary<string, JsonElement>? max)
    {
        if (max is null)
            return;

        foreach (var pair in max)
            ApplyCompareAndSet(ref root, "$max", JsonPath.Parse(pair.Key), pair.Value, replaceWhenComparisonSign: -1);
    }

    private static void ApplyCompareAndSet(
        ref JsonNode? root,
        string op,
        JsonPath path,
        JsonElement candidate,
        int replaceWhenComparisonSign)
    {
        if (!TryGetValue(root, path, out var existing))
        {
            SetValue(ref root, path, CloneElement(candidate));
            return;
        }

        int comparison = CompareJson(existing, candidate, op, path);
        if (Math.Sign(comparison) == replaceWhenComparisonSign)
            SetValue(ref root, path, CloneElement(candidate));
    }

    private static void ApplyPush(ref JsonNode? root, IReadOnlyDictionary<string, JsonElement>? push)
    {
        if (push is null)
            return;

        foreach (var pair in push)
        {
            var path = JsonPath.Parse(pair.Key);
            var array = GetOrCreateArray(ref root, path, "$push");
            array.Add(CloneElement(pair.Value));
        }
    }

    private static void ApplyPull(ref JsonNode? root, IReadOnlyDictionary<string, JsonElement>? pull)
    {
        if (pull is null)
            return;

        foreach (var pair in pull)
        {
            var path = JsonPath.Parse(pair.Key);
            if (!TryGetValue(root, path, out var existing))
                continue;
            if (existing is not JsonArray array)
                throw new InvalidOperationException($"$pull 目标路径 '{path.Text}' 必须是数组。");

            for (int i = array.Count - 1; i >= 0; i--)
            {
                if (JsonValueEquals(array[i], pair.Value))
                    array.RemoveAt(i);
            }
        }
    }

    private static void ApplyAddToSet(ref JsonNode? root, IReadOnlyDictionary<string, JsonElement>? addToSet)
    {
        if (addToSet is null)
            return;

        foreach (var pair in addToSet)
        {
            var path = JsonPath.Parse(pair.Key);
            var array = GetOrCreateArray(ref root, path, "$addToSet");
            bool exists = array.Any(item => JsonValueEquals(item, pair.Value));
            if (!exists)
                array.Add(CloneElement(pair.Value));
        }
    }

    private static void ApplyCurrentDate(ref JsonNode? root, IReadOnlyDictionary<string, JsonElement>? currentDate)
    {
        if (currentDate is null)
            return;

        foreach (var pair in currentDate)
        {
            var path = JsonPath.Parse(pair.Key);
            string mode = ResolveCurrentDateMode(pair.Value);
            JsonNode value = string.Equals(mode, "timestamp", StringComparison.Ordinal)
                ? JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())!
                : JsonValue.Create(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))!;
            SetValue(ref root, path, value);
        }
    }

    private static string ResolveCurrentDateMode(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True)
            return "date";
        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (string.Equals(text, "date", StringComparison.OrdinalIgnoreCase))
                return "date";
            if (string.Equals(text, "timestamp", StringComparison.OrdinalIgnoreCase))
                return "timestamp";
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("$type", out var type) || value.TryGetProperty("type", out type))
            {
                if (type.ValueKind == JsonValueKind.String)
                    return ResolveCurrentDateMode(type);
            }
        }

        throw new InvalidOperationException("$currentDate 的值必须是 true、'date' 或 'timestamp'。");
    }

    private static JsonArray GetOrCreateArray(ref JsonNode? root, JsonPath path, string op)
    {
        if (!TryGetValue(root, path, out var existing))
        {
            var created = new JsonArray();
            SetValue(ref root, path, created);
            return created;
        }

        if (existing is not JsonArray array)
            throw new InvalidOperationException($"{op} 目标路径 '{path.Text}' 必须是数组或不存在。");
        return array;
    }

    private static bool TryGetValue(JsonNode? root, JsonPath path, out JsonNode? value)
    {
        value = root;
        if (path.Segments.Count == 0)
            return true;

        foreach (var segment in path.Segments)
        {
            if (segment.Kind == JsonPathSegmentKind.Property)
            {
                if (value is not JsonObject obj || !obj.TryGetPropertyValue(segment.PropertyName!, out value))
                {
                    value = null;
                    return false;
                }
            }
            else
            {
                if (value is not JsonArray array || segment.ArrayIndex >= array.Count)
                {
                    value = null;
                    return false;
                }

                value = array[segment.ArrayIndex];
            }
        }

        return true;
    }

    private static void SetValue(ref JsonNode? root, JsonPath path, JsonNode? value)
    {
        if (path.Segments.Count == 0)
        {
            root = value;
            return;
        }

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
        if (last.ArrayIndex > array.Count)
            throw new InvalidOperationException($"JSON path '{path.Text}' 的数组下标超出范围。");
        if (last.ArrayIndex == array.Count)
            array.Add(value);
        else
            array[last.ArrayIndex] = value;
    }

    private static void RemoveValue(ref JsonNode? root, JsonPath path)
    {
        if (path.Segments.Count == 0)
        {
            root = null;
            return;
        }

        if (!TryGetParent(root, path, out var parent))
            return;

        var last = path.Segments[^1];
        if (last.Kind == JsonPathSegmentKind.Property)
        {
            if (parent is JsonObject obj)
                obj.Remove(last.PropertyName!);
            return;
        }

        if (parent is JsonArray array && last.ArrayIndex < array.Count)
            array.RemoveAt(last.ArrayIndex);
    }

    private static JsonNode EnsureParent(ref JsonNode? root, JsonPath path)
    {
        if (root is null)
            root = CreateContainer(path.Segments[0]);

        JsonNode current = root;
        for (int i = 0; i < path.Segments.Count - 1; i++)
        {
            var segment = path.Segments[i];
            var nextSegment = path.Segments[i + 1];
            if (segment.Kind == JsonPathSegmentKind.Property)
            {
                if (current is not JsonObject obj)
                    throw new InvalidOperationException($"JSON path '{path.Text}' 在 '{segment.PropertyName}' 前遇到非对象节点。");
                if (!obj.TryGetPropertyValue(segment.PropertyName!, out var child) || child is null)
                {
                    child = CreateContainer(nextSegment);
                    obj[segment.PropertyName!] = child;
                }

                current = child;
            }
            else
            {
                if (current is not JsonArray array)
                    throw new InvalidOperationException($"JSON path '{path.Text}' 在数组下标 {segment.ArrayIndex} 前遇到非数组节点。");
                if (segment.ArrayIndex > array.Count)
                    throw new InvalidOperationException($"JSON path '{path.Text}' 的数组下标超出范围。");
                JsonNode? child;
                if (segment.ArrayIndex == array.Count)
                {
                    child = CreateContainer(nextSegment);
                    array.Add(child);
                }
                else
                {
                    child = array[segment.ArrayIndex];
                }

                if (child is null)
                {
                    child = CreateContainer(nextSegment);
                    array[segment.ArrayIndex] = child;
                }

                current = child;
            }
        }

        return current;
    }

    private static bool TryGetParent(JsonNode? root, JsonPath path, out JsonNode? parent)
    {
        parent = root;
        for (int i = 0; i < path.Segments.Count - 1; i++)
        {
            var segment = path.Segments[i];
            if (segment.Kind == JsonPathSegmentKind.Property)
            {
                if (parent is not JsonObject obj || !obj.TryGetPropertyValue(segment.PropertyName!, out parent))
                {
                    parent = null;
                    return false;
                }
            }
            else
            {
                if (parent is not JsonArray array || segment.ArrayIndex >= array.Count)
                {
                    parent = null;
                    return false;
                }

                parent = array[segment.ArrayIndex];
            }
        }

        return parent is not null;
    }

    private static JsonNode CreateContainer(JsonPathSegment nextSegment)
        => nextSegment.Kind == JsonPathSegmentKind.ArrayIndex ? new JsonArray() : new JsonObject();

    private static JsonNode? CloneElement(JsonElement element)
        => JsonNode.Parse(element.GetRawText());

    private static JsonNode? ToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonElement element => CloneElement(element),
            bool b => JsonValue.Create(b),
            byte or sbyte or short or ushort or int or uint or long => JsonValue.Create(
                Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            ulong ulongValue => JsonValue.Create(ulongValue),
            float or double or decimal => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            string text when TryParseJsonNode(text, out var node) => node,
            string text => JsonValue.Create(text),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
        };
    }

    private static bool TryParseJsonNode(string text, out JsonNode? node)
    {
        node = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        char first = text.TrimStart()[0];
        if (first is not ('{' or '['))
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

    private static NumberValue ReadNumber(JsonElement element, string op, JsonPath path)
    {
        if (element.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException($"{op} 路径 '{path.Text}' 的操作数必须是数值。");

        return element.TryGetInt64(out long longValue)
            ? NumberValue.FromInt64(longValue)
            : NumberValue.FromDouble(element.GetDouble());
    }

    private static NumberValue ReadNumber(JsonNode? node, string op, JsonPath path)
    {
        using var document = JsonDocument.Parse(Normalize(node));
        return ReadNumber(document.RootElement, op, path);
    }

    private static JsonNode NumberToJsonNode(NumberValue value)
        => value.IsInteger
            ? JsonValue.Create(value.Integer) ?? throw new InvalidOperationException("无法写入整数 JSON 值。")
            : JsonValue.Create(value.Double) ?? throw new InvalidOperationException("无法写入浮点 JSON 值。");

    private static int CompareJson(JsonNode? existing, JsonElement candidate, string op, JsonPath path)
    {
        using var existingDocument = JsonDocument.Parse(Normalize(existing));
        return CompareElements(existingDocument.RootElement, candidate)
            ?? throw new InvalidOperationException($"{op} 路径 '{path.Text}' 的现有值与候选值无法比较。");
    }

    private static int? CompareElements(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
            return left.GetDouble().CompareTo(right.GetDouble());
        if (left.ValueKind == JsonValueKind.String && right.ValueKind == JsonValueKind.String)
            return string.Compare(left.GetString(), right.GetString(), StringComparison.Ordinal);
        if ((left.ValueKind == JsonValueKind.True || left.ValueKind == JsonValueKind.False)
            && (right.ValueKind == JsonValueKind.True || right.ValueKind == JsonValueKind.False))
        {
            return left.GetBoolean().CompareTo(right.GetBoolean());
        }

        return null;
    }

    private static bool JsonValueEquals(JsonNode? left, JsonElement right)
    {
        using var leftDocument = JsonDocument.Parse(Normalize(left));
        return JsonElementsEqual(leftDocument.RootElement, right);
    }

    private static bool JsonElementsEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
            return left.GetDouble().Equals(right.GetDouble());

        return JsonNode.DeepEquals(JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText()));
    }

    private static string Normalize(JsonNode? node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (node is null)
                writer.WriteNullValue();
            else
                node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static IEnumerable<DocumentFilter> FlattenAnd(DocumentFilter? filter)
    {
        if (filter is null)
            yield break;

        if (filter is DocumentAndFilter and)
        {
            foreach (var child in and.Filters)
            {
                foreach (var leaf in FlattenAnd(child))
                    yield return leaf;
            }

            yield break;
        }

        yield return filter;
    }

    private sealed record UpdatePath(string Operator, JsonPath Path);

    private readonly record struct NumberValue(bool IsInteger, long Integer, double Double)
    {
        public static NumberValue FromInt64(long value) => new(true, value, value);

        public static NumberValue FromDouble(double value) => new(false, 0, value);

        public NumberValue Add(NumberValue other)
        {
            if (IsInteger && other.IsInteger)
            {
                try
                {
                    return FromInt64(checked(Integer + other.Integer));
                }
                catch (OverflowException)
                {
                    return FromDouble(Double + other.Double);
                }
            }

            return FromDouble(Double + other.Double);
        }
    }
}
