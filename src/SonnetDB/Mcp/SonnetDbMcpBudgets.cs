using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SonnetDB.Exceptions;
using SonnetDB.Graphs;

namespace SonnetDB.Mcp;

/// <summary>固定容量的编码接收器；无法提供请求空间时，在分配或继续编码前拒绝。</summary>
internal sealed class McpBoundedBufferWriter(int capacity) : IBufferWriter<byte>
{
    private readonly byte[] _buffer = new byte[capacity];
    private int _written;

    internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);
    internal int Capacity => _buffer.Length;

    /// <summary>确认写入量；超额时保持计数不变并拒绝。</summary>
    public void Advance(int count)
    {
        if (count < 0 || count > _buffer.Length - _written)
            throw new PayloadLimitExceededException(_buffer.Length);
        _written += count;
    }

    /// <summary>仅返回现有固定缓冲，不扩容。</summary>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        if (Math.Max(1, sizeHint) > _buffer.Length - _written)
            throw new PayloadLimitExceededException(_buffer.Length);
        return _buffer.AsMemory(_written);
    }

    /// <summary>仅返回现有固定缓冲切片。</summary>
    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
}

internal sealed partial class SonnetDbMcpTools
{
    private static ReadOnlyMemory<byte> EncodeBoundedModel<T>(T value, JsonTypeInfo<T> typeInfo, int maxBytes)
    {
        var buffer = new McpBoundedBufferWriter(maxBytes);
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, value, typeInfo);
        writer.Flush();
        return buffer.WrittenMemory;
    }

    private static void ValidateGraphInput(
        IReadOnlyList<int>? labels, IReadOnlyList<GraphPropertyDto>? properties,
        IReadOnlyList<int>? uniquePropertyIds, CancellationToken cancellationToken)
    {
        if ((labels?.Count ?? 0) > 128 || (properties?.Count ?? 0) > 128 || (uniquePropertyIds?.Count ?? 0) > 128)
            throw new PayloadLimitExceededException(MaxModelValueBytes);
        long characters = 0;
        if (properties is null)
            return;
        for (int index = 0; index < properties.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var property = properties[index] ?? throw new ArgumentException("Graph property 不能为空。");
            var value = property.Value ?? throw new ArgumentException("Graph property value 不能为空。");
            // 逐字段先查长度，包含与 Kind 不匹配而不会被 ToValue 使用的字段。
            foreach (string? text in new[] { value.String, value.Json, value.BlobBase64 })
            {
                if (text is null)
                    continue;
                if (text.Length > MaxModelTextChars)
                    throw new PayloadLimitExceededException(MaxModelValueBytes);
                characters += (long)text.Length * 6;
                if (characters > MaxModelValueBytes)
                    throw new PayloadLimitExceededException(MaxModelValueBytes);
            }
        }
    }
}
