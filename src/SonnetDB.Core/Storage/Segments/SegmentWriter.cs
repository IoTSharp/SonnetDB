using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Storage.Segments;

/// <summary>
/// 不可变 Segment 文件构建器：把 <see cref="MemTable"/> 中的有序 (SeriesId, FieldName) 桶
/// 写成 <c>.SDBSEG</c> 文件，使用临时文件 + 原子 rename 保证崩溃安全。
/// <para>
/// 单次构建一次性生效（不支持增量写入；要新增数据请构建新的 Segment）。
/// </para>
/// <para>
/// 文件物理布局（所有多字节整数使用 little-endian）：
/// <code>
/// ┌─────────────────────────────────────────────────────────────────┐
/// │  SegmentHeader  (固定 64 字节，offset = 0)                       │
/// │    Magic = "SDBSEGv1"                                           │
/// │    FormatVersion = 6                                        │
/// │    SegmentId                                                    │
/// │    CreatedAtUtcTicks                                            │
/// │    BlockCount（回填）                                            │
/// ├─────────────────────────────────────────────────────────────────┤
/// │  Block 1 ... Block N  （每个 (SeriesId, FieldName) 桶 = 1 Block） │
/// │  ┌───────────────────────────────────────────────────────────┐  │
/// │  │ BlockHeader (固定 80 字节)                                  │  │
/// │  │   SeriesId / Min/MaxTimestamp / Count                      │  │
/// │  │   FieldNameUtf8Length / TimestampPayloadLength             │  │
/// │  │   ValuePayloadLength / Encoding=None / FieldType           │  │
/// │  │   Crc32 = CRC32(FieldNameUtf8 ++ TsPayload ++ ValPayload)  │  │
/// │  ├───────────────────────────────────────────────────────────┤  │
/// │  │ FieldNameUtf8          (变长)                               │  │
/// │  ├───────────────────────────────────────────────────────────┤  │
/// │  │ TimestampPayload       (Count × 8B int64 LE)               │  │
/// │  ├───────────────────────────────────────────────────────────┤  │
/// │  │ ValuePayload           (按 FieldType 编码)                  │  │
/// │  │   Float64: Count×8B  Int64: Count×8B  Bool: Count×1B      │  │
/// │  │   String: 重复 Count 次 (int32 len + UTF-8 bytes)           │  │
/// │  └───────────────────────────────────────────────────────────┘  │
/// ├─────────────────────────────────────────────────────────────────┤
/// │  BlockIndexEntry[BlockCount]  (每项 48 字节)                     │
/// │    SeriesId / Min/MaxTimestamp / FileOffset / BlockLength       │
/// │    FieldNameHash = XxHash32(FieldNameUtf8)                      │
/// ├─────────────────────────────────────────────────────────────────┤
/// │  v6 Embedded Extension Area（可为空）                            │
/// │    SDBVIDX1 section: HNSW VECTOR block index                    │
/// │    SDBAIDX1 section: TDigest / HyperLogLog aggregate sketch      │
/// ├─────────────────────────────────────────────────────────────────┤
/// │  SegmentFooter  (固定 64 字节；v6 起 Header 保留区含 mini-footer 摘要副本) │
/// │    Magic = "SDBSEGv1"                                           │
/// │    IndexOffset / IndexCount                                     │
/// │    FileLength  (= FooterOffset + 64)                            │
/// │    Crc32 = CRC32(整个 BlockIndexEntry[] 字节)                    │
/// └─────────────────────────────────────────────────────────────────┘
///
/// 不变量：
///   1. IndexOffset == SegmentHeaderSize + Σ BlockLength
///   2. IndexOffset + IndexCount × 48 <= FooterOffset
///   3. FooterOffset + 64 == FileLength
///   4. BlockIndexEntry[i].FileOffset 指向第 i 个 BlockHeader 起点
///   5. 文件以 SegmentFooter.Magic == "SDBSEGv1" 收尾；v6 Header mini-footer 与 Footer 摘要一致
///   6. v6 起不再为新段写 `.SDBVIDX` / `.SDBAIDX` sidecar；旧 sidecar 仅读取兼容
/// </code>
/// </para>
/// </summary>
public sealed class SegmentWriter
{
    /// <summary>段文件扩展名。</summary>
    public const string FileExtension = ".SDBSEG";

    private readonly SegmentWriterOptions _options;

    /// <summary>
    /// 创建 <see cref="SegmentWriter"/> 实例。
    /// </summary>
    /// <param name="options">写入选项；为 null 时使用 <see cref="SegmentWriterOptions.Default"/>。</param>
    public SegmentWriter(SegmentWriterOptions? options = null)
    {
        _options = options ?? SegmentWriterOptions.Default;
    }

    /// <summary>
    /// 直接把 <see cref="MemTable"/> 写到指定路径。
    /// </summary>
    /// <param name="memTable">要写入的 MemTable 实例。</param>
    /// <param name="segmentId">段唯一标识符（单调递增）。</param>
    /// <param name="path">目标文件路径（扩展名通常为 <c>.SDBSEG</c>）。</param>
    /// <returns>构建结果，含文件路径、Block 数量、时间范围、偏移等信息。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="memTable"/> 或 <paramref name="path"/> 为 null。</exception>
    public SegmentBuildResult WriteFrom(
        MemTable memTable,
        long segmentId,
        string path,
        IReadOnlyDictionary<SeriesFieldKey, SonnetDB.Catalog.VectorIndexDefinition>? vectorIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(memTable);
        return Write(memTable.SnapshotAll(), segmentId, path, vectorIndexes);
    }

    /// <summary>
    /// 通用入口：从外部提供有序桶序列写入。供测试与未来的 Compaction 复用。
    /// </summary>
    /// <param name="series">要写入的 <see cref="MemTableSeries"/> 列表（允许包含空桶，将被自动过滤）。</param>
    /// <param name="segmentId">段唯一标识符（单调递增）。</param>
    /// <param name="path">目标文件路径（扩展名通常为 <c>.SDBSEG</c>）。</param>
    /// <returns>构建结果，含文件路径、Block 数量、时间范围、偏移等信息。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> 或 <paramref name="path"/> 为 null。</exception>
    /// <exception cref="IOException">临时文件已存在（路径冲突）或 IO 错误时抛出。</exception>
    public SegmentBuildResult Write(
        IReadOnlyList<MemTableSeries> series,
        long segmentId,
        string path,
        IReadOnlyDictionary<SeriesFieldKey, SonnetDB.Catalog.VectorIndexDefinition>? vectorIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(path);

        // Sort by (SeriesId, FieldName Ordinal)，过滤空桶
        var sorted = new List<MemTableSeries>(series.Count);
        foreach (var s in series)
        {
            if (s.Count > 0)
                sorted.Add(s);
        }
        sorted.Sort(static (a, b) =>
        {
            int cmp = a.Key.SeriesId.CompareTo(b.Key.SeriesId);
            return cmp != 0 ? cmp : string.Compare(a.Key.FieldName, b.Key.FieldName, StringComparison.Ordinal);
        });

        string tempPath = path + _options.TempFileSuffix;
        var sw = Stopwatch.StartNew();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // State tracked across phases (declared before try so accessible in return)
        long segMinTs = long.MaxValue;
        long segMaxTs = long.MinValue;
        long indexOffset = 0L;
        long footerOffset = 0L;
        var indexEntries = new List<BlockIndexEntry>(sorted.Count);
        var vectorIndexBlocks = new List<VectorIndexBlock>();
        var aggregateSketchBlocks = new List<BlockAggregateSketch>();
        bool tempFileCreated = false;
        string vectorIndexPath = SonnetDB.Engine.TsdbPaths.VectorIndexPathForSegment(path);
        string aggregateIndexPath = SonnetDB.Engine.TsdbPaths.AggregateIndexPathForSegment(path);

        try
        {
            using var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            tempFileCreated = true;
            var bs = new BufferedStream(fs, _options.BufferSize);

            // ─── 阶段 A：写占位 SegmentHeader ──────────────────────────────
            var placeholderHeader = SegmentHeader.CreateNew(segmentId);
            WriteStructToStream(bs, in placeholderHeader);
            long currentOffset = FormatSizes.SegmentHeaderSize;

            // ─── 阶段 B：逐 Block 写入 ─────────────────────────────────────
            foreach (var bucket in sorted)
            {
                // 崩溃注入钩子（仅测试使用）
                _options.FailAt?.Invoke(currentOffset);

                long blockOffset = currentOffset;
                var points = bucket.Snapshot();

                // 编码字段名 UTF-8
                int fieldNameMaxBytes = Encoding.UTF8.GetMaxByteCount(bucket.Key.FieldName.Length);
                byte[] fieldNameBuf = ArrayPool<byte>.Shared.Rent(Math.Max(fieldNameMaxBytes, 1));
                try
                {
                    int fieldNameLen = Encoding.UTF8.GetBytes(bucket.Key.FieldName, fieldNameBuf);
                    ReadOnlySpan<byte> fieldNameSpan = fieldNameBuf.AsSpan(0, fieldNameLen);

                    // 编码时间戳载荷：根据 SegmentWriterOptions.TimestampEncoding 选择 V1 或 V2 编码
                    bool useDeltaTs = (_options.TimestampEncoding & BlockEncoding.DeltaTimestamp) != 0
                        && points.Length > 0;

                    int tsPayloadLen;
                    if (useDeltaTs)
                    {
                        // 先把时间戳收集到临时 long[]，再调用 TimestampCodec
                        long[] tsArr = ArrayPool<long>.Shared.Rent(points.Length);
                        try
                        {
                            for (int i = 0; i < points.Length; i++)
                                tsArr[i] = points.Span[i].Timestamp;
                            tsPayloadLen = TimestampCodec.MeasureDeltaOfDelta(tsArr.AsSpan(0, points.Length));
                            byte[] tsBuf = ArrayPool<byte>.Shared.Rent(Math.Max(tsPayloadLen, 1));
                            try
                            {
                                if (tsPayloadLen > 0)
                                    TimestampCodec.WriteDeltaOfDelta(tsArr.AsSpan(0, points.Length), tsBuf.AsSpan(0, tsPayloadLen));
                                ReadOnlySpan<byte> tsSpan = tsBuf.AsSpan(0, tsPayloadLen);
                                WriteOneBlock(bs, bucket, points, fieldNameSpan, tsSpan, BlockEncoding.DeltaTimestamp,
                                    blockOffset, indexEntries, vectorIndexBlocks, aggregateSketchBlocks, vectorIndexes,
                                    ref segMinTs, ref segMaxTs, ref currentOffset);
                            }
                            finally { ArrayPool<byte>.Shared.Return(tsBuf); }
                        }
                        finally { ArrayPool<long>.Shared.Return(tsArr); }
                        continue;
                    }

                    // V1：Count × 8B int64 LE
                    tsPayloadLen = points.Length * 8;
                    byte[] tsBufV1 = ArrayPool<byte>.Shared.Rent(Math.Max(tsPayloadLen, 1));
                    try
                    {
                        if (tsPayloadLen > 0)
                        {
                            var tsWriter = new IO.SpanWriter(tsBufV1.AsSpan(0, tsPayloadLen));
                            foreach (var dp in points.Span)
                                tsWriter.WriteInt64(dp.Timestamp);
                        }

                        ReadOnlySpan<byte> tsSpan = tsBufV1.AsSpan(0, tsPayloadLen);
                        WriteOneBlock(bs, bucket, points, fieldNameSpan, tsSpan, BlockEncoding.None,
                            blockOffset, indexEntries, vectorIndexBlocks, aggregateSketchBlocks, vectorIndexes,
                            ref segMinTs, ref segMaxTs, ref currentOffset);
                    }
                    finally { ArrayPool<byte>.Shared.Return(tsBufV1); }
                }
                finally { ArrayPool<byte>.Shared.Return(fieldNameBuf); }
            }

            // ─── 阶段 C：写 BlockIndexEntry[] + 计算 IndexCrc32 ────────────
            indexOffset = currentOffset;
            var indexCrc = new Crc32();
            foreach (var entry in indexEntries)
                WriteStructToStreamAndHash(bs, in entry, indexCrc);

            uint indexCrc32 = indexCrc.GetCurrentHashAsUInt32();
            currentOffset = indexOffset + (long)indexEntries.Count * FormatSizes.BlockIndexEntrySize;

            // ─── 阶段 D：写 v6 内嵌扩展区（替代旧 .SDBVIDX / .SDBAIDX sidecar）────────────
            if (vectorIndexBlocks.Count > 0)
            {
                long before = bs.Position;
                SegmentVectorIndexFile.WriteTo(bs, vectorIndexBlocks);
                currentOffset += bs.Position - before;
            }

            if (aggregateSketchBlocks.Count > 0)
            {
                long before = bs.Position;
                SegmentAggregateSketchFile.WriteTo(bs, aggregateSketchBlocks);
                currentOffset += bs.Position - before;
            }

            // ─── 阶段 E：写 SegmentFooter ──────────────────────────────────
            footerOffset = currentOffset;
            long fileLength = footerOffset + FormatSizes.SegmentFooterSize;

            var footer = SegmentFooter.CreateNew(indexEntries.Count, indexOffset, fileLength);
            footer.Crc32 = indexCrc32;
            footer.ComputeAndSetFooterChecksum(); // #195：footer 自校验，检出满足布局等式的字段位翻转
            WriteStructToStream(bs, in footer);

            // ─── 阶段 F：Seek(0) 回填 SegmentHeader ───────────────────────
            bs.Flush();
            bs.Seek(0, SeekOrigin.Begin);

            var finalHeader = SegmentHeader.CreateNew(segmentId);
            finalHeader.BlockCount = indexEntries.Count;
            finalHeader.WriteFooterMiniCopy(indexEntries.Count, indexOffset, fileLength, indexCrc32);
            WriteStructToStream(bs, in finalHeader);

            bs.Flush();
            if (_options.FsyncOnCommit)
                fs.Flush(true);

            bs.Dispose();
            // using var fs ensures fs is disposed when the try block exits (idempotent if bs already closed it)
        }
        catch
        {
            // 仅删除我们创建的临时文件，不删除原本已存在的文件
            if (tempFileCreated)
                try { File.Delete(tempPath); } catch { }
            throw;
        }

        // 原子替换：临时文件 → 目标文件
        File.Move(tempPath, path, overwrite: true);
        try { File.Delete(vectorIndexPath); } catch { }
        try { File.Delete(aggregateIndexPath); } catch { }

        // 崩溃注入钩子（仅测试使用）：rename 完成后、Checkpoint 写入之前
        _options.PostRenameAction?.Invoke();

        sw.Stop();
        long durationMicros = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

        return new SegmentBuildResult(
            Path: path,
            SegmentId: segmentId,
            BlockCount: indexEntries.Count,
            TotalBytes: new FileInfo(path).Length,
            MinTimestamp: segMinTs,
            MaxTimestamp: segMaxTs,
            IndexOffset: indexOffset,
            FooterOffset: footerOffset,
            DurationMicros: durationMicros);
    }

    // ── 私有辅助 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 把已编码的时间戳载荷连同字段名/值载荷写入流，并构造对应的 BlockHeader 与 BlockIndexEntry。
    /// </summary>
    private void WriteOneBlock(
        Stream bs,
        MemTableSeries bucket,
        ReadOnlyMemory<SonnetDB.Model.DataPoint> points,
        ReadOnlySpan<byte> fieldNameSpan,
        ReadOnlySpan<byte> tsSpan,
        BlockEncoding tsEncoding,
        long blockOffset,
        List<BlockIndexEntry> indexEntries,
        List<VectorIndexBlock> vectorIndexBlocks,
        List<BlockAggregateSketch> aggregateSketchBlocks,
        IReadOnlyDictionary<SeriesFieldKey, SonnetDB.Catalog.VectorIndexDefinition>? vectorIndexes,
        ref long segMinTs,
        ref long segMaxTs,
        ref long currentOffset)
    {
        // VECTOR / GEOPOINT 类型固定走 V1 raw 编码（V2 / DeltaValue 不支持复合定长类型）。
        bool isVector = bucket.FieldType == FieldType.Vector;
        bool isGeoPoint = bucket.FieldType == FieldType.GeoPoint;
        bool useV2Val = !isVector && !isGeoPoint
            && (_options.ValueEncoding & BlockEncoding.DeltaValue) != 0
            && points.Length > 0;
        int valPayloadLen = useV2Val
            ? ValuePayloadCodecV2.Measure(bucket.FieldType, points)
            : ValuePayloadCodec.MeasureValuePayload(bucket.FieldType, points);
        byte[] valBuf = ArrayPool<byte>.Shared.Rent(Math.Max(valPayloadLen, 1));
        try
        {
            if (valPayloadLen > 0)
            {
                if (useV2Val)
                    ValuePayloadCodecV2.Write(bucket.FieldType, points, valBuf.AsSpan(0, valPayloadLen));
                else
                    ValuePayloadCodec.WritePayload(bucket.FieldType, points, valBuf.AsSpan(0, valPayloadLen));
            }

            ReadOnlySpan<byte> valSpan = valBuf.AsSpan(0, valPayloadLen);

            // CRC32(FieldNameUtf8 ++ TsPayload ++ ValPayload)
            var blockCrc = new Crc32();
            blockCrc.Append(fieldNameSpan);
            blockCrc.Append(tsSpan);
            blockCrc.Append(valSpan);
            uint crc32 = blockCrc.GetCurrentHashAsUInt32();

            int fieldNameHash = FieldNameHash.Compute(fieldNameSpan);

            int blockLength = FormatSizes.BlockHeaderSize + fieldNameSpan.Length + tsSpan.Length + valSpan.Length;
            var bh = BlockHeader.CreateNew(
                seriesId: bucket.Key.SeriesId,
                min: bucket.MinTimestamp,
                max: bucket.MaxTimestamp,
                count: points.Length,
                fieldType: bucket.FieldType,
                fieldNameLen: fieldNameSpan.Length,
                tsLen: tsSpan.Length,
                valLen: valSpan.Length);
            bh.Crc32 = crc32;
            BlockEncoding valueEncodingFlag = useV2Val
                ? BlockEncoding.DeltaValue
                : (isVector ? BlockEncoding.VectorRaw : (isGeoPoint ? BlockEncoding.GeoPointRaw : BlockEncoding.None));
            bh.Encoding = tsEncoding | valueEncodingFlag;
            short aggregateFlags = TryBuildAggregateMetadata(
                bucket.FieldType, points.Span,
                out var aggregateSum, out var aggregateMin, out var aggregateMax);
            if (aggregateFlags != 0)
            {
                bh.AggregateFlags = aggregateFlags;
                bh.AggregateSum = aggregateSum;
                bh.AggregateMin = aggregateMin;
                bh.AggregateMax = aggregateMax;
            }

            if (isGeoPoint && TryBuildGeoHashRange(points.Span, out var geoHashMin, out var geoHashMax))
            {
                bh.GeoHashMin = geoHashMin;
                bh.GeoHashMax = geoHashMax;
            }

            WriteStructToStream(bs, in bh);
            bs.Write(fieldNameSpan);
            bs.Write(tsSpan);
            bs.Write(valSpan);

            if (bucket.MinTimestamp < segMinTs) segMinTs = bucket.MinTimestamp;
            if (bucket.MaxTimestamp > segMaxTs) segMaxTs = bucket.MaxTimestamp;

            int blockIndex = indexEntries.Count;
            indexEntries.Add(new BlockIndexEntry
            {
                SeriesId = bucket.Key.SeriesId,
                MinTimestamp = bucket.MinTimestamp,
                MaxTimestamp = bucket.MaxTimestamp,
                FileOffset = blockOffset,
                BlockLength = blockLength,
                FieldNameHash = fieldNameHash,
            });

            if (isVector
                && vectorIndexes is not null
                && vectorIndexes.TryGetValue(bucket.Key, out var vectorIndex)
                && IsPersistableVectorIndex(vectorIndex))
            {
                int dimension = points.Span[0].Value.VectorDimension;
                var buildResult = new LocalVectorIndexBuilderAdapter().Build(new VectorIndexBuildInput(
                    blockIndex,
                    points,
                    vectorIndex));
                byte[] blob = [];
                uint blobCrc32 = 0;
                var flags = VectorIndexManifestFlags.RebuildFromBlockPayload;
                if (vectorIndex.Kind == SonnetDB.Catalog.VectorIndexKind.Hnsw)
                {
                    using var blobStream = new MemoryStream();
                    blobCrc32 = LocalVectorIndexBuilderAdapter.WriteBlob(blobStream, buildResult.Reader);
                    blob = blobStream.ToArray();
                    flags |= VectorIndexManifestFlags.PersistentBlob;
                }

                var metadata = new VectorIndexBlockMetadata(
                    blockIndex,
                    points.Length,
                    dimension,
                    (int)vectorIndex.Kind,
                    GetManifestM(vectorIndex),
                    GetManifestEf(vectorIndex),
                    GetManifestExtra1(vectorIndex),
                    GetManifestExtra2(vectorIndex),
                    GetManifestExtra3(vectorIndex),
                    crc32,
                    BlobOffset: 0,
                    BlobLength: blob.Length,
                    BlobCrc32: blobCrc32,
                    flags,
                    Metric: (int)vectorIndex.Metric,
                    EfConstruction: GetManifestEfConstruction(vectorIndex));
                vectorIndexBlocks.Add(new VectorIndexBlock(metadata, blob));
            }

            if (BlockAggregateSketch.TryBuild(
                    blockIndex,
                    crc32,
                    bucket.FieldType,
                    points.Span,
                    out var aggregateSketch))
            {
                aggregateSketchBlocks.Add(aggregateSketch);
            }

            currentOffset += blockLength;
        }
        finally { ArrayPool<byte>.Shared.Return(valBuf); }
    }

    private static bool IsPersistableVectorIndex(SonnetDB.Catalog.VectorIndexDefinition vectorIndex)
        => vectorIndex.Kind is SonnetDB.Catalog.VectorIndexKind.Hnsw
            or SonnetDB.Catalog.VectorIndexKind.IvfFlat
            or SonnetDB.Catalog.VectorIndexKind.IvfPq
            or SonnetDB.Catalog.VectorIndexKind.Vamana;

    private static int GetManifestM(SonnetDB.Catalog.VectorIndexDefinition vectorIndex)
        => vectorIndex.Kind switch
        {
            SonnetDB.Catalog.VectorIndexKind.Hnsw => vectorIndex.Hnsw?.M ?? throw new InvalidDataException("HNSW vector index options are missing."),
            SonnetDB.Catalog.VectorIndexKind.IvfFlat => vectorIndex.Ivf?.NList ?? throw new InvalidDataException("IVF vector index options are missing."),
            SonnetDB.Catalog.VectorIndexKind.IvfPq => vectorIndex.IvfPq?.NList ?? throw new InvalidDataException("IVF-PQ vector index options are missing."),
            SonnetDB.Catalog.VectorIndexKind.Vamana => vectorIndex.Vamana?.MaxDegree ?? throw new InvalidDataException("Vamana vector index options are missing."),
            _ => throw new InvalidDataException($"Unsupported vector index kind {vectorIndex.Kind}."),
        };

    private static int GetManifestEf(SonnetDB.Catalog.VectorIndexDefinition vectorIndex)
        => vectorIndex.Kind switch
        {
            SonnetDB.Catalog.VectorIndexKind.Hnsw => vectorIndex.Hnsw?.Ef ?? throw new InvalidDataException("HNSW vector index options are missing."),
            SonnetDB.Catalog.VectorIndexKind.IvfFlat => vectorIndex.Ivf?.NProbe ?? throw new InvalidDataException("IVF vector index options are missing."),
            SonnetDB.Catalog.VectorIndexKind.IvfPq => vectorIndex.IvfPq?.NProbe ?? throw new InvalidDataException("IVF-PQ vector index options are missing."),
            SonnetDB.Catalog.VectorIndexKind.Vamana => vectorIndex.Vamana?.SearchListSize ?? throw new InvalidDataException("Vamana vector index options are missing."),
            _ => throw new InvalidDataException($"Unsupported vector index kind {vectorIndex.Kind}."),
        };

    private static int GetManifestExtra1(SonnetDB.Catalog.VectorIndexDefinition vectorIndex)
        => vectorIndex.Kind switch
        {
            SonnetDB.Catalog.VectorIndexKind.IvfFlat => vectorIndex.Ivf?.MaxIterations ?? throw new InvalidDataException("IVF vector index options are missing."),
            SonnetDB.Catalog.VectorIndexKind.IvfPq => vectorIndex.IvfPq?.MaxIterations ?? throw new InvalidDataException("IVF-PQ vector index options are missing."),
            SonnetDB.Catalog.VectorIndexKind.Vamana => BitConverter.SingleToInt32Bits(vectorIndex.Vamana?.Alpha ?? throw new InvalidDataException("Vamana vector index options are missing.")),
            _ => 0,
        };

    private static int GetManifestExtra2(SonnetDB.Catalog.VectorIndexDefinition vectorIndex)
        => vectorIndex.Kind switch
        {
            SonnetDB.Catalog.VectorIndexKind.IvfPq => vectorIndex.IvfPq?.M ?? throw new InvalidDataException("IVF-PQ vector index options are missing."),
            SonnetDB.Catalog.VectorIndexKind.Vamana => vectorIndex.Vamana?.BeamWidth ?? throw new InvalidDataException("Vamana vector index options are missing."),
            _ => 0,
        };

    private static int GetManifestExtra3(SonnetDB.Catalog.VectorIndexDefinition vectorIndex)
        => vectorIndex.Kind == SonnetDB.Catalog.VectorIndexKind.IvfPq
            ? vectorIndex.IvfPq?.NBits ?? throw new InvalidDataException("IVF-PQ vector index options are missing.")
            : 0;

    // #223：仅 HNSW 有独立的 efConstruction 需持久化到 SDBVIDX；其他算法为 0（不使用）。
    private static int GetManifestEfConstruction(SonnetDB.Catalog.VectorIndexDefinition vectorIndex)
        => vectorIndex.Kind == SonnetDB.Catalog.VectorIndexKind.Hnsw
            ? vectorIndex.Hnsw?.EfConstruction ?? throw new InvalidDataException("HNSW vector index options are missing.")
            : 0;

    /// <summary>
    /// 计算给定数据点的聚合元数据。
    /// 返回值是写入 <see cref="BlockHeader.AggregateFlags"/> 的位掩码（0 表示不写元数据）。
    /// </summary>
    /// <remarks>
    /// v2 起 <see cref="BlockHeader.AggregateMin"/> / <see cref="BlockHeader.AggregateMax"/> 持久化为 8 字节 <see cref="double"/>，
    /// 数值类型一律计算 <c>sum</c>（按 <see cref="double"/> 累加，与查询路径精度一致）并置 <see cref="BlockHeader.HasSumCount"/>，
    /// 同时无损写入 <c>min</c>/<c>max</c> 并置 <see cref="BlockHeader.HasMinMax"/>。
    /// </remarks>
    private static short TryBuildAggregateMetadata(
        FieldType fieldType,
        ReadOnlySpan<DataPoint> points,
        out double sum,
        out double min,
        out double max)
    {
        sum = 0;
        min = 0;
        max = 0;

        if (points.IsEmpty)
            return 0;

        switch (fieldType)
        {
            case FieldType.Float64:
                {
                    double mn = double.PositiveInfinity;
                    double mx = double.NegativeInfinity;
                    for (int i = 0; i < points.Length; i++)
                    {
                        double value = points[i].Value.AsDouble();
                        sum += value;
                        if (value < mn) mn = value;
                        if (value > mx) mx = value;
                    }
                    min = mn;
                    max = mx;
                    return BlockHeader.HasSumCount | BlockHeader.HasMinMax;
                }

            case FieldType.Int64:
                {
                    long mn = long.MaxValue;
                    long mx = long.MinValue;
                    for (int i = 0; i < points.Length; i++)
                    {
                        long value = points[i].Value.AsLong();
                        sum += value;
                        if (value < mn) mn = value;
                        if (value > mx) mx = value;
                    }
                    // Int64 → double：±2^53 之外可能损失精度，sum 已是 double，min/max 这里同样转 double。
                    // 写入路径之外（查询路径）始终把整数值视作 double 比较，因此 min/max 仍可信。
                    min = mn;
                    max = mx;
                    return BlockHeader.HasSumCount | BlockHeader.HasMinMax;
                }

            case FieldType.Boolean:
                {
                    int mn = 1;
                    int mx = 0;
                    for (int i = 0; i < points.Length; i++)
                    {
                        int value = points[i].Value.AsBool() ? 1 : 0;
                        sum += value;
                        if (value < mn) mn = value;
                        if (value > mx) mx = value;
                    }
                    min = mn;
                    max = mx;
                    return BlockHeader.HasSumCount | BlockHeader.HasMinMax;
                }

            default:
                return 0;
        }
    }

    private static bool TryBuildGeoHashRange(
        ReadOnlySpan<DataPoint> points,
        out uint min,
        out uint max)
    {
        min = uint.MaxValue;
        max = uint.MinValue;

        if (points.IsEmpty)
            return false;

        for (int i = 0; i < points.Length; i++)
        {
            uint hash = GeoHash32.Encode(points[i].Value.AsGeoPoint());
            if (hash < min) min = hash;
            if (hash > max) max = hash;
        }

        return true;
    }

    /// <summary>将 unmanaged 结构体序列化写入流。</summary>
    private static void WriteStructToStream<T>(Stream stream, in T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        byte[] buf = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            MemoryMarshal.Write(buf.AsSpan(0, size), in value);
            stream.Write(buf, 0, size);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>将 unmanaged 结构体序列化写入流，同时追加到 CRC32 计算器。</summary>
    private static void WriteStructToStreamAndHash<T>(Stream stream, in T value, Crc32 crc) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        byte[] buf = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            MemoryMarshal.Write(buf.AsSpan(0, size), in value);
            stream.Write(buf, 0, size);
            crc.Append(buf.AsSpan(0, size));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
