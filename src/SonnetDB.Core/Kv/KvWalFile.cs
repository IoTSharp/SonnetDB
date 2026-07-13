using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;

namespace SonnetDB.Kv;

internal sealed class KvWalFile : IDisposable
{
    private const int HeaderSize = 64;
    private const int RecordHeaderSize = 32;
    private const int MaxStackPayloadBytes = 1024;
    private const int PayloadPrefixBytesV1 = 8;
    private const int PayloadPrefixBytesV2 = 16;
    private const int HeaderCrcOffset = 28;
    private const int CurrentVersion = 2;

    private static ReadOnlySpan<byte> Magic => "SDBKVWAL"u8;

    private FileStream? _fileStream;
    private BufferedStream? _stream;
    private long _nextSequence;
    private bool _disposed;

    private KvWalFile(string path, FileStream fileStream, BufferedStream stream, long nextSequence)
    {
        Path = path;
        _fileStream = fileStream;
        _stream = stream;
        _nextSequence = nextSequence;
    }

    public string Path { get; }

    public long NextSequence => _nextSequence;

    public static KvWalFile Open(string path, long startSequence, int bufferSize)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        bool fileExists = File.Exists(path) && new FileInfo(path).Length > 0;
        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            long nextSequence = startSequence;
            long validLength = HeaderSize;

            if (fileExists)
            {
                ReadAndValidateHeader(fs);
                (long lastSequence, validLength) = ScanForLastValidRecord(fs);
                if (lastSequence >= 0)
                    nextSequence = Math.Max(startSequence, lastSequence + 1);
                fs.SetLength(validLength);
            }
            else
            {
                WriteHeader(fs, startSequence);
            }

            fs.Position = validLength;
            var stream = new BufferedStream(fs, bufferSize);
            return new KvWalFile(path, fs, stream, nextSequence);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public long AppendPut(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, DateTimeOffset? expiresAtUtc = null)
    {
        ThrowIfDisposed();
        long sequence = _nextSequence++;
        AppendRecord(KvWalRecordKind.Put, sequence, key, value, expiresAtUtc);
        return sequence;
    }

    public long AppendDelete(ReadOnlySpan<byte> key)
    {
        ThrowIfDisposed();
        long sequence = _nextSequence++;
        AppendRecord(KvWalRecordKind.Delete, sequence, key, default, expiresAtUtc: null);
        return sequence;
    }

    /// <summary>追加 generation 切换记录。</summary>
    public long AppendClearGeneration(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(payload, generation);
        long sequence = _nextSequence++;
        AppendRecord(KvWalRecordKind.ClearGeneration, sequence, ReadOnlySpan<byte>.Empty, payload, expiresAtUtc: null);
        return sequence;
    }

    /// <summary>把一批 delete tombstone 编码为单个带 CRC 的 WAL record。</summary>
    public long AppendDeleteBatch(
        long batchId,
        int chunkIndex,
        int totalChunks,
        IReadOnlyList<byte[]> keys)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchId);
        ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalChunks);
        if (chunkIndex >= totalChunks)
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
            throw new ArgumentException("KV batch delete 至少需要一个 key。", nameof(keys));

        const int metadataBytes = sizeof(long) + (sizeof(int) * 3);
        int payloadLength = metadataBytes;
        for (int i = 0; i < keys.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(keys[i]);
            if (keys[i].Length == 0)
                throw new ArgumentException("KV batch delete key 不能为空。", nameof(keys));
            payloadLength = checked(payloadLength + sizeof(int) + keys[i].Length);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(payloadLength);
        Span<byte> payload = rented.AsSpan(0, payloadLength);
        try
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload[..sizeof(long)], batchId);
            BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(sizeof(long), sizeof(int)), chunkIndex);
            BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(sizeof(long) + sizeof(int), sizeof(int)), totalChunks);
            BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(sizeof(long) + (sizeof(int) * 2), sizeof(int)), keys.Count);
            int offset = metadataBytes;
            for (int i = 0; i < keys.Count; i++)
            {
                byte[] key = keys[i];
                BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(offset, sizeof(int)), key.Length);
                offset += sizeof(int);
                key.CopyTo(payload.Slice(offset, key.Length));
                offset += key.Length;
            }

            long sequence = _nextSequence++;
            AppendRecord(KvWalRecordKind.DeleteBatch, sequence, ReadOnlySpan<byte>.Empty, payload, expiresAtUtc: null);
            return sequence;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>追加 batch-delete commit record；恢复端只应用完整且已提交的 chunk 集合。</summary>
    public long AppendDeleteBatchCommit(long batchId, int totalChunks, int totalKeys)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalChunks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalKeys);
        Span<byte> payload = stackalloc byte[sizeof(long) + (sizeof(int) * 2)];
        BinaryPrimitives.WriteInt64LittleEndian(payload[..sizeof(long)], batchId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(sizeof(long), sizeof(int)), totalChunks);
        BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(sizeof(long) + sizeof(int), sizeof(int)), totalKeys);
        long sequence = _nextSequence++;
        AppendRecord(KvWalRecordKind.DeleteBatchCommit, sequence, ReadOnlySpan<byte>.Empty, payload, expiresAtUtc: null);
        return sequence;
    }

    public void Sync()
    {
        ThrowIfDisposed();
        _stream!.Flush();
        _fileStream!.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _stream?.Flush();
            _fileStream?.Flush(flushToDisk: true);
        }
        finally
        {
            _stream?.Dispose();
            _fileStream = null;
            _stream = null;
        }
    }

    public static IEnumerable<KvWalRecord> Replay(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            yield break;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        ReadAndValidateHeader(fs);

        byte[] headerBuffer = new byte[RecordHeaderSize];
        while (true)
        {
            Span<byte> header = headerBuffer;
            int headerRead = ReadExact(fs, header);
            if (headerRead < RecordHeaderSize)
                yield break;

            if (!TryParseRecordHeader(header, fs.Length - fs.Position, out var kind, out long sequence, out int payloadLength))
                yield break;

            byte[] payload = ArrayPool<byte>.Shared.Rent(Math.Max(payloadLength, 1));
            try
            {
                int payloadRead = payloadLength == 0 ? 0 : ReadExact(fs, payload.AsSpan(0, payloadLength));
                if (payloadRead < payloadLength)
                    yield break;

                uint expectedPayloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24, 4));
                uint actualPayloadCrc = Crc32.HashToUInt32(payload.AsSpan(0, payloadLength));
                if (expectedPayloadCrc != actualPayloadCrc)
                    yield break;

                if (!TryReadPayload(
                    kind,
                    payload.AsSpan(0, payloadLength),
                    out byte[] key,
                    out byte[]? value,
                    out DateTimeOffset? expiresAtUtc))
                    yield break;

                yield return new KvWalRecord(
                    kind,
                    sequence,
                    key,
                    kind is KvWalRecordKind.Put
                        or KvWalRecordKind.ClearGeneration
                        or KvWalRecordKind.DeleteBatch
                        or KvWalRecordKind.DeleteBatchCommit
                        ? value
                        : null,
                    expiresAtUtc);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    private void AppendRecord(
        KvWalRecordKind kind,
        long sequence,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> value,
        DateTimeOffset? expiresAtUtc)
    {
        int valueLength = kind == KvWalRecordKind.Delete ? -1 : value.Length;
        int payloadLength = checked(PayloadPrefixBytesV2 + key.Length + Math.Max(valueLength, 0));

        byte[]? rented = null;
        Span<byte> payload = payloadLength <= MaxStackPayloadBytes
            ? stackalloc byte[payloadLength]
            : (rented = ArrayPool<byte>.Shared.Rent(payloadLength)).AsSpan(0, payloadLength);

        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload[..4], key.Length);
            BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(4, 4), valueLength);
            BinaryPrimitives.WriteInt64LittleEndian(payload.Slice(8, 8), expiresAtUtc?.UtcTicks ?? 0);
            key.CopyTo(payload.Slice(PayloadPrefixBytesV2, key.Length));
            if (valueLength > 0)
                value.CopyTo(payload.Slice(PayloadPrefixBytesV2 + key.Length, valueLength));

            Span<byte> header = stackalloc byte[RecordHeaderSize];
            BinaryPrimitives.WriteInt32LittleEndian(header[..4], payloadLength);
            header[4] = (byte)kind;
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(8, 8), sequence);
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(16, 8), DateTime.UtcNow.Ticks);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24, 4), Crc32.HashToUInt32(payload));
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(HeaderCrcOffset, 4), Crc32.HashToUInt32(header[..HeaderCrcOffset]));

            _stream!.Write(header);
            _stream.Write(payload);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void WriteHeader(FileStream fs, long firstSequence)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header[..8]);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), CurrentVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(16, 8), DateTime.UtcNow.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(24, 8), firstSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(60, 4), Crc32.HashToUInt32(header[..60]));
        fs.Position = 0;
        fs.Write(header);
        fs.Flush(flushToDisk: true);
    }

    private static void ReadAndValidateHeader(FileStream fs)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        fs.Position = 0;
        if (ReadExact(fs, header) < HeaderSize)
            throw new InvalidDataException("KV WAL header is truncated.");

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(60, 4));
        uint actualCrc = Crc32.HashToUInt32(header[..60]);
        int version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        if (!header[..8].SequenceEqual(Magic) ||
            version is < 1 or > CurrentVersion ||
            BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4)) != HeaderSize ||
            expectedCrc != actualCrc)
        {
            throw new InvalidDataException("KV WAL header is invalid.");
        }
    }

    private static (long LastSequence, long LastValidOffset) ScanForLastValidRecord(FileStream fs)
    {
        fs.Position = HeaderSize;
        long lastSequence = -1;
        long lastValidOffset = HeaderSize;

        byte[] headerBuffer = new byte[RecordHeaderSize];
        while (true)
        {
            Span<byte> header = headerBuffer;
            int headerRead = ReadExact(fs, header);
            if (headerRead < RecordHeaderSize)
                break;

            if (!TryParseRecordHeader(header, fs.Length - fs.Position, out var kind, out long sequence, out int payloadLength))
                break;

            byte[] payload = ArrayPool<byte>.Shared.Rent(Math.Max(payloadLength, 1));
            try
            {
                int payloadRead = payloadLength == 0 ? 0 : ReadExact(fs, payload.AsSpan(0, payloadLength));
                if (payloadRead < payloadLength)
                    break;

                uint expectedPayloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24, 4));
                uint actualPayloadCrc = Crc32.HashToUInt32(payload.AsSpan(0, payloadLength));
                if (expectedPayloadCrc != actualPayloadCrc)
                    break;

                if (!TryReadPayload(kind, payload.AsSpan(0, payloadLength), out _, out _, out _))
                    break;

                lastSequence = sequence;
                lastValidOffset = fs.Position;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }

        return (lastSequence, lastValidOffset);
    }

    private static bool TryParseRecordHeader(
        ReadOnlySpan<byte> header,
        long remainingBytes,
        out KvWalRecordKind kind,
        out long sequence,
        out int payloadLength)
    {
        kind = default;
        sequence = 0;
        payloadLength = 0;

        uint expectedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(HeaderCrcOffset, 4));
        uint actualHeaderCrc = Crc32.HashToUInt32(header[..HeaderCrcOffset]);
        if (expectedHeaderCrc != actualHeaderCrc)
            return false;

        payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[..4]);
        if (payloadLength < PayloadPrefixBytesV1 || payloadLength > remainingBytes)
            return false;

        byte rawKind = header[4];
        if (rawKind != (byte)KvWalRecordKind.Put
            && rawKind != (byte)KvWalRecordKind.Delete
            && rawKind != (byte)KvWalRecordKind.ClearGeneration
            && rawKind != (byte)KvWalRecordKind.DeleteBatch
            && rawKind != (byte)KvWalRecordKind.DeleteBatchCommit)
            return false;

        kind = (KvWalRecordKind)rawKind;
        sequence = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(8, 8));
        return sequence > 0;
    }

    private static bool TryReadPayload(
        KvWalRecordKind kind,
        ReadOnlySpan<byte> payload,
        out byte[] key,
        out byte[]? value,
        out DateTimeOffset? expiresAtUtc)
    {
        key = [];
        value = null;
        expiresAtUtc = null;

        if (payload.Length < PayloadPrefixBytesV1)
            return false;

        int keyLength = BinaryPrimitives.ReadInt32LittleEndian(payload[..4]);
        int valueLength = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        if (keyLength < 0 || valueLength < -1)
            return false;

        bool validShape = kind switch
        {
            KvWalRecordKind.Put => keyLength > 0 && valueLength >= 0,
            KvWalRecordKind.Delete => keyLength > 0 && valueLength == -1,
            KvWalRecordKind.ClearGeneration => keyLength == 0 && valueLength == sizeof(long),
            KvWalRecordKind.DeleteBatch => keyLength == 0
                && valueLength >= sizeof(long) + (sizeof(int) * 4),
            KvWalRecordKind.DeleteBatchCommit => keyLength == 0
                && valueLength == sizeof(long) + (sizeof(int) * 2),
            _ => false,
        };
        if (!validShape)
            return false;

        int expectedLengthV1 = PayloadPrefixBytesV1 + keyLength + Math.Max(valueLength, 0);
        int expectedLengthV2 = PayloadPrefixBytesV2 + keyLength + Math.Max(valueLength, 0);
        bool isV2 = payload.Length == expectedLengthV2;
        if (!isV2 && payload.Length != expectedLengthV1)
            return false;

        int payloadPrefixBytes = isV2 ? PayloadPrefixBytesV2 : PayloadPrefixBytesV1;
        if (isV2)
        {
            long expiresAtUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(8, 8));
            if (expiresAtUtcTicks < 0)
                return false;
            if (expiresAtUtcTicks > 0)
                expiresAtUtc = new DateTimeOffset(expiresAtUtcTicks, TimeSpan.Zero);
        }

        key = payload.Slice(payloadPrefixBytes, keyLength).ToArray();
        if (valueLength >= 0)
            value = payload.Slice(payloadPrefixBytes + keyLength, valueLength).ToArray();
        return true;
    }

    internal static long DecodeGeneration(KvWalRecord record)
    {
        if (record.Kind != KvWalRecordKind.ClearGeneration || record.Value is not { Length: sizeof(long) })
            throw new InvalidDataException("KV generation WAL record payload is invalid.");
        long generation = BinaryPrimitives.ReadInt64LittleEndian(record.Value);
        if (generation <= 0)
            throw new InvalidDataException("KV generation WAL record contains an invalid generation.");
        return generation;
    }

    internal static KvDeleteBatchChunk DecodeDeleteBatch(KvWalRecord record)
    {
        const int metadataBytes = sizeof(long) + (sizeof(int) * 3);
        if (record.Kind != KvWalRecordKind.DeleteBatch || record.Value is not { Length: >= metadataBytes + sizeof(int) } payload)
            throw new InvalidDataException("KV batch-delete WAL record payload is invalid.");

        long batchId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, sizeof(long)));
        int chunkIndex = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(sizeof(long), sizeof(int)));
        int totalChunks = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(sizeof(long) + sizeof(int), sizeof(int)));
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(sizeof(long) + (sizeof(int) * 2), sizeof(int)));
        if (batchId <= 0 || chunkIndex < 0 || totalChunks <= 0 || chunkIndex >= totalChunks || count <= 0)
            throw new InvalidDataException("KV batch-delete WAL record contains an invalid count.");
        var keys = new List<byte[]>(count);
        int offset = metadataBytes;
        for (int i = 0; i < count; i++)
        {
            if (offset > payload.Length - sizeof(int))
                throw new InvalidDataException("KV batch-delete WAL record is truncated.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, sizeof(int)));
            offset += sizeof(int);
            if (length <= 0 || offset > payload.Length - length)
                throw new InvalidDataException("KV batch-delete WAL record contains an invalid key length.");
            keys.Add(payload.AsSpan(offset, length).ToArray());
            offset += length;
        }

        if (offset != payload.Length)
            throw new InvalidDataException("KV batch-delete WAL record contains trailing bytes.");
        return new KvDeleteBatchChunk(batchId, chunkIndex, totalChunks, keys);
    }

    internal static KvDeleteBatchCommit DecodeDeleteBatchCommit(KvWalRecord record)
    {
        if (record.Kind != KvWalRecordKind.DeleteBatchCommit
            || record.Value is not { Length: sizeof(long) + (sizeof(int) * 2) } payload)
        {
            throw new InvalidDataException("KV batch-delete commit WAL record payload is invalid.");
        }

        long batchId = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0, sizeof(long)));
        int totalChunks = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(sizeof(long), sizeof(int)));
        int totalKeys = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(sizeof(long) + sizeof(int), sizeof(int)));
        if (batchId <= 0 || totalChunks <= 0 || totalKeys <= 0)
            throw new InvalidDataException("KV batch-delete commit WAL record contains invalid metadata.");
        return new KvDeleteBatchCommit(batchId, totalChunks, totalKeys);
    }

    private static int ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed record KvDeleteBatchChunk(
    long BatchId,
    int ChunkIndex,
    int TotalChunks,
    IReadOnlyList<byte[]> Keys);

internal readonly record struct KvDeleteBatchCommit(long BatchId, int TotalChunks, int TotalKeys);
