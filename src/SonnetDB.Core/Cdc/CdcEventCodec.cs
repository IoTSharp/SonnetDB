using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Cdc;

/// <summary>
/// CDC 事件的有界 UTF-8 JSON 编解码器。
/// </summary>
public static class CdcEventCodec
{
    /// <summary>当前支持的线合同版本。</summary>
    public const int CurrentContractVersion = 1;

    /// <summary>当前支持的实体 schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>当前支持的最大事件 UTF-8 字节数。</summary>
    public const int MaxEventBytes = 1 * 1024 * 1024;

    /// <summary>单个标识或 schema 名称的最大 UTF-8 字节数。</summary>
    public const int MaxStringBytes = 4 * 1024;

    /// <summary>before/after JSON 值的最大 UTF-8 字节数。</summary>
    public const int MaxPayloadBytes = 256 * 1024;

    private const int ReadBufferBytes = 8 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonDocumentOptions PayloadJsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    /// <summary>
    /// 将 CDC 事件编码为确定性的 UTF-8 JSON。
    /// </summary>
    /// <param name="value">待编码事件。</param>
    /// <returns>UTF-8 JSON 字节。</returns>
    /// <exception cref="ArgumentNullException">事件为空时抛出。</exception>
    /// <exception cref="ArgumentException">事件字段违反合同边界时抛出。</exception>
    public static byte[] Encode(CdcEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateForWrite(value);

        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("eventId", value.EventId);
            writer.WriteString("source", value.Source);
            writer.WriteString("entity", value.Entity);
            writer.WriteString("key", value.Key);
            writer.WriteNumber("sequence", value.Sequence);
            writer.WriteString("occurredAtUtc", value.OccurredAtUtc);

            writer.WritePropertyName("metadata");
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", value.Metadata.ContractVersion);
            writer.WriteString("schema", value.Metadata.Schema);
            writer.WriteNumber("schemaVersion", value.Metadata.SchemaVersion);
            writer.WriteString("operation", OperationName(value.Metadata.Operation));
            writer.WritePropertyName("checkpoint");
            writer.WriteStartObject();
            writer.WriteNumber("partition", value.Metadata.Checkpoint.Partition);
            writer.WriteNumber("offset", value.Metadata.Checkpoint.Offset);
            writer.WriteEndObject();
            writer.WriteEndObject();

            WritePayload(writer, "before", value.BeforeJson);
            WritePayload(writer, "after", value.AfterJson);
            writer.WriteEndObject();
        }

        if (output.WrittenCount > MaxEventBytes)
            throw new ArgumentException($"CDC 事件超过 {MaxEventBytes} 字节上限。", nameof(value));
        return output.WrittenSpan.ToArray();
    }

    /// <summary>
    /// 将事件编码并写入流；事件和写入过程都受取消令牌约束。
    /// </summary>
    /// <param name="destination">目标流。</param>
    /// <param name="value">待编码事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步写入操作。</returns>
    public static async ValueTask WriteAsync(
        Stream destination,
        CdcEvent value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encoded = Encode(value);
        await destination.WriteAsync(encoded.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从流读取一个有界 CDC 事件。
    /// </summary>
    /// <param name="source">包含单个 JSON 事件的流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解码后的事件。</returns>
    /// <exception cref="CdcFormatException">流超过边界或 JSON 合同无效时抛出。</exception>
    public static async ValueTask<CdcEvent> ReadAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        using var output = new MemoryStream(capacity: Math.Min(MaxEventBytes, 64 * 1024));
        byte[] rented = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        try
        {
            int total = 0;
            int maxReadOperations = MaxEventBytes + 1;
            for (int readOperation = 0; readOperation < maxReadOperations; readOperation++)
            {
                int remaining = MaxEventBytes + 1 - total;
                if (remaining <= 0)
                    throw new CdcFormatException($"CDC 事件超过 {MaxEventBytes} 字节上限。");

                int requested = Math.Min(rented.Length, remaining);
                int read = await source.ReadAsync(rented.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                total += read;
                if (total > MaxEventBytes)
                    throw new CdcFormatException($"CDC 事件超过 {MaxEventBytes} 字节上限。");
                await output.WriteAsync(rented.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (total == 0)
                throw new CdcFormatException("CDC 事件流为空。");
            return Decode(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// 解码一个严格有界的 UTF-8 JSON CDC 事件。
    /// </summary>
    /// <param name="utf8">单个事件的 UTF-8 JSON 字节。</param>
    /// <returns>解码后的不可变事件。</returns>
    /// <exception cref="CdcFormatException">JSON、版本或字段边界无效时抛出。</exception>
    public static CdcEvent Decode(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0)
            throw new CdcFormatException("CDC 事件为空。");
        if (utf8.Length > MaxEventBytes)
            throw new CdcFormatException($"CDC 事件超过 {MaxEventBytes} 字节上限。");

        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            JsonElement root = document.RootElement;
            RequireObject(root, "事件");
            EnsureKnownProperties(root, "eventId", "source", "entity", "key", "sequence", "occurredAtUtc", "metadata", "before", "after");

            string eventId = RequiredString(root, "eventId");
            string source = RequiredString(root, "source");
            string entity = RequiredString(root, "entity");
            string key = RequiredString(root, "key");
            long sequence = RequiredInt64(root, "sequence");
            DateTimeOffset occurredAtUtc = RequiredDateTimeOffset(root, "occurredAtUtc");
            JsonElement metadata = RequiredProperty(root, "metadata");
            RequireObject(metadata, "metadata");
            EnsureKnownProperties(metadata, "contractVersion", "schema", "schemaVersion", "operation", "checkpoint");

            int contractVersion = RequiredInt32(metadata, "contractVersion");
            if (contractVersion != CurrentContractVersion)
                throw new CdcUnsupportedVersionException(contractVersion);
            string schema = RequiredString(metadata, "schema");
            int schemaVersion = RequiredInt32(metadata, "schemaVersion");
            if (schemaVersion != CurrentSchemaVersion)
                throw new CdcUnsupportedSchemaVersionException(schema, schemaVersion);
            CdcOperation operation = ParseOperation(RequiredString(metadata, "operation"));

            JsonElement checkpoint = RequiredProperty(metadata, "checkpoint");
            RequireObject(checkpoint, "checkpoint");
            EnsureKnownProperties(checkpoint, "partition", "offset");
            long partition = RequiredInt64(checkpoint, "partition");
            long offset = RequiredInt64(checkpoint, "offset");
            string? before = ReadPayload(root, "before");
            string? after = ReadPayload(root, "after");

            var value = new CdcEvent(
                eventId,
                source,
                entity,
                key,
                sequence,
                occurredAtUtc,
                new CdcEventMetadata(contractVersion, schema, schemaVersion, operation, new CdcCheckpoint(partition, offset)),
                before,
                after);
            ValidateDecoded(value);
            return value;
        }
        catch (CdcFormatException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new CdcFormatException("CDC 事件 JSON 无效。", exception);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            throw new CdcFormatException("CDC 事件字段无效。", exception);
        }
    }

    private static void ValidateForWrite(CdcEvent value)
    {
        if (value.Metadata is null)
            throw new ArgumentException("CDC 事件 metadata 不能为空。", nameof(value));
        if (value.Metadata.ContractVersion != CurrentContractVersion)
            throw new CdcUnsupportedVersionException(value.Metadata.ContractVersion);
        ValidateString(value.EventId, nameof(value.EventId), required: true);
        ValidateString(value.Source, nameof(value.Source), required: true);
        ValidateString(value.Entity, nameof(value.Entity), required: true);
        ValidateString(value.Key, nameof(value.Key), required: true);
        ValidateString(value.Metadata.Schema, nameof(value.Metadata.Schema), required: true);
        if (value.Metadata.SchemaVersion != CurrentSchemaVersion)
            throw new CdcUnsupportedSchemaVersionException(value.Metadata.Schema, value.Metadata.SchemaVersion);
        if (!Enum.IsDefined(value.Metadata.Operation))
            throw new ArgumentOutOfRangeException(nameof(value.Metadata.Operation));
        if (value.Sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(value.Sequence));
        if (value.OccurredAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("OccurredAtUtc 必须为 UTC。", nameof(value.OccurredAtUtc));
        if (value.Metadata.Checkpoint.Partition < 0)
            throw new ArgumentOutOfRangeException(nameof(value.Metadata.Checkpoint.Partition));
        if (value.Metadata.Checkpoint.Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(value.Metadata.Checkpoint.Offset));
        ValidatePayloadForWrite("before", value.BeforeJson);
        ValidatePayloadForWrite("after", value.AfterJson);
        if (value.Metadata.Operation == CdcOperation.Insert && value.BeforeJson is not null)
            throw new ArgumentException("insert 事件不能包含 before 值。", nameof(value.BeforeJson));
        if (value.Metadata.Operation == CdcOperation.Delete && value.AfterJson is not null)
            throw new ArgumentException("delete 事件不能包含 after 值。", nameof(value.AfterJson));
    }

    private static void ValidateDecoded(CdcEvent value)
    {
        ValidateStringDecoded(value.EventId, "eventId", required: true);
        ValidateStringDecoded(value.Source, "source", required: true);
        ValidateStringDecoded(value.Entity, "entity", required: true);
        ValidateStringDecoded(value.Key, "key", required: true);
        ValidateStringDecoded(value.Metadata.Schema, "schema", required: true);
        if (value.Sequence < 0 || value.Metadata.Checkpoint.Partition < 0 || value.Metadata.Checkpoint.Offset < 0)
            throw new CdcFormatException("CDC 事件中的序号或 checkpoint 不能为负数。");
        if (value.OccurredAtUtc.Offset != TimeSpan.Zero)
            throw new CdcFormatException("occurredAtUtc 必须为 UTC。");
        ValidatePayloadDecoded("before", value.BeforeJson);
        ValidatePayloadDecoded("after", value.AfterJson);
        if (value.Metadata.Operation == CdcOperation.Insert && value.BeforeJson is not null)
            throw new CdcFormatException("insert 事件不能包含 before 值。");
        if (value.Metadata.Operation == CdcOperation.Delete && value.AfterJson is not null)
            throw new CdcFormatException("delete 事件不能包含 after 值。");
    }

    private static void WritePayload(Utf8JsonWriter writer, string name, string? payload)
    {
        writer.WritePropertyName(name);
        if (payload is null)
            writer.WriteNullValue();
        else
            writer.WriteRawValue(payload, skipInputValidation: false);
    }

    private static string? ReadPayload(JsonElement parent, string name)
    {
        JsonElement property = RequiredProperty(parent, name);
        if (property.ValueKind == JsonValueKind.Null)
            return null;
        return property.GetRawText();
    }

    private static JsonElement RequiredProperty(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new CdcFormatException($"CDC 事件缺少字段 '{name}'。");

    private static string RequiredString(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String)
            throw new CdcFormatException($"CDC 字段 '{name}' 必须为字符串。");
        return value.GetString() ?? throw new CdcFormatException($"CDC 字段 '{name}' 不能为 null。");
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw new CdcFormatException($"CDC 字段 '{name}' 必须为 32 位整数。");
        return result;
    }

    private static long RequiredInt64(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result))
            throw new CdcFormatException($"CDC 字段 '{name}' 必须为 64 位整数。");
        return result;
    }

    private static DateTimeOffset RequiredDateTimeOffset(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out DateTimeOffset result))
            throw new CdcFormatException($"CDC 字段 '{name}' 必须为 ISO 8601 时间。");
        return result;
    }

    private static CdcOperation ParseOperation(string operation)
        => operation switch
        {
            "insert" => CdcOperation.Insert,
            "update" => CdcOperation.Update,
            "delete" => CdcOperation.Delete,
            _ => throw new CdcFormatException($"未知 CDC operation '{operation}'。"),
        };

    private static string OperationName(CdcOperation operation)
        => operation switch
        {
            CdcOperation.Insert => "insert",
            CdcOperation.Update => "update",
            CdcOperation.Delete => "delete",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static void EnsureKnownProperties(JsonElement element, params string[] allowed)
    {
        var known = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name))
                throw new CdcFormatException($"CDC JSON 包含未知字段 '{property.Name}'。请先协商合同版本。");
            if (!seen.Add(property.Name))
                throw new CdcFormatException($"CDC JSON 字段 '{property.Name}' 重复。");
        }
    }

    private static void RequireObject(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new CdcFormatException($"CDC {description} 必须为 JSON 对象。");
    }

    private static void ValidateString(string value, string parameterName, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("字符串不能为空。", parameterName);
        if (GetUtf8ByteCountForWrite(value, parameterName) > MaxStringBytes)
            throw new ArgumentException($"字符串不能超过 {MaxStringBytes} 个 UTF-8 字节。", parameterName);
    }

    private static void ValidateStringDecoded(string value, string fieldName, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value))
            throw new CdcFormatException($"CDC 字段 '{fieldName}' 不能为空。");
        if (GetUtf8ByteCountForDecode(value, fieldName) > MaxStringBytes)
            throw new CdcFormatException($"CDC 字段 '{fieldName}' 超过 {MaxStringBytes} 个 UTF-8 字节上限。");
    }

    private static void ValidatePayloadForWrite(string name, string? payload)
    {
        if (payload is null)
            return;
        int byteCount = GetUtf8ByteCountForWrite(payload, name);
        if (byteCount > MaxPayloadBytes)
            throw new ArgumentException($"{name} JSON 不能超过 {MaxPayloadBytes} 个 UTF-8 字节。", name);
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload, PayloadJsonOptions);
            _ = document.RootElement;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"{name} 不是有效 JSON。", name, exception);
        }
    }

    private static void ValidatePayloadDecoded(string name, string? payload)
    {
        if (payload is null)
            return;
        if (GetUtf8ByteCountForDecode(payload, name) > MaxPayloadBytes)
            throw new CdcFormatException($"{name} JSON 超过 {MaxPayloadBytes} 个 UTF-8 字节上限。");
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload, PayloadJsonOptions);
            _ = document.RootElement;
        }
        catch (JsonException exception)
        {
            throw new CdcFormatException($"{name} 不是有效 JSON。", exception);
        }
    }

    private static int GetUtf8ByteCountForWrite(string value, string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("字符串不是有效的 UTF-8 文本。", parameterName, exception);
        }
    }

    private static int GetUtf8ByteCountForDecode(string value, string fieldName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new CdcFormatException($"CDC 字段 '{fieldName}' 不是有效的 UTF-8 文本。", exception);
        }
    }
}
