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

    private const int FormatVersion = 1;
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
            if (version != FormatVersion)
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
                schemas.Add(ReadCollection(source, crc, i));

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

    private static DocumentCollectionSchema ReadCollection(Stream source, Crc32 crc, int collectionIndex)
    {
        string name = ReadString(source, crc, $"collection {collectionIndex} name");

        Span<byte> createdBuffer = stackalloc byte[8];
        ReadExactSpan(source, createdBuffer, $"collection {collectionIndex} createdAt");
        crc.Append(createdBuffer);
        long createdAt = BinaryPrimitives.ReadInt64LittleEndian(createdBuffer);

        Span<byte> countBuffer = stackalloc byte[2];
        ReadExactSpan(source, countBuffer, $"collection {collectionIndex} indexCount");
        crc.Append(countBuffer);
        int indexCount = BinaryPrimitives.ReadUInt16LittleEndian(countBuffer);

        var indexes = new List<DocumentPathIndexDefinition>(indexCount);
        for (int i = 0; i < indexCount; i++)
        {
            string indexName = ReadString(source, crc, $"collection {collectionIndex} index {i} name");
            string path = ReadString(source, crc, $"collection {collectionIndex} index {i} path");
            ReadExactSpan(source, createdBuffer, $"collection {collectionIndex} index {i} createdAt");
            crc.Append(createdBuffer);
            long indexCreatedAt = BinaryPrimitives.ReadInt64LittleEndian(createdBuffer);
            indexes.Add(new DocumentPathIndexDefinition(indexName, path, indexCreatedAt));
        }

        return DocumentCollectionSchema.Create(name, indexes, createdAt);
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
        int nameLength = _utf8.GetByteCount(schema.Name);
        if (nameLength > ushort.MaxValue)
            throw new InvalidDataException($"Document collection '{schema.Name}' 名称过长。");

        int totalSize = 2 + nameLength + 8 + 2;
        var indexNameLengths = new int[schema.Indexes.Count];
        var indexPathLengths = new int[schema.Indexes.Count];
        for (int i = 0; i < schema.Indexes.Count; i++)
        {
            var index = schema.Indexes[i];
            int indexNameLength = _utf8.GetByteCount(index.Name);
            int indexPathLength = _utf8.GetByteCount(index.Path);
            if (indexNameLength > ushort.MaxValue)
                throw new InvalidDataException($"Document collection '{schema.Name}' 的索引 '{index.Name}' 名称过长。");
            if (indexPathLength > ushort.MaxValue)
                throw new InvalidDataException($"Document collection '{schema.Name}' 的索引 '{index.Name}' path 过长。");

            indexNameLengths[i] = indexNameLength;
            indexPathLengths[i] = indexPathLength;
            totalSize += 2 + indexNameLength + 2 + indexPathLength + 8;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            buffer.AsSpan(0, totalSize).Clear();
            var writer = new SpanWriter(buffer.AsSpan(0, totalSize));
            writer.WriteUInt16((ushort)nameLength);
            int written = _utf8.GetBytes(schema.Name, writer.FreeSpan);
            writer.Advance(written);
            writer.WriteInt64(schema.CreatedAtUtcTicks);
            writer.WriteUInt16((ushort)schema.Indexes.Count);

            for (int i = 0; i < schema.Indexes.Count; i++)
            {
                var index = schema.Indexes[i];
                writer.WriteUInt16((ushort)indexNameLengths[i]);
                int indexNameWritten = _utf8.GetBytes(index.Name, writer.FreeSpan);
                writer.Advance(indexNameWritten);

                writer.WriteUInt16((ushort)indexPathLengths[i]);
                int indexPathWritten = _utf8.GetBytes(index.Path, writer.FreeSpan);
                writer.Advance(indexPathWritten);

                writer.WriteInt64(index.CreatedAtUtcTicks);
            }

            crc.Append(buffer.AsSpan(0, totalSize));
            destination.Write(buffer, 0, totalSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ReadString(Stream source, Crc32 crc, string description)
    {
        Span<byte> lengthBuffer = stackalloc byte[2];
        ReadExactSpan(source, lengthBuffer, description + " length");
        crc.Append(lengthBuffer);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBuffer);
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
