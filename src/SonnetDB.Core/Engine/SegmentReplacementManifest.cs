using System.Buffers;
using System.IO.Hashing;
using SonnetDB.IO;
using SonnetDB.Wal;

namespace SonnetDB.Engine;

internal enum SegmentReplacementState
{
    Pending = 0,
    Committed = 1,
}

internal sealed record SegmentReplacementRecord(
    long ReplacementSegmentId,
    IReadOnlyList<long> SourceSegmentIds,
    SegmentReplacementState State);

internal sealed class SegmentReplacementManifest
{
    private static readonly byte[] Magic = "SDBREPL1"u8.ToArray();
    private static readonly object Sync = new();

    private const int FormatVersion = 1;
    private const int HeaderSize = 32;
    private const int FooterSize = 16;
    private const int RecordFixedSize = 24;

    private readonly IReadOnlyList<SegmentReplacementRecord> _records;

    private SegmentReplacementManifest(IReadOnlyList<SegmentReplacementRecord> records)
    {
        _records = records;
    }

    public static SegmentReplacementManifest Empty { get; } = new([]);

    public IReadOnlyList<SegmentReplacementRecord> Records => _records;

    public long MaxSegmentId
    {
        get
        {
            long max = 0;
            foreach (var record in _records)
            {
                if (record.ReplacementSegmentId > max)
                    max = record.ReplacementSegmentId;

                foreach (long sourceId in record.SourceSegmentIds)
                {
                    if (sourceId > max)
                        max = sourceId;
                }
            }

            return max;
        }
    }

    public static SegmentReplacementManifest LoadForRoot(string root)
        => Load(TsdbPaths.SegmentReplacementManifestPath(root));

    public static SegmentReplacementManifest Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
            return Empty;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Load(fs);
    }

    public static void RecordPendingReplacement(string root, long replacementSegmentId, IReadOnlyList<long> sourceSegmentIds)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(sourceSegmentIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(replacementSegmentId);

        if (sourceSegmentIds.Count == 0)
            throw new ArgumentException("sourceSegmentIds 不能为空。", nameof(sourceSegmentIds));

        Mutate(root, records =>
        {
            RemoveByReplacementId(records, replacementSegmentId);
            records.Add(new SegmentReplacementRecord(
                replacementSegmentId,
                NormalizeSegmentIds(sourceSegmentIds),
                SegmentReplacementState.Pending));
        });
    }

    public static void CommitReplacement(string root, long replacementSegmentId, IReadOnlyList<long> sourceSegmentIds)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(sourceSegmentIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(replacementSegmentId);

        if (sourceSegmentIds.Count == 0)
            throw new ArgumentException("sourceSegmentIds 不能为空。", nameof(sourceSegmentIds));

        Mutate(root, records =>
        {
            RemoveByReplacementId(records, replacementSegmentId);
            records.Add(new SegmentReplacementRecord(
                replacementSegmentId,
                NormalizeSegmentIds(sourceSegmentIds),
                SegmentReplacementState.Committed));
        });
    }

    public static void CommitDroppedSegments(string root, IReadOnlyList<long> sourceSegmentIds)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(sourceSegmentIds);

        if (sourceSegmentIds.Count == 0)
            return;

        Mutate(root, records =>
        {
            records.Add(new SegmentReplacementRecord(
                0,
                NormalizeSegmentIds(sourceSegmentIds),
                SegmentReplacementState.Committed));
        });
    }

    public HashSet<long> GetSegmentIdsToSuppress(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var suppressed = new HashSet<long>();
        foreach (var record in _records)
        {
            if (record.State == SegmentReplacementState.Pending)
            {
                if (record.ReplacementSegmentId > 0)
                    suppressed.Add(record.ReplacementSegmentId);
                continue;
            }

            if (record.ReplacementSegmentId > 0 && !IsReplacementSegmentReadable(root, record.ReplacementSegmentId))
            {
                suppressed.Add(record.ReplacementSegmentId);
                continue;
            }

            foreach (long sourceId in record.SourceSegmentIds)
                suppressed.Add(sourceId);
        }

        return suppressed;
    }

    private static bool IsReplacementSegmentReadable(string root, long segmentId)
    {
        if (!TsdbPaths.TryGetSegmentPath(root, segmentId, out string path))
            return false;

        try
        {
            using var reader = Storage.Segments.SegmentReader.Open(path);
            return reader.Header.SegmentId == segmentId;
        }
        catch (Storage.Segments.SegmentCorruptedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Mutate(string root, Action<List<SegmentReplacementRecord>> mutate)
    {
        string path = TsdbPaths.SegmentReplacementManifestPath(root);
        lock (Sync)
        {
            var records = Load(path).Records.ToList();
            mutate(records);
            Save(path, new SegmentReplacementManifest(records.AsReadOnly()));
        }
    }

    private static void RemoveByReplacementId(List<SegmentReplacementRecord> records, long replacementSegmentId)
    {
        for (int i = records.Count - 1; i >= 0; i--)
        {
            if (records[i].ReplacementSegmentId == replacementSegmentId)
                records.RemoveAt(i);
        }
    }

    private static IReadOnlyList<long> NormalizeSegmentIds(IReadOnlyList<long> segmentIds)
    {
        var normalized = new SortedSet<long>();
        foreach (long segmentId in segmentIds)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentId);
            normalized.Add(segmentId);
        }

        return normalized.ToArray();
    }

    private static SegmentReplacementManifest Load(Stream source)
    {
        byte[] header = ArrayPool<byte>.Shared.Rent(HeaderSize);
        try
        {
            if (ReadExact(source, header, 0, HeaderSize) < HeaderSize)
                throw new InvalidDataException("SegmentReplacementManifest: header is truncated.");

            var reader = new SpanReader(header.AsSpan(0, HeaderSize));
            if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
                throw new InvalidDataException("SegmentReplacementManifest: invalid magic in header.");

            int version = reader.ReadInt32();
            if (version != FormatVersion)
                throw new InvalidDataException($"SegmentReplacementManifest: unsupported format version {version}.");

            int headerSize = reader.ReadInt32();
            if (headerSize != HeaderSize)
                throw new InvalidDataException($"SegmentReplacementManifest: unexpected header size {headerSize}.");

            int recordCount = reader.ReadInt32();
            if (recordCount < 0)
                throw new InvalidDataException("SegmentReplacementManifest: negative record count.");

            var records = new List<SegmentReplacementRecord>(recordCount);
            var crc = new Crc32();

            for (int i = 0; i < recordCount; i++)
            {
                byte[] fixedBytes = ArrayPool<byte>.Shared.Rent(RecordFixedSize);
                try
                {
                    if (ReadExact(source, fixedBytes, 0, RecordFixedSize) < RecordFixedSize)
                        throw new InvalidDataException($"SegmentReplacementManifest: record {i} is truncated.");

                    crc.Append(fixedBytes.AsSpan(0, RecordFixedSize));
                    var fixedReader = new SpanReader(fixedBytes.AsSpan(0, RecordFixedSize));
                    int stateValue = fixedReader.ReadInt32();
                    long replacementSegmentId = fixedReader.ReadInt64();
                    int sourceCount = fixedReader.ReadInt32();
                    _ = fixedReader.ReadInt32();

                    if (stateValue is not 0 and not 1)
                        throw new InvalidDataException($"SegmentReplacementManifest: record {i} has invalid state {stateValue}.");
                    if (replacementSegmentId < 0)
                        throw new InvalidDataException($"SegmentReplacementManifest: record {i} has negative replacement segment id.");
                    if (sourceCount < 0)
                        throw new InvalidDataException($"SegmentReplacementManifest: record {i} has negative source count.");

                    byte[] sourceBytes = ArrayPool<byte>.Shared.Rent(Math.Max(sourceCount * 8, 1));
                    try
                    {
                        int sourceBytesLength = sourceCount * 8;
                        if (sourceBytesLength > 0)
                        {
                            if (ReadExact(source, sourceBytes, 0, sourceBytesLength) < sourceBytesLength)
                                throw new InvalidDataException($"SegmentReplacementManifest: record {i} source list is truncated.");
                            crc.Append(sourceBytes.AsSpan(0, sourceBytesLength));
                        }

                        var sourceReader = new SpanReader(sourceBytes.AsSpan(0, sourceBytesLength));
                        long[] sourceIds = new long[sourceCount];
                        for (int sourceIndex = 0; sourceIndex < sourceIds.Length; sourceIndex++)
                        {
                            long sourceId = sourceReader.ReadInt64();
                            if (sourceId <= 0)
                                throw new InvalidDataException($"SegmentReplacementManifest: record {i} contains invalid source segment id.");
                            sourceIds[sourceIndex] = sourceId;
                        }

                        records.Add(new SegmentReplacementRecord(
                            replacementSegmentId,
                            sourceIds,
                            (SegmentReplacementState)stateValue));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(sourceBytes);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(fixedBytes);
                }
            }

            byte[] footer = ArrayPool<byte>.Shared.Rent(FooterSize);
            try
            {
                if (ReadExact(source, footer, 0, FooterSize) < FooterSize)
                    throw new InvalidDataException("SegmentReplacementManifest: footer is truncated.");

                var footerReader = new SpanReader(footer.AsSpan(0, FooterSize));
                uint storedCrc = footerReader.ReadUInt32();
                if (!footerReader.ReadBytes(Magic.Length).SequenceEqual(Magic))
                    throw new InvalidDataException("SegmentReplacementManifest: invalid magic in footer.");

                uint computedCrc = crc.GetCurrentHashAsUInt32();
                if (computedCrc != storedCrc)
                    throw new InvalidDataException(
                        $"SegmentReplacementManifest: CRC32 mismatch (expected 0x{storedCrc:X8}, got 0x{computedCrc:X8}).");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(footer);
            }

            return new SegmentReplacementManifest(records.AsReadOnly());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    private static void Save(string path, SegmentReplacementManifest manifest)
    {
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        if (directory.Length > 0)
            Directory.CreateDirectory(directory);

        string tempPath = path + ".tmp";
        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bs = new BufferedStream(fs, 65536))
        {
            Save(manifest, bs);
            bs.Flush();
            fs.Flush(true);
        }

        File.Move(tempPath, path, overwrite: true);
        WalCheckpointFile.FlushDirectoryBestEffort(directory);
    }

    private static void Save(SegmentReplacementManifest manifest, Stream destination)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        var headerWriter = new SpanWriter(header);
        headerWriter.WriteBytes(Magic);
        headerWriter.WriteInt32(FormatVersion);
        headerWriter.WriteInt32(HeaderSize);
        headerWriter.WriteInt32(manifest.Records.Count);
        destination.Write(header);

        var crc = new Crc32();
        byte[] fixedBytes = ArrayPool<byte>.Shared.Rent(RecordFixedSize);
        try
        {
            foreach (var record in manifest.Records)
            {
                fixedBytes.AsSpan(0, RecordFixedSize).Clear();
                var fixedWriter = new SpanWriter(fixedBytes.AsSpan(0, RecordFixedSize));
                fixedWriter.WriteInt32((int)record.State);
                fixedWriter.WriteInt64(record.ReplacementSegmentId);
                fixedWriter.WriteInt32(record.SourceSegmentIds.Count);
                fixedWriter.WriteInt32(0);
                destination.Write(fixedBytes, 0, RecordFixedSize);
                crc.Append(fixedBytes.AsSpan(0, RecordFixedSize));

                int sourceBytesLength = record.SourceSegmentIds.Count * 8;
                byte[] sourceBytes = ArrayPool<byte>.Shared.Rent(Math.Max(sourceBytesLength, 1));
                try
                {
                    var sourceWriter = new SpanWriter(sourceBytes.AsSpan(0, sourceBytesLength));
                    foreach (long sourceId in record.SourceSegmentIds)
                        sourceWriter.WriteInt64(sourceId);

                    destination.Write(sourceBytes, 0, sourceBytesLength);
                    crc.Append(sourceBytes.AsSpan(0, sourceBytesLength));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(sourceBytes);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(fixedBytes);
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        var footerWriter = new SpanWriter(footer);
        footerWriter.WriteUInt32(crc.GetCurrentHashAsUInt32());
        footerWriter.WriteBytes(Magic);
        destination.Write(footer);
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
