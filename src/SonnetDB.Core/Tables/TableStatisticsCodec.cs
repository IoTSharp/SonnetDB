using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace SonnetDB.Tables;

internal static class TableStatisticsCodec
{
    private const int FormatVersion = 1;
    private const int FingerprintLength = 32;
    private const int MaxColumns = 16_384;
    private const int MaxIndexes = 16_384;
    private const int MaxItemsPerColumn = 1_024;
    private const int MaxNameBytes = 16 * 1024;
    private static readonly byte[] Magic = "SDBTSTAT"u8.ToArray();
    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static byte[] Encode(TableSchema schema, TableStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(statistics);
        byte[] schemaFingerprint = TableStoreMaintenanceFile.ComputeSchemaFingerprint(schema);

        using var destination = new MemoryStream();
        destination.Write(Magic);
        WriteInt32(destination, FormatVersion);
        destination.Write(schemaFingerprint);
        WriteInt64(destination, statistics.Generation);
        WriteInt64(destination, statistics.SourceSequence);
        WriteInt64(destination, statistics.RefreshedAtUtc.UtcTicks);
        WriteInt64(destination, statistics.RowCount);
        WriteInt64(destination, statistics.LogicalPageCount);
        WriteDouble(destination, statistics.AverageRowWidth);
        WriteInt32(destination, statistics.SampledRows);
        WriteDouble(destination, statistics.SampleRate);
        destination.WriteByte(statistics.IsComplete ? (byte)1 : (byte)0);
        WriteInt32(destination, statistics.LogicalPageBytes);

        WriteInt32(destination, statistics.Columns.Count);
        foreach (TableColumnStatistics column in statistics.Columns)
        {
            WriteString(destination, column.ColumnName);
            WriteDouble(destination, column.NullFraction);
            WriteInt64(destination, column.EstimatedDistinctCount);
            WriteInt64(destination, column.SampledNonNullRows);
            WriteInt32(destination, column.MostCommonValues.Count);
            foreach (TableMostCommonValue value in column.MostCommonValues)
            {
                WriteUInt64(destination, value.Fingerprint);
                WriteInt64(destination, value.EstimatedRows);
            }

            WriteInt32(destination, column.Histogram.Count);
            foreach (TableHistogramBucket bucket in column.Histogram)
            {
                byte kind = bucket.Int64UpperBound.HasValue ? (byte)1 : (byte)2;
                destination.WriteByte(kind);
                if (kind == 1)
                    WriteInt64(destination, bucket.Int64UpperBound!.Value);
                else
                    WriteDouble(destination, bucket.Float64UpperBound ?? throw new InvalidDataException(
                        $"列 '{column.ColumnName}' 的 histogram bucket 缺少上界。"));
                WriteInt64(destination, bucket.EstimatedRows);
            }
        }

        WriteInt32(destination, statistics.Indexes.Count);
        foreach (TableIndexStatistics index in statistics.Indexes)
        {
            WriteString(destination, index.IndexName);
            WriteInt64(destination, index.RowCount);
            WriteInt64(destination, index.LogicalPageCount);
            WriteDouble(destination, index.AverageEntryWidth);
        }

        byte[] payload = destination.ToArray();
        var encoded = new byte[checked(payload.Length + sizeof(uint))];
        payload.CopyTo(encoded, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            encoded.AsSpan(payload.Length),
            Crc32.HashToUInt32(payload));
        return encoded;
    }

    internal static TableStatistics Decode(
        TableSchema schema,
        long expectedGeneration,
        ReadOnlySpan<byte> encoded)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (encoded.Length < Magic.Length + sizeof(int) + FingerprintLength + sizeof(uint))
            throw new InvalidDataException("Table statistics payload 被截断。");

        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(encoded[^sizeof(uint)..]);
        uint actualCrc = Crc32.HashToUInt32(encoded[..^sizeof(uint)]);
        if (storedCrc != actualCrc)
            throw new InvalidDataException("Table statistics CRC32 不匹配。");

        using var source = new MemoryStream(encoded[..^sizeof(uint)].ToArray(), writable: false);
        Span<byte> magic = stackalloc byte[Magic.Length];
        ReadExact(source, magic, "magic");
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("Table statistics magic 无效。");
        int version = ReadInt32(source, "version");
        if (version != FormatVersion)
            throw new InvalidDataException($"Table statistics 不支持格式版本 {version}。");

        Span<byte> fingerprint = stackalloc byte[FingerprintLength];
        ReadExact(source, fingerprint, "schema fingerprint");
        byte[] expectedFingerprint = TableStoreMaintenanceFile.ComputeSchemaFingerprint(schema);
        if (!fingerprint.SequenceEqual(expectedFingerprint))
            throw new InvalidDataException("Table statistics schema fingerprint 不匹配。");

        long generation = ReadNonNegativeInt64(source, "generation");
        if (generation != expectedGeneration)
            throw new InvalidDataException(
                $"Table statistics generation {generation} 与 rowstore {expectedGeneration} 不匹配。");
        long sourceSequence = ReadNonNegativeInt64(source, "source sequence");
        long refreshedTicks = ReadInt64(source, "refreshed utc ticks");
        DateTimeOffset refreshedAtUtc;
        try
        {
            refreshedAtUtc = new DateTimeOffset(refreshedTicks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("Table statistics refreshed time 无效。", exception);
        }

        long rowCount = ReadNonNegativeInt64(source, "row count");
        long logicalPageCount = ReadNonNegativeInt64(source, "logical page count");
        double averageRowWidth = ReadNonNegativeFiniteDouble(source, "average row width");
        int sampledRows = ReadNonNegativeInt32(source, "sampled rows");
        double sampleRate = ReadFraction(source, "sample rate");
        int complete = source.ReadByte();
        if (complete is not (0 or 1))
            throw new InvalidDataException("Table statistics complete 标记无效。");
        int logicalPageBytes = ReadPositiveInt32(source, "logical page bytes");

        int columnCount = ReadCount(source, "column count", MaxColumns);
        if (columnCount != schema.Columns.Count)
            throw new InvalidDataException("Table statistics 列数量与 schema 不匹配。");
        var columns = new TableColumnStatistics[columnCount];
        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            string name = ReadString(source, $"column {columnIndex} name");
            if (!string.Equals(name, schema.Columns[columnIndex].Name, StringComparison.Ordinal))
                throw new InvalidDataException($"Table statistics 列 '{name}' 与 schema 顺序不匹配。");
            double nullFraction = ReadFraction(source, $"column {name} null fraction");
            long distinct = ReadNonNegativeInt64(source, $"column {name} distinct");
            long sampledNonNull = ReadNonNegativeInt64(source, $"column {name} sampled non-null");

            int mcvCount = ReadCount(source, $"column {name} MCV count", MaxItemsPerColumn);
            var mostCommon = new TableMostCommonValue[mcvCount];
            for (int item = 0; item < mcvCount; item++)
            {
                mostCommon[item] = new TableMostCommonValue(
                    ReadUInt64(source, $"column {name} MCV fingerprint"),
                    ReadNonNegativeInt64(source, $"column {name} MCV rows"));
            }

            int histogramCount = ReadCount(source, $"column {name} histogram count", MaxItemsPerColumn);
            var histogram = new TableHistogramBucket[histogramCount];
            for (int item = 0; item < histogramCount; item++)
            {
                int kind = source.ReadByte();
                histogram[item] = kind switch
                {
                    1 => new TableHistogramBucket
                    {
                        Int64UpperBound = ReadInt64(source, $"column {name} histogram int64 bound"),
                        EstimatedRows = ReadNonNegativeInt64(source, $"column {name} histogram rows"),
                    },
                    2 => new TableHistogramBucket
                    {
                        Float64UpperBound = ReadFiniteDouble(source, $"column {name} histogram float bound"),
                        EstimatedRows = ReadNonNegativeInt64(source, $"column {name} histogram rows"),
                    },
                    _ => throw new InvalidDataException($"Table statistics 列 '{name}' histogram 类型 {kind} 无效。"),
                };
            }

            columns[columnIndex] = new TableColumnStatistics(
                name,
                nullFraction,
                distinct,
                sampledNonNull,
                mostCommon,
                histogram);
        }

        int indexCount = ReadCount(source, "index count", MaxIndexes);
        if (indexCount != schema.Indexes.Count)
            throw new InvalidDataException("Table statistics 索引数量与 schema 不匹配。");
        var indexes = new TableIndexStatistics[indexCount];
        for (int index = 0; index < indexCount; index++)
        {
            string name = ReadString(source, $"index {index} name");
            if (!string.Equals(name, schema.Indexes[index].Name, StringComparison.Ordinal))
                throw new InvalidDataException($"Table statistics 索引 '{name}' 与 schema 顺序不匹配。");
            indexes[index] = new TableIndexStatistics(
                name,
                ReadNonNegativeInt64(source, $"index {name} rows"),
                ReadNonNegativeInt64(source, $"index {name} logical pages"),
                ReadNonNegativeFiniteDouble(source, $"index {name} average width"));
        }

        if (source.Position != source.Length)
            throw new InvalidDataException("Table statistics 检测到尾随数据。");
        return new TableStatistics(
            generation,
            sourceSequence,
            refreshedAtUtc,
            rowCount,
            logicalPageCount,
            averageRowWidth,
            sampledRows,
            sampleRate,
            complete == 1,
            logicalPageBytes,
            columns,
            indexes);
    }

    private static int ReadCount(Stream source, string description, int maximum)
    {
        int count = ReadInt32(source, description);
        if (count is < 0 || count > maximum)
            throw new InvalidDataException($"Table statistics {description} {count} 超过上限 {maximum}。");
        return count;
    }

    private static int ReadNonNegativeInt32(Stream source, string description)
    {
        int value = ReadInt32(source, description);
        if (value < 0)
            throw new InvalidDataException($"Table statistics {description} 不能为负数。");
        return value;
    }

    private static int ReadPositiveInt32(Stream source, string description)
    {
        int value = ReadInt32(source, description);
        if (value <= 0)
            throw new InvalidDataException($"Table statistics {description} 必须为正数。");
        return value;
    }

    private static long ReadNonNegativeInt64(Stream source, string description)
    {
        long value = ReadInt64(source, description);
        if (value < 0)
            throw new InvalidDataException($"Table statistics {description} 不能为负数。");
        return value;
    }

    private static double ReadFraction(Stream source, string description)
    {
        double value = ReadFiniteDouble(source, description);
        if (value is < 0 or > 1)
            throw new InvalidDataException($"Table statistics {description} 必须位于 [0,1]。");
        return value;
    }

    private static double ReadNonNegativeFiniteDouble(Stream source, string description)
    {
        double value = ReadFiniteDouble(source, description);
        if (value < 0)
            throw new InvalidDataException($"Table statistics {description} 不能为负数。");
        return value;
    }

    private static double ReadFiniteDouble(Stream source, string description)
    {
        double value = ReadDouble(source, description);
        if (!double.IsFinite(value))
            throw new InvalidDataException($"Table statistics {description} 必须为有限数。");
        return value;
    }

    private static void WriteString(Stream destination, string value)
    {
        byte[] bytes = Utf8.GetBytes(value);
        if (bytes.Length > MaxNameBytes)
            throw new InvalidDataException($"Table statistics 名称 '{value}' 过长。");
        WriteInt32(destination, bytes.Length);
        destination.Write(bytes);
    }

    private static string ReadString(Stream source, string description)
    {
        int length = ReadInt32(source, description + " length");
        if (length is < 0 or > MaxNameBytes)
            throw new InvalidDataException($"Table statistics {description} 长度 {length} 无效。");
        byte[] bytes = new byte[length];
        ReadExact(source, bytes, description);
        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Table statistics {description} 不是有效 UTF-8。", exception);
        }
    }

    private static void WriteInt32(Stream destination, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        destination.Write(bytes);
    }

    private static int ReadInt32(Stream source, string description)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        ReadExact(source, bytes, description);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static void WriteInt64(Stream destination, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        destination.Write(bytes);
    }

    private static long ReadInt64(Stream source, string description)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        ReadExact(source, bytes, description);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    private static void WriteUInt64(Stream destination, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        destination.Write(bytes);
    }

    private static ulong ReadUInt64(Stream source, string description)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ReadExact(source, bytes, description);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteDouble(Stream destination, double value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(double)];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        destination.Write(bytes);
    }

    private static double ReadDouble(Stream source, string description)
    {
        Span<byte> bytes = stackalloc byte[sizeof(double)];
        ReadExact(source, bytes, description);
        return BinaryPrimitives.ReadDoubleLittleEndian(bytes);
    }

    private static void ReadExact(Stream source, Span<byte> destination, string description)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int current = source.Read(destination[read..]);
            if (current == 0)
                throw new InvalidDataException($"Table statistics {description} 被截断。");
            read += current;
        }
    }

    private static void ReadExact(Stream source, byte[] destination, string description)
        => ReadExact(source, destination.AsSpan(), description);
}
