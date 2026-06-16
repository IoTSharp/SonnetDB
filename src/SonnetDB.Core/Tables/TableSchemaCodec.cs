using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.IO;
using SonnetDB.Sql.Ast;

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

    private const int _formatVersion = 5;
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
                schemas.Add(ReadTable(source, crc, i, version));

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

    private static TableSchema ReadTable(Stream source, Crc32 crc, int tableIndex, int version)
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
        var rowVersionColumns = new HashSet<string>(StringComparer.Ordinal);
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
            bool isRowVersion = version >= 4 && (flags[1] & 0b0000_0100) != 0;
            columns.Add((columnName, type, isNullable));
            if (isPrimaryKey)
                primaryKey.Add(columnName);
            if (isRowVersion)
                rowVersionColumns.Add(columnName);
        }

        var indexes = new List<TableIndexDefinition>();
        if (version >= 2)
        {
            ReadExactSpan(source, countBuffer, $"table {tableIndex} indexCount");
            crc.Append(countBuffer);
            int indexCount = BinaryPrimitives.ReadUInt16LittleEndian(countBuffer);
            for (int i = 0; i < indexCount; i++)
                indexes.Add(ReadIndex(source, crc, tableIndex, i, version));
        }

        var foreignKeys = new List<TableForeignKeyDefinition>();
        if (version >= 4)
        {
            ReadExactSpan(source, countBuffer, $"table {tableIndex} foreignKeyCount");
            crc.Append(countBuffer);
            int foreignKeyCount = BinaryPrimitives.ReadUInt16LittleEndian(countBuffer);
            for (int i = 0; i < foreignKeyCount; i++)
                foreignKeys.Add(ReadForeignKey(source, crc, tableIndex, i, version));
        }

        return TableSchema.Create(name, columns, primaryKey, indexes, foreignKeys, rowVersionColumns, createdAt);
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

        var indexNameLengths = new int[schema.Indexes.Count];
        var indexColumnNameLengths = new int[schema.Indexes.Count][];
        totalSize += 2;
        for (int i = 0; i < schema.Indexes.Count; i++)
        {
            var index = schema.Indexes[i];
            int indexNameLength = _utf8.GetByteCount(index.Name);
            if (indexNameLength > ushort.MaxValue)
                throw new InvalidDataException($"Table '{schema.Name}' 的索引 '{index.Name}' 名称过长。");
            indexNameLengths[i] = indexNameLength;
            totalSize += 2 + indexNameLength + 1 + 8 + 2;

            var columnLengths = new int[index.Columns.Count];
            for (int c = 0; c < index.Columns.Count; c++)
            {
                int columnLength = _utf8.GetByteCount(index.Columns[c]);
                if (columnLength > ushort.MaxValue)
                    throw new InvalidDataException($"Table '{schema.Name}' 的索引 '{index.Name}' 列名过长。");
                columnLengths[c] = columnLength;
                totalSize += 2 + columnLength;
            }

            indexColumnNameLengths[i] = columnLengths;
            int jsonPathLength = string.IsNullOrEmpty(index.JsonPath) ? 0 : _utf8.GetByteCount(index.JsonPath);
            if (jsonPathLength > ushort.MaxValue)
                throw new InvalidDataException($"Table '{schema.Name}' 的索引 '{index.Name}' JSON path 过长。");
            totalSize += 2 + jsonPathLength;
        }

        totalSize += 2;
        foreach (var foreignKey in schema.ForeignKeys)
        {
            totalSize += CheckedStringSize(foreignKey.Name, $"Table '{schema.Name}' 的外键名称过长。");
            totalSize += 2;
            foreach (var column in foreignKey.Columns)
                totalSize += CheckedStringSize(column, $"Table '{schema.Name}' 的外键 '{foreignKey.Name}' 列名过长。");
            totalSize += CheckedStringSize(foreignKey.PrincipalTable, $"Table '{schema.Name}' 的外键 '{foreignKey.Name}' 引用表名过长。");
            totalSize += 2;
            foreach (var column in foreignKey.PrincipalColumns)
                totalSize += CheckedStringSize(column, $"Table '{schema.Name}' 的外键 '{foreignKey.Name}' 引用列名过长。");
            totalSize += 1; // v5+：ON DELETE action byte
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
                if (column.IsRowVersion)
                    flags |= 0b0000_0100;
                writer.WriteByte(flags);
            }

            writer.WriteUInt16((ushort)schema.Indexes.Count);
            for (int i = 0; i < schema.Indexes.Count; i++)
            {
                var index = schema.Indexes[i];
                writer.WriteUInt16((ushort)indexNameLengths[i]);
                int indexNameWritten = _utf8.GetBytes(index.Name, writer.FreeSpan);
                writer.Advance(indexNameWritten);
                writer.WriteByte(index.IsUnique ? (byte)1 : (byte)0);
                writer.WriteInt64(index.CreatedAtUtcTicks);
                writer.WriteUInt16((ushort)index.Columns.Count);
                for (int c = 0; c < index.Columns.Count; c++)
                {
                    writer.WriteUInt16((ushort)indexColumnNameLengths[i][c]);
                    int columnWritten = _utf8.GetBytes(index.Columns[c], writer.FreeSpan);
                    writer.Advance(columnWritten);
                }

                if (string.IsNullOrEmpty(index.JsonPath))
                {
                    writer.WriteUInt16(0);
                }
                else
                {
                    int jsonPathLength = _utf8.GetByteCount(index.JsonPath);
                    writer.WriteUInt16((ushort)jsonPathLength);
                    int pathWritten = _utf8.GetBytes(index.JsonPath, writer.FreeSpan);
                    writer.Advance(pathWritten);
                }
            }

            writer.WriteUInt16((ushort)schema.ForeignKeys.Count);
            for (int i = 0; i < schema.ForeignKeys.Count; i++)
            {
                var foreignKey = schema.ForeignKeys[i];
                WriteString(ref writer, foreignKey.Name, $"Table '{schema.Name}' 的外键名称过长。");
                writer.WriteUInt16((ushort)foreignKey.Columns.Count);
                foreach (var column in foreignKey.Columns)
                    WriteString(ref writer, column, $"Table '{schema.Name}' 的外键 '{foreignKey.Name}' 列名过长。");
                WriteString(ref writer, foreignKey.PrincipalTable, $"Table '{schema.Name}' 的外键 '{foreignKey.Name}' 引用表名过长。");
                writer.WriteUInt16((ushort)foreignKey.PrincipalColumns.Count);
                foreach (var column in foreignKey.PrincipalColumns)
                    WriteString(ref writer, column, $"Table '{schema.Name}' 的外键 '{foreignKey.Name}' 引用列名过长。");
                // v5+：ON DELETE 动作字节（0=NoAction, 1=Cascade）
                writer.WriteByte((byte)foreignKey.OnDelete);
            }

            crc.Append(buffer.AsSpan(0, totalSize));
            destination.Write(buffer, 0, totalSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static TableIndexDefinition ReadIndex(Stream source, Crc32 crc, int tableIndex, int indexIndex, int version)
    {
        string indexName = ReadString(source, crc, $"table {tableIndex} index {indexIndex} name");

        Span<byte> flags = stackalloc byte[1];
        ReadExactSpan(source, flags, $"table {tableIndex} index {indexIndex} flags");
        crc.Append(flags);
        bool isUnique = (flags[0] & 0b0000_0001) != 0;

        Span<byte> createdBuffer = stackalloc byte[8];
        ReadExactSpan(source, createdBuffer, $"table {tableIndex} index {indexIndex} createdAt");
        crc.Append(createdBuffer);
        long createdAt = BinaryPrimitives.ReadInt64LittleEndian(createdBuffer);

        Span<byte> countBuffer = stackalloc byte[2];
        ReadExactSpan(source, countBuffer, $"table {tableIndex} index {indexIndex} columnCount");
        crc.Append(countBuffer);
        int columnCount = BinaryPrimitives.ReadUInt16LittleEndian(countBuffer);
        if (columnCount <= 0)
            throw new InvalidDataException($"TableSchema: table {tableIndex} index '{indexName}' has no columns.");

        var columns = new List<string>(columnCount);
        for (int i = 0; i < columnCount; i++)
            columns.Add(ReadString(source, crc, $"table {tableIndex} index {indexIndex} column {i} name"));

        string? jsonPath = null;
        if (version >= 3)
        {
            string rawPath = ReadString(source, crc, $"table {tableIndex} index {indexIndex} jsonPath");
            if (!string.IsNullOrWhiteSpace(rawPath))
                jsonPath = rawPath;
        }

        return new TableIndexDefinition(indexName, columns.AsReadOnly(), isUnique, createdAt, jsonPath);
    }

    private static TableForeignKeyDefinition ReadForeignKey(Stream source, Crc32 crc, int tableIndex, int foreignKeyIndex, int version)
    {
        string name = ReadString(source, crc, $"table {tableIndex} foreignKey {foreignKeyIndex} name");
        Span<byte> countBuffer = stackalloc byte[2];
        ReadExactSpan(source, countBuffer, $"table {tableIndex} foreignKey {foreignKeyIndex} columnCount");
        crc.Append(countBuffer);
        int columnCount = BinaryPrimitives.ReadUInt16LittleEndian(countBuffer);
        if (columnCount <= 0)
            throw new InvalidDataException($"TableSchema: table {tableIndex} foreignKey '{name}' has no columns.");

        var columns = new List<string>(columnCount);
        for (int i = 0; i < columnCount; i++)
            columns.Add(ReadString(source, crc, $"table {tableIndex} foreignKey {foreignKeyIndex} column {i} name"));

        string principalTable = ReadString(source, crc, $"table {tableIndex} foreignKey {foreignKeyIndex} principal table");
        ReadExactSpan(source, countBuffer, $"table {tableIndex} foreignKey {foreignKeyIndex} principalColumnCount");
        crc.Append(countBuffer);
        int principalColumnCount = BinaryPrimitives.ReadUInt16LittleEndian(countBuffer);
        if (principalColumnCount <= 0)
            throw new InvalidDataException($"TableSchema: table {tableIndex} foreignKey '{name}' has no principal columns.");

        var principalColumns = new List<string>(principalColumnCount);
        for (int i = 0; i < principalColumnCount; i++)
            principalColumns.Add(ReadString(source, crc, $"table {tableIndex} foreignKey {foreignKeyIndex} principal column {i} name"));

        var onDelete = ForeignKeyAction.NoAction;
        if (version >= 5)
        {
            Span<byte> actionByte = stackalloc byte[1];
            ReadExactSpan(source, actionByte, $"table {tableIndex} foreignKey {foreignKeyIndex} onDelete");
            crc.Append(actionByte);
            if (actionByte[0] > 1)
                throw new InvalidDataException($"TableSchema: foreignKey '{name}' has unknown onDelete action {actionByte[0]}.");
            onDelete = (ForeignKeyAction)actionByte[0];
        }

        return new TableForeignKeyDefinition(name, columns.AsReadOnly(), principalTable, principalColumns.AsReadOnly(), onDelete);
    }

    private static int CheckedStringSize(string value, string lengthError)
    {
        int length = _utf8.GetByteCount(value);
        if (length > ushort.MaxValue)
            throw new InvalidDataException(lengthError);
        return 2 + length;
    }

    private static void WriteString(ref SpanWriter writer, string value, string lengthError)
    {
        int length = _utf8.GetByteCount(value);
        if (length > ushort.MaxValue)
            throw new InvalidDataException(lengthError);
        writer.WriteUInt16((ushort)length);
        int written = _utf8.GetBytes(value, writer.FreeSpan);
        writer.Advance(written);
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
