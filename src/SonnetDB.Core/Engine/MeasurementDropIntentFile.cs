using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Model;
using SonnetDB.Wal;

namespace SonnetDB.Engine;

/// <summary>DROP MEASUREMENT 跨 schema、段和 series catalog 的持久恢复意图。</summary>
internal static class MeasurementDropIntentFile
{
    private const int Version = 1;
    private const int HeaderSize = 16;
    private const int FooterSize = 4;
    private const int MaxNameBytes = 255 * 4;
    private static readonly byte[] Magic = "SDBDROP1"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static void Save(string path, string measurement)
    {
        ArgumentNullException.ThrowIfNull(path);
        PointValidation.ValidateMeasurement(measurement);
        byte[] nameBytes = Utf8.GetBytes(measurement);
        if (nameBytes.Length > MaxNameBytes)
            throw new ArgumentException("Measurement 名称编码超过删除意图文件上限。", nameof(measurement));

        string directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("删除意图文件必须位于数据库目录中。", nameof(path));
        string tempPath = path + ".tmp";
        byte[] bytes = new byte[HeaderSize + nameBytes.Length + FooterSize];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), nameBytes.Length);
        nameBytes.CopyTo(bytes, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - FooterSize),
            Crc32.HashToUInt32(bytes.AsSpan(0, bytes.Length - FooterSize)));

        bool temporaryCreated = false;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                temporaryCreated = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
            DirectoryFsync.FlushRequired(directory);
        }
        finally
        {
            if (temporaryCreated && File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    internal static string? Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path))
                throw new InvalidDataException($"Measurement drop intent '{path}' is a directory.");
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < HeaderSize + FooterSize || stream.Length > HeaderSize + MaxNameBytes + FooterSize)
            throw new InvalidDataException($"Measurement drop intent '{path}' has an invalid length.");

        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (!bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != Version)
            throw new InvalidDataException($"Measurement drop intent '{path}' has an invalid header.");

        int nameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        if (nameLength <= 0 || nameLength != bytes.Length - HeaderSize - FooterSize)
            throw new InvalidDataException($"Measurement drop intent '{path}' has an invalid name length.");

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - FooterSize));
        if (Crc32.HashToUInt32(bytes.AsSpan(0, bytes.Length - FooterSize)) != expectedCrc)
            throw new InvalidDataException($"Measurement drop intent '{path}' failed its checksum.");

        try
        {
            string measurement = Utf8.GetString(bytes, HeaderSize, nameLength);
            PointValidation.ValidateMeasurement(measurement);
            return measurement;
        }
        catch (Exception exception) when (exception is DecoderFallbackException or ArgumentException)
        {
            throw new InvalidDataException($"Measurement drop intent '{path}' has an invalid name.", exception);
        }
    }

    internal static void Clear(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Directory.Exists(path))
            throw new InvalidDataException($"Measurement drop intent '{path}' is a directory.");

        File.Delete(path);
        DirectoryFsync.FlushRequired(Path.GetDirectoryName(path)
            ?? throw new ArgumentException("删除意图文件必须位于数据库目录中。", nameof(path)));
    }
}
