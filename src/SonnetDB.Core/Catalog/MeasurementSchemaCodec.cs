using System.Buffers;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using SonnetDB.IO;
using SonnetDB.Storage.Format;

namespace SonnetDB.Catalog;

/// <summary>
/// Measurement schema 文件（<c>measurements.tslschema</c>）的序列化与反序列化器。
/// <para>
/// 物理布局（二进制，little-endian）：
/// <code>
/// MeasurementSchemaHeader (32B)
///   Magic = "SDBMEAv1"      (8B)
///   FormatVersion = 1       (4B)
///   HeaderSize = 32         (4B)
///   MeasurementCount        (4B)
///   Reserved                (12B)
///
/// MeasurementSchema[N]（每条变长）
///   uint16 NameUtf8Length              (2B)
///   byte[] NameUtf8                    变长
///   long   CreatedAtUtcTicks           (8B)
///   uint16 ColumnCount                 (2B)
///   Column[ColumnCount]:
///     uint16 ColumnNameUtf8Length      (2B)
///     byte[] ColumnNameUtf8            变长
///     byte   Role                      (1B)  0=Tag, 1=Field
///     byte   DataType                  (1B)  SonnetDB.Storage.Format.FieldType
///     // PR #58 b（v2 起）：仅当 DataType == Vector(5) 时追加：
///     int32  VectorDimension           (4B)  必须 > 0
///     // PR #61（v3 起）：仅当 DataType == Vector(5) 时追加；v4 起 HNSW 继续兼容 v3 布局，新增算法追加参数块：
///     byte   VectorIndexKind           (1B)  0=None, 1=Hnsw
///     // #223（v5 起）：仅当 VectorIndexKind != 0 时，在 kind 字节之后、kind 参数之前追加一个度量字节：
///     byte   VectorMetric              (1B)  0=Cosine, 1=L2, 2=InnerProduct
///     // 若 VectorIndexKind == 1(Hnsw)：
///     int32  HnswM                     (4B)
///     int32  HnswEf                    (4B)  efSearch
///     // #223（v5 起）：HNSW 追加 efConstruction，与 efSearch 解耦：
///     int32  HnswEfConstruction        (4B)
///     // 若 FormatVersion >= 4 且 VectorIndexKind in (2,3,4)：
///     byte   ParameterLength           (1B)
///     byte[] ParameterPayload          变长，little-endian int32/float32
///
/// MeasurementSchemaFooter (16B)
///   Crc32                              (4B)  整个 MeasurementSchema[] 区域的 CRC32
///   Magic = "SDBMEAv1"                 (8B)
///   Reserved                           (4B)
/// </code>
/// </para>
/// <para>写入策略：临时文件 + 原子 rename（崩溃安全）。</para>
/// <para>
/// 版本兼容：当前写入版本为 v5（见 <see cref="_formatVersion"/>）。v1 文件只能包含非 VECTOR 列；v2
/// 支持 VECTOR(dim) 但不含索引声明；v3 支持 HNSW；v4 支持 IVF / IVF-PQ / Vamana；v5 为所有向量索引
/// 持久化距离度量（metric）并为 HNSW 追加独立的 efConstruction（缺陷 I7 / I9）。读取 v&lt;5 文件时
/// metric 默认 cosine、HNSW efConstruction 取 <c>max(ef, 200)</c>，与旧行为一致。
/// </para>
/// </summary>
public static class MeasurementSchemaCodec
{
    /// <summary>schema 文件名。</summary>
    public const string FileName = "measurements.tslschema";

    private static readonly byte[] _magic = "SDBMEAv1"u8.ToArray();
    private static readonly Encoding _utf8 = Encoding.UTF8;

    private const int _formatVersion = 5;
    private const int _headerSize = 32;
    private const int _footerSize = 16;

    /// <summary>
    /// 从指定路径加载 schema 列表；文件不存在时返回空列表。
    /// </summary>
    /// <param name="path">schema 文件完整路径。</param>
    /// <returns>schema 只读列表；文件不存在时返回空列表。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> 为 null。</exception>
    /// <exception cref="InvalidDataException">Magic / Version / Crc32 校验失败时抛出。</exception>
    public static IReadOnlyList<MeasurementSchema> Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return [];

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(fs);
    }

    /// <summary>
    /// 把 schema 列表持久化到指定路径（临时文件 + 原子 rename + fsync）。
    /// </summary>
    /// <param name="path">目标文件完整路径。</param>
    /// <param name="schemas">要持久化的 schema 列表。</param>
    /// <param name="tempSuffix">临时文件后缀（默认 <c>".tmp"</c>）。</param>
    /// <exception cref="ArgumentNullException">任何参数为 null。</exception>
    public static void Save(string path, IReadOnlyList<MeasurementSchema> schemas, string tempSuffix = ".tmp")
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(tempSuffix);

        string tmpPath = path + tempSuffix;

        using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bs = new BufferedStream(fs, 65536))
        {
            Save(schemas, bs);
            bs.Flush();
            fs.Flush(true);
        }

        File.Move(tmpPath, path, overwrite: true);
        // 目录 fsync：使原子改名对崩溃/掉电可见（measurement schema 与 CREATE 语义的崩溃安全性）。#189
        SonnetDB.Wal.DirectoryFsync.FlushBestEffort(Path.GetDirectoryName(path) ?? string.Empty);
    }

    // ── 私有实现 ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<MeasurementSchema> Load(Stream source)
    {
        byte[] headerBuf = ArrayPool<byte>.Shared.Rent(_headerSize);
        try
        {
            int read = ReadExact(source, headerBuf, 0, _headerSize);
            if (read < _headerSize)
                throw new InvalidDataException("MeasurementSchema: header is truncated.");

            var headerReader = new SpanReader(headerBuf.AsSpan(0, _headerSize));
            ReadOnlySpan<byte> magic = headerReader.ReadBytes(8);
            if (!magic.SequenceEqual(_magic))
                throw new InvalidDataException("MeasurementSchema: invalid magic in header.");

            int version = headerReader.ReadInt32();
            if (version is < 1 or > _formatVersion)
                throw new InvalidDataException($"MeasurementSchema: unsupported format version {version}.");

            int hSize = headerReader.ReadInt32();
            if (hSize != _headerSize)
                throw new InvalidDataException($"MeasurementSchema: unexpected header size {hSize}.");

            int measurementCount = headerReader.ReadInt32();
            if (measurementCount < 0)
                throw new InvalidDataException("MeasurementSchema: negative measurement count.");

            var crcHasher = new Crc32();
            var result = new List<MeasurementSchema>(measurementCount);

            for (int i = 0; i < measurementCount; i++)
                result.Add(ReadMeasurement(source, crcHasher, i, version));

            // Footer
            byte[] footerBuf = ArrayPool<byte>.Shared.Rent(_footerSize);
            try
            {
                int footerRead = ReadExact(source, footerBuf, 0, _footerSize);
                if (footerRead < _footerSize)
                    throw new InvalidDataException("MeasurementSchema: footer is truncated.");

                uint storedCrc = MemoryMarshal.Read<uint>(footerBuf.AsSpan(0, 4));
                ReadOnlySpan<byte> footerMagic = footerBuf.AsSpan(4, 8);
                if (!footerMagic.SequenceEqual(_magic))
                    throw new InvalidDataException("MeasurementSchema: invalid magic in footer.");

                uint computedCrc = crcHasher.GetCurrentHashAsUInt32();
                if (computedCrc != storedCrc)
                    throw new InvalidDataException(
                        $"MeasurementSchema: CRC32 mismatch (expected 0x{storedCrc:X8}, got 0x{computedCrc:X8}).");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(footerBuf);
            }

            return result.AsReadOnly();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuf);
        }
    }

    private static MeasurementSchema ReadMeasurement(Stream source, Crc32 crc, int index, int version)
    {
        // Name: uint16 length + bytes
        string name = ReadString(source, crc, fieldDescription: $"measurement {index} name");

        // CreatedAt: long (8B)
        Span<byte> createdBuf = stackalloc byte[8];
        ReadExactSpan(source, createdBuf, $"measurement {index} createdAt");
        crc.Append(createdBuf);
        long createdAt = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(createdBuf);

        // ColumnCount: uint16 (2B)
        Span<byte> countBuf = stackalloc byte[2];
        ReadExactSpan(source, countBuf, $"measurement {index} columnCount");
        crc.Append(countBuf);
        int columnCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(countBuf);

        var columns = new List<MeasurementColumn>(columnCount);
        Span<byte> roleAndType = stackalloc byte[2];
        Span<byte> dimBuf = stackalloc byte[4];
        Span<byte> indexKindBuf = stackalloc byte[1];
        Span<byte> hnswBuf = stackalloc byte[8];
        Span<byte> efcBuf = stackalloc byte[4];
        Span<byte> parameterLengthBuf = stackalloc byte[1];
        for (int c = 0; c < columnCount; c++)
        {
            string colName = ReadString(source, crc, fieldDescription: $"measurement {index} column {c} name");

            ReadExactSpan(source, roleAndType, $"measurement {index} column {c} role/type");
            crc.Append(roleAndType);

            byte roleByte = roleAndType[0];
            byte typeByte = roleAndType[1];

            if (roleByte > 1)
                throw new InvalidDataException(
                    $"MeasurementSchema: invalid column role {roleByte} (measurement '{name}', column {c}).");

            var role = (MeasurementColumnRole)roleByte;
            var dataType = (FieldType)typeByte;
            if (!Enum.IsDefined(dataType))
                throw new InvalidDataException(
                    $"MeasurementSchema: invalid column data type {typeByte} (measurement '{name}', column '{colName}').");

            int? vectorDim = null;
            VectorIndexDefinition? vectorIndex = null;
            if (dataType == FieldType.Vector)
            {
                if (version < 2)
                    throw new InvalidDataException(
                        $"MeasurementSchema: VECTOR column '{colName}' requires format version >= 2 (file version {version}).");
                ReadExactSpan(source, dimBuf, $"measurement {index} column {c} vector dim");
                crc.Append(dimBuf);
                int dim = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(dimBuf);
                if (dim <= 0)
                    throw new InvalidDataException(
                        $"MeasurementSchema: invalid VECTOR dimension {dim} (measurement '{name}', column '{colName}').");
                vectorDim = dim;

                if (version >= 3)
                {
                    ReadExactSpan(source, indexKindBuf, $"measurement {index} column {c} vector index kind");
                    crc.Append(indexKindBuf);
                    byte indexKind = indexKindBuf[0];

                    // #223（v5 起）：非 None 索引在 kind 之后追加一个 metric 字节；v<5 默认 cosine。
                    var metric = SonnetDB.Query.KnnMetric.Cosine;
                    if (version >= 5 && indexKind != 0)
                    {
                        ReadExactSpan(source, indexKindBuf, $"measurement {index} column {c} vector metric");
                        crc.Append(indexKindBuf);
                        byte metricByte = indexKindBuf[0];
                        if (metricByte > (byte)SonnetDB.Query.KnnMetric.InnerProduct)
                            throw new InvalidDataException(
                                $"MeasurementSchema: invalid vector metric {metricByte} (measurement '{name}', column '{colName}').");
                        metric = (SonnetDB.Query.KnnMetric)metricByte;
                    }

                    switch (indexKind)
                    {
                        case 0:
                            break;

                        case (byte)VectorIndexKind.Hnsw:
                            ReadExactSpan(source, hnswBuf, $"measurement {index} column {c} hnsw options");
                            crc.Append(hnswBuf);
                            int m = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(hnswBuf[..4]);
                            int ef = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(hnswBuf[4..]);
                            // #223（v5 起）：追加独立的 efConstruction；v<5 取 max(ef, 200) 复刻旧行为。
                            int efConstruction = Math.Max(ef, 200);
                            if (version >= 5)
                            {
                                ReadExactSpan(source, efcBuf, $"measurement {index} column {c} hnsw efConstruction");
                                crc.Append(efcBuf);
                                efConstruction = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(efcBuf);
                                if (efConstruction <= 0)
                                    throw new InvalidDataException(
                                        $"MeasurementSchema: invalid HNSW efConstruction {efConstruction} (measurement '{name}', column '{colName}').");
                            }
                            vectorIndex = VectorIndexDefinition.CreateHnsw(m, ef, metric, efConstruction);
                            break;

                        case (byte)VectorIndexKind.IvfFlat when version >= 4:
                            vectorIndex = ReadIvfIndexDefinition(source, crc, parameterLengthBuf, index, c) with { Metric = metric };
                            break;

                        case (byte)VectorIndexKind.IvfPq when version >= 4:
                            vectorIndex = ReadIvfPqIndexDefinition(source, crc, parameterLengthBuf, index, c) with { Metric = metric };
                            break;

                        case (byte)VectorIndexKind.Vamana when version >= 4:
                            vectorIndex = ReadVamanaIndexDefinition(source, crc, parameterLengthBuf, index, c) with { Metric = metric };
                            break;

                        default:
                            throw new InvalidDataException(
                                $"MeasurementSchema: invalid vector index kind {indexKind} (measurement '{name}', column '{colName}').");
                    }
                }
            }

            columns.Add(new MeasurementColumn(colName, role, dataType, vectorDim, vectorIndex));
        }

        return MeasurementSchema.Create(name, columns, createdAt);
    }

    private static void Save(IReadOnlyList<MeasurementSchema> schemas, Stream destination)
    {
        // Header
        byte[] headerBuf = ArrayPool<byte>.Shared.Rent(_headerSize);
        try
        {
            headerBuf.AsSpan(0, _headerSize).Clear();
            var writer = new SpanWriter(headerBuf.AsSpan(0, _headerSize));
            writer.WriteBytes(_magic);
            writer.WriteInt32(_formatVersion);
            writer.WriteInt32(_headerSize);
            writer.WriteInt32(schemas.Count);
            destination.Write(headerBuf, 0, _headerSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuf);
        }

        var crcHasher = new Crc32();

        foreach (var schema in schemas)
            WriteMeasurement(destination, schema, crcHasher);

        // Footer
        uint crc = crcHasher.GetCurrentHashAsUInt32();
        byte[] footerBuf = ArrayPool<byte>.Shared.Rent(_footerSize);
        try
        {
            footerBuf.AsSpan(0, _footerSize).Clear();
            var writer = new SpanWriter(footerBuf.AsSpan(0, _footerSize));
            writer.WriteUInt32(crc);
            writer.WriteBytes(_magic);
            destination.Write(footerBuf, 0, _footerSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(footerBuf);
        }
    }

    private static void WriteMeasurement(Stream destination, MeasurementSchema schema, Crc32 crc)
    {
        int nameLen = _utf8.GetByteCount(schema.Name);
        if (nameLen > ushort.MaxValue)
            throw new InvalidDataException($"Measurement '{schema.Name}' 名称过长（{nameLen} 字节，最大 {ushort.MaxValue}）。");

        // Compute total size for measurement record
        int totalSize = 2 + nameLen + 8 + 2; // nameLen + name + createdAt + columnCount

        var columnSizes = new int[schema.Columns.Count];
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            int colNameLen = _utf8.GetByteCount(schema.Columns[i].Name);
            if (colNameLen > ushort.MaxValue)
                throw new InvalidDataException(
                    $"Column '{schema.Columns[i].Name}' 名称过长（{colNameLen} 字节，最大 {ushort.MaxValue}）。");
            int colSize = 2 + colNameLen + 1 + 1; // nameLen + name + role + type
            if (schema.Columns[i].DataType == FieldType.Vector)
            {
                colSize += 4; // VectorDimension (int32)
                colSize += 1; // VectorIndexKind
                var vectorIndex = schema.Columns[i].VectorIndex;
                if (vectorIndex is not null)
                    colSize += GetVectorIndexParameterSize(vectorIndex);
            }
            columnSizes[i] = colSize;
            totalSize += colSize;
        }

        byte[] buf = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            buf.AsSpan(0, totalSize).Clear();
            var writer = new SpanWriter(buf.AsSpan(0, totalSize));

            writer.WriteUInt16((ushort)nameLen);
            int written = _utf8.GetBytes(schema.Name, writer.FreeSpan);
            writer.Advance(written);

            writer.WriteInt64(schema.CreatedAtUtcTicks);
            writer.WriteUInt16((ushort)schema.Columns.Count);

            for (int i = 0; i < schema.Columns.Count; i++)
            {
                var col = schema.Columns[i];
                int colNameLen = _utf8.GetByteCount(col.Name);
                writer.WriteUInt16((ushort)colNameLen);
                int colWritten = _utf8.GetBytes(col.Name, writer.FreeSpan);
                writer.Advance(colWritten);
                writer.WriteByte((byte)col.Role);
                writer.WriteByte((byte)col.DataType);
                if (col.DataType == FieldType.Vector)
                {
                    int dim = col.VectorDimension
                        ?? throw new InvalidDataException(
                            $"VECTOR column '{col.Name}' missing dimension when persisting schema.");
                    writer.WriteInt32(dim);
                    byte indexKind = col.VectorIndex is null ? (byte)0 : (byte)col.VectorIndex.Kind;
                    writer.WriteByte(indexKind);
                    if (col.VectorIndex is not null)
                        WriteVectorIndexParameters(writer, col.VectorIndex);
                }
            }

            crc.Append(buf.AsSpan(0, totalSize));
            destination.Write(buf, 0, totalSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static VectorIndexDefinition ReadIvfIndexDefinition(
        Stream source,
        Crc32 crc,
        Span<byte> parameterLengthBuf,
        int measurementIndex,
        int columnIndex)
    {
        Span<byte> payload = stackalloc byte[12];
        ReadVectorIndexPayload(source, crc, parameterLengthBuf, payload, measurementIndex, columnIndex, "ivf");
        return VectorIndexDefinition.CreateIvfFlat(
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[..4]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[4..8]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[8..12]));
    }

    private static VectorIndexDefinition ReadIvfPqIndexDefinition(
        Stream source,
        Crc32 crc,
        Span<byte> parameterLengthBuf,
        int measurementIndex,
        int columnIndex)
    {
        Span<byte> payload = stackalloc byte[20];
        ReadVectorIndexPayload(source, crc, parameterLengthBuf, payload, measurementIndex, columnIndex, "ivf_pq");
        return VectorIndexDefinition.CreateIvfPq(
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[..4]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[4..8]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[8..12]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[12..16]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[16..20]));
    }

    private static VectorIndexDefinition ReadVamanaIndexDefinition(
        Stream source,
        Crc32 crc,
        Span<byte> parameterLengthBuf,
        int measurementIndex,
        int columnIndex)
    {
        Span<byte> payload = stackalloc byte[16];
        ReadVectorIndexPayload(source, crc, parameterLengthBuf, payload, measurementIndex, columnIndex, "vamana");
        return VectorIndexDefinition.CreateVamana(
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[..4]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[4..8]),
            System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(payload[8..12]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[12..16]));
    }

    private static void ReadVectorIndexPayload(
        Stream source,
        Crc32 crc,
        Span<byte> parameterLengthBuf,
        Span<byte> payload,
        int measurementIndex,
        int columnIndex,
        string indexName)
    {
        ReadExactSpan(source, parameterLengthBuf, $"measurement {measurementIndex} column {columnIndex} {indexName} option length");
        crc.Append(parameterLengthBuf);
        int length = parameterLengthBuf[0];
        if (length != payload.Length)
            throw new InvalidDataException(
                $"MeasurementSchema: invalid {indexName} option length {length} (expected {payload.Length}).");
        ReadExactSpan(source, payload, $"measurement {measurementIndex} column {columnIndex} {indexName} options");
        crc.Append(payload);
    }

    private static int GetVectorIndexParameterSize(VectorIndexDefinition index)
        // #223：所有非 None 索引在 kind 之后先写 1 字节 metric；HNSW 参数块因追加 efConstruction 从 8→12。
        => 1 + index.Kind switch
        {
            VectorIndexKind.Hnsw => 12,
            VectorIndexKind.IvfFlat => 1 + 12,
            VectorIndexKind.IvfPq => 1 + 20,
            VectorIndexKind.Vamana => 1 + 16,
            _ => throw new InvalidDataException($"Unsupported vector index kind {index.Kind}."),
        };

    private static void WriteVectorIndexParameters(SpanWriter writer, VectorIndexDefinition index)
    {
        // #223：kind 字节已在调用方写出；此处先写 metric 字节，再写 kind 专属参数。
        writer.WriteByte((byte)index.Metric);
        switch (index.Kind)
        {
            case VectorIndexKind.Hnsw:
                var hnsw = index.Hnsw ?? throw new InvalidDataException("HNSW vector index options are missing.");
                writer.WriteInt32(hnsw.M);
                writer.WriteInt32(hnsw.Ef);
                writer.WriteInt32(hnsw.EfConstruction);
                break;

            case VectorIndexKind.IvfFlat:
                var ivf = index.Ivf ?? throw new InvalidDataException("IVF vector index options are missing.");
                writer.WriteByte(12);
                writer.WriteInt32(ivf.NList);
                writer.WriteInt32(ivf.NProbe);
                writer.WriteInt32(ivf.MaxIterations);
                break;

            case VectorIndexKind.IvfPq:
                var ivfPq = index.IvfPq ?? throw new InvalidDataException("IVF-PQ vector index options are missing.");
                writer.WriteByte(20);
                writer.WriteInt32(ivfPq.NList);
                writer.WriteInt32(ivfPq.NProbe);
                writer.WriteInt32(ivfPq.MaxIterations);
                writer.WriteInt32(ivfPq.M);
                writer.WriteInt32(ivfPq.NBits);
                break;

            case VectorIndexKind.Vamana:
                var vamana = index.Vamana ?? throw new InvalidDataException("Vamana vector index options are missing.");
                writer.WriteByte(16);
                writer.WriteInt32(vamana.MaxDegree);
                writer.WriteInt32(vamana.SearchListSize);
                writer.WriteSingle(vamana.Alpha);
                writer.WriteInt32(vamana.BeamWidth);
                break;

            default:
                throw new InvalidDataException($"Unsupported vector index kind {index.Kind}.");
        }
    }

    private static string ReadString(Stream source, Crc32 crc, string fieldDescription)
    {
        Span<byte> lenBuf = stackalloc byte[2];
        ReadExactSpan(source, lenBuf, fieldDescription + " length");
        crc.Append(lenBuf);
        int len = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(lenBuf);
        if (len == 0)
            return string.Empty;

        byte[] buf = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            int read = ReadExact(source, buf, 0, len);
            if (read < len)
                throw new InvalidDataException($"MeasurementSchema: {fieldDescription} is truncated.");
            crc.Append(buf.AsSpan(0, len));
            return _utf8.GetString(buf, 0, len);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void ReadExactSpan(Stream source, Span<byte> buffer, string fieldDescription)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = source.Read(buffer[total..]);
            if (read == 0)
                throw new InvalidDataException($"MeasurementSchema: {fieldDescription} is truncated.");
            total += read;
        }
    }

    private static int ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }
}
