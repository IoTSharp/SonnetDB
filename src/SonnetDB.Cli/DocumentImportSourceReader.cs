using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Cli;

/// <summary>
/// 读取 MongoDB 导出或 JSON 文档源，并规范化常用 Extended JSON 值。
/// </summary>
internal static class DocumentImportSourceReader
{
    private const int MaxSourceDocumentBytes = 12 * 1024 * 1024;
    private const int MaxSourceDocumentChars = 12 * 1024 * 1024;
    private const int SourceReadBufferBytes = 64 * 1024;

    /// <summary>解析输入文件和实际格式。</summary>
    internal static (string DataPath, string? MetadataPath, string Format) ResolveSource(
        string inputPath,
        string collection,
        string requestedFormat)
    {
        string fullPath = Path.GetFullPath(inputPath);
        string? metadataPath = null;
        if (Directory.Exists(fullPath))
        {
            string expected = Path.Combine(fullPath, collection + ".bson");
            if (!File.Exists(expected))
                throw new CliUsageException($"mongodump 目录中未找到 collection 文件 '{collection}.bson'。");
            metadataPath = Path.Combine(fullPath, collection + ".metadata.json");
            return (expected, File.Exists(metadataPath) ? metadataPath : null, "bson");
        }

        if (!File.Exists(fullPath))
            throw new CliUsageException($"导入文件不存在: {fullPath}");

        string format = requestedFormat.ToLowerInvariant();
        if (format is "mongodump" or "mongodb-dump")
            format = "bson";
        if (format == "auto")
        {
            string extension = Path.GetExtension(fullPath).ToLowerInvariant();
            format = extension switch
            {
                ".bson" => "bson",
                ".jsonl" or ".ndjson" => "ndjson",
                ".json" => DetectJsonFormat(fullPath),
                _ => "ndjson",
            };
        }

        if (format is not ("bson" or "ndjson" or "json" or "json-array"))
            throw new CliUsageException($"不支持的导入格式 '{requestedFormat}'。");
        if (format == "bson")
        {
            string candidate = Path.ChangeExtension(fullPath, ".metadata.json");
            metadataPath = File.Exists(candidate) ? candidate : null;
        }

        return (fullPath, metadataPath, format);
    }

    /// <summary>计算数据和 metadata 的稳定 SHA-256。</summary>
    internal static string ComputeSourceSha256(string dataPath, string? metadataPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFile(hash, dataPath);
        if (metadataPath is not null)
            AppendFile(hash, metadataPath);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>流式读取并规范化文档；单项错误作为结果返回，不中止后续 NDJSON/BSON 文档。</summary>
    internal static async IAsyncEnumerable<DocumentImportReadResult> ReadAsync(
        string dataPath,
        string format,
        string idPath,
        DocumentImportGapCollector gaps,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (format == "bson")
        {
            long ordinal = 0;
            await using var stream = File.OpenRead(dataPath);
            foreach (var bson in BsonDocumentReader.Read(stream, dataPath, gaps))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ordinal++;
                if (bson.Error is not null)
                {
                    yield return new DocumentImportReadResult(null, bson.Error with { SourceOrdinal = ordinal });
                    continue;
                }
                yield return CreateResult(dataPath, ordinal, bson.Json!, idPath, gaps);
            }
            yield break;
        }

        if (format == "ndjson")
        {
            await using var stream = File.OpenRead(dataPath);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            await foreach (var line in ReadBoundedLinesAsync(reader, cancellationToken).ConfigureAwait(false))
            {
                if (line.ErrorMessage is not null)
                {
                    yield return new DocumentImportReadResult(null, new DocumentImportItemError(
                        dataPath,
                        line.Number,
                        null,
                        line.TooLarge ? "document_too_large_for_bulk" : "source_read_failed",
                        line.ErrorMessage));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(line.Value))
                    continue;
                yield return CreateResult(dataPath, line.Number, line.Value, idPath, gaps);
            }
            yield break;
        }

        if (format == "json-array")
        {
            await foreach (var result in ReadBoundedJsonArrayAsync(
                dataPath,
                idPath,
                gaps,
                cancellationToken).ConfigureAwait(false))
            {
                yield return result;
            }
            yield break;
        }

        JsonDocument? parsedDocument = null;
        DocumentImportItemError? parseError = null;
        try
        {
            if (new FileInfo(dataPath).Length > MaxSourceDocumentBytes)
            {
                parseError = new DocumentImportItemError(
                    dataPath,
                    1,
                    null,
                    "document_too_large_for_bulk",
                    "单 JSON 文档超过 12 MiB migration batch 安全预算；大规模输入请改用 NDJSON。");
            }
            if (parseError is not null)
                throw new SourceDocumentRejectedException();

            await using var stream = File.OpenRead(dataPath);
            parsedDocument = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (SourceDocumentRejectedException)
        {
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            parseError = new DocumentImportItemError(
                dataPath,
                1,
                null,
                ex is JsonException ? "invalid_source_document" : "source_read_failed",
                ex.Message);
        }

        if (parseError is not null)
        {
            yield return new DocumentImportReadResult(null, parseError);
            yield break;
        }

        using (parsedDocument)
        {
            JsonDocument document = parsedDocument!;
            yield return CreateResult(dataPath, 1, document.RootElement.GetRawText(), idPath, gaps);
        }
    }

    /// <summary>
    /// 使用固定上限缓冲逐项解析 JSON array，避免整文件或超大单项导致无界扩容。
    /// </summary>
    private static async IAsyncEnumerable<DocumentImportReadResult> ReadBoundedJsonArrayAsync(
        string dataPath,
        string idPath,
        DocumentImportGapCollector gaps,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(dataPath);
        int bufferLimit = MaxSourceDocumentBytes + 1;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferLimit);
        int start = 0;
        int end = 0;
        bool endOfStream = false;
        bool bomChecked = false;
        bool canCloseWithoutValue = true;
        long ordinal = 0;
        JsonArrayReadState state = JsonArrayReadState.StartArray;

        try
        {
            while (true)
            {
                if (state == JsonArrayReadState.StartArray)
                {
                    if (!bomChecked)
                    {
                        if (end - start < 3 && !endOfStream)
                        {
                            Exception? fillError = await FillAsync().ConfigureAwait(false);
                            if (fillError is not null)
                            {
                                yield return SourceReadError(fillError);
                                yield break;
                            }
                            continue;
                        }
                        if (end - start >= 3
                            && buffer[start] == 0xEF
                            && buffer[start + 1] == 0xBB
                            && buffer[start + 2] == 0xBF)
                        {
                            start += 3;
                        }
                        bomChecked = true;
                    }

                    SkipJsonWhitespace(buffer, ref start, end);
                    if (start == end)
                    {
                        if (endOfStream)
                        {
                            yield return InvalidJsonArray("JSON array 输入为空或缺少 '['。");
                            yield break;
                        }
                        Exception? fillError = await FillAsync().ConfigureAwait(false);
                        if (fillError is not null)
                        {
                            yield return SourceReadError(fillError);
                            yield break;
                        }
                        continue;
                    }
                    if (buffer[start] != (byte)'[')
                    {
                        yield return InvalidJsonArray("JSON array 输入必须以 '[' 开始。");
                        yield break;
                    }

                    start++;
                    state = JsonArrayReadState.ValueOrEnd;
                    continue;
                }

                if (state == JsonArrayReadState.ValueOrEnd)
                {
                    SkipJsonWhitespace(buffer, ref start, end);
                    if (start == end)
                    {
                        if (endOfStream)
                        {
                            yield return InvalidJsonArray("JSON array 在读取文档前意外结束。");
                            yield break;
                        }
                        Exception? fillError = await FillAsync().ConfigureAwait(false);
                        if (fillError is not null)
                        {
                            yield return SourceReadError(fillError);
                            yield break;
                        }
                        continue;
                    }
                    if (buffer[start] == (byte)']')
                    {
                        if (!canCloseWithoutValue)
                        {
                            yield return InvalidJsonArray("JSON array 不允许尾随逗号。");
                            yield break;
                        }
                        start++;
                        state = JsonArrayReadState.TrailingWhitespace;
                        continue;
                    }

                    BufferedJsonValueStatus valueStatus = TryReadBufferedJsonValue(
                        buffer.AsSpan(start, end - start),
                        endOfStream,
                        out int bytesConsumed,
                        out string? json,
                        out string? parseError);
                    if (valueStatus == BufferedJsonValueStatus.Invalid)
                    {
                        yield return InvalidJsonArray(parseError!);
                        yield break;
                    }
                    if (valueStatus == BufferedJsonValueStatus.Incomplete)
                    {
                        if (end - start >= bufferLimit)
                        {
                            yield return TooLargeJsonArrayItem(ordinal + 1);
                            yield break;
                        }
                        if (endOfStream)
                        {
                            yield return InvalidJsonArray("JSON array 文档不完整。");
                            yield break;
                        }
                        Exception? fillError = await FillAsync().ConfigureAwait(false);
                        if (fillError is not null)
                        {
                            yield return SourceReadError(fillError);
                            yield break;
                        }
                        continue;
                    }

                    start += bytesConsumed;
                    ordinal++;
                    if (bytesConsumed > MaxSourceDocumentBytes)
                    {
                        yield return TooLargeJsonArrayItem(ordinal);
                    }
                    else
                    {
                        yield return CreateResult(dataPath, ordinal, json!, idPath, gaps);
                    }
                    state = JsonArrayReadState.DelimiterOrEnd;
                    continue;
                }

                if (state == JsonArrayReadState.DelimiterOrEnd)
                {
                    SkipJsonWhitespace(buffer, ref start, end);
                    if (start == end)
                    {
                        if (endOfStream)
                        {
                            yield return InvalidJsonArray("JSON array 缺少结束标记 ']'.");
                            yield break;
                        }
                        Exception? fillError = await FillAsync().ConfigureAwait(false);
                        if (fillError is not null)
                        {
                            yield return SourceReadError(fillError);
                            yield break;
                        }
                        continue;
                    }
                    if (buffer[start] == (byte)',')
                    {
                        start++;
                        canCloseWithoutValue = false;
                        state = JsonArrayReadState.ValueOrEnd;
                        continue;
                    }
                    if (buffer[start] == (byte)']')
                    {
                        start++;
                        state = JsonArrayReadState.TrailingWhitespace;
                        continue;
                    }

                    yield return InvalidJsonArray("JSON array 文档之间必须使用逗号分隔。");
                    yield break;
                }

                SkipJsonWhitespace(buffer, ref start, end);
                if (start < end)
                {
                    yield return InvalidJsonArray("JSON array 结束标记之后存在额外内容。");
                    yield break;
                }
                if (endOfStream)
                    yield break;
                Exception? trailingFillError = await FillAsync().ConfigureAwait(false);
                if (trailingFillError is not null)
                {
                    yield return SourceReadError(trailingFillError);
                    yield break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        async ValueTask<Exception?> FillAsync()
        {
            if (start > 0)
            {
                Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                end -= start;
                start = 0;
            }
            if (end >= bufferLimit)
                return null;

            try
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(end, Math.Min(SourceReadBufferBytes, bufferLimit - end)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    endOfStream = true;
                else
                    end += read;
                return null;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                return ex;
            }
        }

        DocumentImportReadResult InvalidJsonArray(string message)
            => new(null, new DocumentImportItemError(
                dataPath,
                ordinal + 1,
                null,
                "invalid_source_document",
                message));

        DocumentImportReadResult SourceReadError(Exception exception)
            => new(null, new DocumentImportItemError(
                dataPath,
                ordinal + 1,
                null,
                "source_read_failed",
                exception.Message));

        DocumentImportReadResult TooLargeJsonArrayItem(long sourceOrdinal)
            => new(null, new DocumentImportItemError(
                dataPath,
                sourceOrdinal,
                null,
                "document_too_large_for_bulk",
                "单个 JSON array 文档超过 12 MiB migration batch 安全预算。"));
    }

    private static BufferedJsonValueStatus TryReadBufferedJsonValue(
        ReadOnlySpan<byte> source,
        bool isFinalBlock,
        out int bytesConsumed,
        out string? json,
        out string? error)
    {
        bytesConsumed = 0;
        json = null;
        error = null;
        try
        {
            var reader = new Utf8JsonReader(source, isFinalBlock, default);
            if (!JsonDocument.TryParseValue(ref reader, out JsonDocument? parsed))
                return BufferedJsonValueStatus.Incomplete;

            using (parsed)
            {
                bytesConsumed = checked((int)reader.BytesConsumed);
                json = parsed!.RootElement.GetRawText();
            }
            return BufferedJsonValueStatus.Complete;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return BufferedJsonValueStatus.Invalid;
        }
    }

    private static void SkipJsonWhitespace(byte[] buffer, ref int start, int end)
    {
        while (start < end && buffer[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            start++;
    }

    private enum JsonArrayReadState
    {
        StartArray,
        ValueOrEnd,
        DelimiterOrEnd,
        TrailingWhitespace,
    }

    private enum BufferedJsonValueStatus
    {
        Incomplete,
        Complete,
        Invalid,
    }

    private static async IAsyncEnumerable<BoundedSourceLine> ReadBoundedLinesAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new char[16 * 1024];
        var builder = new StringBuilder();
        long lineNumber = 1;
        bool tooLarge = false;
        bool hasCurrentLineContent = false;

        while (true)
        {
            int read = 0;
            Exception? readError = null;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or DecoderFallbackException)
            {
                readError = ex;
            }

            if (readError is not null)
            {
                yield return new BoundedSourceLine(lineNumber, null, false, readError.Message);
                yield break;
            }
            if (read == 0)
            {
                if (hasCurrentLineContent || tooLarge)
                    yield return CompleteLine();
                yield break;
            }

            int segmentStart = 0;
            for (int index = 0; index < read; index++)
            {
                if (buffer[index] != '\n')
                    continue;

                AppendSegment(segmentStart, index - segmentStart);
                yield return CompleteLine();
                lineNumber++;
                builder.Clear();
                tooLarge = false;
                hasCurrentLineContent = false;
                segmentStart = index + 1;
            }
            AppendSegment(segmentStart, read - segmentStart);
        }

        void AppendSegment(int start, int length)
        {
            if (length <= 0)
                return;
            hasCurrentLineContent = true;
            if (tooLarge)
                return;
            if (builder.Length + length > MaxSourceDocumentChars)
            {
                builder.Clear();
                tooLarge = true;
                return;
            }
            builder.Append(buffer, start, length);
        }

        BoundedSourceLine CompleteLine()
        {
            if (tooLarge)
            {
                return new BoundedSourceLine(
                    lineNumber,
                    null,
                    true,
                    "单行 NDJSON 文档超过 12 MiB migration batch 安全预算。");
            }

            int length = builder.Length;
            if (length > 0 && builder[length - 1] == '\r')
                length--;
            return new BoundedSourceLine(lineNumber, builder.ToString(0, length), false, null);
        }
    }

    private static DocumentImportReadResult CreateResult(
        string file,
        long ordinal,
        string json,
        string idPath,
        DocumentImportGapCollector gaps)
    {
        try
        {
            using var source = JsonDocument.Parse(json);
            JsonElement body = source.RootElement;
            string? id;
            if (body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("id", out var pairId)
                && pairId.ValueKind == JsonValueKind.String
                && body.TryGetProperty("document", out var pairDocument))
            {
                id = pairId.GetString();
                body = pairDocument;
            }
            else
            {
                id = TryResolveId(body, idPath, out var idElement)
                    ? ConvertId(idElement)
                    : null;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                return new DocumentImportReadResult(null, new DocumentImportItemError(
                    file,
                    ordinal,
                    null,
                    "missing_document_id",
                    $"未能从 '{idPath}' 读取稳定文档 ID。"));
            }

            string normalized = NormalizeJson(body, gaps);
            using var normalizedDocument = JsonDocument.Parse(normalized);
            IReadOnlyList<string> scalarPaths = CollectScalarPaths(normalizedDocument.RootElement);
            return new DocumentImportReadResult(
                new DocumentImportSourceItem(file, ordinal, id, normalized, scalarPaths),
                null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or OverflowException
            or FormatException or ArgumentOutOfRangeException)
        {
            return new DocumentImportReadResult(null, new DocumentImportItemError(
                file,
                ordinal,
                null,
                "invalid_source_document",
                ex.Message));
        }
    }

    private sealed record BoundedSourceLine(
        long Number,
        string? Value,
        bool TooLarge,
        string? ErrorMessage);

    private sealed class SourceDocumentRejectedException : Exception;

    private static string DetectJsonFormat(string path)
    {
        using var stream = File.OpenRead(path);
        int value;
        do value = stream.ReadByte(); while (value >= 0 && char.IsWhiteSpace((char)value));
        return value == '[' ? "json-array" : "json";
    }

    private static void AppendFile(IncrementalHash hash, string path)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
        using var stream = File.OpenRead(path);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            hash.AppendData(buffer.AsSpan(0, read));
    }

    private static bool TryResolveId(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        string normalized = path.Trim();
        if (normalized == "_id")
            normalized = "$._id";
        if (!normalized.StartsWith("$.", StringComparison.Ordinal))
            return false;

        foreach (string segment in normalized[2..].Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }
        return true;
    }

    private static string? ConvertId(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Object when TryGetSingleString(element, "$oid", out string? oid)
                && oid is not null && oid.Length == 24 => oid.ToLowerInvariant(),
            JsonValueKind.Object when TryGetSingleString(element, "$uuid", out string? uuid)
                && uuid is not null => uuid.ToLowerInvariant(),
            JsonValueKind.Object when TryGetExtendedNumber(element, out string? kind, out string? number)
                => $"{kind}:{number}",
            _ => null,
        };
    }

    private static string NormalizeJson(JsonElement value, DocumentImportGapCollector gaps)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteNormalized(value, writer, gaps);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteNormalized(JsonElement value, Utf8JsonWriter writer, DocumentImportGapCollector gaps)
    {
        if (value.ValueKind == JsonValueKind.Object && TryWriteExtendedJson(value, writer, gaps))
            return;

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteNormalized(property.Value, writer, gaps);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteNormalized(item, writer, gaps);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static bool TryWriteExtendedJson(
        JsonElement value,
        Utf8JsonWriter writer,
        DocumentImportGapCollector gaps)
    {
        if (TryGetSingleString(value, "$oid", out string? oid))
        {
            if (oid is null || oid.Length != 24 || !oid.All(Uri.IsHexDigit))
                throw new FormatException("Extended JSON $oid 必须是 24 位十六进制字符串。");
            writer.WriteStringValue(oid.ToLowerInvariant());
            return true;
        }
        if (TryGetSingleString(value, "$uuid", out string? uuid))
        {
            if (!Guid.TryParse(uuid, out Guid parsedUuid))
                throw new FormatException("Extended JSON $uuid 必须是有效 UUID。");
            writer.WriteStringValue(parsedUuid.ToString("D"));
            return true;
        }
        if (TryGetSingleString(value, "$numberInt", out string? intText))
        {
            if (!int.TryParse(intText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
                throw new FormatException("Extended JSON $numberInt 超出 Int32 范围或格式非法。");
            writer.WriteNumberValue(intValue);
            return true;
        }
        if (TryGetSingleString(value, "$numberLong", out string? longText))
        {
            if (!long.TryParse(longText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
                throw new FormatException("Extended JSON $numberLong 超出 Int64 范围或格式非法。");
            writer.WriteNumberValue(longValue);
            return true;
        }
        if (TryGetSingleString(value, "$numberDouble", out string? doubleText))
        {
            if (double.TryParse(doubleText, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue)
                && double.IsFinite(doubleValue))
            {
                writer.WriteNumberValue(doubleValue);
                return true;
            }
            if (doubleText is "NaN" or "Infinity" or "-Infinity")
            {
                gaps.Add("extended_json_non_finite_double", "partial", "非有限 Extended JSON double 保留原 wrapper，未转换为 JSON number。");
                return false;
            }
            throw new FormatException("Extended JSON $numberDouble 格式非法。");
        }
        if (TryGetSingleString(value, "$numberDecimal", out string? decimalText))
        {
            if (decimal.TryParse(decimalText, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal decimalValue))
                writer.WriteNumberValue(decimalValue);
            else
            {
                writer.WriteStringValue(decimalText);
                gaps.Add("extended_json_decimal_string", "partial", "超出 System.Decimal 范围的 Decimal128 以字符串保留。");
            }
            return true;
        }
        if (TryGetSingleProperty(value, "$date", out var date))
        {
            if (date.ValueKind == JsonValueKind.String)
            {
                string? dateText = date.GetString();
                if (!DateTimeOffset.TryParse(
                    dateText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _))
                {
                    throw new FormatException("Extended JSON $date 必须是 ISO-8601 字符串或 $numberLong 毫秒值。");
                }
                writer.WriteStringValue(dateText);
                return true;
            }
            if (date.ValueKind == JsonValueKind.Object
                && TryGetSingleString(date, "$numberLong", out string? milliseconds)
                && long.TryParse(milliseconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixMilliseconds))
            {
                writer.WriteStringValue(DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds));
                return true;
            }
            throw new FormatException("Extended JSON $date 必须是 ISO-8601 字符串或 $numberLong 毫秒值。");
        }
        if (value.TryGetProperty("$binary", out _) || value.TryGetProperty("$regularExpression", out _)
            || value.TryGetProperty("$timestamp", out _))
        {
            gaps.Add("extended_json_wrapper_preserved", "partial", "Binary、regex 与 timestamp wrapper 以 JSON 对象保留，未转换为 SonnetDB 原生标量。");
        }
        if (value.TryGetProperty("$code", out _) || value.TryGetProperty("$scope", out _))
        {
            gaps.Add("extended_json_code_wrapper_preserved", "partial", "JavaScript code/scope wrapper 以 JSON 对象保留，不执行代码或提供 BSON 类型排序语义。");
        }
        return false;
    }

    private static bool TryGetSingleString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!TryGetSingleProperty(element, name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString();
        return true;
    }

    private static bool TryGetSingleProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var enumerator = element.EnumerateObject();
        if (!enumerator.MoveNext())
            return false;
        JsonProperty property = enumerator.Current;
        if (!string.Equals(property.Name, name, StringComparison.Ordinal) || enumerator.MoveNext())
            return false;
        value = property.Value;
        return true;
    }

    private static bool TryGetExtendedNumber(JsonElement element, out string? kind, out string? number)
    {
        foreach (string name in new[] { "$numberInt", "$numberLong", "$numberDecimal", "$numberDouble" })
        {
            if (TryGetSingleString(element, name, out number))
            {
                kind = name[1..];
                return true;
            }
        }
        kind = null;
        number = null;
        return false;
    }

    private static IReadOnlyList<string> CollectScalarPaths(JsonElement root)
    {
        var paths = new List<string>();
        CollectScalarPaths(root, "$", depth: 0, paths);
        return paths;
    }

    private static void CollectScalarPaths(JsonElement value, string path, int depth, ICollection<string> output)
    {
        if (depth >= 4 || output.Count >= 64)
            return;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
                CollectScalarPaths(property.Value, path + "." + property.Name, depth + 1, output);
            return;
        }
        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            output.Add(path);
    }
}

internal sealed class DocumentImportGapCollector
{
    private readonly Dictionary<string, (string Status, string Message, long Count)> _items = new(StringComparer.Ordinal);

    internal void Add(string code, string status, string message)
    {
        if (_items.TryGetValue(code, out var item))
            _items[code] = (item.Status, item.Message, item.Count + 1);
        else
            _items.Add(code, (status, message, 1));
    }

    internal IReadOnlyList<DocumentImportGap> ToList()
        => _items.OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => new DocumentImportGap(item.Key, item.Value.Status, item.Value.Count, item.Value.Message))
            .ToArray();
}
