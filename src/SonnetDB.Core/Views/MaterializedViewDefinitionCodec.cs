using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Sql;

namespace SonnetDB.Views;

/// <summary>
/// 物化视图目录文件的版本化二进制编解码器。
/// </summary>
public static class MaterializedViewDefinitionCodec
{
    /// <summary>物化视图目录文件名。</summary>
    public const string FileName = "materialized-views.sdbmv";

    private const int FormatVersion = 1;
    private const int HeaderSize = 32;
    private const int FooterSize = 16;
    private const int MaxDefinitionCount = 100_000;
    private const int MaxNameBytes = 1_024;
    private const int MaxDefinitionBytes = 4 * 1024 * 1024;
    private const int MaxErrorBytes = 1024 * 1024;
    private const int MaxDependencyCount = 100_000;
    private static readonly byte[] Magic = "SDBMVC01"u8.ToArray();
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// 从指定文件加载全部物化视图；文件不存在时返回空集合。
    /// </summary>
    /// <param name="path">目录文件路径。</param>
    /// <returns>物化视图定义列表。</returns>
    public static IReadOnlyList<MaterializedViewDefinition> Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(stream);
    }

    /// <summary>
    /// 将全部物化视图定义原子保存到指定文件。
    /// </summary>
    /// <param name="path">目录文件路径。</param>
    /// <param name="definitions">待保存定义。</param>
    /// <param name="tempSuffix">同目录临时文件后缀。</param>
    public static void Save(
        string path,
        IReadOnlyList<MaterializedViewDefinition> definitions,
        string tempSuffix = ".tmp")
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(tempSuffix);
        if (definitions.Count > MaxDefinitionCount)
            throw new InvalidDataException($"物化视图数量超过上限 {MaxDefinitionCount}。");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + tempSuffix;
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Save(definitions, file);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private static IReadOnlyList<MaterializedViewDefinition> Load(Stream source)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        ReadExact(source, header, "header");
        if (!header[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("MaterializedViewCatalog: invalid header magic.");
        int version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        if (version != FormatVersion)
            throw new InvalidDataException($"MaterializedViewCatalog: unsupported format version {version}.");
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4));
        if (headerSize != HeaderSize)
            throw new InvalidDataException($"MaterializedViewCatalog: unexpected header size {headerSize}.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4));
        if (count is < 0 or > MaxDefinitionCount)
            throw new InvalidDataException($"MaterializedViewCatalog: invalid definition count {count}.");

        var crc = new Crc32();
        var definitions = new List<MaterializedViewDefinition>(count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var storageIds = new HashSet<Guid>();
        for (int index = 0; index < count; index++)
        {
            Guid storageId = ReadGuid(source, crc, $"definition {index} storage id");
            string name = ReadString(source, crc, MaxNameBytes, $"definition {index} name")!;
            string definitionSql = ReadString(source, crc, MaxDefinitionBytes, $"definition {index} SQL")!;
            int dependencyCount = ReadInt32(source, crc, $"definition {index} dependency count");
            if (dependencyCount is < 0 or > MaxDependencyCount)
                throw new InvalidDataException($"MaterializedViewCatalog: invalid dependency count {dependencyCount}.");
            var dependencies = new string[dependencyCount];
            for (int dependencyIndex = 0; dependencyIndex < dependencyCount; dependencyIndex++)
            {
                dependencies[dependencyIndex] = ReadString(
                    source,
                    crc,
                    MaxNameBytes,
                    $"definition {index} dependency {dependencyIndex}")!;
            }

            long definitionVersion = ReadInt64(source, crc, $"definition {index} version");
            long createdAt = ReadInt64(source, crc, $"definition {index} created at");
            var status = (MaterializedViewRefreshStatus)ReadByte(source, crc, $"definition {index} status");
            long activeGeneration = ReadInt64(source, crc, $"definition {index} active generation");
            long rowCount = ReadInt64(source, crc, $"definition {index} row count");
            long lastRefreshAt = ReadInt64(source, crc, $"definition {index} last refresh");
            long lastSuccessfulRefreshAt = ReadInt64(source, crc, $"definition {index} last successful refresh");
            string? lastError = ReadString(source, crc, MaxErrorBytes, $"definition {index} last error", nullable: true);

            if (!names.Add(name))
                throw new InvalidDataException($"MaterializedViewCatalog: duplicate name '{name}'.");
            if (!storageIds.Add(storageId))
                throw new InvalidDataException($"MaterializedViewCatalog: duplicate storage id '{storageId}'.");
            try
            {
                definitions.Add(MaterializedViewDefinition.Restore(
                    storageId,
                    name,
                    definitionSql,
                    dependencies,
                    definitionVersion,
                    createdAt,
                    status,
                    activeGeneration,
                    rowCount,
                    lastRefreshAt,
                    lastSuccessfulRefreshAt,
                    lastError));
            }
            catch (Exception exception) when (exception is ArgumentException or SqlParseException or InvalidDataException)
            {
                throw new InvalidDataException(
                    $"MaterializedViewCatalog: definition '{name}' is invalid.",
                    exception);
            }
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        ReadExact(source, footer, "footer");
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[..4]);
        uint actualCrc = crc.GetCurrentHashAsUInt32();
        if (storedCrc != actualCrc)
            throw new InvalidDataException("MaterializedViewCatalog: payload CRC mismatch.");
        if (!footer.Slice(4, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("MaterializedViewCatalog: invalid footer magic.");
        if (source.ReadByte() != -1)
            throw new InvalidDataException("MaterializedViewCatalog: trailing bytes detected.");
        return definitions;
    }

    private static void Save(IReadOnlyList<MaterializedViewDefinition> definitions, Stream destination)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), definitions.Count);
        destination.Write(header);

        var crc = new Crc32();
        foreach (var definition in definitions.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            WriteGuid(destination, crc, definition.StorageId);
            WriteString(destination, crc, definition.Name, MaxNameBytes, nullable: false);
            WriteString(destination, crc, definition.DefinitionSql, MaxDefinitionBytes, nullable: false);
            WriteInt32(destination, crc, definition.Dependencies.Count);
            foreach (string dependency in definition.Dependencies)
                WriteString(destination, crc, dependency, MaxNameBytes, nullable: false);
            WriteInt64(destination, crc, definition.DefinitionVersion);
            WriteInt64(destination, crc, definition.CreatedAtUtcTicks);
            WriteByte(destination, crc, (byte)definition.Status);
            WriteInt64(destination, crc, definition.ActiveGeneration);
            WriteInt64(destination, crc, definition.RowCount);
            WriteInt64(destination, crc, definition.LastRefreshAtUtcTicks);
            WriteInt64(destination, crc, definition.LastSuccessfulRefreshAtUtcTicks);
            WriteString(destination, crc, definition.LastError, MaxErrorBytes, nullable: true);
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        footer.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(footer[..4], crc.GetCurrentHashAsUInt32());
        Magic.CopyTo(footer.Slice(4, Magic.Length));
        destination.Write(footer);
    }

    private static void WriteString(
        Stream destination,
        Crc32 crc,
        string? value,
        int maxBytes,
        bool nullable)
    {
        if (value is null)
        {
            if (!nullable)
                throw new InvalidDataException("MaterializedViewCatalog: required string is null.");
            WriteInt32(destination, crc, -1);
            return;
        }

        int byteCount = Utf8.GetByteCount(value);
        if (byteCount > maxBytes)
            throw new InvalidDataException($"MaterializedViewCatalog: string exceeds {maxBytes} UTF-8 bytes.");
        WriteInt32(destination, crc, byteCount);
        if (byteCount == 0)
            return;
        byte[] buffer = Utf8.GetBytes(value);
        WritePayload(destination, crc, buffer);
    }

    private static string? ReadString(
        Stream source,
        Crc32 crc,
        int maxBytes,
        string description,
        bool nullable = false)
    {
        int length = ReadInt32(source, crc, description + " length");
        if (nullable && length == -1)
            return null;
        if (length is < 0 || length > maxBytes)
            throw new InvalidDataException($"MaterializedViewCatalog: invalid {description} length {length}.");
        if (length == 0)
            return string.Empty;
        byte[] buffer = new byte[length];
        ReadPayload(source, crc, buffer, description);
        try
        {
            return Utf8.GetString(buffer);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"MaterializedViewCatalog: {description} is not valid UTF-8.", exception);
        }
    }

    private static void WriteGuid(Stream destination, Crc32 crc, Guid value)
    {
        Span<byte> buffer = stackalloc byte[16];
        value.TryWriteBytes(buffer);
        WritePayload(destination, crc, buffer);
    }

    private static Guid ReadGuid(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[16];
        ReadPayload(source, crc, buffer, description);
        return new Guid(buffer);
    }

    private static void WriteByte(Stream destination, Crc32 crc, byte value)
    {
        Span<byte> buffer = stackalloc byte[1] { value };
        WritePayload(destination, crc, buffer);
    }

    private static byte ReadByte(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[1];
        ReadPayload(source, crc, buffer, description);
        return buffer[0];
    }

    private static void WriteInt32(Stream destination, Crc32 crc, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        WritePayload(destination, crc, buffer);
    }

    private static int ReadInt32(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadPayload(source, crc, buffer, description);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static void WriteInt64(Stream destination, Crc32 crc, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        WritePayload(destination, crc, buffer);
    }

    private static long ReadInt64(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadPayload(source, crc, buffer, description);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    private static void WritePayload(Stream destination, Crc32 crc, ReadOnlySpan<byte> payload)
    {
        destination.Write(payload);
        crc.Append(payload);
    }

    private static void ReadPayload(Stream source, Crc32 crc, Span<byte> payload, string description)
    {
        ReadExact(source, payload, description);
        crc.Append(payload);
    }

    private static void ReadExact(Stream source, Span<byte> buffer, string description)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = source.Read(buffer[total..]);
            if (read == 0)
                throw new InvalidDataException($"MaterializedViewCatalog: {description} is truncated.");
            total += read;
        }
    }
}
