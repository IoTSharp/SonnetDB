using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace SonnetDB.Graphs;

/// <summary>SQL/PGQ 关系映射目录的严格二进制编解码器。</summary>
internal static class PropertyGraphCatalogCodec
{
    internal const string FileName = SonnetDB.Engine.TsdbPaths.PropertyGraphCatalogFileName;

    private const int FormatVersion = 1;
    private const int HeaderSize = 32;
    private const int FooterSize = 16;
    private const int MaxDefinitions = 100_000;
    private const int MaxMappingsPerGraph = 10_000;
    private const int MaxColumnsPerMapping = 1_024;
    private static readonly byte[] Magic = "SDBPGQ01"u8.ToArray();
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static PropertyGraphCatalogState Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return new PropertyGraphCatalogState(0, []);
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(source);
    }

    internal static void Save(string path, PropertyGraphCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);

        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Property graph 目录文件必须包含父目录。", nameof(path));
        Directory.CreateDirectory(directory);
        string temporaryPath = path + ".tmp";
        try
        {
            using (var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan))
            {
                Save(destination, state);
                destination.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
            SonnetDB.Wal.DirectoryFsync.FlushRequired(directory);
        }
        catch
        {
            TryDeleteTemporary(temporaryPath);
            throw;
        }
    }

    private static PropertyGraphCatalogState Load(Stream source)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        ReadExact(source, header, "header");
        if (!header[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("PropertyGraphCatalog: header magic 无效。");
        if (BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != FormatVersion)
            throw new InvalidDataException("PropertyGraphCatalog: 不支持目录格式版本。");
        if (BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != HeaderSize)
            throw new InvalidDataException("PropertyGraphCatalog: header size 无效。");
        long revision = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
        if (revision < 0)
            throw new InvalidDataException("PropertyGraphCatalog: revision 不能为负数。");
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
        ValidateCount(count, MaxDefinitions, "property graph");
        if (BinaryPrimitives.ReadInt32LittleEndian(header[28..]) != 0)
            throw new InvalidDataException("PropertyGraphCatalog: header reserved 字段必须为零。");

        var crc = new Crc32();
        crc.Append(header);
        var definitions = new List<PropertyGraphDefinition>(count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int definitionIndex = 0; definitionIndex < count; definitionIndex++)
        {
            string name = ReadString(source, crc, $"graph {definitionIndex} name");
            long createdAtUtcTicks = ReadInt64(source, crc, $"graph {definitionIndex} created at");
            int vertexCount = ReadCount(source, crc, MaxMappingsPerGraph, $"graph {definitionIndex} vertex");
            var vertices = new List<PropertyGraphVertexTable>(vertexCount);
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                string description = $"graph {definitionIndex} vertex {vertexIndex}";
                vertices.Add(new PropertyGraphVertexTable(
                    ReadString(source, crc, description + " table"),
                    ReadStringList(source, crc, description + " key"),
                    ReadString(source, crc, description + " label"),
                    ReadStringList(source, crc, description + " properties")));
            }

            int edgeCount = ReadCount(source, crc, MaxMappingsPerGraph, $"graph {definitionIndex} edge");
            var edges = new List<PropertyGraphEdgeTable>(edgeCount);
            for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
            {
                string description = $"graph {definitionIndex} edge {edgeIndex}";
                edges.Add(new PropertyGraphEdgeTable(
                    ReadString(source, crc, description + " table"),
                    ReadStringList(source, crc, description + " key"),
                    ReadString(source, crc, description + " source table"),
                    ReadStringList(source, crc, description + " source key"),
                    ReadStringList(source, crc, description + " source reference"),
                    ReadString(source, crc, description + " destination table"),
                    ReadStringList(source, crc, description + " destination key"),
                    ReadStringList(source, crc, description + " destination reference"),
                    ReadString(source, crc, description + " label"),
                    ReadStringList(source, crc, description + " properties")));
            }

            if (!names.Add(name))
                throw new InvalidDataException($"PropertyGraphCatalog: duplicate graph '{name}'。");
            try
            {
                definitions.Add(PropertyGraphDefinition.Restore(name, vertices, edges, createdAtUtcTicks));
            }
            catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
            {
                throw new InvalidDataException($"PropertyGraphCatalog: graph '{name}' 定义无效。", exception);
            }
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        ReadExact(source, footer, "footer");
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[..4]) != crc.GetCurrentHashAsUInt32())
            throw new InvalidDataException("PropertyGraphCatalog: catalog CRC32 不匹配。");
        if (!footer.Slice(4, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("PropertyGraphCatalog: footer magic 无效。");
        if (BinaryPrimitives.ReadInt32LittleEndian(footer[12..]) != 0)
            throw new InvalidDataException("PropertyGraphCatalog: footer reserved 字段必须为零。");
        if (source.ReadByte() != -1)
            throw new InvalidDataException("PropertyGraphCatalog: 检测到尾随数据。");
        return new PropertyGraphCatalogState(revision, definitions);
    }

    private static void Save(Stream destination, PropertyGraphCatalogState state)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], state.Revision);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], state.Definitions.Count);
        destination.Write(header);

        var crc = new Crc32();
        crc.Append(header);
        foreach (PropertyGraphDefinition definition in state.Definitions.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            WriteString(destination, crc, definition.Name);
            WriteInt64(destination, crc, definition.CreatedAtUtcTicks);
            WriteInt32(destination, crc, definition.VertexTables.Count);
            foreach (PropertyGraphVertexTable vertex in definition.VertexTables)
            {
                WriteString(destination, crc, vertex.TableName);
                WriteStringList(destination, crc, vertex.KeyColumns);
                WriteString(destination, crc, vertex.Label);
                WriteStringList(destination, crc, vertex.PropertyColumns);
            }
            WriteInt32(destination, crc, definition.EdgeTables.Count);
            foreach (PropertyGraphEdgeTable edge in definition.EdgeTables)
            {
                WriteString(destination, crc, edge.TableName);
                WriteStringList(destination, crc, edge.KeyColumns);
                WriteString(destination, crc, edge.SourceTable);
                WriteStringList(destination, crc, edge.SourceColumns);
                WriteStringList(destination, crc, edge.SourceReferenceColumns);
                WriteString(destination, crc, edge.DestinationTable);
                WriteStringList(destination, crc, edge.DestinationColumns);
                WriteStringList(destination, crc, edge.DestinationReferenceColumns);
                WriteString(destination, crc, edge.Label);
                WriteStringList(destination, crc, edge.PropertyColumns);
            }
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        footer.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(footer[..4], crc.GetCurrentHashAsUInt32());
        Magic.CopyTo(footer[4..]);
        destination.Write(footer);
    }

    private static void ValidateState(PropertyGraphCatalogState state)
    {
        if (state.Revision < 0)
            throw new InvalidDataException("PropertyGraphCatalog: revision 不能为负数。");
        ValidateCount(state.Definitions.Count, MaxDefinitions, "property graph");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyGraphDefinition definition in state.Definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (!names.Add(definition.Name))
                throw new InvalidDataException($"PropertyGraphCatalog: duplicate graph '{definition.Name}'。");
            ValidateCount(definition.VertexTables.Count, MaxMappingsPerGraph, "vertex mapping");
            ValidateCount(definition.EdgeTables.Count, MaxMappingsPerGraph, "edge mapping");
            _ = PropertyGraphDefinition.Restore(
                definition.Name,
                definition.VertexTables,
                definition.EdgeTables,
                definition.CreatedAtUtcTicks);
        }
    }

    private static IReadOnlyList<string> ReadStringList(Stream source, Crc32 crc, string description)
    {
        int count = ReadCount(source, crc, MaxColumnsPerMapping, description);
        var values = new string[count];
        for (int index = 0; index < count; index++)
            values[index] = ReadString(source, crc, $"{description} {index}");
        return values;
    }

    private static void WriteStringList(Stream destination, Crc32 crc, IReadOnlyList<string> values)
    {
        WriteInt32(destination, crc, values.Count);
        foreach (string value in values)
            WriteString(destination, crc, value);
    }

    private static int ReadCount(Stream source, Crc32 crc, int maximum, string description)
    {
        int count = ReadInt32(source, crc, description + " count");
        ValidateCount(count, maximum, description);
        return count;
    }

    private static void ValidateCount(int count, int maximum, string description)
    {
        if (count is < 0 || count > maximum)
            throw new InvalidDataException($"PropertyGraphCatalog: {description} 数量 {count} 超过上限 {maximum}。");
    }

    private static string ReadString(Stream source, Crc32 crc, string description)
    {
        int length = ReadInt32(source, crc, description + " length");
        if (length <= 0 || length > GraphDefinition.MaxNameBytes)
            throw new InvalidDataException($"PropertyGraphCatalog: {description} 长度 {length} 无效。");
        byte[] bytes = new byte[length];
        ReadExact(source, bytes, description);
        crc.Append(bytes);
        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"PropertyGraphCatalog: {description} 不是有效 UTF-8。", exception);
        }
    }

    private static int ReadInt32(Stream source, Crc32 crc, string description)
    {
        Span<byte> bytes = stackalloc byte[4];
        ReadExact(source, bytes, description);
        crc.Append(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static long ReadInt64(Stream source, Crc32 crc, string description)
    {
        Span<byte> bytes = stackalloc byte[8];
        ReadExact(source, bytes, description);
        crc.Append(bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    private static void WriteString(Stream destination, Crc32 crc, string value)
    {
        GraphDefinition.ValidateName(value);
        byte[] bytes = Utf8.GetBytes(value);
        WriteInt32(destination, crc, bytes.Length);
        crc.Append(bytes);
        destination.Write(bytes);
    }

    private static void WriteInt32(Stream destination, Crc32 crc, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        crc.Append(bytes);
        destination.Write(bytes);
    }

    private static void WriteInt64(Stream destination, Crc32 crc, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        crc.Append(bytes);
        destination.Write(bytes);
    }

    private static void ReadExact(Stream source, Span<byte> destination, string description)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int current = source.Read(destination[read..]);
            if (current == 0)
                throw new InvalidDataException($"PropertyGraphCatalog: {description} 被截断。");
            read += current;
        }
    }

    private static void ReadExact(Stream source, byte[] destination, string description)
        => ReadExact(source, destination.AsSpan(), description);

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 保留原始持久化异常；下次保存会覆盖同名临时文件。
        }
    }
}
