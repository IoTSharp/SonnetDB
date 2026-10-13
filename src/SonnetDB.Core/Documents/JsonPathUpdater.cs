using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Documents;

/// <summary>以固定资源上限重写单个 JSON 标量路径，不产生存储副作用。</summary>
internal static class JsonPathUpdater
{
    private const int MaxUtf8Bytes = 8 * 1024 * 1024;
    private const int MaxDepth = 64;
    private const int MaxNodes = 100_000;
    private const int MaxOperations = 1_000_000;
    private const int EncodingChars = 1024;
    private const int EncodingBytes = EncodingChars * 3;
    private const int MaxScratchBytes = EncodingChars * sizeof(char) + EncodingBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly AsyncLocal<WorkObservation?> Observation = new();

    internal enum WorkStage { ValidationNode, RewritingNode, BeforeReturn }

    // 测试观察器只作用于当前执行上下文；生产调用使用系统单调时钟和相同的检查路径。
    internal static IDisposable ObserveWork(Action<WorkStage> observer, TimeProvider? timeProvider = null,
        Action<int>? scratchObserver = null)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var previous = Observation.Value;
        Observation.Value = new WorkObservation(observer, timeProvider ?? TimeProvider.System, scratchObserver);
        return new ObservationScope(previous);
    }

    internal static string SetValue(string json, string pathText, object? value)
        => SetValueCore(json, pathText, value, new WorkBudget(Observation.Value), allowContainers: false);

    internal static string SetSerializedValue(string json, string pathText, string? value)
    {
        var budget = new WorkBudget(Observation.Value);
        budget.Check();
        var text = value ?? "null";
        RequireUtf8Budget(text, budget);
        JsonDocument replacement;
        try
        {
            replacement = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException exception)
        {
            throw Error("replacement JSON 无效或嵌套深度超过 64。", exception);
        }

        using (replacement)
        {
            ValidateDocument(replacement.RootElement, 0, budget);
            return SetValueCore(json, pathText, replacement.RootElement, budget, allowContainers: true);
        }
    }

    private static string SetValueCore(string json, string pathText, object? value, WorkBudget budget, bool allowContainers)
    {
        budget.Check();
        RequireUtf8Budget(json, budget);
        if (pathText.Length > 1024)
            throw Error("JSON path 超过 1024 字符上限。");
        RequireUtf8Budget(pathText, budget);
        if (!allowContainers)
            ValidateScalar(value, budget);

        JsonPath path;
        try
        {
            path = JsonPath.Parse(pathText);
        }
        catch (ArgumentException exception)
        {
            throw Error("JSON path 无效。", exception);
        }
        if (path.Segments.Count is 0 or > 64)
            throw Error("只支持 1 到 64 个片段的非根 JSON path。");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException exception)
        {
            throw Error("JSON 无效或嵌套深度超过 64。", exception);
        }

        using (document)
        {
            budget.Check();
            ValidateDocument(document.RootElement, 0, budget);
            ValidateTarget(document.RootElement, path, budget, allowContainers);
            var output = new CappedOutput(budget);
            Rewrite(document.RootElement, output, path, value, 0, true, 0, budget);
            budget.Check();
            string result = output.GetText();
            budget.AtStage(WorkStage.BeforeReturn);
            return result;
        }
    }

    private static void RequireUtf8Budget(string text, WorkBudget budget)
    {
        if (text.Length > MaxUtf8Bytes)
            throw Error("输入超过 8388608 UTF-8 字节上限。");

        int bytes = 0;
        try
        {
            // 每块最多 4096 字符，保留跨块 surrogate pair；总长度与操作/时间预算共同约束循环。
            for (int offset = 0; offset < text.Length;)
            {
                budget.Check();
                budget.Consume();
                int count = Math.Min(4096, text.Length - offset);
                if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1]))
                    count--;
                bytes += StrictUtf8.GetByteCount(text.AsSpan(offset, count));
                if (bytes > MaxUtf8Bytes)
                    throw Error("输入超过 8388608 UTF-8 字节上限。");
                offset += count;
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw Error("输入包含无效 UTF-16 字符。", exception);
        }
    }

    private static void ValidateScalar(object? value, WorkBudget budget)
    {
        if (value is string text)
        {
            RequireUtf8Budget(text, budget);
            return;
        }
        if (value is null or bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal
            || value is float single && float.IsFinite(single)
            || value is double number && double.IsFinite(number))
            return;

        throw Error("value 必须是字符串、NULL、布尔值或有限的受支持数值标量。");
    }

    private static void ValidateDocument(JsonElement element, int depth, WorkBudget budget)
    {
        budget.VisitNode(depth, writing: false);
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                budget.Consume();
                if (!names.Add(property.Name))
                    throw Error("JSON 对象包含重复属性名。");
                ValidateDocument(property.Value, depth + 1, budget);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateDocument(item, depth + 1, budget);
        }
    }

    private static void ValidateTarget(JsonElement root, JsonPath path, WorkBudget budget, bool allowContainers)
    {
        var parent = root;
        for (int index = 0; index < path.Segments.Count; index++)
        {
            budget.Consume();
            var segment = path.Segments[index];
            bool last = index == path.Segments.Count - 1;
            if (segment.Kind == JsonPathSegmentKind.Property)
            {
                if (parent.ValueKind != JsonValueKind.Object)
                    throw Error("属性路径的 parent 必须是现有 JSON 对象。");
                if (last)
                {
                    if (!allowContainers && parent.TryGetProperty(segment.PropertyName!, out var existing))
                        RequireScalarLeaf(existing);
                    return;
                }
                if (!parent.TryGetProperty(segment.PropertyName!, out parent))
                    throw Error("JSON path 的中间 parent 不存在。");
            }
            else
            {
                if (parent.ValueKind != JsonValueKind.Array || segment.ArrayIndex >= parent.GetArrayLength())
                    throw Error("数组路径必须指向现有数组中未越界的元素。");
                parent = parent[segment.ArrayIndex];
                if (last && !allowContainers)
                    RequireScalarLeaf(parent);
            }
        }
    }

    private static void RequireScalarLeaf(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            throw Error("只支持标量 leaf，不允许覆盖 JSON 对象或数组子树。");
    }

    private static void Rewrite(JsonElement element, CappedOutput output, JsonPath path, object? value,
        int matched, bool onPath, int depth, WorkBudget budget)
    {
        if (onPath && matched == path.Segments.Count)
        {
            WriteReplacement(output, path, value, depth, budget);
            return;
        }

        budget.VisitNode(depth, writing: true);
        if (element.ValueKind == JsonValueKind.Object)
        {
            output.WriteByte((byte)'{');
            string? target = onPath ? path.Segments[matched].PropertyName : null;
            bool found = false;
            bool first = true;
            foreach (var property in element.EnumerateObject())
            {
                budget.Consume();
                if (!first)
                    output.WriteByte((byte)',');
                first = false;
                string name = property.Name;
                WriteString(output, name, budget);
                output.WriteByte((byte)':');
                bool selected = onPath && string.Equals(name, target, StringComparison.Ordinal);
                found |= selected;
                Rewrite(property.Value, output, path, value, selected ? matched + 1 : 0,
                    selected, depth + 1, budget);
            }
            if (onPath && !found)
            {
                // ValidateTarget 已保证只有末端属性允许新增，不自动创建 parent。
                if (!first)
                    output.WriteByte((byte)',');
                WriteString(output, target!, budget);
                output.WriteByte((byte)':');
                WriteReplacement(output, path, value, depth + 1, budget);
            }
            output.WriteByte((byte)'}');
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            output.WriteByte((byte)'[');
            int index = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (index != 0)
                    output.WriteByte((byte)',');
                bool selected = onPath && index == path.Segments[matched].ArrayIndex;
                Rewrite(item, output, path, value, selected ? matched + 1 : 0,
                    selected, depth + 1, budget);
                index++;
            }
            output.WriteByte((byte)']');
        }
        else
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String: WriteString(output, element.GetString()!, budget); break;
                case JsonValueKind.Number: WriteRawNumber(output, element.GetRawText(), budget); break;
                case JsonValueKind.True: output.Write("true"u8); break;
                case JsonValueKind.False: output.Write("false"u8); break;
                case JsonValueKind.Null: output.Write("null"u8); break;
                default: throw Error("不支持的 JSON 值。");
            }
        }
    }

    private static void WriteReplacement(CappedOutput output, JsonPath path, object? value, int depth, WorkBudget budget)
    {
        if (value is JsonElement replacement)
        {
            Rewrite(replacement, output, path, null, 0, false, depth, budget);
            return;
        }

        budget.VisitNode(depth, writing: true);
        WriteScalar(output, value, budget);
    }

    private static void WriteString(CappedOutput output, string text, WorkBudget budget)
    {
        output.WriteByte((byte)'"');
        budget.AcquireScratch(MaxScratchBytes);
        try
        {
            // 默认 encoder 与 Utf8JsonWriter 相同。固定输出块不按 token 的最坏膨胀量预留，
            // surrogate pair 由 encoder 作为完整 scalar 消费；只对实际写出的字节应用结果上限。
            Span<char> escaped = stackalloc char[EncodingChars];
            Span<byte> utf8 = stackalloc byte[EncodingBytes];
            for (int offset = 0; offset < text.Length;)
            {
                budget.Consume();
                OperationStatus status = JavaScriptEncoder.Default.Encode(text.AsSpan(offset), escaped,
                    out int consumed, out int written, isFinalBlock: true);
                if (status is not (OperationStatus.Done or OperationStatus.DestinationTooSmall) || consumed == 0)
                    throw Error("JSON 字符串编码失败。");
                int byteCount = StrictUtf8.GetBytes(escaped[..written], utf8);
                output.Write(utf8[..byteCount]);
                offset += consumed;
            }
        }
        finally
        {
            budget.ReleaseScratch(MaxScratchBytes);
        }
        output.WriteByte((byte)'"');
    }

    private static void WriteRawNumber(CappedOutput output, string text, WorkBudget budget)
    {
        budget.AcquireScratch(EncodingBytes);
        try
        {
            // JsonDocument 已验证数字词法；按块复制原文，长整数也不经过 double 或 writer 预留。
            Span<byte> utf8 = stackalloc byte[EncodingBytes];
            for (int offset = 0; offset < text.Length;)
            {
                budget.Consume();
                int count = Math.Min(EncodingChars, text.Length - offset);
                int written = StrictUtf8.GetBytes(text.AsSpan(offset, count), utf8);
                output.Write(utf8[..written]);
                offset += count;
            }
        }
        finally
        {
            budget.ReleaseScratch(EncodingBytes);
        }
    }

    private static void WriteScalar(CappedOutput output, object? value, WorkBudget budget)
    {
        switch (value)
        {
            case null: output.Write("null"u8); return;
            case string text: WriteString(output, text, budget); return;
            case bool boolean: output.Write(boolean ? "true"u8 : "false"u8); return;
        }

        budget.AcquireScratch(NumberBuffer.Capacity);
        try
        {
            // 每个 replacement 数字独用一个固定缓冲，保留 Utf8JsonWriter 的数值格式合同。
            // 该缓冲既不接收字符串，也不使用结果剩余容量约束 writer 的 sizeHint。
            var buffer = new NumberBuffer();
            using var writer = new Utf8JsonWriter(buffer);
            WriteNumber(writer, value);
            writer.Flush();
            output.Write(buffer.WrittenSpan);
        }
        finally
        {
            budget.ReleaseScratch(NumberBuffer.Capacity);
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case byte number: writer.WriteNumberValue(number); break;
            case sbyte number: writer.WriteNumberValue(number); break;
            case short number: writer.WriteNumberValue(number); break;
            case ushort number: writer.WriteNumberValue(number); break;
            case int number: writer.WriteNumberValue(number); break;
            case uint number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case ulong number: writer.WriteNumberValue(number); break;
            case float number: writer.WriteNumberValue(number); break;
            case double number: writer.WriteNumberValue(number); break;
            case decimal number: writer.WriteNumberValue(number); break;
            default: throw Error("不支持的 JSON 标量类型。");
        }
    }

    private static InvalidOperationException Error(string message, Exception? inner = null)
        => new("json_set_value: " + message, inner);

    private sealed record WorkObservation(Action<WorkStage> Observer, TimeProvider Clock, Action<int>? ScratchObserver);

    private sealed class ObservationScope(WorkObservation? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            Observation.Value = previous;
            _disposed = true;
        }
    }

    private sealed class WorkBudget(WorkObservation? observation)
    {
        private readonly TimeProvider _clock = observation?.Clock ?? TimeProvider.System;
        private readonly long _started = (observation?.Clock ?? TimeProvider.System).GetTimestamp();
        private int _operations;
        private int _inputNodes;
        private int _outputNodes;
        private int _scratchBytes;

        internal void Check()
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (_clock.GetElapsedTime(_started) > TimeSpan.FromSeconds(10))
                throw Error("单次求值超过 10 秒上限。");
        }

        internal void AtStage(WorkStage stage)
        {
            observation?.Observer(stage);
            Check();
        }

        internal void AcquireScratch(int bytes)
        {
            Check();
            if (bytes > MaxScratchBytes - _scratchBytes)
                throw Error("编码临时缓冲超过 5120 字节上限。");
            _scratchBytes += bytes;
            observation?.ScratchObserver?.Invoke(_scratchBytes);
        }

        internal void ReleaseScratch(int bytes)
        {
            _scratchBytes -= bytes;
            observation?.ScratchObserver?.Invoke(_scratchBytes);
        }

        internal void Consume()
        {
            if ((_operations & 255) == 0)
                Check();
            if (++_operations > MaxOperations)
                throw Error("操作次数超过 1000000 上限。");
        }

        internal void VisitNode(int depth, bool writing)
        {
            Consume();
            if (depth > MaxDepth)
                throw Error("JSON 深度超过 64 上限。");
            int nodes = writing ? ++_outputNodes : ++_inputNodes;
            if (nodes > MaxNodes)
                throw Error("JSON 节点数超过 100000 上限。");
            AtStage(writing ? WorkStage.RewritingNode : WorkStage.ValidationNode);
        }
    }

    private sealed class NumberBuffer : IBufferWriter<byte>
    {
        internal const int Capacity = 256;
        private readonly byte[] _buffer = new byte[Capacity];
        private int _written;

        internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Advance(int count)
        {
            if (count < 0 || count > Capacity - _written)
                throw Error("数字编码超出固定临时缓冲。");
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint < 0 || sizeHint > Capacity - _written)
                throw Error("数字编码预留超出固定临时缓冲。");
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    private sealed class CappedOutput(WorkBudget budget)
    {
        private byte[] _buffer = [];
        private int _length;

        internal string GetText() => StrictUtf8.GetString(_buffer, 0, _length);

        internal void Write(ReadOnlySpan<byte> buffer)
        {
            budget.Check();
            if (buffer.Length > MaxUtf8Bytes - _length)
                throw Error("输出超过 8388608 UTF-8 字节上限。");
            int required = _length + buffer.Length;
            if (required > _buffer.Length)
            {
                int capacity = Math.Min(MaxUtf8Bytes, Math.Max(required, Math.Max(256, _buffer.Length * 2)));
                Array.Resize(ref _buffer, capacity);
            }
            buffer.CopyTo(_buffer.AsSpan(_length));
            _length = required;
        }

        internal void WriteByte(byte value) => Write([value]);
    }
}
