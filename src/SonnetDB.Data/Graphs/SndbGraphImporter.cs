using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Graphs;

namespace SonnetDB.Data.Graphs;

/// <summary>Graph JSON/CSV 分批导入选项。</summary>
public sealed record SndbGraphImportOptions
{
    /// <summary>根导入 request ID；相同值可安全重试。</summary>
    public Guid RequestId { get; init; } = Guid.NewGuid();

    /// <summary>每个原子批次最多包含的元素数。</summary>
    public int BatchSize { get; init; } = 1_000;

    internal void Validate()
    {
        if (RequestId == Guid.Empty)
            throw new ArgumentException("导入 request ID 不能为空。", nameof(RequestId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BatchSize);
        if (BatchSize > 10_000)
            throw new ArgumentOutOfRangeException(nameof(BatchSize), "BatchSize 不能超过 10,000。");
    }
}

/// <summary>分批导入结果。</summary>
/// <param name="VertexCount">导入的顶点数量。</param>
/// <param name="EdgeCount">导入的边数量。</param>
/// <param name="BatchCount">提交的原子批次数量。</param>
/// <param name="LastSequence">最后一次提交的序列号。</param>
public sealed record SndbGraphImportReport(
    int VertexCount,
    int EdgeCount,
    int BatchCount,
    long LastSequence);

/// <summary>
/// Graph JSON/CSV 输入导入器。JSON 兼容 native vertices/edges 以及 nodes/relationships 别名。
/// </summary>
public static class SndbGraphImporter
{
    /// <summary>把外部字符串元素 ID 映射为稳定的正数 Graph ID。</summary>
    /// <param name="externalId">外部元素 ID。</param>
    /// <returns>由 SHA-256 确定性映射的 Graph 元素 ID。</returns>
    public static GraphElementId GetStableElementId(string externalId)
        => new(GetStableInt64("element", externalId));

    /// <summary>把外部 label/type 名称映射为稳定的正数 label ID。</summary>
    /// <param name="label">外部 label/type 名称。</param>
    /// <returns>由 SHA-256 确定性映射的 label ID。</returns>
    public static LabelId GetStableLabelId(string label)
        => new(GetStableInt32("label", label));

    /// <summary>把外部属性名称映射为稳定的正数 property ID。</summary>
    /// <param name="property">外部属性名称。</param>
    /// <returns>由 SHA-256 确定性映射的 property ID。</returns>
    public static int GetStablePropertyId(string property)
        => GetStableInt32("property", property);

    /// <summary>从 source-generated JSON DTO 导入图。</summary>
    /// <param name="client">Graph typed client。</param>
    /// <param name="graph">图名称。</param>
    /// <param name="source">JSON 输入流。</param>
    /// <param name="options">分批和幂等选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导入计数和最后提交序列号。</returns>
    public static async Task<SndbGraphImportReport> ImportJsonAsync(
        SndbGraphClient client,
        string graph,
        Stream source,
        SndbGraphImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(source);
        SndbGraphImportOptions importOptions = options ?? new SndbGraphImportOptions();
        importOptions.Validate();
        string spoolDirectory = Path.Combine(
            Path.GetTempPath(),
            "sonnetdb-graph-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spoolDirectory);
        try
        {
            string vertexPath = Path.Combine(spoolDirectory, "vertices.ndjson");
            string edgePath = Path.Combine(spoolDirectory, "edges.ndjson");
            await using (var vertexFile = new FileStream(vertexPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16 * 1024, FileOptions.SequentialScan))
            await using (var edgeFile = new FileStream(edgePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16 * 1024, FileOptions.SequentialScan))
            using (var vertexWriter = new StreamWriter(vertexFile, new UTF8Encoding(false), 16 * 1024, leaveOpen: true))
            using (var edgeWriter = new StreamWriter(edgeFile, new UTF8Encoding(false), 16 * 1024, leaveOpen: true))
            {
                long relationshipOrdinal = 0;
                await ParseJsonToSpoolAsync(
                    source,
                    (arrayName, document) =>
                    {
                        if (arrayName is "vertices" or "nodes")
                        {
                            GraphImportVertexDto vertex = DeserializeVertex(document);
                            vertexWriter.WriteLine(
                                JsonSerializer.Serialize(vertex, Remote.RemoteJsonContext.Default.GraphImportVertexDto));
                        }
                        else if (arrayName is "edges" or "relationships")
                        {
                            GraphImportEdgeDto edge = DeserializeEdge(document, relationshipOrdinal++);
                            edgeWriter.WriteLine(
                                JsonSerializer.Serialize(edge, Remote.RemoteJsonContext.Default.GraphImportEdgeDto));
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
                await vertexWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                await edgeWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return await ImportSpoolAsync(
                client,
                graph,
                vertexPath,
                edgePath,
                importOptions,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(spoolDirectory))
                    Directory.Delete(spoolDirectory, recursive: true);
            }
            catch
            {
                // Preserve import outcome; the OS temporary directory can be cleaned later.
            }
        }
    }

    /// <summary>
    /// 从简洁 CSV 导入图。顶点格式为 <c>id,labels</c>，labels 使用分号分隔；
    /// 边格式为 <c>id,sourceId,targetId,labelId</c>。
    /// </summary>
    /// <param name="client">Graph typed client。</param>
    /// <param name="graph">图名称。</param>
    /// <param name="vertices">顶点 CSV 流。</param>
    /// <param name="edges">边 CSV 流。</param>
    /// <param name="options">分批和幂等选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导入计数和最后提交序列号。</returns>
    public static async Task<SndbGraphImportReport> ImportCsvAsync(
        SndbGraphClient client,
        string graph,
        Stream vertices,
        Stream edges,
        SndbGraphImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(edges);
        SndbGraphImportOptions importOptions = options ?? new SndbGraphImportOptions();
        importOptions.Validate();
        int batchIndex = 0;
        int vertexCount = 0;
        int edgeCount = 0;
        long lastSequence = 0;
        int batchCount = 0;
        using (var reader = new StreamReader(vertices, leaveOpen: true))
        {
            var batch = new List<GraphImportVertexDto>(importOptions.BatchSize);
            await ReadCsvLinesAsync(
                reader,
                expectedFields: 2,
                line => new GraphImportVertexDto
                {
                    Id = ParsePositiveLong(line[0], "vertex id"),
                    Labels = string.IsNullOrWhiteSpace(line[1])
                        ? []
                        : line[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(value => ParsePositiveInt(value, "label"))
                            .ToArray(),
                },
                async item =>
                {
                    batch.Add(item);
                    vertexCount++;
                    if (batch.Count < importOptions.BatchSize)
                        return;
                    GraphImportResponse result = await client.ImportAsync(
                        graph,
                        new GraphImportRequest { RequestId = BatchRequestId(importOptions.RequestId, batchIndex++), Vertices = batch.ToArray() },
                        cancellationToken).ConfigureAwait(false);
                    lastSequence = result.Sequence;
                    batchCount++;
                    batch.Clear();
                },
                cancellationToken).ConfigureAwait(false);
            if (batch.Count > 0)
            {
                GraphImportResponse result = await client.ImportAsync(
                    graph,
                    new GraphImportRequest { RequestId = BatchRequestId(importOptions.RequestId, batchIndex++), Vertices = batch.ToArray() },
                    cancellationToken).ConfigureAwait(false);
                lastSequence = result.Sequence;
                batchCount++;
            }
        }

        using (var reader = new StreamReader(edges, leaveOpen: true))
        {
            var batch = new List<GraphImportEdgeDto>(importOptions.BatchSize);
            await ReadCsvLinesAsync(
                reader,
                expectedFields: 4,
                line => new GraphImportEdgeDto
                {
                    Id = ParsePositiveLong(line[0], "edge id"),
                    SourceId = ParsePositiveLong(line[1], "source id"),
                    TargetId = ParsePositiveLong(line[2], "target id"),
                    LabelId = ParsePositiveInt(line[3], "edge label"),
                },
                async item =>
                {
                    batch.Add(item);
                    edgeCount++;
                    if (batch.Count < importOptions.BatchSize)
                        return;
                    GraphImportResponse result = await client.ImportAsync(
                        graph,
                        new GraphImportRequest { RequestId = BatchRequestId(importOptions.RequestId, batchIndex++), Edges = batch.ToArray() },
                        cancellationToken).ConfigureAwait(false);
                    lastSequence = result.Sequence;
                    batchCount++;
                    batch.Clear();
                },
                cancellationToken).ConfigureAwait(false);
            if (batch.Count > 0)
            {
                GraphImportResponse result = await client.ImportAsync(
                    graph,
                    new GraphImportRequest { RequestId = BatchRequestId(importOptions.RequestId, batchIndex++), Edges = batch.ToArray() },
                    cancellationToken).ConfigureAwait(false);
                lastSequence = result.Sequence;
                batchCount++;
            }
        }

        return new SndbGraphImportReport(vertexCount, edgeCount, batchCount, lastSequence);
    }

    private static async Task<SndbGraphImportReport> ImportSpoolAsync(
        SndbGraphClient client,
        string graph,
        string vertexPath,
        string edgePath,
        SndbGraphImportOptions options,
        CancellationToken cancellationToken)
    {
        int batchCount = 0;
        int vertexCount = 0;
        int edgeCount = 0;
        int batchIndex = 0;
        long lastSequence = 0;
        using (var reader = new StreamReader(vertexPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            var batch = new List<GraphImportVertexDto>(options.BatchSize);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                batch.Add(JsonSerializer.Deserialize(line, Remote.RemoteJsonContext.Default.GraphImportVertexDto)
                    ?? throw new InvalidDataException("Graph vertex spool 行为空。"));
                vertexCount++;
                if (batch.Count < options.BatchSize)
                    continue;
                GraphImportResponse result = await client.ImportAsync(
                    graph,
                    new GraphImportRequest { RequestId = BatchRequestId(options.RequestId, batchIndex++), Vertices = batch.ToArray() },
                    cancellationToken).ConfigureAwait(false);
                lastSequence = result.Sequence;
                batchCount++;
                batch.Clear();
            }
            if (batch.Count > 0)
            {
                GraphImportResponse result = await client.ImportAsync(
                    graph,
                    new GraphImportRequest { RequestId = BatchRequestId(options.RequestId, batchIndex++), Vertices = batch.ToArray() },
                    cancellationToken).ConfigureAwait(false);
                lastSequence = result.Sequence;
                batchCount++;
            }
        }
        using (var reader = new StreamReader(edgePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            var batch = new List<GraphImportEdgeDto>(options.BatchSize);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                batch.Add(JsonSerializer.Deserialize(line, Remote.RemoteJsonContext.Default.GraphImportEdgeDto)
                    ?? throw new InvalidDataException("Graph edge spool 行为空。"));
                edgeCount++;
                if (batch.Count < options.BatchSize)
                    continue;
                GraphImportResponse result = await client.ImportAsync(
                    graph,
                    new GraphImportRequest { RequestId = BatchRequestId(options.RequestId, batchIndex++), Edges = batch.ToArray() },
                    cancellationToken).ConfigureAwait(false);
                lastSequence = result.Sequence;
                batchCount++;
                batch.Clear();
            }
            if (batch.Count > 0)
            {
                GraphImportResponse result = await client.ImportAsync(
                    graph,
                    new GraphImportRequest { RequestId = BatchRequestId(options.RequestId, batchIndex++), Edges = batch.ToArray() },
                    cancellationToken).ConfigureAwait(false);
                lastSequence = result.Sequence;
                batchCount++;
            }
        }
        return new SndbGraphImportReport(vertexCount, edgeCount, batchCount, lastSequence);
    }

    private static Guid BatchRequestId(Guid root, int batch)
    {
        Span<byte> input = stackalloc byte[20];
        root.TryWriteBytes(input[..16], bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(input[16..], batch);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16], bigEndian: true);
    }

    private static GraphImportVertexDto DeserializeVertex(JsonDocument document)
    {
        try
        {
            return document.RootElement.Deserialize(Remote.RemoteJsonContext.Default.GraphImportVertexDto)
                ?? throw new InvalidDataException("Graph JSON vertex 元素为空。");
        }
        catch (JsonException)
        {
            JsonElement element = document.RootElement;
            JsonElement idElement = GetRequiredProperty(element, "id");
            return new GraphImportVertexDto
            {
                Id = ReadElementId(idElement),
                Labels = ReadLabels(element),
                Properties = ReadNormalizedProperties(element, isRelationship: false),
                ExpectedElementVersion = ReadOptionalInt64(element, "expectedElementVersion"),
            };
        }
    }

    private static GraphImportEdgeDto DeserializeEdge(JsonDocument document, long ordinal)
    {
        try
        {
            return document.RootElement.Deserialize(Remote.RemoteJsonContext.Default.GraphImportEdgeDto)
                ?? throw new InvalidDataException("Graph JSON edge 元素为空。");
        }
        catch (JsonException)
        {
            JsonElement element = document.RootElement;
            JsonElement source = GetRequiredProperty(element, "sourceId", "source", "from");
            JsonElement target = GetRequiredProperty(element, "targetId", "target", "to");
            JsonElement? id = FindProperty(element, "id");
            long sourceId = ReadElementId(source);
            long targetId = ReadElementId(target);
            string labelText = ReadRequiredText(element, "labelId", "label", "type", "relationship");
            int labelId = int.TryParse(
                labelText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int numericLabel)
                && numericLabel > 0
                    ? numericLabel
                    : GetStableLabelId(labelText).Value;
            long edgeId = id is { } idValue
                ? ReadElementId(idValue)
                : GetStableInt64(
                    "relationship",
                    sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ":" + targetId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ":" + labelText
                    + ":" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return new GraphImportEdgeDto
            {
                Id = edgeId,
                SourceId = sourceId,
                TargetId = targetId,
                LabelId = labelId,
                Properties = ReadNormalizedProperties(element, isRelationship: true),
                ExpectedElementVersion = ReadOptionalInt64(element, "expectedElementVersion"),
            };
        }
    }

    private static int[] ReadLabels(JsonElement element)
    {
        var labels = new HashSet<int>();
        if (FindProperty(element, "labels") is { } labelArray)
        {
            if (labelArray.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Graph JSON labels 必须是数组。");
            foreach (JsonElement label in labelArray.EnumerateArray())
                labels.Add(ReadLabelId(label));
        }
        foreach (string name in new[] { "label", "type" })
        {
            if (FindProperty(element, name) is { } label)
                labels.Add(ReadLabelId(label));
        }
        return labels.Order().ToArray();
    }

    private static GraphPropertyDto[] ReadNormalizedProperties(JsonElement element, bool isRelationship)
    {
        var properties = new Dictionary<int, GraphPropertyDto>();
        if (FindProperty(element, "properties") is { } propertyObject)
        {
            if (propertyObject.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Graph JSON properties 必须是对象或 native 属性数组。");
            foreach (JsonProperty property in propertyObject.EnumerateObject())
                AddNormalizedProperty(properties, property.Name, property.Value);
        }

        string[] provenanceNames = isRelationship
            ? ["provenance", "confidence", "sourceFile", "sourceLocation", "sourceUri", "sourceText"]
            : ["provenance", "confidence", "source", "sourceFile", "sourceLocation", "sourceUri", "sourceText"];
        foreach (string name in provenanceNames)
        {
            if (FindProperty(element, name) is { } value)
                AddNormalizedProperty(properties, name, value);
        }
        return properties.Values.OrderBy(static property => property.PropertyId).ToArray();
    }

    private static void AddNormalizedProperty(
        Dictionary<int, GraphPropertyDto> properties,
        string name,
        JsonElement value)
    {
        int propertyId = GetStablePropertyId(name);
        properties[propertyId] = new GraphPropertyDto
        {
            PropertyId = propertyId,
            Value = ToValueDto(value),
        };
    }

    private static GraphValueDto ToValueDto(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => new GraphValueDto { Kind = GraphPropertyKind.Null },
            JsonValueKind.True => new GraphValueDto { Kind = GraphPropertyKind.Boolean, Boolean = true },
            JsonValueKind.False => new GraphValueDto { Kind = GraphPropertyKind.Boolean, Boolean = false },
            JsonValueKind.Number when value.TryGetInt64(out long integer) =>
                new GraphValueDto { Kind = GraphPropertyKind.Int64, Int64 = integer },
            JsonValueKind.Number =>
                new GraphValueDto { Kind = GraphPropertyKind.Float64, Float64 = value.GetDouble() },
            JsonValueKind.String =>
                new GraphValueDto { Kind = GraphPropertyKind.String, String = value.GetString()! },
            JsonValueKind.Array or JsonValueKind.Object =>
                new GraphValueDto { Kind = GraphPropertyKind.Json, Json = value.GetRawText() },
            _ => throw new InvalidDataException("Graph JSON property value 类型无效。"),
        };

    private static long ReadElementId(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long numeric) && numeric > 0)
            return numeric;
        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            return GetStableElementId(value.GetString()!).Value;
        throw new InvalidDataException("Graph JSON element ID 必须是正整数或非空字符串。");
    }

    private static int ReadLabelId(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numeric) && numeric > 0)
            return numeric;
        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            return GetStableLabelId(value.GetString()!).Value;
        throw new InvalidDataException("Graph JSON label 必须是正整数或非空字符串。");
    }

    private static long ReadOptionalInt64(JsonElement element, string name)
    {
        if (FindProperty(element, name) is not { } value)
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result) && result >= 0)
            return result;
        throw new InvalidDataException($"Graph JSON {name} 必须是非负整数。");
    }

    private static string ReadRequiredText(JsonElement element, params string[] names)
    {
        JsonElement value = GetRequiredProperty(element, names);
        return value.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            _ => throw new InvalidDataException($"Graph JSON {string.Join('/', names)} 必须是正整数或非空字符串。"),
        };
    }

    private static JsonElement GetRequiredProperty(JsonElement element, params string[] names)
        => FindProperty(element, names)
            ?? throw new InvalidDataException($"Graph JSON 缺少 {string.Join('/', names)}。 ");

    private static JsonElement? FindProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Graph JSON 元素必须是对象。");
        foreach (JsonProperty property in element.EnumerateObject())
        foreach (string name in names)
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }
        return null;
    }

    private static long GetStableInt64(string category, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(category + ":" + value));
        long result = BinaryPrimitives.ReadInt64BigEndian(digest) & long.MaxValue;
        return result == 0 ? 1 : result;
    }

    private static int GetStableInt32(string category, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(category + ":" + value));
        int result = BinaryPrimitives.ReadInt32BigEndian(digest) & int.MaxValue;
        return result == 0 ? 1 : result;
    }

    private static async Task ParseJsonToSpoolAsync(
        Stream source,
        Action<string, JsonDocument> consume,
        CancellationToken cancellationToken)
    {
        const int InitialBufferSize = 64 * 1024;
        const int MaximumElementBytes = 32 * 1024 * 1024;
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        int buffered = 0;
        bool finalBlock = false;
        bool rootStarted = false;
        bool rootCompleted = false;
        string? currentProperty = null;
        string? arrayProperty = null;
        var state = new JsonReaderState(new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        try
        {
            while (!finalBlock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffered == buffer.Length)
                {
                    if (buffer.Length >= MaximumElementBytes)
                        throw new InvalidDataException($"Graph JSON 单个元素不能超过 {MaximumElementBytes} 字节。");
                    int nextLength = Math.Min(checked(buffer.Length * 2), MaximumElementBytes);
                    byte[] replacement = System.Buffers.ArrayPool<byte>.Shared.Rent(nextLength);
                    buffer.AsSpan(0, buffered).CopyTo(replacement);
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                    buffer = replacement;
                }

                int read = await source.ReadAsync(buffer.AsMemory(buffered, buffer.Length - buffered), cancellationToken).ConfigureAwait(false);
                finalBlock = read == 0;
                buffered += read;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), finalBlock, state);
                while (true)
                {
                    Utf8JsonReader checkpoint = reader;
                    if (!reader.Read())
                        break;

                    if (arrayProperty is not null)
                    {
                        if (reader.TokenType == JsonTokenType.EndArray)
                        {
                            arrayProperty = null;
                            continue;
                        }
                        if (reader.TokenType != JsonTokenType.StartObject)
                            throw new InvalidDataException($"Graph JSON '{arrayProperty}' 数组只能包含对象。");

                        if (!JsonDocument.TryParseValue(ref reader, out JsonDocument? document))
                        {
                            reader = checkpoint;
                            break;
                        }
                        using JsonDocument parsedDocument = document
                            ?? throw new InvalidDataException("Graph JSON 数组元素为空。");
                        consume(arrayProperty, parsedDocument);
                        continue;
                    }

                    if (!rootStarted)
                    {
                        if (reader.TokenType != JsonTokenType.StartObject)
                            throw new InvalidDataException("Graph JSON 根值必须是对象。");
                        rootStarted = true;
                        continue;
                    }

                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        currentProperty = reader.GetString();
                        continue;
                    }

                    if (currentProperty is not null)
                    {
                        if (reader.TokenType == JsonTokenType.StartArray
                            && currentProperty is "vertices" or "nodes" or "edges" or "relationships")
                        {
                            arrayProperty = currentProperty;
                            currentProperty = null;
                            continue;
                        }

                        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                        {
                            if (!JsonDocument.TryParseValue(ref reader, out JsonDocument? ignored))
                            {
                                reader = checkpoint;
                                break;
                            }
                            ignored?.Dispose();
                        }
                        currentProperty = null;
                        continue;
                    }

                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        rootCompleted = true;
                        continue;
                    }
                    if (rootCompleted && reader.TokenType != JsonTokenType.None)
                        throw new InvalidDataException("Graph JSON 根对象之后包含额外值。");
                }

                int consumedBytes = checked((int)reader.BytesConsumed);
                state = reader.CurrentState;
                if (consumedBytes > 0)
                {
                    buffer.AsSpan(consumedBytes, buffered - consumedBytes).CopyTo(buffer);
                    buffered -= consumedBytes;
                }
                if (finalBlock)
                {
                    if (!rootStarted || !rootCompleted || arrayProperty is not null || currentProperty is not null)
                        throw new InvalidDataException("Graph JSON 文档被截断或结构无效。");
                    if (ContainsNonWhitespace(buffer.AsSpan(0, buffered)))
                        throw new InvalidDataException("Graph JSON 文档尾部包含未解析数据。");
                }
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Graph JSON 文档语法无效。", exception);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool ContainsNonWhitespace(ReadOnlySpan<byte> value)
    {
        foreach (byte item in value)
        {
            if (item is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                return true;
        }
        return false;
    }

    private static async Task ReadCsvLinesAsync<T>(
        StreamReader reader,
        int expectedFields,
        Func<string[], T> parse,
        Func<T, ValueTask> consume,
        CancellationToken cancellationToken)
    {
        bool first = true;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (first && line.StartsWith("id,", StringComparison.OrdinalIgnoreCase))
            {
                first = false;
                continue;
            }
            first = false;
            await consume(parse(SplitCsv(line, expectedFields))).ConfigureAwait(false);
        }
    }

    private static string[] SplitCsv(string line, int expected)
    {
        var fields = new List<string>(expected);
        var field = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char current = line[index];
            if (current == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
                continue;
            }
            if (current == ',' && !quoted)
            {
                fields.Add(field.ToString().Trim());
                field.Clear();
                continue;
            }
            field.Append(current);
        }
        if (quoted)
            throw new InvalidDataException("Graph CSV 引号未闭合；当前 profile 不支持跨行 quoted field。");
        fields.Add(field.ToString().Trim());
        if (fields.Count != expected)
            throw new InvalidDataException($"Graph CSV 行需要 {expected} 个字段，实际为 {fields.Count}。 ");
        return fields.ToArray();
    }

    private static long ParsePositiveLong(string value, string name)
        => long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long result) && result > 0
            ? result
            : throw new InvalidDataException($"Graph CSV {name} 无效。");

    private static int ParsePositiveInt(string value, string name)
        => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int result) && result > 0
            ? result
            : throw new InvalidDataException($"Graph CSV {name} 无效。");
}
