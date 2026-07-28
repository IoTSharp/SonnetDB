using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.IO;
using SonnetDB.Sql;

namespace SonnetDB.Views;

/// <summary>
/// 逻辑视图目录文件（<c>views/views.sdbview</c>）的版本化二进制编解码器。
/// </summary>
public static class ViewDefinitionCodec
{
    /// <summary>视图目录文件名。</summary>
    public const string FileName = "views.sdbview";

    private const int FormatVersion = 1;
    private const int HeaderSize = 32;
    private const int FooterSize = 16;
    private const int MaxViewCount = 100_000;
    private const int MaxNameBytes = 1_024;
    private const int MaxDefinitionBytes = 4 * 1024 * 1024;
    private static readonly byte[] Magic = "SDBVIEW1"u8.ToArray();
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// 从指定文件加载全部逻辑视图；文件不存在时返回空集合。
    /// </summary>
    /// <param name="path">视图目录文件路径。</param>
    /// <returns>视图定义列表。</returns>
    /// <exception cref="InvalidDataException">文件版本、长度、CRC 或定义内容无效时抛出。</exception>
    public static IReadOnlyList<ViewDefinition> Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return [];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(stream);
    }

    /// <summary>
    /// 将全部逻辑视图原子保存到指定文件。
    /// </summary>
    /// <param name="path">视图目录文件路径。</param>
    /// <param name="definitions">待保存的视图定义。</param>
    /// <param name="tempSuffix">同目录临时文件后缀。</param>
    public static void Save(
        string path,
        IReadOnlyList<ViewDefinition> definitions,
        string tempSuffix = ".tmp")
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(tempSuffix);
        if (definitions.Count > MaxViewCount)
            throw new InvalidDataException($"视图数量超过上限 {MaxViewCount}。");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + tempSuffix;
        using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var buffered = new BufferedStream(file, 65_536))
        {
            Save(definitions, buffered);
            buffered.Flush();
            file.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private static IReadOnlyList<ViewDefinition> Load(Stream source)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        ReadExact(source, header, "header");
        var reader = new SpanReader(header);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("ViewCatalog: invalid magic in header.");
        int version = reader.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"ViewCatalog: unsupported format version {version}.");
        int headerSize = reader.ReadInt32();
        if (headerSize != HeaderSize)
            throw new InvalidDataException($"ViewCatalog: unexpected header size {headerSize}.");
        int viewCount = reader.ReadInt32();
        if (viewCount is < 0 or > MaxViewCount)
            throw new InvalidDataException($"ViewCatalog: invalid view count {viewCount}.");

        var crc = new Crc32();
        var definitions = new List<ViewDefinition>(viewCount);
        var names = new HashSet<string>(StringComparer.Ordinal);
        Span<byte> createdBuffer = stackalloc byte[8];
        for (var i = 0; i < viewCount; i++)
        {
            string name = ReadString(source, crc, MaxNameBytes, $"view {i} name");
            string definitionSql = ReadString(source, crc, MaxDefinitionBytes, $"view {i} definition");
            ReadExact(source, createdBuffer, $"view {i} createdAt");
            crc.Append(createdBuffer);
            long createdAtUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(createdBuffer);

            if (!names.Add(name))
                throw new InvalidDataException($"ViewCatalog: duplicate view '{name}'.");
            try
            {
                definitions.Add(ViewDefinition.Create(name, definitionSql, createdAtUtcTicks));
            }
            catch (Exception exception) when (exception is ArgumentException or SqlParseException)
            {
                throw new InvalidDataException($"ViewCatalog: view '{name}' definition is invalid.", exception);
            }
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        ReadExact(source, footer, "footer");
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[..4]);
        if (!footer.Slice(4, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("ViewCatalog: invalid magic in footer.");
        uint actualCrc = crc.GetCurrentHashAsUInt32();
        if (storedCrc != actualCrc)
        {
            throw new InvalidDataException(
                $"ViewCatalog: CRC32 mismatch (expected 0x{storedCrc:X8}, got 0x{actualCrc:X8}).");
        }
        if (source.ReadByte() != -1)
            throw new InvalidDataException("ViewCatalog: unexpected trailing data.");

        return definitions.AsReadOnly();
    }

    private static void Save(IReadOnlyList<ViewDefinition> definitions, Stream destination)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        var writer = new SpanWriter(header);
        writer.WriteBytes(Magic);
        writer.WriteInt32(FormatVersion);
        writer.WriteInt32(HeaderSize);
        writer.WriteInt32(definitions.Count);
        destination.Write(header);

        var crc = new Crc32();
        Span<byte> createdBuffer = stackalloc byte[8];
        foreach (var definition in definitions)
        {
            WriteString(destination, crc, definition.Name, MaxNameBytes, "视图名称过长。");
            WriteString(destination, crc, definition.DefinitionSql, MaxDefinitionBytes, $"视图 '{definition.Name}' 定义过长。");
            BinaryPrimitives.WriteInt64LittleEndian(createdBuffer, definition.CreatedAtUtcTicks);
            crc.Append(createdBuffer);
            destination.Write(createdBuffer);
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        footer.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(footer[..4], crc.GetCurrentHashAsUInt32());
        Magic.CopyTo(footer.Slice(4, Magic.Length));
        destination.Write(footer);
    }

    private static string ReadString(
        Stream source,
        Crc32 crc,
        int maximumBytes,
        string description)
    {
        Span<byte> lengthBuffer = stackalloc byte[4];
        ReadExact(source, lengthBuffer, description + " length");
        crc.Append(lengthBuffer);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException($"ViewCatalog: invalid {description} length {length}.");

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var content = buffer.AsSpan(0, length);
            ReadExact(source, content, description);
            crc.Append(content);
            try
            {
                return Utf8.GetString(content);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException($"ViewCatalog: {description} is not valid UTF-8.", exception);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void WriteString(
        Stream destination,
        Crc32 crc,
        string value,
        int maximumBytes,
        string lengthError)
    {
        int length = Utf8.GetByteCount(value);
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException(lengthError);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(4 + length);
        try
        {
            var content = buffer.AsSpan(0, 4 + length);
            BinaryPrimitives.WriteInt32LittleEndian(content[..4], length);
            int written = Utf8.GetBytes(value, content[4..]);
            if (written != length)
                throw new InvalidDataException("ViewCatalog: UTF-8 encoded length mismatch.");
            crc.Append(content);
            destination.Write(content);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReadExact(Stream source, Span<byte> destination, string description)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int current = source.Read(destination[read..]);
            if (current == 0)
                throw new InvalidDataException($"ViewCatalog: {description} is truncated.");
            read += current;
        }
    }
}
