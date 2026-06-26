using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.VectorData;

namespace SonnetDB.Data.VectorData.Internal;

[RequiresUnreferencedCode("SonnetDBVectorRecordMapper 通过反射访问 TRecord 的属性，可能被 trim 移除。")]
[RequiresDynamicCode("SonnetDBVectorRecordMapper 通过反射访问 TRecord 的属性，AOT 下可能不可用。")]
internal sealed class SonnetDBVectorRecordMapper<TKey, TRecord> : ISqlFilterFieldResolver
    where TKey : notnull
    where TRecord : class
{
    private readonly PropertyInfo _keyProperty;
    private readonly PropertyInfo _vectorProperty;
    private readonly bool _vectorIsArray;
    private readonly PropertyInfo[] _dataProperties;
    private readonly Dictionary<string, PropertyInfo> _dataByStorageName;
    private readonly Dictionary<string, string> _dataStorageNames;

    public SonnetDBVectorRecordMapper()
    {
        var t = typeof(TRecord);
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo? key = null;
        PropertyInfo? vector = null;
        var data = new List<(PropertyInfo Prop, string StorageName)>();
        string? vectorStorageName = null;
        string? distance = null;

        foreach (var prop in props)
        {
            if (prop.GetCustomAttribute<VectorStoreKeyAttribute>() is not null)
            {
                key = key is null
                    ? prop
                    : throw new InvalidOperationException($"{t.FullName} 上声明了多个 [VectorStoreKey] 属性。");
                continue;
            }

            if (prop.GetCustomAttribute<VectorStoreVectorAttribute>() is { } vectorAttribute)
            {
                vector = vector is null
                    ? prop
                    : throw new InvalidOperationException($"{t.FullName} 上声明了多个 [VectorStoreVector] 属性。");
                vectorStorageName = string.IsNullOrEmpty(vectorAttribute.StorageName) ? prop.Name : vectorAttribute.StorageName;
                distance = vectorAttribute.DistanceFunction;
                continue;
            }

            if (prop.GetCustomAttribute<VectorStoreDataAttribute>() is { } dataAttribute)
            {
                var storageName = string.IsNullOrEmpty(dataAttribute.StorageName) ? prop.Name : dataAttribute.StorageName;
                data.Add((prop, storageName));
            }
        }

        (_keyProperty, _vectorProperty, _vectorIsArray) = ValidateProperties(t, key, vector);
        _dataProperties = data.Select(static d => d.Prop).ToArray();
        _dataStorageNames = data.ToDictionary(static d => d.Prop.Name, static d => d.StorageName, StringComparer.Ordinal);
        _dataByStorageName = data.ToDictionary(static d => d.StorageName, static d => d.Prop, StringComparer.Ordinal);
        VectorStorageName = vectorStorageName ?? _vectorProperty.Name;
        VectorJsonPath = "$." + VectorStorageName;
        DistanceFunction = distance;
    }

    public SonnetDBVectorRecordMapper(VectorStoreCollectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var t = typeof(TRecord);
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(static p => p.Name, StringComparer.Ordinal);
        PropertyInfo? key = null;
        PropertyInfo? vector = null;
        var data = new List<(PropertyInfo Prop, string StorageName)>();
        string? vectorStorageName = null;
        string? distance = null;

        foreach (var property in definition.Properties)
        {
            if (!props.TryGetValue(property.Name, out var clr))
                throw new InvalidOperationException($"VectorStoreCollectionDefinition 中声明的属性 '{property.Name}' 在 {t.FullName} 上不存在。");

            switch (property)
            {
                case VectorStoreKeyProperty:
                    key = key is null ? clr : throw new InvalidOperationException("VectorStoreCollectionDefinition 中存在多个 Key 属性。");
                    break;
                case VectorStoreVectorProperty vectorProperty:
                    vector = vector is null ? clr : throw new InvalidOperationException("VectorStoreCollectionDefinition 中存在多个 Vector 属性。");
                    vectorStorageName = string.IsNullOrEmpty(property.StorageName) ? property.Name : property.StorageName;
                    distance = vectorProperty.DistanceFunction;
                    break;
                case VectorStoreDataProperty dataProperty:
                    data.Add((clr, string.IsNullOrEmpty(dataProperty.StorageName) ? dataProperty.Name : dataProperty.StorageName));
                    break;
            }
        }

        (_keyProperty, _vectorProperty, _vectorIsArray) = ValidateProperties(t, key, vector);
        _dataProperties = data.Select(static d => d.Prop).ToArray();
        _dataStorageNames = data.ToDictionary(static d => d.Prop.Name, static d => d.StorageName, StringComparer.Ordinal);
        _dataByStorageName = data.ToDictionary(static d => d.StorageName, static d => d.Prop, StringComparer.Ordinal);
        VectorStorageName = vectorStorageName ?? _vectorProperty.Name;
        VectorJsonPath = "$." + VectorStorageName;
        DistanceFunction = distance;
    }

    public string VectorStorageName { get; }

    public string VectorJsonPath { get; }

    public string? DistanceFunction { get; }

    public TKey GetKey(TRecord record)
    {
        var value = _keyProperty.GetValue(record)
            ?? throw new InvalidOperationException($"{typeof(TRecord).Name}.{_keyProperty.Name} 不能为 null。");
        return (TKey)value;
    }

    public string ToJson(TRecord record)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        payload[VectorStorageName] = GetVector(record);
        foreach (var property in _dataProperties)
            payload[_dataStorageNames[property.Name]] = property.GetValue(record);
        return JsonSerializer.Serialize(payload, VectorJsonSerializerContext.Default.DictionaryStringObject);
    }

    public TRecord FromJson(string id, string json, bool includeVector)
    {
        var record = (TRecord?)Activator.CreateInstance(typeof(TRecord))
            ?? throw new InvalidOperationException($"无法创建 {typeof(TRecord).FullName} 的实例，请确保其定义了公共无参构造器。");
        _keyProperty.SetValue(record, KeyConverter<TKey>.FromStorageId(id));

        using var document = JsonDocument.Parse(json);
        foreach (var (storageName, property) in _dataByStorageName)
        {
            if (!document.RootElement.TryGetProperty(storageName, out var element) || element.ValueKind == JsonValueKind.Null)
                continue;
            property.SetValue(record, ConvertElement(element, property.PropertyType));
        }

        if (includeVector && document.RootElement.TryGetProperty(VectorStorageName, out var vectorElement))
        {
            var vector = ReadVector(vectorElement);
            if (_vectorIsArray)
                _vectorProperty.SetValue(record, vector);
            else
                _vectorProperty.SetValue(record, new ReadOnlyMemory<float>(vector));
        }

        return record;
    }

    public bool TryResolveField(string propertyName, [NotNullWhen(true)] out string? sqlExpression)
    {
        if (string.Equals(propertyName, _keyProperty.Name, StringComparison.Ordinal))
        {
            sqlExpression = "id";
            return true;
        }

        if (_dataStorageNames.TryGetValue(propertyName, out var storageName))
        {
            sqlExpression = $"json_value(document, '$.{storageName}')";
            return true;
        }

        sqlExpression = null;
        return false;
    }

    private float[] GetVector(TRecord record)
    {
        var raw = _vectorProperty.GetValue(record)
            ?? throw new InvalidOperationException($"{typeof(TRecord).Name}.{_vectorProperty.Name} 不能为 null。");
        return _vectorIsArray ? (float[])raw : ((ReadOnlyMemory<float>)raw).ToArray();
    }

    private static float[] ReadVector(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("VectorData JSON 中的向量字段必须是 number array。");
        var result = new float[element.GetArrayLength()];
        var index = 0;
        foreach (var item in element.EnumerateArray())
            result[index++] = item.GetSingle();
        return result;
    }

    private static object? ConvertElement(JsonElement element, Type targetType)
    {
        var effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (effectiveType == typeof(string))
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        if (effectiveType == typeof(bool))
            return element.GetBoolean();
        if (effectiveType == typeof(int))
            return element.GetInt32();
        if (effectiveType == typeof(long))
            return element.GetInt64();
        if (effectiveType == typeof(float))
            return element.GetSingle();
        if (effectiveType == typeof(double))
            return element.GetDouble();
        if (effectiveType == typeof(Guid))
            return Guid.Parse(element.GetString() ?? element.GetRawText());
        if (effectiveType.IsEnum)
            return Enum.Parse(effectiveType, element.GetString() ?? element.GetRawText());

        return JsonSerializer.Deserialize(element.GetRawText(), effectiveType);
    }

    private static (PropertyInfo Key, PropertyInfo Vector, bool VectorIsArray) ValidateProperties(
        Type recordType,
        PropertyInfo? key,
        PropertyInfo? vector)
    {
        if (key is null)
            throw new InvalidOperationException($"{recordType.FullName} 必须包含一个 VectorStore key 属性。");
        if (key.PropertyType != typeof(TKey))
            throw new InvalidOperationException($"{recordType.FullName}.{key.Name} 的类型 {key.PropertyType.Name} 与 TKey={typeof(TKey).Name} 不一致。");
        if (vector is null)
            throw new InvalidOperationException($"{recordType.FullName} 必须包含一个 VectorStore vector 属性。");
        if (vector.PropertyType == typeof(float[]))
            return (key, vector, true);
        if (vector.PropertyType == typeof(ReadOnlyMemory<float>))
            return (key, vector, false);
        throw new NotSupportedException($"{recordType.FullName}.{vector.Name} 的类型 {vector.PropertyType.Name} 不受支持；当前仅支持 float[] 与 ReadOnlyMemory<float>。");
    }
}
