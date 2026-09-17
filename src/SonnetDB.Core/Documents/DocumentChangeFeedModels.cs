using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Documents;

/// <summary>
/// 文档集合中的一条持久化变更记录。
/// </summary>
/// <param name="Sequence">集合内单调递增的变更序号。</param>
/// <param name="OccurredAtUtc">变更发生的 UTC 时间。</param>
/// <param name="Operation">变更类型：insert、update 或 delete。</param>
/// <param name="DocumentId">文档 ID。</param>
/// <param name="DocumentVersion">变更完成后的底层版本；删除时为删除操作完成后的版本。</param>
/// <param name="BeforeJson">变更前文档；插入或大文档截断时为空。</param>
/// <param name="AfterJson">变更后文档；删除或大文档截断时为空。</param>
/// <param name="PayloadTruncated">前后镜像是否因大小限制被省略。</param>
public sealed record DocumentChangeFeedEntry(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string Operation,
    string DocumentId,
    long DocumentVersion,
    string? BeforeJson,
    string? AfterJson,
    bool PayloadTruncated,
    string Cause = DocumentChangeCauses.User,
    string? RequestId = null,
    int? OperationIndex = null,
    string? PatchJson = null);

/// <summary>文档变更的原生来源分类。</summary>
public static class DocumentChangeCauses
{
    /// <summary>普通用户 CRUD/更新写入。</summary>
    public const string User = "user";
    /// <summary>带 requestId 的 mixed bulk 写入。</summary>
    public const string Bulk = "bulk";
    /// <summary>TTL 索引触发的系统过期删除。</summary>
    public const string Ttl = "ttl";
}

/// <summary>
/// 文档变更订阅读取结果。
/// </summary>
/// <param name="Changes">本批匹配的变更。</param>
/// <param name="ResumeSequence">下一次读取应携带的最后处理序号。</param>
/// <param name="LatestSequence">读取时集合的最新变更序号。</param>
/// <param name="OldestAvailableSequence">当前保留窗口内最早可用序号。</param>
/// <param name="HasMore">当前最新序号之前是否仍有未扫描记录。</param>
public sealed record DocumentChangeFeedPage(
    IReadOnlyList<DocumentChangeFeedEntry> Changes,
    long ResumeSequence,
    long LatestSequence,
    long? OldestAvailableSequence,
    bool HasMore);

/// <summary>
/// 一条局部更新预览，不修改集合状态。
/// </summary>
/// <param name="Id">文档 ID。</param>
/// <param name="Version">当前文档版本；upsert 预览为 0。</param>
/// <param name="BeforeJson">更新前文档；upsert 预览为空。</param>
/// <param name="AfterJson">应用更新操作符后的文档。</param>
/// <param name="IsUpsert">是否为未匹配时的新文档预览。</param>
/// <param name="Changed">应用操作符后内容是否发生变化。</param>
public sealed record DocumentUpdatePreviewItem(
    string Id,
    long Version,
    string? BeforeJson,
    string AfterJson,
    bool IsUpsert,
    bool Changed);

internal static class DocumentChangeFeedCodec
{
    private const int MaxSnapshotBytes = 256 * 1024;
    private static readonly byte[] _prefix = [(byte)'c'];
    private static readonly byte[] _sequenceKey = [(byte)'m', (byte)'f'];

    public static ReadOnlySpan<byte> Prefix => _prefix;

    public static ReadOnlySpan<byte> SequenceKey => _sequenceKey;

    public static byte[] EncodeKey(long sequence)
    {
        var key = new byte[9];
        key[0] = (byte)'c';
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(1), sequence);
        return key;
    }

    public static long DecodeSequence(ReadOnlySpan<byte> key)
    {
        if (key.Length != 9 || key[0] != (byte)'c')
            throw new InvalidDataException("Document change feed key is invalid.");
        return BinaryPrimitives.ReadInt64BigEndian(key.Slice(1));
    }

    public static byte[] Encode(
        long sequence,
        DateTimeOffset occurredAtUtc,
        string operation,
        string documentId,
        long documentVersion,
        string? beforeJson,
        string? afterJson,
        string cause = DocumentChangeCauses.User,
        string? requestId = null,
        int? operationIndex = null,
        string? patchJson = null)
    {
        bool truncated = IsTooLarge(beforeJson) || IsTooLarge(afterJson);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", sequence);
            writer.WriteNumber("occurredAtUtcTicks", occurredAtUtc.UtcTicks);
            writer.WriteString("operation", operation);
            writer.WriteString("documentId", documentId);
            writer.WriteNumber("documentVersion", documentVersion);
            WriteSnapshot(writer, "before", truncated ? null : beforeJson);
            WriteSnapshot(writer, "after", truncated ? null : afterJson);
            writer.WriteBoolean("payloadTruncated", truncated);
            writer.WriteString("cause", cause);
            if (requestId is not null)
                writer.WriteString("requestId", requestId);
            if (operationIndex.HasValue)
                writer.WriteNumber("operationIndex", operationIndex.Value);
            if (patchJson is not null)
            {
                writer.WritePropertyName("patch");
                writer.WriteRawValue(patchJson, skipInputValidation: true);
            }
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static DocumentChangeFeedEntry Decode(ReadOnlySpan<byte> value)
    {
        using var document = JsonDocument.Parse(value.ToArray());
        var root = document.RootElement;
        return new DocumentChangeFeedEntry(
            root.GetProperty("sequence").GetInt64(),
            new DateTimeOffset(root.GetProperty("occurredAtUtcTicks").GetInt64(), TimeSpan.Zero),
            root.GetProperty("operation").GetString() ?? "unknown",
            root.GetProperty("documentId").GetString() ?? string.Empty,
            root.GetProperty("documentVersion").GetInt64(),
            ReadSnapshot(root, "before"),
            ReadSnapshot(root, "after"),
            root.TryGetProperty("payloadTruncated", out var truncated) && truncated.GetBoolean(),
            root.TryGetProperty("cause", out var cause) ? cause.GetString() ?? DocumentChangeCauses.User : DocumentChangeCauses.User,
            root.TryGetProperty("requestId", out var requestId) ? requestId.GetString() : null,
            root.TryGetProperty("operationIndex", out var operationIndex) && operationIndex.TryGetInt32(out int index) ? index : null,
            ReadSnapshot(root, "patch"));
    }

    private static bool IsTooLarge(string? json)
        => json is not null && Encoding.UTF8.GetByteCount(json) > MaxSnapshotBytes;

    private static void WriteSnapshot(Utf8JsonWriter writer, string name, string? json)
    {
        writer.WritePropertyName(name);
        if (json is null)
            writer.WriteNullValue();
        else
            writer.WriteRawValue(json, skipInputValidation: true);
    }

    private static string? ReadSnapshot(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetRawText()
            : null;
}
