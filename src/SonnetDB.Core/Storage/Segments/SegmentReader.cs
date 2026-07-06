using System.Buffers.Binary;
using System.Collections.Frozen;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using SonnetDB.Buffers;
using SonnetDB.Catalog;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Storage.Segments;

/// <summary>
/// 不可变 Segment 文件的只读访问器。
/// <para>
/// Open 时解析索引；Block payload 解码按需进行。默认路径保留整段 <c>byte[]</c> 读取，
/// 可通过 <see cref="SegmentReaderOptions.UseMemoryMappedFileForLargeSegments"/> 对大段启用 safe-only mmap 读取路径。
/// </para>
/// <para>
/// 默认仍选择 <see cref="File.ReadAllBytes"/> 整段加载策略，原因：
/// <list type="bullet">
///   <item><description><c>.SDBSEG</c> 体量 v1 通常 &lt; 16 MB（受 MemTableFlushPolicy.MaxBytes 限制）。</description></item>
///   <item><description><c>byte[]</c> reader 可为 <see cref="ReadBlock"/> 提供零拷贝 span。</description></item>
///   <item><description>mmap 路径完全通过 <c>MemoryMappedViewAccessor</c> 安全复制，不使用 unsafe 指针。</description></item>
/// </list>
/// 注意：v1 仅支持 little-endian 主机字节序。
/// </para>
/// </summary>
public sealed class SegmentReader : IDisposable
{
    private static readonly HnswVectorIndexCache SharedVectorIndexCache = new();
    private static readonly IReadOnlyDictionary<int, long> EmptyVectorIndexOffsets = new Dictionary<int, long>();
    private static readonly IReadOnlyDictionary<int, long> EmptyAggregateSketchOffsets = new Dictionary<int, long>();

    private SegmentByteSource? _source;
    private readonly SegmentReaderOptions _options;
    private readonly BlockDescriptor[] _blocks;
    private readonly FrozenDictionary<ulong, BlockDescriptor[]> _blocksBySeries;
    private readonly BlockTimeRangeIndex _blocksByTimeRange;
    private readonly BlockDecodeCache? _decodeCache;
    private readonly HnswVectorIndexCache? _vectorIndexCache;
    private readonly long _embeddedExtensionOffset;
    private readonly long _embeddedExtensionLength;
    private readonly object _vectorIndexLoadLock = new();
    private readonly object _aggregateSketchLoadLock = new();
    private IReadOnlyDictionary<int, long>? _vectorIndexOffsetsByBlock;
    private IReadOnlyDictionary<int, long>? _aggregateSketchOffsetsByBlock;
    private bool _vectorIndexOffsetsEmbedded;
    private bool _aggregateSketchOffsetsEmbedded;
    private volatile bool _vectorIndexOffsetsLoaded;
    private volatile bool _aggregateSketchOffsetsLoaded;

    /// <summary>段文件路径。</summary>
    public string Path { get; }

    /// <summary>段文件头部。</summary>
    public SegmentHeader Header { get; }

    /// <summary>段文件尾部。</summary>
    public SegmentFooter Footer { get; }

    /// <summary>段文件内的 Block 数量。</summary>
    public int BlockCount => _blocks.Length;

    /// <summary>段文件总字节数。</summary>
    public long FileLength { get; }

    /// <summary>段内所有 Block 中最小的时间戳（毫秒 UTC）；无 Block 时为 <see cref="long.MaxValue"/>。</summary>
    public long MinTimestamp { get; }

    /// <summary>段内所有 Block 中最大的时间戳（毫秒 UTC）；无 Block 时为 <see cref="long.MinValue"/>。</summary>
    public long MaxTimestamp { get; }

    /// <summary>所有 <see cref="BlockDescriptor"/>，按写入顺序（即 (SeriesId, FieldName) 升序）。</summary>
    public IReadOnlyList<BlockDescriptor> Blocks => _blocks;

    /// <summary>
    /// 计算段文件的编码 / 字节统计快照（PR #31）。按需遍历所有 <see cref="BlockDescriptor"/>，
    /// 不缓存；适合运维巡检与基准测试输出，亦可用于压缩率对比（同一数据用 V1 与 V2 写入后对比 payload 字节）。
    /// </summary>
    /// <returns>包含 Block 数、点数、字段名/时间戳/值载荷字节、按编码与按 <see cref="FieldType"/> 分组的统计。</returns>
    public SegmentStats GetStats()
    {
        int totalPoints = 0;
        long fieldNameBytes = 0L;
        long tsBytes = 0L;
        long valBytes = 0L;
        int rawTs = 0, deltaTs = 0, rawVal = 0, deltaVal = 0;

        var byField = new Dictionary<FieldType, (int blocks, int points, long valBytes, int deltaVal)>();

        foreach (var b in _blocks)
        {
            totalPoints += b.Count;
            fieldNameBytes += b.FieldNameUtf8Length;
            tsBytes += b.TimestampPayloadLength;
            valBytes += b.ValuePayloadLength;

            if ((b.TimestampEncoding & BlockEncoding.DeltaTimestamp) != 0) deltaTs++;
            else rawTs++;

            bool isDeltaVal = (b.ValueEncoding & BlockEncoding.DeltaValue) != 0;
            if (isDeltaVal) deltaVal++;
            else rawVal++;

            byField.TryGetValue(b.FieldType, out var s);
            s.blocks++;
            s.points += b.Count;
            s.valBytes += b.ValuePayloadLength;
            if (isDeltaVal) s.deltaVal++;
            byField[b.FieldType] = s;
        }

        var byFieldDict = new Dictionary<FieldType, FieldTypeStats>(byField.Count);
        foreach (var (ft, s) in byField)
            byFieldDict[ft] = new FieldTypeStats(s.blocks, s.points, s.valBytes, s.deltaVal);

        return new SegmentStats
        {
            BlockCount = _blocks.Length,
            TotalPointCount = totalPoints,
            TotalFieldNameBytes = fieldNameBytes,
            TotalTimestampPayloadBytes = tsBytes,
            TotalValuePayloadBytes = valBytes,
            RawTimestampBlocks = rawTs,
            DeltaTimestampBlocks = deltaTs,
            RawValueBlocks = rawVal,
            DeltaValueBlocks = deltaVal,
            ByFieldType = byFieldDict,
        };
    }

    private SegmentReader(
        string path,
        SegmentByteSource source,
        SegmentHeader header,
        SegmentFooter footer,
        BlockDescriptor[] blocks,
        FrozenDictionary<ulong, BlockDescriptor[]> blocksBySeries,
        BlockTimeRangeIndex blocksByTimeRange,
        long embeddedExtensionOffset,
        long embeddedExtensionLength,
        SegmentReaderOptions options)
    {
        Path = path;
        _source = source;
        Header = header;
        Footer = footer;
        _blocks = blocks;
        _blocksBySeries = blocksBySeries;
        _blocksByTimeRange = blocksByTimeRange;
        _embeddedExtensionOffset = embeddedExtensionOffset;
        _embeddedExtensionLength = embeddedExtensionLength;
        FileLength = source.Length;
        _options = options;
        _decodeCache = options.DecodeBlockCacheMaxBytes > 0
            ? new BlockDecodeCache(options.DecodeBlockCacheMaxBytes)
            : null;
        _vectorIndexCache = options.VectorIndexCacheMaxBytes > 0
            ? SharedVectorIndexCache
            : null;
        _vectorIndexCache?.TrimToBudget(options.VectorIndexCacheMaxBytes);

        long minTs = long.MaxValue;
        long maxTs = long.MinValue;
        foreach (var b in blocks)
        {
            if (b.MinTimestamp < minTs) minTs = b.MinTimestamp;
            if (b.MaxTimestamp > maxTs) maxTs = b.MaxTimestamp;
        }
        MinTimestamp = minTs;
        MaxTimestamp = maxTs;
    }

    /// <summary>
    /// 打开并解析段文件。
    /// </summary>
    /// <param name="path">段文件路径（通常扩展名为 <c>.SDBSEG</c>）。</param>
    /// <param name="options">读取选项；为 null 时使用 <see cref="SegmentReaderOptions.Default"/>。</param>
    /// <returns>已初始化的 <see cref="SegmentReader"/> 实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> 为 null。</exception>
    /// <exception cref="SegmentCorruptedException">文件格式不合法或校验失败时抛出。</exception>
    /// <exception cref="IOException">文件 IO 错误时抛出。</exception>
    public static SegmentReader Open(string path, SegmentReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= SegmentReaderOptions.Default;

        var source = OpenByteSource(path, options);
        try
        {
            return OpenCore(path, source, options);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static SegmentReader OpenCore(
        string path,
        SegmentByteSource source,
        SegmentReaderOptions options)
    {
        long length = source.Length;
        int minLen = FormatSizes.SegmentHeaderSize + FormatSizes.SegmentFooterSize;

        if (length < FormatSizes.SegmentHeaderSize)
            throw new SegmentCorruptedException(path, 0,
                $"文件过短（{length} 字节），最小需要完整 SegmentHeader {FormatSizes.SegmentHeaderSize} 字节。");

        // 读 SegmentHeader（offset 0）
        var header = MemoryMarshal.Read<SegmentHeader>(source.ReadSpan(0, FormatSizes.SegmentHeaderSize));

        if (!header.IsCompatibleForRead())
            throw new SegmentCorruptedException(path, 0,
                $"SegmentHeader Magic 或 FormatVersion 不匹配（Magic={Encoding.ASCII.GetString(header.Magic.AsReadOnlySpan())}, " +
                $"Version={header.FormatVersion}，期望 v{TsdbMagic.SegmentFormatVersion}，兼容版本 v{string.Join("/v", TsdbMagic.SupportedSegmentFormatVersions.ToArray())}）。" +
                "SonnetDB v2（PR #50）将 BlockHeader.AggregateMin/Max 升级为 8 字节 double，BlockHeader 大小由 64B 增至 72B；" +
                "v3（PR #58 c）新增 BlockEncoding.VectorRaw 与 FieldType.Vector，仅在使用 VECTOR 列时落盘；" +
                "v4（PR #70）新增 BlockEncoding.GeoPointRaw 与 FieldType.GeoPoint，仅在使用 GEOPOINT 列时落盘；" +
                "v5（PR #76）将 BlockHeader 扩展为 80B 并新增 GeoHashMin/GeoHashMax；" +
                "v6 将 HNSW/聚合 sketch section 内嵌到 .SDBSEG，并在 SegmentHeader 写入 mini-footer；" +
                "旧 v1 段文件需通过重放 WAL（删除 .SDBSEG 后启动）重新生成。");

        if (header.HeaderSize != FormatSizes.SegmentHeaderSize)
            throw new SegmentCorruptedException(path, 0,
                $"SegmentHeader.HeaderSize={header.HeaderSize} 不等于预期值 {FormatSizes.SegmentHeaderSize}。");

        if (length < minLen)
            throw new SegmentCorruptedException(path, length,
                BuildTooShortSegmentMessage(header, length, minLen));

        if (!TryReadPrimaryFooter(path, source, length, out var footer, out long footerStart, out string primaryFooterError)
            && !TryReadFooterFromHeaderMiniCopy(path, header, length, out footer, out footerStart, out string miniFooterError))
        {
            throw new SegmentCorruptedException(path, length - FormatSizes.SegmentFooterSize,
                $"{primaryFooterError}；{miniFooterError}");
        }

        long indexEndOffset = footer.IndexOffset + (long)footer.IndexCount * FormatSizes.BlockIndexEntrySize;
        long embeddedExtensionOffset = indexEndOffset;
        long embeddedExtensionLength = footerStart - indexEndOffset;

        // 读 BlockIndexEntry[]
        long indexByteLenLong = (long)footer.IndexCount * FormatSizes.BlockIndexEntrySize;
        if (indexByteLenLong > int.MaxValue)
            throw new SegmentCorruptedException(path, (long)footer.IndexOffset,
                $"BlockIndexEntry 区域过大：{indexByteLenLong} 字节，超过单次安全读取上限 {int.MaxValue}。");

        int indexByteLen = (int)indexByteLenLong;
        ReadOnlySpan<byte> indexBytes = source.ReadSpan(footer.IndexOffset, indexByteLen);
        ReadOnlySpan<BlockIndexEntry> indexEntries = MemoryMarshal.Cast<byte, BlockIndexEntry>(indexBytes);

        // 校验 IndexCrc32
        if (options.VerifyIndexCrc && footer.IndexCount > 0)
        {
            uint computedCrc = Crc32.HashToUInt32(indexBytes);
            if (computedCrc != footer.Crc32)
                throw new SegmentCorruptedException(path, (long)footer.IndexOffset,
                    $"BlockIndexEntry[] CRC32 校验失败（期望 0x{footer.Crc32:X8}，实际 0x{computedCrc:X8}）。");
        }

        // 遍历 BlockHeader 构建 BlockDescriptor[]
        var blocks = new BlockDescriptor[footer.IndexCount];
        for (int i = 0; i < footer.IndexCount; i++)
        {
            BlockIndexEntry entry = indexEntries[i];
            long headerStart = entry.FileOffset;

            int blockHeaderSize = header.FormatVersion >= 5
                ? FormatSizes.BlockHeaderSize
                : FormatSizes.LegacyBlockHeaderSizeV4;

            if (headerStart + blockHeaderSize > length)
                throw new SegmentCorruptedException(path, headerStart,
                    $"BlockIndexEntry[{i}].FileOffset={headerStart} 指向越界区域。");

            var bh = ReadBlockHeader(source.ReadSpan(headerStart, blockHeaderSize), blockHeaderSize);

            // 校验 BlockHeader 与 IndexEntry 一致性
            if (bh.SeriesId != entry.SeriesId)
                throw new SegmentCorruptedException(path, headerStart,
                    $"Block[{i}] SeriesId 不一致：BlockHeader={bh.SeriesId}, IndexEntry={entry.SeriesId}。");

            if (bh.MinTimestamp != entry.MinTimestamp)
                throw new SegmentCorruptedException(path, headerStart,
                    $"Block[{i}] MinTimestamp 不一致：BlockHeader={bh.MinTimestamp}, IndexEntry={entry.MinTimestamp}。");

            if (bh.MaxTimestamp != entry.MaxTimestamp)
                throw new SegmentCorruptedException(path, headerStart,
                    $"Block[{i}] MaxTimestamp 不一致：BlockHeader={bh.MaxTimestamp}, IndexEntry={entry.MaxTimestamp}。");

            // 校验 BlockLength 一致性
            int expectedBlockLen = blockHeaderSize
                + bh.FieldNameUtf8Length
                + bh.TimestampPayloadLength
                + bh.ValuePayloadLength;

            if (expectedBlockLen != entry.BlockLength)
                throw new SegmentCorruptedException(path, headerStart,
                    $"Block[{i}] BlockLength 不一致：根据 BlockHeader 计算 {expectedBlockLen}，IndexEntry={entry.BlockLength}。");

            // 读字段名
            long fieldNameStart = headerStart + blockHeaderSize;
            if (fieldNameStart + bh.FieldNameUtf8Length > length)
                throw new SegmentCorruptedException(path, fieldNameStart,
                    $"Block[{i}] FieldName 区域越界。");

            string fieldName = Encoding.UTF8.GetString(
                source.ReadSpan(fieldNameStart, bh.FieldNameUtf8Length));

            blocks[i] = new BlockDescriptor
            {
                Index = i,
                SeriesId = bh.SeriesId,
                MinTimestamp = bh.MinTimestamp,
                MaxTimestamp = bh.MaxTimestamp,
                Count = bh.Count,
                FieldType = bh.FieldType,
                TimestampEncoding = (bh.Encoding & BlockEncoding.DeltaTimestamp) != 0
                    ? BlockEncoding.DeltaTimestamp
                    : BlockEncoding.None,
                ValueEncoding = (bh.Encoding & BlockEncoding.DeltaValue) != 0
                    ? BlockEncoding.DeltaValue
                    : ((bh.Encoding & BlockEncoding.VectorRaw) != 0
                        ? BlockEncoding.VectorRaw
                        : ((bh.Encoding & BlockEncoding.GeoPointRaw) != 0
                            ? BlockEncoding.GeoPointRaw
                            : BlockEncoding.None)),
                FieldName = fieldName,
                FileOffset = headerStart,
                BlockLength = entry.BlockLength,
                Crc32 = bh.Crc32,
                HasAggregateSumCount = (bh.AggregateFlags & BlockHeader.HasSumCount) != 0,
                HasAggregateMinMax = (bh.AggregateFlags & BlockHeader.HasMinMax) != 0,
                AggregateSum = bh.AggregateSum,
                AggregateMin = bh.AggregateMin,
                AggregateMax = bh.AggregateMax,
                GeoHashMin = bh.GeoHashMin,
                GeoHashMax = bh.GeoHashMax,
                FieldNameUtf8Length = bh.FieldNameUtf8Length,
                TimestampPayloadLength = bh.TimestampPayloadLength,
                ValuePayloadLength = bh.ValuePayloadLength,
                HeaderSize = blockHeaderSize,
            };
        }

        var blocksBySeries = BuildBlocksBySeriesIndex(blocks);
        var blocksByTimeRange = BlockTimeRangeIndex.Build(blocks);
        return new SegmentReader(
            path,
            source,
            header,
            footer,
            blocks,
            blocksBySeries,
            blocksByTimeRange,
            embeddedExtensionOffset,
            embeddedExtensionLength,
            options);
    }

    /// <summary>
    /// 按 SeriesId 过滤；返回属于该序列的所有 <see cref="BlockDescriptor"/>（按写入顺序）。
    /// </summary>
    /// <param name="seriesId">目标序列 ID。</param>
    /// <returns>匹配的 BlockDescriptor 列表（可能为空）。</returns>
    public IReadOnlyList<BlockDescriptor> FindBySeries(ulong seriesId)
        => _blocksBySeries.TryGetValue(seriesId, out var blocks)
            ? blocks
            : Array.Empty<BlockDescriptor>();

    /// <summary>
    /// 按 (SeriesId, FieldName) 过滤；通常 0 或 1 个结果。
    /// </summary>
    /// <param name="seriesId">目标序列 ID。</param>
    /// <param name="fieldName">目标字段名。</param>
    /// <returns>匹配的 BlockDescriptor 列表（通常 0 或 1 个）。</returns>
    public IReadOnlyList<BlockDescriptor> FindBySeriesAndField(ulong seriesId, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);

        if (!_blocksBySeries.TryGetValue(seriesId, out var seriesBlocks))
            return Array.Empty<BlockDescriptor>();

        List<BlockDescriptor>? result = null;
        foreach (var b in seriesBlocks)
        {
            if (string.Equals(b.FieldName, fieldName, StringComparison.Ordinal))
            {
                result ??= [];
                result.Add(b);
            }
        }
        return result is null ? Array.Empty<BlockDescriptor>() : result.ToArray();
    }

    /// <summary>
    /// 按时间范围过滤：返回与 [<paramref name="from"/>, <paramref name="toInclusive"/>] 有重叠的所有 <see cref="BlockDescriptor"/>。
    /// </summary>
    /// <param name="from">查询起始时间戳（含，毫秒 UTC）。</param>
    /// <param name="toInclusive">查询结束时间戳（含，毫秒 UTC）。</param>
    /// <returns>时间范围重叠的 BlockDescriptor 列表。</returns>
    public IReadOnlyList<BlockDescriptor> FindByTimeRange(long from, long toInclusive)
    {
        if (_blocks.Length == 0 || MinTimestamp > toInclusive || MaxTimestamp < from)
            return Array.Empty<BlockDescriptor>();

        if (from <= MinTimestamp && toInclusive >= MaxTimestamp)
            return _blocks;

        return _blocksByTimeRange.Find(from, toInclusive);
    }

    /// <summary>
    /// 读取一个 Block 的零拷贝 payload 视图（生命周期等同于 <see cref="SegmentReader"/>）。
    /// </summary>
    /// <param name="descriptor">目标 Block 描述符。</param>
    /// <returns>包含三段 payload 的 <see cref="BlockData"/> 零拷贝视图。</returns>
    /// <exception cref="ObjectDisposedException">Reader 已被释放。</exception>
    /// <exception cref="SegmentCorruptedException">Block CRC32 校验失败时抛出（当 VerifyBlockCrc = true）。</exception>
    public BlockData ReadBlock(in BlockDescriptor descriptor)
    {
        ThrowIfDisposed();

        Diagnostics.SonnetDbMeter.SegmentBlockReads.Add(1);
        Diagnostics.SonnetDbMeter.SegmentBlockReadBytes.Add(
            descriptor.FieldNameUtf8Length + descriptor.TimestampPayloadLength + descriptor.ValuePayloadLength);

        var source = _source!;

        int headerSize = descriptor.HeaderSize == 0 ? FormatSizes.BlockHeaderSize : descriptor.HeaderSize;
        long nameOff = descriptor.FileOffset + headerSize;
        long tsOff = nameOff + descriptor.FieldNameUtf8Length;
        long valOff = tsOff + descriptor.TimestampPayloadLength;

        ReadOnlySpan<byte> fieldNameUtf8 = source.ReadSpan(nameOff, descriptor.FieldNameUtf8Length);
        ReadOnlySpan<byte> tsPayload = source.ReadSpan(tsOff, descriptor.TimestampPayloadLength);
        ReadOnlySpan<byte> valPayload = source.ReadSpan(valOff, descriptor.ValuePayloadLength);

        if (_options.VerifyBlockCrc)
        {
            var crc = new Crc32();
            crc.Append(fieldNameUtf8);
            crc.Append(tsPayload);
            crc.Append(valPayload);
            uint computed = crc.GetCurrentHashAsUInt32();

            if (computed != descriptor.Crc32)
                throw new SegmentCorruptedException(Path, descriptor.FileOffset,
                    $"Block[{descriptor.Index}] CRC32 校验失败（期望 0x{descriptor.Crc32:X8}，实际 0x{computed:X8}）。");
        }

        return new BlockData
        {
            Descriptor = descriptor,
            FieldNameUtf8 = fieldNameUtf8,
            TimestampPayload = tsPayload,
            ValuePayload = valPayload,
        };
    }

    /// <summary>
    /// 解码一个 Block 的全部 <see cref="DataPoint"/>（分配新数组）。
    /// </summary>
    /// <param name="descriptor">目标 Block 描述符。</param>
    /// <returns>按时间戳升序排列的 DataPoint 数组。</returns>
    /// <exception cref="ObjectDisposedException">Reader 已被释放。</exception>
    /// <exception cref="SegmentCorruptedException">Block CRC32 校验失败时抛出（当 VerifyBlockCrc = true）。</exception>
    public DataPoint[] DecodeBlock(in BlockDescriptor descriptor)
    {
        if (_decodeCache is not null)
        {
            var key = CreateDecodeCacheKey(descriptor);
            if (_decodeCache.TryGet(key, out var cached))
                return cached.ToArray();

            var blockData = ReadBlock(descriptor);
            var decoded = BlockDecoder.Decode(descriptor, blockData.TimestampPayload, blockData.ValuePayload);
            long estimatedBytes = EstimateDecodedBlockBytes(descriptor);
            bool cachedNow = _decodeCache.TryAdd(key, decoded, estimatedBytes);
            return cachedNow ? decoded.ToArray() : decoded;
        }

        var data = ReadBlock(descriptor);
        return BlockDecoder.Decode(descriptor, data.TimestampPayload, data.ValuePayload);
    }

    /// <summary>
    /// 解码并按 [<paramref name="from"/>, <paramref name="toInclusive"/>] 时间裁剪。
    /// </summary>
    /// <param name="descriptor">目标 Block 描述符。</param>
    /// <param name="from">起始时间戳（含，毫秒 UTC）。</param>
    /// <param name="toInclusive">结束时间戳（含，毫秒 UTC）。</param>
    /// <returns>在时间范围内的 DataPoint 数组（可能为空）。</returns>
    /// <exception cref="ObjectDisposedException">Reader 已被释放。</exception>
    /// <exception cref="SegmentCorruptedException">Block CRC32 校验失败时抛出（当 VerifyBlockCrc = true）。</exception>
    public DataPoint[] DecodeBlockRange(in BlockDescriptor descriptor, long from, long toInclusive)
    {
        if (_decodeCache is not null)
        {
            var key = CreateDecodeCacheKey(descriptor);
            if (_decodeCache.TryGet(key, out var cached))
                return CopyDecodedRange(cached, from, toInclusive);

            long estimatedBytes = EstimateDecodedBlockBytes(descriptor);
            if (_decodeCache.CanStore(estimatedBytes))
            {
                var fullData = ReadBlock(descriptor);
                var decoded = BlockDecoder.Decode(descriptor, fullData.TimestampPayload, fullData.ValuePayload);
                _decodeCache.TryAdd(key, decoded, estimatedBytes);
                return CopyDecodedRange(decoded, from, toInclusive);
            }
        }

        var data = ReadBlock(descriptor);
        return BlockDecoder.DecodeRange(descriptor, data.TimestampPayload, data.ValuePayload, from, toInclusive);
    }

    /// <summary>
    /// 解码并按 [<paramref name="from"/>, <paramref name="toInclusive"/>] 时间裁剪，返回零拷贝视图。
    /// <para>
    /// 与 <see cref="DecodeBlockRange"/> 的区别：解码缓存命中时返回缓存数组的子区间 <see cref="ReadOnlyMemory{DataPoint}"/>
    /// 视图（不复制），而非每次分配裁剪副本；未命中但可缓存时缓存整块并返回其视图。缓存数组不可变
    /// （<see cref="DataPoint"/> 为只读结构，缓存替换只新建数组、不改动已发布数组），故视图在被持有期间始终有效。
    /// 返回值仅供只读消费。
    /// </para>
    /// </summary>
    /// <param name="descriptor">目标 Block 描述符。</param>
    /// <param name="from">起始时间戳（含，毫秒 UTC）。</param>
    /// <param name="toInclusive">结束时间戳（含，毫秒 UTC）。</param>
    /// <returns>在时间范围内的 DataPoint 只读内存视图（可能为空）。</returns>
    /// <exception cref="ObjectDisposedException">Reader 已被释放。</exception>
    /// <exception cref="SegmentCorruptedException">Block CRC32 校验失败时抛出（当 VerifyBlockCrc = true）。</exception>
    internal ReadOnlyMemory<DataPoint> DecodeBlockRangeView(in BlockDescriptor descriptor, long from, long toInclusive)
    {
        if (_decodeCache is not null)
        {
            var key = CreateDecodeCacheKey(descriptor);
            if (_decodeCache.TryGet(key, out var cached))
                return SliceDecodedRange(cached, from, toInclusive);

            long estimatedBytes = EstimateDecodedBlockBytes(descriptor);
            if (_decodeCache.CanStore(estimatedBytes))
            {
                var fullData = ReadBlock(descriptor);
                var decoded = BlockDecoder.Decode(descriptor, fullData.TimestampPayload, fullData.ValuePayload);
                _decodeCache.TryAdd(key, decoded, estimatedBytes);
                return SliceDecodedRange(decoded, from, toInclusive);
            }
        }

        var data = ReadBlock(descriptor);
        return BlockDecoder.DecodeRange(descriptor, data.TimestampPayload, data.ValuePayload, from, toInclusive);
    }

    /// <summary>
    /// 尝试获取指定 block 对应的 SonnetDB HNSW 向量索引 reader。
    /// </summary>
    /// <param name="descriptor">目标 block。</param>
    /// <param name="reader">命中时返回的索引 reader。</param>
    /// <returns>存在索引返回 true，否则返回 false。</returns>
    internal bool TryGetVectorIndexReader(in BlockDescriptor descriptor, out IVectorIndexReader reader)
    {
        ThrowIfDisposed();

        reader = null!;
        if (descriptor.FieldType != FieldType.Vector
            || descriptor.Index < 0
            || descriptor.Index >= _blocks.Length)
        {
            return false;
        }

        var key = CreateVectorIndexCacheKey(descriptor);
        if (_vectorIndexCache is not null && _vectorIndexCache.TryGet(key, out reader))
            return true;

        lock (_vectorIndexLoadLock)
        {
            if (_vectorIndexCache is not null && _vectorIndexCache.TryGet(key, out reader))
                return true;

            var offsets = EnsureVectorIndexOffsetsLoaded();
            if (!offsets.TryGetValue(descriptor.Index, out long offset))
                return false;

            VectorIndexBlockMetadata metadata;
            bool loadedOk = _vectorIndexOffsetsEmbedded
                ? SegmentVectorIndexFile.TryLoadEmbeddedBlockAt(
                    Path,
                    offset,
                    _embeddedExtensionOffset,
                    _embeddedExtensionLength,
                    _blocks,
                    descriptor.Index,
                    out metadata)
                : SegmentVectorIndexFile.TryLoadBlockAt(
                    Path,
                    offset,
                    _blocks,
                    descriptor.Index,
                    out metadata);

            if (!loadedOk || metadata.BlockCrc32 != descriptor.Crc32)
            {
                return false;
            }

            if (TryLoadVectorIndexReaderFromBlob(metadata, out reader)
                || (metadata.CanRebuildFromBlockPayload && TryRebuildVectorIndexReaderFromBlock(descriptor, metadata, out reader)))
            {
                _vectorIndexCache?.TryAdd(
                    key,
                    reader,
                    reader.EstimatedBytes,
                    _options.VectorIndexCacheMaxBytes);
                return true;
            }

            return false;
        }
    }

    private bool TryLoadVectorIndexReaderFromBlob(VectorIndexBlockMetadata metadata, out IVectorIndexReader reader)
    {
        reader = null!;
        if (!metadata.HasPersistentBlob)
            return false;

        bool opened = _vectorIndexOffsetsEmbedded
            ? SegmentVectorIndexFile.TryOpenEmbeddedBlob(
                Path,
                _embeddedExtensionOffset,
                _embeddedExtensionLength,
                metadata,
                out var blobStream)
            : SegmentVectorIndexFile.TryOpenBlob(Path, metadata, out blobStream);
        if (!opened)
            return false;

        using (blobStream)
        {
            try
            {
                var buildResult = LocalVectorIndexBuilderAdapter.BuildFromBlob(
                    metadata.BlockIndex,
                    blobStream,
                    metadata.BlobLength,
                    metadata.BlobCrc32,
                    metadata.Count,
                    metadata.Dimension,
                    metadata.Ef);
                reader = buildResult.Reader;
                return true;
            }
            catch (Exception ex) when (IsRecoverableVectorIndexLoadError(ex))
            {
                reader = null!;
                return false;
            }
        }
    }

    private bool TryRebuildVectorIndexReaderFromBlock(
        in BlockDescriptor descriptor,
        VectorIndexBlockMetadata metadata,
        out IVectorIndexReader reader)
    {
        reader = null!;
        try
        {
            var data = ReadBlock(descriptor);
            var buildResult = LocalVectorIndexBuilderAdapter.BuildFromPayload(
                descriptor.Index,
                data.ValuePayload,
                metadata.Count,
                metadata.Dimension,
                ToVectorIndexDefinition(metadata));
            reader = buildResult.Reader;
            return true;
        }
        catch (Exception ex) when (IsRecoverableVectorIndexLoadError(ex))
        {
            reader = null!;
            return false;
        }
    }

    private static VectorIndexDefinition ToVectorIndexDefinition(VectorIndexBlockMetadata metadata)
    {
        var kind = (VectorIndexKind)metadata.IndexKind;
        // #223：SDBVIDX 持久化的 Metric（v3 段默认 Cosine）贯通回建图度量，HNSW 还原独立的 efConstruction。
        var metric = (SonnetDB.Query.KnnMetric)metadata.Metric;
        return kind switch
        {
            VectorIndexKind.Hnsw => VectorIndexDefinition.CreateHnsw(metadata.M, metadata.Ef, metric, metadata.EfConstruction),
            VectorIndexKind.IvfFlat => VectorIndexDefinition.CreateIvfFlat(metadata.M, metadata.Ef, metadata.Extra1, metric),
            VectorIndexKind.IvfPq => VectorIndexDefinition.CreateIvfPq(metadata.M, metadata.Ef, metadata.Extra1, metadata.Extra2, metadata.Extra3, metric),
            VectorIndexKind.Vamana => VectorIndexDefinition.CreateVamana(metadata.M, metadata.Ef, BitConverter.Int32BitsToSingle(metadata.Extra1), metadata.Extra2, metric),
            _ => throw new InvalidDataException($"SDBVIDX 包含不支持的向量索引类型 {metadata.IndexKind}。"),
        };
    }

    private static bool IsRecoverableVectorIndexLoadError(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException;

    /// <summary>
    /// 尝试获取指定 block 对应的扩展聚合 sketch。
    /// </summary>
    /// <param name="descriptor">目标 block。</param>
    /// <param name="sketch">命中时返回的 sketch 快照。</param>
    /// <returns>存在并通过校验返回 true，否则返回 false。</returns>
    internal bool TryGetAggregateSketch(in BlockDescriptor descriptor, out BlockAggregateSketch sketch)
    {
        ThrowIfDisposed();

        sketch = null!;
        if (descriptor.FieldType is not (FieldType.Float64 or FieldType.Int64 or FieldType.Boolean)
            || descriptor.Index < 0
            || descriptor.Index >= _blocks.Length)
        {
            return false;
        }

        var offsets = EnsureAggregateSketchOffsetsLoaded();
        if (!offsets.TryGetValue(descriptor.Index, out long offset))
            return false;

        return _aggregateSketchOffsetsEmbedded
            ? SegmentAggregateSketchFile.TryLoadEmbeddedBlockAt(
                Path,
                offset,
                _embeddedExtensionOffset,
                _embeddedExtensionLength,
                _blocks,
                descriptor.Index,
                descriptor.Crc32,
                out sketch)
            : SegmentAggregateSketchFile.TryLoadBlockAt(
                Path,
                offset,
                _blocks,
                descriptor.Index,
                descriptor.Crc32,
                out sketch);
    }

    /// <summary>
    /// 释放内部 <c>byte[]</c> 引用以便 GC 回收。调用后不可再调用其他方法。
    /// </summary>
    public void Dispose()
    {
        _source?.Dispose();
        _source = null;
        _decodeCache?.Clear();
        _vectorIndexCache?.RemoveSegment(Header.SegmentId);
    }

    internal long DecodeCacheHitCount => _decodeCache?.HitCount ?? 0L;

    internal long DecodeCacheMissCount => _decodeCache?.MissCount ?? 0L;

    internal long DecodeCacheCurrentBytes => _decodeCache?.CurrentBytes ?? 0L;

    internal int DecodeCacheEntryCount => _decodeCache?.Count ?? 0;

    internal long VectorIndexCacheHitCount => _vectorIndexCache?.HitCount ?? 0L;

    internal long VectorIndexCacheMissCount => _vectorIndexCache?.MissCount ?? 0L;

    internal long VectorIndexCacheCurrentBytes => _vectorIndexCache?.CurrentBytes ?? 0L;

    internal int VectorIndexCacheEntryCount => _vectorIndexCache?.Count ?? 0;

    internal long VectorIndexCacheCurrentBytesForSegment
        => _vectorIndexCache?.GetSegmentBytes(Header.SegmentId) ?? 0L;

    internal int VectorIndexCacheEntryCountForSegment
        => _vectorIndexCache?.GetSegmentCount(Header.SegmentId) ?? 0;

    internal bool VectorIndexOffsetsLoaded => _vectorIndexOffsetsLoaded;

    internal bool AggregateSketchOffsetsLoaded => _aggregateSketchOffsetsLoaded;

    internal bool VectorIndexOffsetsEmbedded => _vectorIndexOffsetsEmbedded;

    internal IReadOnlyDictionary<int, VectorIndexBlockMetadata> VectorIndexManifest
        => SegmentVectorIndexFile.TryLoadEmbeddedManifest(
            Path,
            _embeddedExtensionOffset,
            _embeddedExtensionLength,
            _blocks);

    internal bool AggregateSketchOffsetsEmbedded => _aggregateSketchOffsetsEmbedded;

    internal bool UsesMemoryMappedStorage => _source?.IsMemoryMapped == true;

    // ── 受保护虚方法（供测试或未来 mmap 派生类替换） ──────────────────────────

    /// <summary>
    /// 加载段文件全部字节（供测试或未来 mmap 等路径复用）。
    /// </summary>
    /// <param name="path">段文件路径。</param>
    /// <returns>文件的全部字节。</returns>
    internal static byte[] LoadAll(string path) => File.ReadAllBytes(path);

    private static SegmentByteSource OpenByteSource(string path, SegmentReaderOptions options)
    {
        long fileLength = new FileInfo(path).Length;
        if (ShouldUseMemoryMappedSource(fileLength, options)
            && TryOpenMemoryMappedSource(path, fileLength, out var memoryMappedSource))
        {
            return memoryMappedSource;
        }

        return new ByteArraySegmentByteSource(LoadAll(path));
    }

    private static bool ShouldUseMemoryMappedSource(long fileLength, SegmentReaderOptions options)
    {
        if (!options.UseMemoryMappedFileForLargeSegments)
            return false;

        long threshold = options.MemoryMappedFileThresholdBytes;
        return threshold <= 0 || fileLength >= threshold;
    }

    private static bool TryOpenMemoryMappedSource(
        string path,
        long fileLength,
        out SegmentByteSource source)
    {
        try
        {
            source = MemoryMappedSegmentByteSource.Open(path, fileLength);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PlatformNotSupportedException)
        {
            source = null!;
            return false;
        }
    }

    // ── 私有辅助 ──────────────────────────────────────────────────────────────

    private static string BuildTooShortSegmentMessage(SegmentHeader header, long length, int minLen)
    {
        string message = $"文件过短（{length} 字节），最小需要 {minLen} 字节。";
        if (header.TryReadFooterMiniCopy(out var mini))
        {
            message +=
                $" SegmentHeader mini-footer 指示完整文件长度应为 {mini.FileLength} 字节，" +
                $"IndexOffset={mini.IndexOffset}，IndexCount={mini.IndexCount}。";
        }

        return message;
    }

    private static bool TryReadPrimaryFooter(
        string path,
        SegmentByteSource source,
        long length,
        out SegmentFooter footer,
        out long footerStart,
        out string error)
    {
        footerStart = length - FormatSizes.SegmentFooterSize;
        footer = MemoryMarshal.Read<SegmentFooter>(source.ReadSpan(footerStart, FormatSizes.SegmentFooterSize));

        if (!footer.IsCompatibleForRead())
        {
            error = "SegmentFooter Magic 或 FormatVersion 不匹配";
            return false;
        }

        // #195：footer 自校验（仅当 v6+ 且 FooterChecksum 非 0）。旧版本（v2~v5）或未写该校验的
        // 旧 v6 文件 FooterChecksum==0，跳过以保持向后读兼容。检出满足布局等式但字段被位翻转的静默损坏。
        if (footer.FormatVersion >= TsdbMagic.SegmentFormatVersion && !footer.VerifyFooterChecksum())
        {
            error = $"SegmentFooter 自校验 CRC 失败（footer 字段疑似损坏，FooterChecksum=0x{footer.FooterChecksum:X8}）";
            return false;
        }

        return TryValidateFooterLayout(path, length, footerStart, footer, out error);
    }

    private static bool TryReadFooterFromHeaderMiniCopy(
        string path,
        SegmentHeader header,
        long length,
        out SegmentFooter footer,
        out long footerStart,
        out string error)
    {
        footer = default;
        footerStart = 0;

        if (!header.TryReadFooterMiniCopy(out var mini))
        {
            error = "SegmentHeader mini-footer 不存在或形状无效";
            return false;
        }

        long expectedIndexEnd = mini.IndexOffset + (long)mini.IndexCount * FormatSizes.BlockIndexEntrySize;
        if (expectedIndexEnd > mini.FileLength - FormatSizes.SegmentFooterSize)
        {
            error =
                $"SegmentHeader mini-footer 自身不一致：IndexOffset({mini.IndexOffset}) + " +
                $"IndexCount({mini.IndexCount}) * {FormatSizes.BlockIndexEntrySize} = {expectedIndexEnd}，" +
                $"超过 FooterOffset({mini.FileLength - FormatSizes.SegmentFooterSize})";
            return false;
        }

        if (mini.FileLength != length)
        {
            error =
                $"SegmentHeader mini-footer 可读，但文件尾部疑似截断：mini-footer FileLength={mini.FileLength}，" +
                $"实际文件长度={length}，IndexOffset={mini.IndexOffset}，IndexCount={mini.IndexCount}";
            return false;
        }

        footerStart = mini.FileLength - FormatSizes.SegmentFooterSize;
        footer = SegmentFooter.CreateNew(mini.IndexCount, mini.IndexOffset, mini.FileLength);
        footer.FormatVersion = header.FormatVersion;
        footer.Crc32 = mini.IndexCrc32;

        return TryValidateFooterLayout(path, length, footerStart, footer, out error);
    }

    private static bool TryValidateFooterLayout(
        string path,
        long length,
        long footerStart,
        SegmentFooter footer,
        out string error)
    {
        _ = path;

        long expectedIndexEnd = footer.IndexOffset + (long)footer.IndexCount * FormatSizes.BlockIndexEntrySize;
        if (footer.FormatVersion >= 6)
        {
            if (expectedIndexEnd > footerStart)
            {
                error =
                    $"SegmentFooter 位置不一致：IndexOffset({footer.IndexOffset}) + " +
                    $"IndexCount({footer.IndexCount}) * {FormatSizes.BlockIndexEntrySize} = {expectedIndexEnd}，" +
                    $"超过 FooterOffset({footerStart})";
                return false;
            }
        }
        else if (expectedIndexEnd != footerStart)
        {
            error =
                $"SegmentFooter 位置不一致：IndexOffset({footer.IndexOffset}) + " +
                $"IndexCount({footer.IndexCount}) * {FormatSizes.BlockIndexEntrySize} = {expectedIndexEnd}，" +
                $"但实际 FooterOffset = {footerStart}";
            return false;
        }

        if (footer.FileLength != length)
        {
            error = $"SegmentFooter.FileLength={footer.FileLength} 与实际文件长度 {length} 不一致";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static FrozenDictionary<ulong, BlockDescriptor[]> BuildBlocksBySeriesIndex(
        IReadOnlyList<BlockDescriptor> blocks)
    {
        var grouped = new Dictionary<ulong, List<BlockDescriptor>>();
        foreach (var block in blocks)
        {
            if (!grouped.TryGetValue(block.SeriesId, out var seriesBlocks))
            {
                seriesBlocks = [];
                grouped.Add(block.SeriesId, seriesBlocks);
            }
            seriesBlocks.Add(block);
        }

        var index = new Dictionary<ulong, BlockDescriptor[]>(grouped.Count);
        foreach (var (seriesId, seriesBlocks) in grouped)
            index.Add(seriesId, seriesBlocks.ToArray());
        return index.ToFrozenDictionary();
    }

    private BlockDecodeCacheKey CreateDecodeCacheKey(in BlockDescriptor descriptor)
        => new(Header.SegmentId, descriptor.Index, descriptor.Crc32);

    private HnswVectorIndexCacheKey CreateVectorIndexCacheKey(in BlockDescriptor descriptor)
        => new(Header.SegmentId, descriptor.Index, descriptor.Crc32);

    private IReadOnlyDictionary<int, long> EnsureVectorIndexOffsetsLoaded()
    {
        if (_vectorIndexOffsetsLoaded)
            return _vectorIndexOffsetsByBlock ?? EmptyVectorIndexOffsets;

        lock (_vectorIndexLoadLock)
        {
            if (!_vectorIndexOffsetsLoaded)
            {
                var embeddedOffsets = SegmentVectorIndexFile.TryLoadEmbeddedOffsets(
                    Path,
                    _embeddedExtensionOffset,
                    _embeddedExtensionLength,
                    _blocks);
                if (embeddedOffsets.Count > 0)
                {
                    _vectorIndexOffsetsByBlock = embeddedOffsets;
                    _vectorIndexOffsetsEmbedded = true;
                }
                else
                {
                    _vectorIndexOffsetsByBlock = SegmentVectorIndexFile.TryLoadOffsets(Path, _blocks);
                    _vectorIndexOffsetsEmbedded = false;
                }
                _vectorIndexOffsetsLoaded = true;
            }

            return _vectorIndexOffsetsByBlock ?? EmptyVectorIndexOffsets;
        }
    }

    private IReadOnlyDictionary<int, long> EnsureAggregateSketchOffsetsLoaded()
    {
        if (_aggregateSketchOffsetsLoaded)
            return _aggregateSketchOffsetsByBlock ?? EmptyAggregateSketchOffsets;

        lock (_aggregateSketchLoadLock)
        {
            if (!_aggregateSketchOffsetsLoaded)
            {
                var embeddedOffsets = SegmentAggregateSketchFile.TryLoadEmbeddedOffsets(
                    Path,
                    _embeddedExtensionOffset,
                    _embeddedExtensionLength,
                    _blocks);
                if (embeddedOffsets.Count > 0)
                {
                    _aggregateSketchOffsetsByBlock = embeddedOffsets;
                    _aggregateSketchOffsetsEmbedded = true;
                }
                else
                {
                    _aggregateSketchOffsetsByBlock = SegmentAggregateSketchFile.TryLoadOffsets(Path, _blocks);
                    _aggregateSketchOffsetsEmbedded = false;
                }
                _aggregateSketchOffsetsLoaded = true;
            }

            return _aggregateSketchOffsetsByBlock ?? EmptyAggregateSketchOffsets;
        }
    }

    private static long EstimateDecodedBlockBytes(in BlockDescriptor descriptor)
    {
        const long ArrayOverheadBytes = 24L;
        const long DataPointApproxBytes = 48L;

        long bytes = ArrayOverheadBytes + (long)descriptor.Count * DataPointApproxBytes;
        if (descriptor.FieldType == FieldType.String)
            bytes += (long)descriptor.ValuePayloadLength * 2L;
        else if (descriptor.FieldType is FieldType.Vector or FieldType.GeoPoint)
            bytes += descriptor.ValuePayloadLength;

        return Math.Max(1L, bytes);
    }

    private static DataPoint[] CopyDecodedRange(DataPoint[] points, long from, long toInclusive)
    {
        if (points.Length == 0)
            return [];

        int start = LowerBound(points, from);
        int end = UpperBound(points, toInclusive);
        if (start >= end)
            return [];

        int length = end - start;
        var result = new DataPoint[length];
        Array.Copy(points, start, result, 0, length);
        return result;
    }

    /// <summary>
    /// 与 <see cref="CopyDecodedRange"/> 同样二分裁剪 [from, toInclusive]，但返回 <paramref name="points"/> 的
    /// 零拷贝子区间视图（不新建数组）。仅供只读消费；<paramref name="points"/> 必须是不可变的解码缓存数组。
    /// </summary>
    private static ReadOnlyMemory<DataPoint> SliceDecodedRange(DataPoint[] points, long from, long toInclusive)
    {
        if (points.Length == 0)
            return ReadOnlyMemory<DataPoint>.Empty;

        int start = LowerBound(points, from);
        int end = UpperBound(points, toInclusive);
        if (start >= end)
            return ReadOnlyMemory<DataPoint>.Empty;

        return new ReadOnlyMemory<DataPoint>(points, start, end - start);
    }

    private static int LowerBound(DataPoint[] points, long timestamp)
    {
        int lo = 0, hi = points.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (points[mid].Timestamp < timestamp)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private static int UpperBound(DataPoint[] points, long timestamp)
    {
        int lo = 0, hi = points.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (points[mid].Timestamp <= timestamp)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private sealed class BlockTimeRangeIndex
    {
        private readonly BlockDescriptor[] _byMinTimestamp;
        private readonly BlockDescriptor[] _byMaxTimestamp;

        private BlockTimeRangeIndex(
            BlockDescriptor[] byMinTimestamp,
            BlockDescriptor[] byMaxTimestamp)
        {
            _byMinTimestamp = byMinTimestamp;
            _byMaxTimestamp = byMaxTimestamp;
        }

        public static BlockTimeRangeIndex Build(IReadOnlyList<BlockDescriptor> blocks)
        {
            var byMinTimestamp = new BlockDescriptor[blocks.Count];
            var byMaxTimestamp = new BlockDescriptor[blocks.Count];
            for (int i = 0; i < blocks.Count; i++)
            {
                byMinTimestamp[i] = blocks[i];
                byMaxTimestamp[i] = blocks[i];
            }

            Array.Sort(byMinTimestamp, static (a, b) =>
            {
                int cmp = a.MinTimestamp.CompareTo(b.MinTimestamp);
                return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
            });
            Array.Sort(byMaxTimestamp, static (a, b) =>
            {
                int cmp = a.MaxTimestamp.CompareTo(b.MaxTimestamp);
                return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
            });

            return new BlockTimeRangeIndex(byMinTimestamp, byMaxTimestamp);
        }

        public IReadOnlyList<BlockDescriptor> Find(long from, long toInclusive)
        {
            int count = _byMinTimestamp.Length;
            if (count == 0)
                return Array.Empty<BlockDescriptor>();

            int minUpper = FindMinUpperBound(_byMinTimestamp, toInclusive);
            if (minUpper == 0)
                return Array.Empty<BlockDescriptor>();

            int maxLower = FindMaxLowerBound(_byMaxTimestamp, from);
            if (maxLower == count)
                return Array.Empty<BlockDescriptor>();

            int maxCandidateCount = count - maxLower;
            return minUpper <= maxCandidateCount
                ? FilterByMax(_byMinTimestamp, 0, minUpper, from)
                : FilterByMin(_byMaxTimestamp, maxLower, count, toInclusive);
        }

        private static IReadOnlyList<BlockDescriptor> FilterByMax(
            BlockDescriptor[] sorted,
            int start,
            int end,
            long from)
        {
            List<BlockDescriptor>? result = null;
            for (int i = start; i < end; i++)
            {
                if (sorted[i].MaxTimestamp >= from)
                {
                    result ??= [];
                    result.Add(sorted[i]);
                }
            }

            return ToSegmentOrder(result);
        }

        private static IReadOnlyList<BlockDescriptor> FilterByMin(
            BlockDescriptor[] sorted,
            int start,
            int end,
            long toInclusive)
        {
            List<BlockDescriptor>? result = null;
            for (int i = start; i < end; i++)
            {
                if (sorted[i].MinTimestamp <= toInclusive)
                {
                    result ??= [];
                    result.Add(sorted[i]);
                }
            }

            return ToSegmentOrder(result);
        }

        private static IReadOnlyList<BlockDescriptor> ToSegmentOrder(List<BlockDescriptor>? result)
        {
            if (result is null || result.Count == 0)
                return Array.Empty<BlockDescriptor>();

            if (result.Count > 1)
                result.Sort(static (a, b) => a.Index.CompareTo(b.Index));

            return result;
        }

        private static int FindMinUpperBound(BlockDescriptor[] sorted, long toInclusive)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (sorted[mid].MinTimestamp <= toInclusive)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        private static int FindMaxLowerBound(BlockDescriptor[] sorted, long from)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (sorted[mid].MaxTimestamp < from)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_source is null)
            throw new ObjectDisposedException(nameof(SegmentReader));
    }

    private static BlockHeader ReadBlockHeader(ReadOnlySpan<byte> bytes, int blockHeaderSize)
    {
        if (blockHeaderSize == FormatSizes.BlockHeaderSize)
            return MemoryMarshal.Read<BlockHeader>(bytes);

        var header = default(BlockHeader);
        var destination = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref header, 1));
        bytes[..68].CopyTo(destination[..68]);
        header.Crc32 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(68, 4));
        return header;
    }
}
