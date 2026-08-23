using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SonnetDB.Graphs;

namespace SonnetDB.Data.Graphs;

/// <summary>Graph JSON/CSV 分批导入选项。</summary>
public sealed record SndbGraphImportOptions
{
    /// <summary>根导入 request ID；相同值可安全重试。</summary>
    public Guid RequestId { get; init; } = Guid.NewGuid();

    /// <summary>每个原子批次最多包含的元素数。</summary>
    public int BatchSize { get; init; } = 1_000;

    /// <summary>每个原子批次规范化 JSON 编码允许的最大字节数。</summary>
    public int MaxBatchBytes { get; init; } = GraphImportLimits.MaxBatchBytes;

    /// <summary>CSV 输入允许的最大单行 UTF-8 字节数。</summary>
    public int MaxCsvLineBytes { get; init; } = GraphImportLimits.DefaultMaxCsvLineBytes;

    internal void Validate()
    {
        if (RequestId == Guid.Empty)
            throw new ArgumentException("导入 request ID 不能为空。", nameof(RequestId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BatchSize);
        if (BatchSize > GraphImportLimits.MaxBatchElements)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BatchSize),
                $"BatchSize 不能超过 {GraphImportLimits.MaxBatchElements}。");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBatchBytes);
        if (MaxBatchBytes > GraphImportLimits.MaxBatchBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxBatchBytes),
                $"MaxBatchBytes 不能超过 {GraphImportLimits.MaxBatchBytes}。");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxCsvLineBytes);
        if (MaxCsvLineBytes > MaxBatchBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxCsvLineBytes), "MaxCsvLineBytes 不能超过 MaxBatchBytes。");
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
                int emptyRequestBytes = MeasureEmptyRequestBytes(importOptions.RequestId);
                long relationshipOrdinal = 0;
                await ParseJsonToSpoolAsync(
                    source,
                    (arrayName, document) =>
                    {
                        if (arrayName is "vertices" or "nodes")
                        {
                            GraphImportVertexDto vertex = DeserializeVertex(document);
                            WriteSpoolLine(
                                vertexWriter,
                                JsonSerializer.Serialize(vertex, Remote.RemoteJsonContext.Default.GraphImportVertexDto),
                                emptyRequestBytes,
                                importOptions.MaxBatchBytes);
                        }
                        else if (arrayName is "edges" or "relationships")
                        {
                            GraphImportEdgeDto edge = DeserializeEdge(document, relationshipOrdinal++);
                            WriteSpoolLine(
                                edgeWriter,
                                JsonSerializer.Serialize(edge, Remote.RemoteJsonContext.Default.GraphImportEdgeDto),
                                emptyRequestBytes,
                                importOptions.MaxBatchBytes);
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
                int emptyRequestBytes = MeasureEmptyRequestBytes(importOptions.RequestId);
                await using var vertexReader = new BoundedUtf8LineReader(
                    vertices,
                    importOptions.MaxCsvLineBytes,
                    "csv_line");
                await ReadCsvLinesAsync(
                    vertexReader,
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
                    item =>
                    {
                        WriteSpoolLine(
                            vertexWriter,
                            JsonSerializer.Serialize(item, Remote.RemoteJsonContext.Default.GraphImportVertexDto),
                            emptyRequestBytes,
                            importOptions.MaxBatchBytes);
                        return ValueTask.CompletedTask;
                    },
                    cancellationToken).ConfigureAwait(false);

                await using var edgeReader = new BoundedUtf8LineReader(
                    edges,
                    importOptions.MaxCsvLineBytes,
                    "csv_line");
                await ReadCsvLinesAsync(
                    edgeReader,
                    expectedFields: 4,
                    line => new GraphImportEdgeDto
                    {
                        Id = ParsePositiveLong(line[0], "edge id"),
                        SourceId = ParsePositiveLong(line[1], "source id"),
                        TargetId = ParsePositiveLong(line[2], "target id"),
                        LabelId = ParsePositiveInt(line[3], "edge label"),
                    },
                    item =>
                    {
                        WriteSpoolLine(
                            edgeWriter,
                            JsonSerializer.Serialize(item, Remote.RemoteJsonContext.Default.GraphImportEdgeDto),
                            emptyRequestBytes,
                            importOptions.MaxBatchBytes);
                        return ValueTask.CompletedTask;
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

    private static async Task<SndbGraphImportReport> ImportSpoolAsync(
        SndbGraphClient client,
        string graph,
        string vertexPath,
        string edgePath,
        SndbGraphImportOptions options,
        CancellationToken cancellationToken)
    {
        ImportBatchResult vertices = await ImportSpoolItemsAsync(
            client,
            graph,
            vertexPath,
            options,
            startingBatchIndex: 0,
            Remote.RemoteJsonContext.Default.GraphImportVertexDto,
            static (requestId, items) => new GraphImportRequest
            {
                RequestId = requestId,
                Vertices = items,
            },
            cancellationToken).ConfigureAwait(false);
        ImportBatchResult edges = await ImportSpoolItemsAsync(
            client,
            graph,
            edgePath,
            options,
            vertices.NextBatchIndex,
            Remote.RemoteJsonContext.Default.GraphImportEdgeDto,
            static (requestId, items) => new GraphImportRequest
            {
                RequestId = requestId,
                Edges = items,
            },
            cancellationToken).ConfigureAwait(false);
        return new SndbGraphImportReport(
            vertices.ItemCount,
            edges.ItemCount,
            vertices.BatchCount + edges.BatchCount,
            edges.BatchCount > 0 ? edges.LastSequence : vertices.LastSequence);
    }

    private static async Task<ImportBatchResult> ImportSpoolItemsAsync<T>(
        SndbGraphClient client,
        string graph,
        string path,
        SndbGraphImportOptions options,
        int startingBatchIndex,
        JsonTypeInfo<T> typeInfo,
        Func<Guid, IReadOnlyList<T>, GraphImportRequest> createRequest,
        CancellationToken cancellationToken)
        where T : class
    {
        int batchIndex = startingBatchIndex;
        int batchCount = 0;
        int itemCount = 0;
        long lastSequence = 0;
        long batchItemBytes = 0;
        int emptyRequestBytes = MeasureEmptyRequestBytes(options.RequestId);
        var batch = new List<T>(options.BatchSize);
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var reader = new BoundedUtf8LineReader(source, options.MaxBatchBytes, "batch_element");
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            T item = JsonSerializer.Deserialize(line, typeInfo)
                ?? throw new InvalidDataException("Graph import spool 行为空。");
            int itemBytes = Encoding.UTF8.GetByteCount(line);
            long projectedBytes = MeasureBatchBytes(emptyRequestBytes, batchItemBytes, batch.Count, itemBytes);
            if (projectedBytes > options.MaxBatchBytes && batch.Count > 0)
            {
                GraphImportResponse committed = await client.ImportAsync(
                    graph,
                    createRequest(BatchRequestId(options.RequestId, batchIndex++), batch.ToArray()),
                    cancellationToken).ConfigureAwait(false);
                lastSequence = committed.Sequence;
                batchCount++;
                batch.Clear();
                batchItemBytes = 0;
                projectedBytes = MeasureBatchBytes(emptyRequestBytes, 0, 0, itemBytes);
            }
            if (projectedBytes > options.MaxBatchBytes)
            {
                throw new GraphImportLimitExceededException(
                    "batch",
                    projectedBytes,
                    options.MaxBatchBytes);
            }

            batch.Add(item);
            batchItemBytes += itemBytes;
            itemCount++;
            if (batch.Count < options.BatchSize)
                continue;

            GraphImportResponse result = await client.ImportAsync(
                graph,
                createRequest(BatchRequestId(options.RequestId, batchIndex++), batch.ToArray()),
                cancellationToken).ConfigureAwait(false);
            lastSequence = result.Sequence;
            batchCount++;
            batch.Clear();
            batchItemBytes = 0;
        }

        if (batch.Count > 0)
        {
            GraphImportResponse result = await client.ImportAsync(
                graph,
                createRequest(BatchRequestId(options.RequestId, batchIndex++), batch.ToArray()),
                cancellationToken).ConfigureAwait(false);
            lastSequence = result.Sequence;
            batchCount++;
        }

        return new ImportBatchResult(itemCount, batchCount, lastSequence, batchIndex);
    }

    private static long MeasureBatchBytes(
        int emptyRequestBytes,
        long batchItemBytes,
        int batchCount,
        int nextItemBytes)
        => checked(emptyRequestBytes + batchItemBytes + nextItemBytes + (batchCount == 0 ? 0 : batchCount));

    private static int MeasureEmptyRequestBytes(Guid requestId)
        => JsonSerializer.SerializeToUtf8Bytes(
            new GraphImportRequest { RequestId = requestId },
            Remote.RemoteJsonContext.Default.GraphImportRequest).Length;

    private static void WriteSpoolLine(
        StreamWriter writer,
        string line,
        int emptyRequestBytes,
        int maximumBatchBytes)
    {
        long batchBytes = checked(emptyRequestBytes + Encoding.UTF8.GetByteCount(line));
        if (batchBytes > maximumBatchBytes)
            throw new GraphImportLimitExceededException("batch", batchBytes, maximumBatchBytes);
        writer.WriteLine(line);
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
        BoundedUtf8LineReader reader,
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

    private readonly record struct ImportBatchResult(
        int ItemCount,
        int BatchCount,
        long LastSequence,
        int NextBatchIndex);

    private sealed class BoundedUtf8LineReader : IAsyncDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly Stream _source;
        private readonly int _maximumLineBytes;
        private readonly string _limitName;
        private byte[]? _readBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(16 * 1024);
        private byte[]? _lineBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(16 * 1024);
        private int _readOffset;
        private int _readCount;
        private int _lineLength;
        private bool _completed;
        private bool _firstLine = true;

        internal BoundedUtf8LineReader(Stream source, int maximumLineBytes, string limitName)
        {
            _source = source;
            _maximumLineBytes = maximumLineBytes;
            _limitName = limitName;
        }

        internal async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_readBuffer is null, this);
            while (true)
            {
                if (_readOffset < _readCount)
                {
                    ReadOnlySpan<byte> remaining = _readBuffer.AsSpan(_readOffset, _readCount - _readOffset);
                    int newline = remaining.IndexOf((byte)'\n');
                    int count = newline < 0 ? remaining.Length : newline;
                    Append(remaining[..count]);
                    _readOffset += count + (newline < 0 ? 0 : 1);
                    if (newline >= 0)
                        return DecodeLine();
                }

                if (_completed)
                    return _lineLength == 0 ? null : DecodeLine();

                _readOffset = 0;
                _readCount = await _source.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
                _completed = _readCount == 0;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_readBuffer is { } readBuffer)
                System.Buffers.ArrayPool<byte>.Shared.Return(readBuffer);
            if (_lineBuffer is { } lineBuffer)
                System.Buffers.ArrayPool<byte>.Shared.Return(lineBuffer);
            _readBuffer = null;
            _lineBuffer = null;
            return ValueTask.CompletedTask;
        }

        private void Append(ReadOnlySpan<byte> value)
        {
            int required = checked(_lineLength + value.Length);
            if (required > _maximumLineBytes)
            {
                throw new GraphImportLimitExceededException(
                    _limitName,
                    required,
                    _maximumLineBytes);
            }
            byte[] lineBuffer = _lineBuffer
                ?? throw new ObjectDisposedException(nameof(BoundedUtf8LineReader));
            if (required > lineBuffer.Length)
            {
                int nextLength = Math.Min(
                    _maximumLineBytes,
                    Math.Max(required, checked(lineBuffer.Length * 2)));
                byte[] replacement = System.Buffers.ArrayPool<byte>.Shared.Rent(nextLength);
                lineBuffer.AsSpan(0, _lineLength).CopyTo(replacement);
                System.Buffers.ArrayPool<byte>.Shared.Return(lineBuffer);
                _lineBuffer = lineBuffer = replacement;
            }
            value.CopyTo(lineBuffer.AsSpan(_lineLength));
            _lineLength = required;
        }

        private string DecodeLine()
        {
            byte[] lineBuffer = _lineBuffer
                ?? throw new ObjectDisposedException(nameof(BoundedUtf8LineReader));
            ReadOnlySpan<byte> value = lineBuffer.AsSpan(0, _lineLength);
            if (value.Length > 0 && value[^1] == (byte)'\r')
                value = value[..^1];
            if (_firstLine
                && value.Length >= 3
                && value[0] == 0xEF
                && value[1] == 0xBB
                && value[2] == 0xBF)
                value = value[3..];
            _firstLine = false;
            _lineLength = 0;
            try
            {
                return StrictUtf8.GetString(value);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("Graph import 文本必须是有效 UTF-8。", exception);
            }
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
