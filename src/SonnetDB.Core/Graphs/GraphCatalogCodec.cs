using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace SonnetDB.Graphs;

/// <summary>仅供 catalog 持久化故障注入的原子保存阶段。</summary>
internal enum GraphCatalogSavePhase
{
    BeforeReplace,
    AfterReplaceBeforeDirectoryFlush,
    AfterDirectoryFlush,
}

/// <summary>
/// 原生图目录文件的严格二进制编解码器。
/// </summary>
/// <remarks>
/// 目录使用独立于关系表、视图和其它模型的格式版本。未知版本、损坏内容和尾随数据都会拒绝加载，
/// 以免把不完整的 graph 定义发布到内存目录。
/// </remarks>
internal sealed class GraphCatalogCodec
{
    /// <summary>图目录文件名。</summary>
    public const string FileName = SonnetDB.Engine.TsdbPaths.GraphCatalogFileName;

    private const int FormatVersion = 1;
    private const int HeaderSize = 32;
    private const int FooterSize = 16;
    private const int MaxDefinitions = 100_000;
    private static readonly byte[] Magic = "SDBGRPH1"u8.ToArray();
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal Action<GraphCatalogSavePhase>? SavePhaseTestHook { get; set; }

    /// <summary>
    /// 从磁盘加载图目录；文件不存在时返回空目录状态。
    /// </summary>
    /// <param name="path">图目录文件路径。</param>
    /// <returns>图目录修订号和定义快照。</returns>
    /// <exception cref="InvalidDataException">文件格式、版本或校验和不正确时抛出。</exception>
    public static GraphCatalogState Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return new GraphCatalogState(0, []);

        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(source);
    }

    /// <summary>
    /// 将图目录状态以临时文件加原子替换方式保存。
    /// </summary>
    /// <param name="path">目标图目录文件路径。</param>
    /// <param name="state">待保存的目录状态。</param>
    public static void Save(string path, GraphCatalogState state)
        => SaveCore(path, state, savePhaseHook: null);

    internal void Persist(string path, GraphCatalogState state)
        => SaveCore(path, state, SavePhaseTestHook);

    private static void SaveCore(
        string path,
        GraphCatalogState state,
        Action<GraphCatalogSavePhase>? savePhaseHook)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);

        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("图目录文件必须包含父目录。", nameof(path));
        Directory.CreateDirectory(directory);

        string temporaryPath = path + ".tmp";
        try
        {
            using (var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan))
            {
                Save(destination, state);
                destination.Flush(flushToDisk: true);
            }

            savePhaseHook?.Invoke(GraphCatalogSavePhase.BeforeReplace);
            File.Move(temporaryPath, path, overwrite: true);
            savePhaseHook?.Invoke(GraphCatalogSavePhase.AfterReplaceBeforeDirectoryFlush);
            SonnetDB.Wal.DirectoryFsync.FlushRequired(directory);
            savePhaseHook?.Invoke(GraphCatalogSavePhase.AfterDirectoryFlush);
        }
        catch
        {
            TryDeleteTemporary(temporaryPath);
            throw;
        }
    }

    private static GraphCatalogState Load(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Span<byte> header = stackalloc byte[HeaderSize];
        ReadExact(source, header, "header");
        if (!header[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("GraphCatalog: header magic 无效。");

        int version = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (version != FormatVersion)
            throw new InvalidDataException($"GraphCatalog: 不支持目录格式版本 {version}。");

        if (BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != HeaderSize)
            throw new InvalidDataException("GraphCatalog: header size 无效。");

        long revision = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
        if (revision < 0)
            throw new InvalidDataException("GraphCatalog: revision 不能为负数。");

        int count = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
        ValidateCount(count);
        if (BinaryPrimitives.ReadInt32LittleEndian(header[28..]) != 0)
            throw new InvalidDataException("GraphCatalog: header reserved 字段必须为零。");

        var crc = new Crc32();
        crc.Append(header);
        var definitions = new List<GraphDefinition>(count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var storageIds = new HashSet<Guid>();

        for (int index = 0; index < count; index++)
        {
            string name = ReadString(source, crc, $"graph {index} name");
            Guid storageId = ReadGuid(source, crc, $"graph {index} storage id");
            long createdAtUtcTicks = ReadInt64(source, crc, $"graph {index} created at");
            int recordFormatVersion = ReadInt32(source, crc, $"graph {index} record format");

            if (!names.Add(name))
                throw new InvalidDataException($"GraphCatalog: duplicate graph '{name}'。");
            if (!storageIds.Add(storageId))
            {
                throw new InvalidDataException(
                    $"GraphCatalog: duplicate storage id '{storageId:N}'。");
            }

            try
            {
                definitions.Add(GraphDefinition.Restore(
                    name,
                    storageId,
                    createdAtUtcTicks,
                    recordFormatVersion));
            }
            catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
            {
                throw new InvalidDataException($"GraphCatalog: graph '{name}' 定义无效。", exception);
            }
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        ReadExact(source, footer, "footer");
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[..4]);
        if (storedCrc != crc.GetCurrentHashAsUInt32())
            throw new InvalidDataException("GraphCatalog: catalog CRC32 不匹配。");
        if (!footer.Slice(4, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("GraphCatalog: footer magic 无效。");
        if (BinaryPrimitives.ReadInt32LittleEndian(footer[12..]) != 0)
            throw new InvalidDataException("GraphCatalog: footer reserved 字段必须为零。");
        if (source.ReadByte() != -1)
            throw new InvalidDataException("GraphCatalog: 检测到尾随数据。");

        return new GraphCatalogState(revision, definitions);
    }

    private static void Save(Stream destination, GraphCatalogState state)
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
        foreach (GraphDefinition definition in state.Definitions.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            WriteString(destination, crc, definition.Name);
            WriteGuid(destination, crc, definition.StorageId);
            WriteInt64(destination, crc, definition.CreatedAtUtcTicks);
            WriteInt32(destination, crc, definition.RecordFormatVersion);
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        footer.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(footer[..4], crc.GetCurrentHashAsUInt32());
        Magic.CopyTo(footer[4..]);
        destination.Write(footer);
    }

    private static void ValidateState(GraphCatalogState state)
    {
        if (state.Revision < 0)
            throw new InvalidDataException("GraphCatalog: revision 不能为负数。");
        ValidateCount(state.Definitions.Count);

        var names = new HashSet<string>(StringComparer.Ordinal);
        var storageIds = new HashSet<Guid>();
        foreach (GraphDefinition definition in state.Definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            GraphDefinition.ValidateName(definition.Name);
            if (!names.Add(definition.Name))
                throw new InvalidDataException($"GraphCatalog: duplicate graph '{definition.Name}'。");
            if (!storageIds.Add(definition.StorageId))
                throw new InvalidDataException(
                    $"GraphCatalog: duplicate storage id '{definition.StorageId:N}'。");
            _ = GraphDefinition.Restore(
                definition.Name,
                definition.StorageId,
                definition.CreatedAtUtcTicks,
                definition.RecordFormatVersion);
        }
    }

    private static void ValidateCount(int count)
    {
        if (count is < 0 or > MaxDefinitions)
            throw new InvalidDataException($"GraphCatalog: graph 数量 {count} 超过上限 {MaxDefinitions}。");
    }

    private static string ReadString(Stream source, Crc32 crc, string description)
    {
        int length = ReadInt32(source, crc, description + " length");
        if (length <= 0 || length > GraphDefinition.MaxNameBytes)
            throw new InvalidDataException($"GraphCatalog: {description} 长度 {length} 无效。");
        byte[] bytes = new byte[length];
        ReadExact(source, bytes, description);
        crc.Append(bytes);
        try
        {
            string value = Utf8.GetString(bytes);
            GraphDefinition.ValidateName(value);
            return value;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"GraphCatalog: {description} 不是有效 UTF-8。", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"GraphCatalog: {description} 无效。", exception);
        }
    }

    private static Guid ReadGuid(Stream source, Crc32 crc, string description)
    {
        Span<byte> bytes = stackalloc byte[16];
        ReadExact(source, bytes, description);
        crc.Append(bytes);
        var value = new Guid(bytes);
        if (value == Guid.Empty)
            throw new InvalidDataException($"GraphCatalog: {description} 不能为空。");
        return value;
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

    private static void WriteGuid(Stream destination, Crc32 crc, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
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
                throw new InvalidDataException($"GraphCatalog: {description} 被截断。");
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
            // 保留原始持久化异常；下一次保存会覆盖临时文件。
        }
    }
}
