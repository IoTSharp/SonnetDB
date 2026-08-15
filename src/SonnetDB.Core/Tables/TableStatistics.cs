using System.IO.Hashing;
using System.Text;
using SonnetDB.Kv;
using SonnetDB.Query.Functions.Aggregates;

namespace SonnetDB.Tables;

/// <summary>关系表统计刷新的扫描、采样和内存预算。</summary>
public sealed record TableStatisticsRefreshOptions
{
    /// <summary>每页最多读取的关系行数。</summary>
    public int PageSize { get; init; } = 512;

    /// <summary>每页最多复制的 key/value payload 字节数。</summary>
    public int MaxPageBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>单次刷新最多采样的关系行数。</summary>
    public int MaxSampleRows { get; init; } = 100_000;

    /// <summary>每列最多保存的 MCV fingerprint 数量。</summary>
    public int MostCommonValueCount { get; init; } = 8;

    /// <summary>数值列等深直方图的最大桶数。</summary>
    public int HistogramBucketCount { get; init; } = 16;

    /// <summary>每个数值列为构造直方图保留的最大样本数。</summary>
    public int MaxHistogramSamples { get; init; } = 4_096;

    /// <summary>逻辑页估算使用的页字节数。</summary>
    public int LogicalPageBytes { get; init; } = 16 * 1024;

    internal void Validate()
    {
        if (PageSize is <= 0 or > 4_096)
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        if (MaxPageBytes is <= 0 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxPageBytes));
        if (MaxSampleRows is <= 0 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(MaxSampleRows));
        if (MostCommonValueCount is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(MostCommonValueCount));
        if (HistogramBucketCount is < 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(HistogramBucketCount));
        if (MaxHistogramSamples is <= 0 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(MaxHistogramSamples));
        if (LogicalPageBytes is < 1_024 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(LogicalPageBytes));
    }
}

/// <summary>不包含原始列值的高频值统计。</summary>
/// <param name="Fingerprint">类型化列值的稳定 64 位 fingerprint。</param>
/// <param name="EstimatedRows">估算包含该值的行数。</param>
public sealed record TableMostCommonValue(ulong Fingerprint, long EstimatedRows);

/// <summary>数值列等深直方图的一个桶。</summary>
public sealed record TableHistogramBucket
{
    /// <summary>INT/DATETIME 桶的排他上界；其它类型为空。</summary>
    public long? Int64UpperBound { get; init; }

    /// <summary>FLOAT 桶的排他上界；其它类型为空。</summary>
    public double? Float64UpperBound { get; init; }

    /// <summary>估算落入该桶的行数。</summary>
    public long EstimatedRows { get; init; }
}

/// <summary>单个关系表列的轻量统计。</summary>
public sealed class TableColumnStatistics
{
    internal TableColumnStatistics(
        string columnName,
        double nullFraction,
        long estimatedDistinctCount,
        long sampledNonNullRows,
        IReadOnlyList<TableMostCommonValue> mostCommonValues,
        IReadOnlyList<TableHistogramBucket> histogram)
    {
        ColumnName = columnName;
        NullFraction = nullFraction;
        EstimatedDistinctCount = estimatedDistinctCount;
        SampledNonNullRows = sampledNonNullRows;
        MostCommonValues = mostCommonValues;
        Histogram = histogram;
    }

    /// <summary>列名。</summary>
    public string ColumnName { get; }

    /// <summary>NULL 行占比，范围为 0 到 1。</summary>
    public double NullFraction { get; }

    /// <summary>基于 HLL 与采样率外推的 distinct 数量。</summary>
    public long EstimatedDistinctCount { get; }

    /// <summary>刷新时实际采样的非 NULL 行数。</summary>
    public long SampledNonNullRows { get; }

    /// <summary>高频值 fingerprint 及估算频次，不保存原始值。</summary>
    public IReadOnlyList<TableMostCommonValue> MostCommonValues { get; }

    /// <summary>数值列的等深直方图；字符串、JSON、BLOB 和 BOOL 为空。</summary>
    public IReadOnlyList<TableHistogramBucket> Histogram { get; }
}

/// <summary>单个关系表索引的轻量统计。</summary>
public sealed record TableIndexStatistics(
    string IndexName,
    long RowCount,
    long LogicalPageCount,
    double AverageEntryWidth);

/// <summary>持久化的关系表轻量统计快照。</summary>
public sealed class TableStatistics
{
    internal TableStatistics(
        long generation,
        long sourceSequence,
        DateTimeOffset refreshedAtUtc,
        long rowCount,
        long logicalPageCount,
        double averageRowWidth,
        int sampledRows,
        double sampleRate,
        bool isComplete,
        int logicalPageBytes,
        IReadOnlyList<TableColumnStatistics> columns,
        IReadOnlyList<TableIndexStatistics> indexes)
    {
        Generation = generation;
        SourceSequence = sourceSequence;
        RefreshedAtUtc = refreshedAtUtc;
        RowCount = rowCount;
        LogicalPageCount = logicalPageCount;
        AverageRowWidth = averageRowWidth;
        SampledRows = sampledRows;
        SampleRate = sampleRate;
        IsComplete = isComplete;
        LogicalPageBytes = logicalPageBytes;
        Columns = columns;
        Indexes = indexes;
    }

    /// <summary>统计所属的 rowstore generation。</summary>
    public long Generation { get; }

    /// <summary>采样使用的稳定 KV sequence。</summary>
    public long SourceSequence { get; }

    /// <summary>统计完成刷新的 UTC 时间。</summary>
    public DateTimeOffset RefreshedAtUtc { get; }

    /// <summary>采样快照中的关系行数。</summary>
    public long RowCount { get; }

    /// <summary>按平均行宽估算的逻辑页数。</summary>
    public long LogicalPageCount { get; }

    /// <summary>编码后关系行 payload 的平均字节数。</summary>
    public double AverageRowWidth { get; }

    /// <summary>实际参与统计计算的样本行数。</summary>
    public int SampledRows { get; }

    /// <summary>样本行数占快照总行数的比例。</summary>
    public double SampleRate { get; }

    /// <summary>是否扫描了快照中的全部关系行。</summary>
    public bool IsComplete { get; }

    /// <summary>逻辑页估算采用的页字节数。</summary>
    public int LogicalPageBytes { get; }

    /// <summary>按 schema 顺序排列的列统计。</summary>
    public IReadOnlyList<TableColumnStatistics> Columns { get; }

    /// <summary>按 schema 顺序排列的二级索引统计。</summary>
    public IReadOnlyList<TableIndexStatistics> Indexes { get; }

    /// <summary>按列名查找统计。</summary>
    /// <param name="columnName">列名。</param>
    /// <returns>存在时返回列统计，否则返回 null。</returns>
    public TableColumnStatistics? TryGetColumn(string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        return Columns.FirstOrDefault(column => string.Equals(column.ColumnName, columnName, StringComparison.Ordinal));
    }

    /// <summary>按索引名查找统计。</summary>
    /// <param name="indexName">索引名。</param>
    /// <returns>存在时返回索引统计，否则返回 null。</returns>
    public TableIndexStatistics? TryGetIndex(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return Indexes.FirstOrDefault(index => string.Equals(index.IndexName, indexName, StringComparison.Ordinal));
    }
}

internal static class TableStatisticsCalculator
{
    internal static TableStatistics Refresh(
        TableReadSnapshot tableSnapshot,
        TableStatisticsRefreshOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tableSnapshot);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        TableSchema schema = tableSnapshot.Schema;
        var columns = schema.Columns
            .Select(column => new ColumnAccumulator(column, options))
            .ToArray();
        var indexes = schema.Indexes
            .Select(index => new IndexAccumulator(index))
            .ToArray();

        long rowPayloadBytes = 0;
        int sampledRows = 0;
        using KvRangeCursor cursor = tableSnapshot.Snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = new byte[] { (byte)'r' },
            PageSize = options.PageSize,
            MaxPageBytes = options.MaxPageBytes,
        });
        while (sampledRows < options.MaxSampleRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<KvEntry> page = cursor.ReadNextPage(cancellationToken);
            if (page.Count == 0)
                break;

            foreach (KvEntry entry in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sampledRows >= options.MaxSampleRows)
                    break;

                object?[] values = TableRowCodec.Decode(schema, entry.Value.Span);
                byte[] primaryKey = TableIndexCodec.DecodePrimaryKeyFromRowKey(entry.Key).ToArray();
                sampledRows++;
                rowPayloadBytes = checked(rowPayloadBytes + entry.Value.Length);
                for (int columnIndex = 0; columnIndex < columns.Length; columnIndex++)
                    columns[columnIndex].Add(values[columnIndex]);

                for (int index = 0; index < indexes.Length; index++)
                {
                    byte[]? key = TableIndexCodec.TryEncodeIndexEntryKey(
                        indexes[index].Index,
                        values,
                        schema,
                        primaryKey);
                    if (key is not null)
                        indexes[index].Add(checked(key.Length + primaryKey.Length));
                }
            }
        }

        long rowCount = tableSnapshot.RowCount;
        double sampleRate = rowCount == 0 ? 1 : Math.Min(1, (double)sampledRows / rowCount);
        bool isComplete = sampledRows >= rowCount;
        double averageRowWidth = sampledRows == 0 ? 0 : (double)rowPayloadBytes / sampledRows;
        long logicalPages = EstimatePages(rowCount, averageRowWidth, options.LogicalPageBytes);

        return new TableStatistics(
            tableSnapshot.Generation,
            tableSnapshot.Snapshot.Sequence,
            DateTimeOffset.UtcNow,
            rowCount,
            logicalPages,
            averageRowWidth,
            sampledRows,
            sampleRate,
            isComplete,
            options.LogicalPageBytes,
            columns.Select(column => column.Build(rowCount, sampleRate, options)).ToArray(),
            indexes.Select(index => index.Build(rowCount, sampleRate, options.LogicalPageBytes)).ToArray());
    }

    private static long EstimatePages(long rows, double averageWidth, int logicalPageBytes)
    {
        if (rows == 0 || averageWidth <= 0)
            return 0;
        double pages = Math.Ceiling(rows * averageWidth / logicalPageBytes);
        return pages >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)pages);
    }

    private sealed class IndexAccumulator(TableIndex index)
    {
        private long _sampleRows;
        private long _sampleBytes;

        public TableIndex Index { get; } = index;

        public void Add(int width)
        {
            _sampleRows++;
            _sampleBytes = checked(_sampleBytes + width);
        }

        public TableIndexStatistics Build(long tableRows, double sampleRate, int logicalPageBytes)
        {
            long estimatedRows = sampleRate <= 0
                ? 0
                : Math.Min(tableRows, (long)Math.Round(_sampleRows / sampleRate));
            double averageWidth = _sampleRows == 0 ? 0 : (double)_sampleBytes / _sampleRows;
            return new TableIndexStatistics(
                Index.Name,
                estimatedRows,
                EstimatePages(estimatedRows, averageWidth, logicalPageBytes),
                averageWidth);
        }
    }

    private sealed class ColumnAccumulator
    {
        private readonly TableColumn _column;
        private readonly HyperLogLog _distinct = new();
        private readonly Dictionary<ulong, long> _heavyHitters = [];
        private readonly List<long> _int64HistogramSamples;
        private readonly List<double> _float64HistogramSamples;
        private readonly int _heavyHitterCapacity;
        private readonly int _maxHistogramSamples;
        private ulong _randomState;
        private long _nonNullRows;
        private long _nullRows;
        private long _numericRows;

        public ColumnAccumulator(TableColumn column, TableStatisticsRefreshOptions options)
        {
            _column = column;
            _heavyHitterCapacity = Math.Max(16, options.MostCommonValueCount * 16);
            _maxHistogramSamples = options.MaxHistogramSamples;
            int histogramCapacity = Math.Min(options.MaxHistogramSamples, 4_096);
            _int64HistogramSamples = new List<long>(histogramCapacity);
            _float64HistogramSamples = new List<double>(histogramCapacity);
            _randomState = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(column.Name))
                ^ (ulong)column.DataType;
        }

        public void Add(object? value)
        {
            if (value is null)
            {
                _nullRows++;
                return;
            }

            _nonNullRows++;
            ulong fingerprint = TableValueFingerprint.Create(_column, value);
            _distinct.AddHash(fingerprint);
            AddHeavyHitter(fingerprint);
            switch (_column.DataType)
            {
                case TableColumnType.Int64:
                    AddInt64HistogramSample(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case TableColumnType.Float64:
                    AddFloat64HistogramSample(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case TableColumnType.DateTime:
                    AddInt64HistogramSample(ToUnixMilliseconds(value));
                    break;
            }
        }

        public TableColumnStatistics Build(
            long tableRows,
            double sampleRate,
            TableStatisticsRefreshOptions options)
        {
            long sampledRows = checked(_nullRows + _nonNullRows);
            double nullFraction = sampledRows == 0 ? 0 : (double)_nullRows / sampledRows;
            long nonNullRows = Math.Max(0, tableRows - (long)Math.Round(tableRows * nullFraction));
            long sampleDistinct = _nonNullRows == 0 ? 0 : _distinct.Estimate();
            long estimatedDistinct = sampleRate <= 0
                ? 0
                : Math.Min(nonNullRows, Math.Max(sampleDistinct, (long)Math.Round(sampleDistinct / sampleRate)));

            TableMostCommonValue[] mostCommon = _heavyHitters
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key)
                .Take(options.MostCommonValueCount)
                .Select(pair => new TableMostCommonValue(
                    pair.Key,
                    sampleRate <= 0 ? 0 : Math.Max(1, (long)Math.Round(pair.Value / sampleRate))))
                .ToArray();

            return new TableColumnStatistics(
                _column.Name,
                nullFraction,
                estimatedDistinct,
                _nonNullRows,
                mostCommon,
                BuildHistogram(sampleRate, options.HistogramBucketCount));
        }

        private void AddHeavyHitter(ulong fingerprint)
        {
            if (_heavyHitters.TryGetValue(fingerprint, out long count))
            {
                _heavyHitters[fingerprint] = count + 1;
                return;
            }
            if (_heavyHitters.Count < _heavyHitterCapacity)
            {
                _heavyHitters.Add(fingerprint, 1);
                return;
            }

            ulong[] keys = _heavyHitters.Keys.ToArray();
            foreach (ulong key in keys)
            {
                long next = _heavyHitters[key] - 1;
                if (next == 0)
                    _heavyHitters.Remove(key);
                else
                    _heavyHitters[key] = next;
            }
        }

        private void AddInt64HistogramSample(long value)
        {
            _numericRows++;
            if (_int64HistogramSamples.Count < _maxHistogramSamples)
            {
                _int64HistogramSamples.Add(value);
                return;
            }

            ulong slot = NextRandom() % (ulong)_numericRows;
            if (slot < (ulong)_maxHistogramSamples)
                _int64HistogramSamples[(int)slot] = value;
        }

        private void AddFloat64HistogramSample(double value)
        {
            if (!double.IsFinite(value))
                return;
            _numericRows++;
            if (_float64HistogramSamples.Count < _maxHistogramSamples)
            {
                _float64HistogramSamples.Add(value);
                return;
            }

            ulong slot = NextRandom() % (ulong)_numericRows;
            if (slot < (ulong)_maxHistogramSamples)
                _float64HistogramSamples[(int)slot] = value;
        }

        private IReadOnlyList<TableHistogramBucket> BuildHistogram(double sampleRate, int bucketCount)
        {
            if (bucketCount == 0)
                return Array.Empty<TableHistogramBucket>();

            if (_column.DataType is TableColumnType.Int64 or TableColumnType.DateTime)
                return BuildInt64Histogram(sampleRate, bucketCount);
            if (_column.DataType == TableColumnType.Float64)
                return BuildFloat64Histogram(sampleRate, bucketCount);
            return Array.Empty<TableHistogramBucket>();
        }

        private IReadOnlyList<TableHistogramBucket> BuildInt64Histogram(double sampleRate, int bucketCount)
        {
            if (_int64HistogramSamples.Count == 0)
                return Array.Empty<TableHistogramBucket>();

            _int64HistogramSamples.Sort();
            int actualBuckets = Math.Min(bucketCount, _int64HistogramSamples.Count);
            var buckets = new TableHistogramBucket[actualBuckets];
            int previous = 0;
            for (int bucket = 0; bucket < actualBuckets; bucket++)
            {
                int endExclusive = (int)Math.Ceiling((double)(bucket + 1) * _int64HistogramSamples.Count / actualBuckets);
                int sampleRows = endExclusive - previous;
                long estimatedRows = sampleRate <= 0
                    ? 0
                    : Math.Max(1, (long)Math.Round(sampleRows / sampleRate));
                buckets[bucket] = new TableHistogramBucket
                {
                    Int64UpperBound = _int64HistogramSamples[endExclusive - 1],
                    EstimatedRows = estimatedRows,
                };
                previous = endExclusive;
            }
            return buckets;
        }

        private IReadOnlyList<TableHistogramBucket> BuildFloat64Histogram(double sampleRate, int bucketCount)
        {
            if (_float64HistogramSamples.Count == 0)
                return Array.Empty<TableHistogramBucket>();

            _float64HistogramSamples.Sort();
            int actualBuckets = Math.Min(bucketCount, _float64HistogramSamples.Count);
            var buckets = new TableHistogramBucket[actualBuckets];
            int previous = 0;
            for (int bucket = 0; bucket < actualBuckets; bucket++)
            {
                int endExclusive = (int)Math.Ceiling((double)(bucket + 1) * _float64HistogramSamples.Count / actualBuckets);
                int sampleRows = endExclusive - previous;
                long estimatedRows = sampleRate <= 0
                    ? 0
                    : Math.Max(1, (long)Math.Round(sampleRows / sampleRate));
                buckets[bucket] = new TableHistogramBucket
                {
                    Float64UpperBound = _float64HistogramSamples[endExclusive - 1],
                    EstimatedRows = estimatedRows,
                };
                previous = endExclusive;
            }
            return buckets;
        }

        private ulong NextRandom()
        {
            ulong value = _randomState;
            value ^= value << 13;
            value ^= value >> 7;
            value ^= value << 17;
            _randomState = value;
            return value;
        }

        private static long ToUnixMilliseconds(object value)
        {
            return value switch
            {
                DateTimeOffset offset => offset.ToUnixTimeMilliseconds(),
                DateTime dateTime => new DateTimeOffset(
                    dateTime.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                        : dateTime).ToUnixTimeMilliseconds(),
                long milliseconds => milliseconds,
                _ => throw new InvalidOperationException($"无法把 {value.GetType().Name} 转换为 DATETIME。"),
            };
        }
    }
}

internal sealed record TableStatisticsState(
    TableStatistics? Statistics,
    bool IsStale,
    string EstimateSource,
    long? FreshnessMilliseconds);

internal static class TableValueFingerprint
{
    internal static ulong Create(TableColumn column, object value)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);
        var hash = new XxHash64();
        Span<byte> type = stackalloc byte[1];
        type[0] = (byte)column.DataType;
        hash.Append(type);

        Span<byte> scalar = stackalloc byte[8];
        switch (column.DataType)
        {
            case TableColumnType.Int64:
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
                    scalar,
                    Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                hash.Append(scalar);
                break;
            case TableColumnType.Float64:
                System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(
                    scalar,
                    Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                hash.Append(scalar);
                break;
            case TableColumnType.Boolean:
                scalar[0] = (bool)value ? (byte)1 : (byte)0;
                hash.Append(scalar[..1]);
                break;
            case TableColumnType.DateTime:
                long milliseconds = value switch
                {
                    DateTimeOffset offset => offset.ToUnixTimeMilliseconds(),
                    DateTime dateTime => new DateTimeOffset(
                        dateTime.Kind == DateTimeKind.Unspecified
                            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                            : dateTime).ToUnixTimeMilliseconds(),
                    long raw => raw,
                    _ => throw new InvalidOperationException($"无法把 {value.GetType().Name} 转换为 DATETIME。"),
                };
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(scalar, milliseconds);
                hash.Append(scalar);
                break;
            case TableColumnType.String:
            case TableColumnType.Json:
                hash.Append(Encoding.UTF8.GetBytes((string)value));
                break;
            case TableColumnType.Blob:
                hash.Append((byte[])value);
                break;
            default:
                throw new InvalidOperationException($"不支持的关系表类型 {column.DataType}。");
        }

        return hash.GetCurrentHashAsUInt64();
    }
}
