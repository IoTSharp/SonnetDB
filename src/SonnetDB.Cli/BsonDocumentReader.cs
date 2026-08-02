using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Cli;

/// <summary>
/// 读取 mongodump 连接 BSON 文档流；仅实现迁移报告声明的常用 BSON 类型子集。
/// </summary>
internal static class BsonDocumentReader
{
    private const int MaxDocumentBytes = 64 * 1024 * 1024;

    internal sealed record Result(string? Json, DocumentImportItemError? Error);

    /// <summary>逐条读取 BSON 文档，单条类型错误不会阻断后续文档。</summary>
    internal static IEnumerable<Result> Read(
        Stream stream,
        string sourceFile,
        DocumentImportGapCollector gaps)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long ordinal = 0;
        var lengthBuffer = new byte[4];
        while (true)
        {
            int first = stream.ReadByte();
            if (first < 0)
                yield break;
            lengthBuffer[0] = (byte)first;
            Result? headerError = null;
            try
            {
                ReadExactly(stream, lengthBuffer.AsSpan(1));
            }
            catch (EndOfStreamException ex)
            {
                ordinal++;
                headerError = Error(sourceFile, ordinal, "invalid_bson", ex.Message, gaps);
            }
            if (headerError is not null)
            {
                yield return headerError;
                yield break;
            }
            int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            ordinal++;
            if (length is < 5 or > MaxDocumentBytes)
            {
                yield return Error(sourceFile, ordinal, "invalid_bson_length", $"BSON 文档长度 {length} 非法或超过 64 MiB。", gaps);
                yield break;
            }

            var bytes = new byte[length];
            lengthBuffer.CopyTo(bytes, 0);
            Result result;
            try
            {
                ReadExactly(stream, bytes.AsSpan(4));
                result = new Result(Parse(bytes, gaps), null);
            }
            catch (UnsupportedBsonException ex)
            {
                result = Error(sourceFile, ordinal, "unsupported_bson_type", ex.Message, gaps);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException or OverflowException)
            {
                result = Error(sourceFile, ordinal, "invalid_bson", ex.Message, gaps);
            }
            yield return result;
        }
    }

    private static Result Error(
        string file,
        long ordinal,
        string code,
        string message,
        DocumentImportGapCollector gaps)
    {
        gaps.Add(code, code == "unsupported_bson_type" ? "not_planned" : "partial", message);
        return new Result(null, new DocumentImportItemError(file, ordinal, null, code, message));
    }

    private static string Parse(ReadOnlySpan<byte> bytes, DocumentImportGapCollector gaps)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var reader = new BsonSpanReader(bytes);
        using (var writer = new Utf8JsonWriter(buffer))
            WriteDocument(ref reader, writer, asArray: false, gaps);
        if (!reader.End)
            throw new InvalidDataException("BSON 文档包含长度边界之外的数据。");
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteDocument(
        ref BsonSpanReader reader,
        Utf8JsonWriter writer,
        bool asArray,
        DocumentImportGapCollector gaps)
    {
        int start = reader.Position;
        int length = reader.ReadInt32();
        if (length < 5 || start + length > reader.Length)
            throw new InvalidDataException("BSON 嵌套文档长度非法。");
        int terminator = start + length - 1;

        if (asArray) writer.WriteStartArray(); else writer.WriteStartObject();
        while (reader.Position < terminator)
        {
            byte type = reader.ReadByte();
            if (type == 0)
                throw new InvalidDataException("BSON 文档提前结束。");
            string name = reader.ReadCString();
            if (!asArray)
                writer.WritePropertyName(name);
            WriteValue(ref reader, writer, type, gaps);
        }
        if (reader.Position != terminator || reader.ReadByte() != 0)
            throw new InvalidDataException("BSON 文档缺少结束标记。");
        if (asArray) writer.WriteEndArray(); else writer.WriteEndObject();
    }

    private static void WriteValue(
        ref BsonSpanReader reader,
        Utf8JsonWriter writer,
        byte type,
        DocumentImportGapCollector gaps)
    {
        switch (type)
        {
            case 0x01:
                writer.WriteNumberValue(reader.ReadDouble());
                return;
            case 0x02:
                writer.WriteStringValue(reader.ReadString());
                return;
            case 0x0D:
                throw new UnsupportedBsonException("BSON JavaScript code 暂不转换；请先导出 canonical Extended JSON。");
            case 0x0E:
                writer.WriteStringValue(reader.ReadString());
                gaps.Add("bson_symbol_to_string", "partial", "已废弃的 BSON symbol 被转换为 JSON string。");
                return;
            case 0x03:
                WriteDocument(ref reader, writer, asArray: false, gaps);
                return;
            case 0x04:
                WriteDocument(ref reader, writer, asArray: true, gaps);
                return;
            case 0x05:
                WriteBinary(ref reader, writer, gaps);
                return;
            case 0x06:
                writer.WriteNullValue();
                gaps.Add("bson_undefined_to_null", "partial", "已废弃的 BSON undefined 被转换为 JSON null。");
                return;
            case 0x07:
                writer.WriteStringValue(Convert.ToHexString(reader.ReadBytes(12)).ToLowerInvariant());
                return;
            case 0x08:
                writer.WriteBooleanValue(reader.ReadByte() switch
                {
                    0 => false,
                    1 => true,
                    _ => throw new InvalidDataException("BSON boolean 值非法。"),
                });
                return;
            case 0x09:
                writer.WriteStringValue(DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64()));
                return;
            case 0x0A:
                writer.WriteNullValue();
                return;
            case 0x0B:
                writer.WriteStartObject();
                writer.WritePropertyName("$regularExpression");
                writer.WriteStartObject();
                writer.WriteString("pattern", reader.ReadCString());
                writer.WriteString("options", reader.ReadCString());
                writer.WriteEndObject();
                writer.WriteEndObject();
                gaps.Add("bson_regex_wrapper", "partial", "BSON regex 以 Extended JSON wrapper 保留，不具备原生 BSON 类型排序语义。");
                return;
            case 0x10:
                writer.WriteNumberValue(reader.ReadInt32());
                return;
            case 0x11:
                ulong timestamp = reader.ReadUInt64();
                writer.WriteStartObject();
                writer.WritePropertyName("$timestamp");
                writer.WriteStartObject();
                writer.WriteNumber("t", timestamp >> 32);
                writer.WriteNumber("i", timestamp & uint.MaxValue);
                writer.WriteEndObject();
                writer.WriteEndObject();
                gaps.Add("bson_timestamp_wrapper", "partial", "BSON timestamp 以 Extended JSON wrapper 保留。");
                return;
            case 0x12:
                writer.WriteNumberValue(reader.ReadInt64());
                return;
            case 0x13:
                throw new UnsupportedBsonException("BSON Decimal128 暂不解析；请先导出 canonical Extended JSON。");
            default:
                throw new UnsupportedBsonException($"不支持 BSON type 0x{type:X2}；请先导出 canonical Extended JSON。");
        }
    }

    private sealed class UnsupportedBsonException(string message) : Exception(message);

    private static void WriteBinary(
        ref BsonSpanReader reader,
        Utf8JsonWriter writer,
        DocumentImportGapCollector gaps)
    {
        int length = reader.ReadInt32();
        if (length < 0)
            throw new InvalidDataException("BSON binary 长度非法。");
        byte subtype = reader.ReadByte();
        int payloadLength = length;
        if (subtype == 0x02)
        {
            if (length < sizeof(int))
                throw new InvalidDataException("旧式 BSON binary subtype 2 长度非法。");
            payloadLength = reader.ReadInt32();
            if (payloadLength != length - sizeof(int))
                throw new InvalidDataException("旧式 BSON binary subtype 2 内外长度不一致。");
            gaps.Add("bson_old_binary_subtype", "partial", "旧式 BSON binary subtype 2 已去除内嵌长度并保留为 Extended JSON wrapper。");
        }
        byte[] value = reader.ReadBytes(payloadLength).ToArray();
        writer.WriteStartObject();
        writer.WritePropertyName("$binary");
        writer.WriteStartObject();
        writer.WriteBase64String("base64", value);
        writer.WriteString("subType", subtype.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteEndObject();
        writer.WriteEndObject();
        gaps.Add("bson_binary_wrapper", "partial", "BSON binary 以 Extended JSON wrapper 保留。");
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            int read = stream.Read(destination);
            if (read == 0)
                throw new EndOfStreamException("BSON 文件意外结束。");
            destination = destination[read..];
        }
    }

    private ref struct BsonSpanReader(ReadOnlySpan<byte> source)
    {
        private readonly ReadOnlySpan<byte> _source = source;
        private int _position;

        internal int Position => _position;
        internal int Length => _source.Length;
        internal bool End => _position == _source.Length;

        internal byte ReadByte()
        {
            Ensure(1);
            return _source[_position++];
        }

        internal int ReadInt32()
        {
            Ensure(4);
            int value = BinaryPrimitives.ReadInt32LittleEndian(_source[_position..]);
            _position += 4;
            return value;
        }

        internal long ReadInt64()
        {
            Ensure(8);
            long value = BinaryPrimitives.ReadInt64LittleEndian(_source[_position..]);
            _position += 8;
            return value;
        }

        internal ulong ReadUInt64()
        {
            Ensure(8);
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_source[_position..]);
            _position += 8;
            return value;
        }

        internal double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

        internal string ReadString()
        {
            int length = ReadInt32();
            if (length <= 0)
                throw new InvalidDataException("BSON string 长度非法。");
            ReadOnlySpan<byte> bytes = ReadBytes(length);
            if (bytes[^1] != 0)
                throw new InvalidDataException("BSON string 缺少 null 结束符。");
            return Encoding.UTF8.GetString(bytes[..^1]);
        }

        internal string ReadCString()
        {
            int relative = _source[_position..].IndexOf((byte)0);
            if (relative < 0)
                throw new InvalidDataException("BSON cstring 缺少 null 结束符。");
            string value = Encoding.UTF8.GetString(_source.Slice(_position, relative));
            _position += relative + 1;
            return value;
        }

        internal ReadOnlySpan<byte> ReadBytes(int length)
        {
            Ensure(length);
            ReadOnlySpan<byte> value = _source.Slice(_position, length);
            _position += length;
            return value;
        }

        private void Ensure(int length)
        {
            if (length < 0 || _position > _source.Length - length)
                throw new EndOfStreamException("BSON 文档意外结束。");
        }
    }
}
