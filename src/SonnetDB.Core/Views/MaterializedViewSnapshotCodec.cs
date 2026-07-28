using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Model;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Views;

internal static class MaterializedViewSnapshotCodec
{
    private const int FormatVersion = 1;
    private const int HeaderSize = 32;
    private const int FooterSize = 16;
    private const int MaxColumnCount = 65_535;
    private const int MaxRowCount = 100_000_000;
    private const int MaxColumnNameBytes = 1_024 * 1024;
    private const int MaxValueBytes = 64 * 1024 * 1024;
    private static readonly byte[] Magic = "SDBMVS01"u8.ToArray();
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    private enum ValueKind : byte
    {
        Null = 0,
        Int64 = 1,
        UInt64 = 2,
        Float64 = 3,
        Decimal = 4,
        False = 5,
        True = 6,
        String = 7,
        DateTime = 8,
        DateTimeOffset = 9,
        Blob = 10,
        Vector = 11,
        GeoPoint = 12,
    }

    public static void Save(string path, SelectExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(result);
        ValidateShape(result);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Save(file, result);
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

    public static SelectExecutionResult Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(file);
    }

    private static void ValidateShape(SelectExecutionResult result)
    {
        if (result.Columns.Count > MaxColumnCount)
            throw new InvalidDataException($"物化视图列数超过上限 {MaxColumnCount}。");
        if (result.Rows.Count > MaxRowCount)
            throw new InvalidDataException($"物化视图行数超过上限 {MaxRowCount}。");
        foreach (var row in result.Rows)
        {
            if (row.Count != result.Columns.Count)
                throw new InvalidDataException("物化视图结果行宽与列数不一致。");
        }
    }

    private static void Save(Stream destination, SelectExecutionResult result)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), result.Columns.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(20, 4), result.Rows.Count);
        destination.Write(header);

        var crc = new Crc32();
        foreach (string column in result.Columns)
            WriteString(destination, crc, column, MaxColumnNameBytes);
        foreach (var row in result.Rows)
        {
            foreach (object? value in row)
                WriteValue(destination, crc, value);
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        footer.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(footer[..4], crc.GetCurrentHashAsUInt32());
        Magic.CopyTo(footer.Slice(4, Magic.Length));
        destination.Write(footer);
    }

    private static SelectExecutionResult Load(Stream source)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        ReadExact(source, header, "header");
        if (!header[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("MaterializedViewSnapshot: invalid header magic.");
        int version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        if (version != FormatVersion)
            throw new InvalidDataException($"MaterializedViewSnapshot: unsupported format version {version}.");
        if (BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4)) != HeaderSize)
            throw new InvalidDataException("MaterializedViewSnapshot: invalid header size.");
        int columnCount = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4));
        int rowCount = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(20, 4));
        if (columnCount is < 0 or > MaxColumnCount || rowCount is < 0 or > MaxRowCount)
            throw new InvalidDataException("MaterializedViewSnapshot: invalid result dimensions.");
        if (columnCount == 0 && rowCount != 0)
            throw new InvalidDataException("MaterializedViewSnapshot: rows cannot exist without columns.");
        if (source.CanSeek)
        {
            long minimumRemaining = checked(
                (long)columnCount * sizeof(int)
                + (long)rowCount * columnCount
                + FooterSize);
            if (source.Length - source.Position < minimumRemaining)
                throw new InvalidDataException("MaterializedViewSnapshot: declared dimensions exceed file length.");
        }

        var crc = new Crc32();
        var columns = new string[columnCount];
        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
            columns[columnIndex] = ReadString(source, crc, MaxColumnNameBytes, $"column {columnIndex}");
        var rows = new IReadOnlyList<object?>[rowCount];
        for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = new object?[columnCount];
            for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
                row[columnIndex] = ReadValue(source, crc, rowIndex, columnIndex);
            rows[rowIndex] = row;
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        ReadExact(source, footer, "footer");
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[..4]) != crc.GetCurrentHashAsUInt32())
            throw new InvalidDataException("MaterializedViewSnapshot: payload CRC mismatch.");
        if (!footer.Slice(4, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("MaterializedViewSnapshot: invalid footer magic.");
        if (source.ReadByte() != -1)
            throw new InvalidDataException("MaterializedViewSnapshot: trailing bytes detected.");
        return new SelectExecutionResult(columns, rows);
    }

    private static void WriteValue(Stream destination, Crc32 crc, object? value)
    {
        switch (value)
        {
            case null:
                WriteByte(destination, crc, (byte)ValueKind.Null);
                return;
            case bool boolean:
                WriteByte(destination, crc, boolean ? (byte)ValueKind.True : (byte)ValueKind.False);
                return;
            case byte or sbyte or short or ushort or int or uint or long:
                WriteByte(destination, crc, (byte)ValueKind.Int64);
                WriteInt64(destination, crc, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;
            case ulong unsigned:
                WriteByte(destination, crc, (byte)ValueKind.UInt64);
                WriteUInt64(destination, crc, unsigned);
                return;
            case float or double:
                WriteByte(destination, crc, (byte)ValueKind.Float64);
                WriteDouble(destination, crc, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                return;
            case decimal decimalValue:
                WriteByte(destination, crc, (byte)ValueKind.Decimal);
                foreach (int part in decimal.GetBits(decimalValue))
                    WriteInt32(destination, crc, part);
                return;
            case string text:
                WriteByte(destination, crc, (byte)ValueKind.String);
                WriteString(destination, crc, text, MaxValueBytes);
                return;
            case DateTime dateTime:
                WriteByte(destination, crc, (byte)ValueKind.DateTime);
                WriteInt64(destination, crc, dateTime.Ticks);
                WriteByte(destination, crc, (byte)dateTime.Kind);
                return;
            case DateTimeOffset dateTimeOffset:
                WriteByte(destination, crc, (byte)ValueKind.DateTimeOffset);
                WriteInt64(destination, crc, dateTimeOffset.Ticks);
                WriteInt64(destination, crc, dateTimeOffset.Offset.Ticks);
                return;
            case byte[] bytes:
                WriteByte(destination, crc, (byte)ValueKind.Blob);
                WriteBytes(destination, crc, bytes, MaxValueBytes);
                return;
            case float[] vector:
                WriteByte(destination, crc, (byte)ValueKind.Vector);
                if (vector.Length > MaxValueBytes / sizeof(float))
                    throw new InvalidDataException("物化视图向量值过大。");
                WriteInt32(destination, crc, vector.Length);
                foreach (float item in vector)
                    WriteInt32(destination, crc, BitConverter.SingleToInt32Bits(item));
                return;
            case GeoPoint point:
                WriteByte(destination, crc, (byte)ValueKind.GeoPoint);
                WriteDouble(destination, crc, point.Lat);
                WriteDouble(destination, crc, point.Lon);
                return;
            default:
                throw new InvalidDataException(
                    $"物化视图结果包含不支持的运行时类型 '{value.GetType().FullName}'。");
        }
    }

    private static object? ReadValue(Stream source, Crc32 crc, int rowIndex, int columnIndex)
    {
        var kind = (ValueKind)ReadByte(source, crc, $"row {rowIndex} column {columnIndex} kind");
        return kind switch
        {
            ValueKind.Null => null,
            ValueKind.Int64 => ReadInt64(source, crc, "int64 value"),
            ValueKind.UInt64 => ReadUInt64(source, crc, "uint64 value"),
            ValueKind.Float64 => ReadDouble(source, crc, "float64 value"),
            ValueKind.Decimal => ReadDecimal(source, crc),
            ValueKind.False => false,
            ValueKind.True => true,
            ValueKind.String => ReadString(source, crc, MaxValueBytes, "string value"),
            ValueKind.DateTime => ReadDateTime(source, crc),
            ValueKind.DateTimeOffset => ReadDateTimeOffset(source, crc),
            ValueKind.Blob => ReadBytes(source, crc, MaxValueBytes, "blob value"),
            ValueKind.Vector => ReadVector(source, crc),
            ValueKind.GeoPoint => GeoPoint.Create(
                ReadDouble(source, crc, "geopoint latitude"),
                ReadDouble(source, crc, "geopoint longitude")),
            _ => throw new InvalidDataException(
                $"MaterializedViewSnapshot: unknown value kind {(byte)kind} at row {rowIndex}, column {columnIndex}."),
        };
    }

    private static decimal ReadDecimal(Stream source, Crc32 crc)
    {
        try
        {
            return new decimal([
                ReadInt32(source, crc, "decimal lo"),
                ReadInt32(source, crc, "decimal mid"),
                ReadInt32(source, crc, "decimal hi"),
                ReadInt32(source, crc, "decimal flags"),
            ]);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("MaterializedViewSnapshot: invalid decimal bits.", exception);
        }
    }

    private static DateTime ReadDateTime(Stream source, Crc32 crc)
    {
        long ticks = ReadInt64(source, crc, "datetime ticks");
        byte rawKind = ReadByte(source, crc, "datetime kind");
        if (rawKind > (byte)DateTimeKind.Local)
            throw new InvalidDataException($"MaterializedViewSnapshot: invalid DateTime kind {rawKind}.");
        try
        {
            return new DateTime(ticks, (DateTimeKind)rawKind);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("MaterializedViewSnapshot: invalid DateTime ticks.", exception);
        }
    }

    private static DateTimeOffset ReadDateTimeOffset(Stream source, Crc32 crc)
    {
        long ticks = ReadInt64(source, crc, "datetimeoffset ticks");
        long offsetTicks = ReadInt64(source, crc, "datetimeoffset offset");
        try
        {
            return new DateTimeOffset(ticks, TimeSpan.FromTicks(offsetTicks));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("MaterializedViewSnapshot: invalid DateTimeOffset value.", exception);
        }
    }

    private static float[] ReadVector(Stream source, Crc32 crc)
    {
        int length = ReadInt32(source, crc, "vector length");
        if (length is < 0 || length > MaxValueBytes / sizeof(float))
            throw new InvalidDataException($"MaterializedViewSnapshot: invalid vector length {length}.");
        var vector = new float[length];
        for (int index = 0; index < length; index++)
            vector[index] = BitConverter.Int32BitsToSingle(ReadInt32(source, crc, $"vector item {index}"));
        return vector;
    }

    private static void WriteString(Stream destination, Crc32 crc, string value, int maxBytes)
    {
        int byteCount = Utf8.GetByteCount(value);
        if (byteCount > maxBytes)
            throw new InvalidDataException($"物化视图字符串值超过 {maxBytes} UTF-8 bytes。");
        WriteInt32(destination, crc, byteCount);
        if (byteCount == 0)
            return;
        WritePayload(destination, crc, Utf8.GetBytes(value));
    }

    private static string ReadString(Stream source, Crc32 crc, int maxBytes, string description)
    {
        byte[] bytes = ReadBytes(source, crc, maxBytes, description);
        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"MaterializedViewSnapshot: {description} is not valid UTF-8.", exception);
        }
    }

    private static void WriteBytes(Stream destination, Crc32 crc, byte[] value, int maxBytes)
    {
        if (value.Length > maxBytes)
            throw new InvalidDataException($"物化视图二进制值超过 {maxBytes} bytes。");
        WriteInt32(destination, crc, value.Length);
        WritePayload(destination, crc, value);
    }

    private static byte[] ReadBytes(Stream source, Crc32 crc, int maxBytes, string description)
    {
        int length = ReadInt32(source, crc, description + " length");
        if (length is < 0 || length > maxBytes)
            throw new InvalidDataException($"MaterializedViewSnapshot: invalid {description} length {length}.");
        var bytes = new byte[length];
        ReadPayload(source, crc, bytes, description);
        return bytes;
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

    private static void WriteUInt64(Stream destination, Crc32 crc, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        WritePayload(destination, crc, buffer);
    }

    private static ulong ReadUInt64(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadPayload(source, crc, buffer, description);
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void WriteDouble(Stream destination, Crc32 crc, double value)
        => WriteInt64(destination, crc, BitConverter.DoubleToInt64Bits(value));

    private static double ReadDouble(Stream source, Crc32 crc, string description)
        => BitConverter.Int64BitsToDouble(ReadInt64(source, crc, description));

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
                throw new InvalidDataException($"MaterializedViewSnapshot: {description} is truncated.");
            total += read;
        }
    }
}
