using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.IO;

namespace SonnetDB.Tables;

/// <summary>
/// 关系表 schema 文件（<c>tables/tables.tblschema</c>）的二进制序列化器。
/// </summary>
public static class TableSchemaCodec
{
    /// <summary>schema 文件名。</summary>
    public const string FileName = "tables.tblschema";

    private static readonly byte[] _magic = "SDBTBLv1"u8.ToArray();
    private static readonly Encoding _utf8 = Encoding.UTF8;

    private const int _formatVersion = 1;
    private const int _headerSize = 32;
    private const int _footerSize = 16;

    /// <summary>
    /// 从文件加载全部表 schema；文件不存在时返回空集合。
    /// </summary>
    /// <param name="path">schema 文件路径。</param>
    /// <returns>schema 列表。</returns>
    public static IReadOnlyList<TableSchema> Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return [];

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(fs);
    }

    /// <summary>
    /// 保存全部表 schema。
    /// </summary>
    /// <param name="path">schema 文件路径。</param>
    /// <param name="schemas">schema 列表。</param>
    /// <param name="tempSuffix">临时文件后缀。</param>
    public static void Save(string path, IReadOnlyList<TableSchema> schemas, string tempSuffix = ".tmp")
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

    private static IReadOnlyList<TableSchema> Load(Stream source)
    {
        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(_headerSize);
        try
        {
            int read = ReadExact(source, headerBuffer, 0, _headerSize);
            if (read < _headerSize)
                throw new InvalidDataException("TableSchema: header is truncated.");

            var reader = new SpanReader(headerBuffer.AsSpan(0, _headerSize));
            if (!reader.ReadBytes(8).SequenceEqual(_magic))
                throw new InvalidDataException("TableSchema: invalid magic in header.");

            int version = reader.ReadInt32();
            if (version is < 1 or > _formatVersion)
                throw new InvalidDataException($"TableSchema: unsupported format version {version}.");

            int headerSize = reader.ReadInt32();
            if (headerSize != _headerSize)
                throw new InvalidDataException($"TableSchema: unexpected header size {headerSize}.");

            int tableCount = reader.ReadInt32();
            if (tableCount < 0)
                throw new InvalidDataException("TableSchema: negative table count.");

            var crc = new Crc32();
            var schemas = new List<TableSchema>(tableCount);
            for (int i = 0; i < tableCount; i++)
                schemas.Add(ReadTable(source, crc, i));

            byte[] footerBuffer = ArrayPool<byte>.Shared.Rent(_footerSize);
            try
            {
                int footerRead = ReadExact(source, footerBuffer, 0, _footerSize);
                if (footerRead < _footerSize)
                    throw new InvalidDataException("TableSchema: footer is truncated.");

                uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footerBuffer.AsSpan(0, 4));
                if (!footerBuffer.AsSpan(4, 8).SequenceEqual(_magic))
                    throw new InvalidDataException("TableSchema: invalid magic in footer.");

                uint actualCrc = crc.GetCurrentHashAsUInt32();
                if (storedCrc != actualCrc)
                    throw new InvalidDataException(
                        $"TableSchema: CRC32 mismatch (expected 0x{storedCrc:X8}, got 0x{actualCrc:X8}).");
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

    private static TableSchema ReadTable(Stream source, Crc32 crc, int tableIndex)
    {
        string name = ReadString(source, crc, $"table {tableIndex} name");

        Span<byte> createdBuffer = stackalloc byte[8];
        ReadExactSpan(source, createdBuffer, $"table {tableIndex} createdAt");
        crc.Append(createdBuffer);
        long createdAt = BinaryPrimitives.ReadInt64LittleEndian(createdBuffer);

        Span<byte> countBuffer = stackalloc byte[2];
        ReadExactSpan(source, countBuffer, $"table {tableIndex} columnCount");
        crc.Append(countBuffer);
        int columnCount = BinaryPrimitives.ReadUInt16LittleEndian(countBuffer);
        if (columnCount <= 0)
            throw new InvalidDataException($"TableSchema: table '{name}' has no columns.");

        var columns = new List<(string Name, TableColumnType DataType, bool IsNullable)>(columnCount);
        var primaryKey = new List<string>();
        Span<byte> flags = stackalloc byte[2];
        for (int i = 0; i < columnCount; i++)
        {
            string columnName = ReadString(source, crc, $"table {tableIndex} column {i} name");
            ReadExactSpan(source, flags, $"table {tableIndex} column {i} flags");
            crc.Append(flags);

            var type = (TableColumnType)flags[0];
            if (!Enum.IsDefined(type))
                throw new InvalidDataException($"TableSchema: invalid column type {flags[0]} for '{columnName}'.");

            bool isPrimaryKey = (flags[1] & 0b0000_0001) != 0;
            bool isNullable = (flags[1] & 0b0000_0010) != 0;
            columns.Add((columnName, type, isNullable));
            if (isPrimaryKey)
                primaryKey.Add(columnName);
        }

        return TableSchema.Create(name, columns, primaryKey, createdAt);
    }

    private static void Save(IReadOnlyList<TableSchema> schemas, Stream destination)
    {
        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(_headerSize);
        try
        {
            headerBuffer.AsSpan(0, _headerSize).Clear();
            var writer = new SpanWriter(headerBuffer.AsSpan(0, _headerSize));
            writer.WriteBytes(_magic);
            writer.WriteInt32(_formatVersion);
            writer.WriteInt32(_headerSize);
            writer.WriteInt32(schemas.Count);
            destination.Write(headerBuffer, 0, _headerSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }

        var crc = new Crc32();
        foreach (var schema in schemas)
            WriteTable(destination, schema, crc);

        Span<byte> footer = stackalloc byte[_footerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(footer[..4], crc.GetCurrentHashAsUInt32());
        _magic.CopyTo(footer.Slice(4, 8));
        destination.Write(footer);
    }

    private static void WriteTable(Stream destination, TableSchema schema, Crc32 crc)
    {
        int nameLength = _utf8.GetByteCount(schema.Name);
        if (nameLength > ushort.MaxValue)
            throw new InvalidDataException($"Table '{schema.Name}' 名称过长。");

        int totalSize = 2 + nameLength + 8 + 2;
        var columnNameLengths = new int[schema.Columns.Count];
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            int length = _utf8.GetByteCount(schema.Columns[i].Name);
            if (length > ushort.MaxValue)
                throw new InvalidDataException($"Table '{schema.Name}' 的列 '{schema.Columns[i].Name}' 名称过长。");
            columnNameLengths[i] = length;
            totalSize += 2 + length + 2;
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
            writer.WriteUInt16((ushort)schema.Columns.Count);

            for (int i = 0; i < schema.Columns.Count; i++)
            {
                var column = schema.Columns[i];
                writer.WriteUInt16((ushort)columnNameLengths[i]);
                int columnWritten = _utf8.GetBytes(column.Name, writer.FreeSpan);
                writer.Advance(columnWritten);
                writer.WriteByte((byte)column.DataType);
                byte flags = 0;
                if (column.IsPrimaryKey)
                    flags |= 0b0000_0001;
                if (column.IsNullable)
                    flags |= 0b0000_0010;
                writer.WriteByte(flags);
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
                throw new InvalidDataException($"TableSchema: {description} is truncated.");
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
                throw new InvalidDataException($"TableSchema: {description} is truncated.");
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
