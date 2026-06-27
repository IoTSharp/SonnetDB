using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.IO;

namespace SonnetDB.Documents;

/// <summary>
/// JSON 文档集合 schema 文件（<c>documents/documents.docschema</c>）的二进制序列化器。
/// </summary>
public static class DocumentCollectionSchemaCodec
{
    /// <summary>schema 文件名。</summary>
    public const string FileName = "documents.docschema";

    private static readonly byte[] _magic = "SDBDOCv1"u8.ToArray();
    private static readonly Encoding _utf8 = Encoding.UTF8;

    private const int FormatVersion = 4;
    private const int MinFormatVersion = 1;
    private const int HeaderSize = 32;
    private const int FooterSize = 16;

    /// <summary>
    /// 从文件加载全部文档集合 schema；文件不存在时返回空集合。
    /// </summary>
    /// <param name="path">schema 文件路径。</param>
    /// <returns>schema 列表。</returns>
    public static IReadOnlyList<DocumentCollectionSchema> Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return [];

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(fs);
    }

    /// <summary>
    /// 保存全部文档集合 schema。
    /// </summary>
    /// <param name="path">schema 文件路径。</param>
    /// <param name="schemas">schema 列表。</param>
    /// <param name="tempSuffix">临时文件后缀。</param>
    public static void Save(string path, IReadOnlyList<DocumentCollectionSchema> schemas, string tempSuffix = ".tmp")
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(tempSuffix);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmpPath = path + tempSuffix;

        using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bs = new BufferedStream(fs, 65536))
        {
            Save(schemas, bs);
            bs.Flush();
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmpPath, path, overwrite: true);
    }

    private static IReadOnlyList<DocumentCollectionSchema> Load(Stream source)
    {
        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(HeaderSize);
        try
        {
            int read = ReadExact(source, headerBuffer, 0, HeaderSize);
            if (read < HeaderSize)
                throw new InvalidDataException("DocumentCollectionSchema: header is truncated.");

            var reader = new SpanReader(headerBuffer.AsSpan(0, HeaderSize));
            if (!reader.ReadBytes(8).SequenceEqual(_magic))
                throw new InvalidDataException("DocumentCollectionSchema: invalid magic in header.");

            int version = reader.ReadInt32();
            if (version is < MinFormatVersion or > FormatVersion)
                throw new InvalidDataException($"DocumentCollectionSchema: unsupported format version {version}.");

            int headerSize = reader.ReadInt32();
            if (headerSize != HeaderSize)
                throw new InvalidDataException($"DocumentCollectionSchema: unexpected header size {headerSize}.");

            int collectionCount = reader.ReadInt32();
            if (collectionCount < 0)
                throw new InvalidDataException("DocumentCollectionSchema: negative collection count.");

            var crc = new Crc32();
            var schemas = new List<DocumentCollectionSchema>(collectionCount);
            for (int i = 0; i < collectionCount; i++)
                schemas.Add(ReadCollection(source, crc, i, version));

            byte[] footerBuffer = ArrayPool<byte>.Shared.Rent(FooterSize);
            try
            {
                int footerRead = ReadExact(source, footerBuffer, 0, FooterSize);
                if (footerRead < FooterSize)
                    throw new InvalidDataException("DocumentCollectionSchema: footer is truncated.");

                uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footerBuffer.AsSpan(0, 4));
                if (!footerBuffer.AsSpan(4, 8).SequenceEqual(_magic))
                    throw new InvalidDataException("DocumentCollectionSchema: invalid magic in footer.");

                uint actualCrc = crc.GetCurrentHashAsUInt32();
                if (storedCrc != actualCrc)
                    throw new InvalidDataException(
                        $"DocumentCollectionSchema: CRC32 mismatch (expected 0x{storedCrc:X8}, got 0x{actualCrc:X8}).");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(footerBuffer);
            }

            return schemas.AsReadOnly();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    private static DocumentCollectionSchema ReadCollection(Stream source, Crc32 crc, int collectionIndex, int version)
    {
        string name = ReadString(source, crc, $"collection {collectionIndex} name");
        long createdAt = ReadInt64(source, crc, $"collection {collectionIndex} createdAt");
        int indexCount = ReadUInt16(source, crc, $"collection {collectionIndex} indexCount");

        var indexes = new List<DocumentPathIndexDefinition>(indexCount);
        for (int i = 0; i < indexCount; i++)
        {
            indexes.Add(version >= 3
                ? ReadDocumentIndex(source, crc, collectionIndex, i)
                : ReadLegacyDocumentIndex(source, crc, collectionIndex, i));
        }

        var fullTextIndexes = new List<DocumentFullTextIndexDefinition>();
        if (version >= 2)
        {
            int fullTextIndexCount = ReadUInt16(source, crc, $"collection {collectionIndex} fulltextIndexCount");
            for (int i = 0; i < fullTextIndexCount; i++)
            {
                string indexName = ReadString(source, crc, $"collection {collectionIndex} fulltext index {i} name");
                string tokenizer = ReadString(source, crc, $"collection {collectionIndex} fulltext index {i} tokenizer");
                int fieldCount = ReadUInt16(source, crc, $"collection {collectionIndex} fulltext index {i} fieldCount");
                var fields = new string[fieldCount];
                for (int fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
                    fields[fieldIndex] = ReadString(source, crc, $"collection {collectionIndex} fulltext index {i} field {fieldIndex}");

                long indexCreatedAt = ReadInt64(source, crc, $"collection {collectionIndex} fulltext index {i} createdAt");
                fullTextIndexes.Add(new DocumentFullTextIndexDefinition(indexName, Array.AsReadOnly(fields), tokenizer, indexCreatedAt));
            }
        }

        var validator = version >= 4
            ? ReadValidator(source, crc, collectionIndex)
            : null;

        return DocumentCollectionSchema.Create(name, indexes, fullTextIndexes, createdAt, validator);
    }

    private static DocumentPathIndexDefinition ReadLegacyDocumentIndex(Stream source, Crc32 crc, int collectionIndex, int index)
    {
        string indexName = ReadString(source, crc, $"collection {collectionIndex} index {index} name");
        string path = ReadString(source, crc, $"collection {collectionIndex} index {index} path");
        long indexCreatedAt = ReadInt64(source, crc, $"collection {collectionIndex} index {index} createdAt");
        return new DocumentPathIndexDefinition(indexName, path, indexCreatedAt);
    }

    private static DocumentPathIndexDefinition ReadDocumentIndex(Stream source, Crc32 crc, int collectionIndex, int index)
    {
        string indexName = ReadString(source, crc, $"collection {collectionIndex} index {index} name");
        int pathCount = ReadUInt16(source, crc, $"collection {collectionIndex} index {index} pathCount");
        var paths = new string[pathCount];
        for (int pathIndex = 0; pathIndex < pathCount; pathIndex++)
            paths[pathIndex] = ReadString(source, crc, $"collection {collectionIndex} index {index} path {pathIndex}");

        byte flags = ReadByte(source, crc, $"collection {collectionIndex} index {index} flags");
        var partialFilter = ReadPartialFilter(source, crc, collectionIndex, index);
        string? ttlPath = ReadNullableString(source, crc, $"collection {collectionIndex} index {index} ttlPath");
        long? ttlSeconds = ReadNullableInt64(source, crc, $"collection {collectionIndex} index {index} ttlSeconds");
        long indexCreatedAt = ReadInt64(source, crc, $"collection {collectionIndex} index {index} createdAt");

        return new DocumentPathIndexDefinition(
            indexName,
            Array.AsReadOnly(paths),
            indexCreatedAt,
            IsUnique: (flags & 0b0000_0001) != 0,
            IsSparse: (flags & 0b0000_0010) != 0,
            partialFilter,
            ttlPath,
            ttlSeconds);
    }

    private static DocumentIndexPartialFilter? ReadPartialFilter(Stream source, Crc32 crc, int collectionIndex, int index)
    {
        byte hasFilter = ReadByte(source, crc, $"collection {collectionIndex} index {index} partialFilter");
        if (hasFilter == 0)
            return null;
        if (hasFilter != 1)
            throw new InvalidDataException("DocumentCollectionSchema: invalid partial filter marker.");

        string path = ReadString(source, crc, $"collection {collectionIndex} index {index} partialFilter path");
        byte operatorByte = ReadByte(source, crc, $"collection {collectionIndex} index {index} partialFilter operator");
        if (!Enum.IsDefined(typeof(DocumentIndexPartialFilterOperator), (int)operatorByte))
            throw new InvalidDataException("DocumentCollectionSchema: invalid partial filter operator.");
        string? value = ReadNullableString(source, crc, $"collection {collectionIndex} index {index} partialFilter value");
        return new DocumentIndexPartialFilter(path, (DocumentIndexPartialFilterOperator)operatorByte, value);
    }

    private static void Save(IReadOnlyList<DocumentCollectionSchema> schemas, Stream destination)
    {
        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(HeaderSize);
        try
        {
            headerBuffer.AsSpan(0, HeaderSize).Clear();
            var writer = new SpanWriter(headerBuffer.AsSpan(0, HeaderSize));
            writer.WriteBytes(_magic);
            writer.WriteInt32(FormatVersion);
            writer.WriteInt32(HeaderSize);
            writer.WriteInt32(schemas.Count);
            destination.Write(headerBuffer, 0, HeaderSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }

        var crc = new Crc32();
        foreach (var schema in schemas)
            WriteCollection(destination, schema, crc);

        Span<byte> footer = stackalloc byte[FooterSize];
        BinaryPrimitives.WriteUInt32LittleEndian(footer[..4], crc.GetCurrentHashAsUInt32());
        _magic.CopyTo(footer.Slice(4, 8));
        destination.Write(footer);
    }

    private static void WriteCollection(Stream destination, DocumentCollectionSchema schema, Crc32 crc)
    {
        using var body = new MemoryStream();
        WriteString(body, schema.Name);
        WriteInt64(body, schema.CreatedAtUtcTicks);
        WriteUInt16(body, schema.Indexes.Count, $"Document collection '{schema.Name}' index count");

        foreach (var index in schema.Indexes)
        {
            WriteString(body, index.Name);
            WriteUInt16(body, index.Paths.Count, $"Document index '{index.Name}' path count");
            foreach (string path in index.Paths)
                WriteString(body, path);

            byte flags = 0;
            if (index.IsUnique)
                flags |= 0b0000_0001;
            if (index.IsSparse)
                flags |= 0b0000_0010;
            body.WriteByte(flags);
            WritePartialFilter(body, index.PartialFilter);
            WriteNullableString(body, index.TtlPath);
            WriteNullableInt64(body, index.TtlSeconds);
            WriteInt64(body, index.CreatedAtUtcTicks);
        }

        WriteUInt16(body, schema.FullTextIndexes.Count, $"Document collection '{schema.Name}' fulltext index count");
        foreach (var index in schema.FullTextIndexes)
        {
            WriteString(body, index.Name);
            WriteString(body, index.Tokenizer);
            WriteUInt16(body, index.Fields.Count, $"Document fulltext index '{index.Name}' field count");
            foreach (string field in index.Fields)
                WriteString(body, field);
            WriteInt64(body, index.CreatedAtUtcTicks);
        }

        WriteValidator(body, schema.Validator);

        byte[] bytes = body.ToArray();
        crc.Append(bytes);
        destination.Write(bytes, 0, bytes.Length);
    }

    private static DocumentValidatorDefinition? ReadValidator(Stream source, Crc32 crc, int collectionIndex)
    {
        byte hasValidator = ReadByte(source, crc, $"collection {collectionIndex} validator");
        if (hasValidator == 0)
            return null;
        if (hasValidator != 1)
            throw new InvalidDataException("DocumentCollectionSchema: invalid validator marker.");

        byte actionByte = ReadByte(source, crc, $"collection {collectionIndex} validator action");
        if (!Enum.IsDefined(typeof(DocumentValidationAction), (int)actionByte))
            throw new InvalidDataException("DocumentCollectionSchema: invalid validator action.");
        long createdAt = ReadInt64(source, crc, $"collection {collectionIndex} validator createdAt");
        long updatedAt = ReadInt64(source, crc, $"collection {collectionIndex} validator updatedAt");
        int ruleCount = ReadUInt16(source, crc, $"collection {collectionIndex} validator ruleCount");
        var rules = new List<DocumentValidatorRuleDefinition>(ruleCount);
        for (int ruleIndex = 0; ruleIndex < ruleCount; ruleIndex++)
        {
            string path = ReadString(source, crc, $"collection {collectionIndex} validator rule {ruleIndex} path");
            byte required = ReadByte(source, crc, $"collection {collectionIndex} validator rule {ruleIndex} required");
            if (required is not (0 or 1))
                throw new InvalidDataException("DocumentCollectionSchema: invalid validator required marker.");

            int typeCount = ReadUInt16(source, crc, $"collection {collectionIndex} validator rule {ruleIndex} typeCount");
            var types = new DocumentValidatorValueType[typeCount];
            for (int typeIndex = 0; typeIndex < typeCount; typeIndex++)
            {
                byte typeByte = ReadByte(source, crc, $"collection {collectionIndex} validator rule {ruleIndex} type {typeIndex}");
                if (!Enum.IsDefined(typeof(DocumentValidatorValueType), (int)typeByte))
                    throw new InvalidDataException("DocumentCollectionSchema: invalid validator value type.");
                types[typeIndex] = (DocumentValidatorValueType)typeByte;
            }

            double? minimum = ReadNullableDouble(source, crc, $"collection {collectionIndex} validator rule {ruleIndex} minimum");
            double? maximum = ReadNullableDouble(source, crc, $"collection {collectionIndex} validator rule {ruleIndex} maximum");
            int enumCount = ReadUInt16(source, crc, $"collection {collectionIndex} validator rule {ruleIndex} enumCount");
            var enumValues = new string[enumCount];
            for (int enumIndex = 0; enumIndex < enumCount; enumIndex++)
                enumValues[enumIndex] = ReadString(source, crc, $"collection {collectionIndex} validator rule {ruleIndex} enum {enumIndex}");
            string? pattern = ReadNullableString(source, crc, $"collection {collectionIndex} validator rule {ruleIndex} pattern");

            rules.Add(new DocumentValidatorRuleDefinition(
                path,
                required == 1,
                Array.AsReadOnly(types),
                minimum,
                maximum,
                Array.AsReadOnly(enumValues),
                pattern));
        }

        return new DocumentValidatorDefinition(
            rules.AsReadOnly(),
            (DocumentValidationAction)actionByte,
            createdAt,
            updatedAt);
    }

    private static void WriteValidator(Stream destination, DocumentValidator? validator)
    {
        if (validator is null)
        {
            destination.WriteByte(0);
            return;
        }

        destination.WriteByte(1);
        destination.WriteByte((byte)validator.Action);
        WriteInt64(destination, validator.CreatedAtUtcTicks);
        WriteInt64(destination, validator.UpdatedAtUtcTicks);
        WriteUInt16(destination, validator.Rules.Count, "Document validator rule count");
        foreach (var rule in validator.Rules)
        {
            WriteString(destination, rule.Path);
            destination.WriteByte(rule.Required ? (byte)1 : (byte)0);
            WriteUInt16(destination, rule.Types.Count, $"Document validator rule '{rule.Path}' type count");
            foreach (var type in rule.Types)
                destination.WriteByte((byte)type);
            WriteNullableDouble(destination, rule.Minimum);
            WriteNullableDouble(destination, rule.Maximum);
            WriteUInt16(destination, rule.EnumValues.Count, $"Document validator rule '{rule.Path}' enum count");
            foreach (string value in rule.EnumValues)
                WriteString(destination, value);
            WriteNullableString(destination, rule.Pattern);
        }
    }

    private static void WritePartialFilter(Stream destination, DocumentIndexPartialFilter? filter)
    {
        if (filter is null)
        {
            destination.WriteByte(0);
            return;
        }

        destination.WriteByte(1);
        WriteString(destination, filter.Path);
        destination.WriteByte((byte)filter.Operator);
        WriteNullableString(destination, filter.ValueScalar);
    }

    private static string ReadString(Stream source, Crc32 crc, string description)
    {
        int length = ReadUInt16(source, crc, description + " length");
        if (length == 0)
            return string.Empty;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int read = ReadExact(source, buffer, 0, length);
            if (read < length)
                throw new InvalidDataException($"DocumentCollectionSchema: {description} is truncated.");
            crc.Append(buffer.AsSpan(0, length));
            return _utf8.GetString(buffer, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string? ReadNullableString(Stream source, Crc32 crc, string description)
    {
        byte marker = ReadByte(source, crc, description + " marker");
        if (marker == 0)
            return null;
        if (marker != 1)
            throw new InvalidDataException($"DocumentCollectionSchema: invalid nullable string marker for {description}.");
        return ReadString(source, crc, description);
    }

    private static long ReadInt64(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadExactSpan(source, buffer, description);
        crc.Append(buffer);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    private static long? ReadNullableInt64(Stream source, Crc32 crc, string description)
    {
        byte marker = ReadByte(source, crc, description + " marker");
        if (marker == 0)
            return null;
        if (marker != 1)
            throw new InvalidDataException($"DocumentCollectionSchema: invalid nullable int64 marker for {description}.");
        return ReadInt64(source, crc, description);
    }

    private static double? ReadNullableDouble(Stream source, Crc32 crc, string description)
    {
        long? bits = ReadNullableInt64(source, crc, description);
        return bits is null ? null : BitConverter.Int64BitsToDouble(bits.Value);
    }

    private static int ReadUInt16(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadExactSpan(source, buffer, description);
        crc.Append(buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    private static byte ReadByte(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[1];
        ReadExactSpan(source, buffer, description);
        crc.Append(buffer);
        return buffer[0];
    }

    private static void WriteString(Stream destination, string value)
    {
        int length = _utf8.GetByteCount(value);
        if (length > ushort.MaxValue)
            throw new InvalidDataException("DocumentCollectionSchema: string value is too long.");

        Span<byte> lengthBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, (ushort)length);
        destination.Write(lengthBytes);
        if (length == 0)
            return;

        byte[] bytes = _utf8.GetBytes(value);
        destination.Write(bytes, 0, bytes.Length);
    }

    private static void WriteNullableString(Stream destination, string? value)
    {
        if (value is null)
        {
            destination.WriteByte(0);
            return;
        }

        destination.WriteByte(1);
        WriteString(destination, value);
    }

    private static void WriteInt64(Stream destination, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        destination.Write(buffer);
    }

    private static void WriteNullableInt64(Stream destination, long? value)
    {
        if (value is null)
        {
            destination.WriteByte(0);
            return;
        }

        destination.WriteByte(1);
        WriteInt64(destination, value.Value);
    }

    private static void WriteNullableDouble(Stream destination, double? value)
    {
        if (value is null)
        {
            destination.WriteByte(0);
            return;
        }

        destination.WriteByte(1);
        WriteInt64(destination, BitConverter.DoubleToInt64Bits(value.Value));
    }

    private static void WriteUInt16(Stream destination, int value, string description)
    {
        if (value is < 0 or > ushort.MaxValue)
            throw new InvalidDataException($"{description} is out of range.");

        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)value);
        destination.Write(buffer);
    }

    private static void ReadExactSpan(Stream source, Span<byte> buffer, string description)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = source.Read(buffer[total..]);
            if (read == 0)
                throw new InvalidDataException($"DocumentCollectionSchema: {description} is truncated.");
            total += read;
        }
    }

    private static int ReadExact(Stream source, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = source.Read(buffer, offset + total, count - total);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }
}
