using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.ExceptionServices;

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
    private long _length;
    private bool _disposed;

    internal Action? DisposeFlushTestHook { get; set; }

    private KvWalFile(
        string path,
        FileStream fileStream,
        BufferedStream stream,
        long firstSequence,
        long nextSequence,
        long length)
    {
        Path = path;
        _fileStream = fileStream;
        _stream = stream;
        FirstSequence = firstSequence;
        _nextSequence = nextSequence;
        _length = length;
    }

    public string Path { get; }

    public long FirstSequence { get; }

    public long NextSequence => _nextSequence;

    public long Length => _length;

    public bool HasRecords => _length > HeaderSize;

    public static KvWalFile Open(string path, long startSequence, int bufferSize)
        => Open(
            path,
            startSequence,
            bufferSize,
            replayRecord: null,
            requireStartSequence: false,
            requireRecordSequenceContinuity: false,
            allowLegacyFirstRecordSequence: false);

    public static KvWalFile Open(
        string path,
        long startSequence,
        int bufferSize,
        Action<KvWalRecord>? replayRecord,
        bool requireStartSequence = false,
        bool requireRecordSequenceContinuity = false,
        bool allowLegacyFirstRecordSequence = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        bool fileExists = File.Exists(path) && new FileInfo(path).Length > 0;
        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            long firstSequence = startSequence;
            long nextSequence = startSequence;
            long validLength = HeaderSize;

            if (fileExists)
            {
                firstSequence = ReadAndValidateHeader(fs);
                if (requireStartSequence && firstSequence != startSequence)
                {
                    throw new InvalidDataException(
                        $"KV active WAL sequence chain is discontinuous: expected {startSequence}, " +
                        $"but '{System.IO.Path.GetFileName(path)}' starts at {firstSequence}.");
                }
                (long lastSequence, validLength) = ScanForLastValidRecord(
                    fs,
                    firstSequence,
                    replayRecord,
                    requireRecordSequenceContinuity,
                    allowLegacyFirstRecordSequence);
                if (lastSequence >= 0)
                    nextSequence = Math.Max(startSequence, lastSequence + 1);
                fs.SetLength(validLength);

                if (lastSequence < 0 && firstSequence != nextSequence)
                {
                    fs.SetLength(0);
                    WriteHeader(fs, nextSequence);
                    firstSequence = nextSequence;
                }
            }
            else
            {
                WriteHeader(fs, startSequence);
            }

            fs.Position = validLength;
            var stream = new BufferedStream(fs, bufferSize);
            return new KvWalFile(path, fs, stream, firstSequence, nextSequence, validLength);
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

    public void Seal(string sealedPath)
    {
        ArgumentNullException.ThrowIfNull(sealedPath);
        ThrowIfDisposed();
        Dispose();
        File.Move(Path, sealedPath);
        SonnetDB.Wal.DirectoryFsync.FlushBestEffort(
            System.IO.Path.GetDirectoryName(sealedPath) ?? string.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        BufferedStream? stream = _stream;
        FileStream? fileStream = _fileStream;
        _stream = null;
        _fileStream = null;
        Exception? failure = null;
        try
        {
            DisposeFlushTestHook?.Invoke();
            stream?.Flush();
            fileStream?.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            stream?.Dispose();
        }
        catch (Exception) when (failure is not null)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            fileStream?.Dispose();
        }
        catch (Exception) when (failure is not null)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public static IEnumerable<KvWalRecord> Replay(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return LooksLikeSealedWalPath(path)
            ? ReplaySealedRecords(path, expectedFirstSequence: null)
            : ReplayRecoverable(path);
    }

    internal static KvWalHeaderInfo ReadHeaderInfo(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ReadAndValidateHeaderInfo(fs);
    }

    internal static KvWalReplayInfo ReplaySealed(
        string path,
        Action<KvWalRecord> replayRecord,
        long? expectedFirstSequence = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(replayRecord);
        if (expectedFirstSequence is <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedFirstSequence));

        long firstSequence = 0;
        long lastSequence = 0;
        long recordCount = 0;
        foreach (KvWalRecord record in ReplaySealedRecords(path, expectedFirstSequence))
        {
            if (recordCount == 0)
                firstSequence = record.Sequence;
            lastSequence = record.Sequence;
            recordCount++;
            replayRecord(record);
        }

        return new KvWalReplayInfo(firstSequence, lastSequence, recordCount);
    }

    private static IEnumerable<KvWalRecord> ReplayRecoverable(string path)
    {
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

    private static IEnumerable<KvWalRecord> ReplaySealedRecords(
        string path,
        long? expectedFirstSequence)
    {
        if (!TryParseSealedWalEndSequence(path, out long filenameEndSequence))
            throw new InvalidDataException($"KV sealed WAL filename is invalid: '{System.IO.Path.GetFileName(path)}'.");
        if (!File.Exists(path))
            throw new FileNotFoundException("KV sealed WAL does not exist.", path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long headerFirstSequence = ReadAndValidateHeader(fs);
        if (expectedFirstSequence.HasValue && headerFirstSequence != expectedFirstSequence.GetValueOrDefault())
        {
            throw new InvalidDataException(
                $"KV sealed WAL sequence chain is discontinuous: expected {expectedFirstSequence.Value}, " +
                $"but '{System.IO.Path.GetFileName(path)}' starts at {headerFirstSequence}.");
        }

        long nextSequence = headerFirstSequence;
        long lastSequence = 0;
        long recordCount = 0;
        byte[] headerBuffer = new byte[RecordHeaderSize];
        while (fs.Position < fs.Length)
        {
            Span<byte> header = headerBuffer;
            int headerRead = ReadExact(fs, header);
            if (headerRead < RecordHeaderSize)
                throw InvalidSealedWal(path, "record header is truncated");

            if (!TryParseRecordHeader(
                header,
                fs.Length - fs.Position,
                out var kind,
                out long sequence,
                out int payloadLength))
            {
                throw InvalidSealedWal(path, "record header is invalid or its payload is truncated");
            }

            if (sequence != nextSequence)
            {
                throw InvalidSealedWal(
                    path,
                    $"record sequence is discontinuous: expected {nextSequence}, found {sequence}");
            }

            byte[] payload = ArrayPool<byte>.Shared.Rent(Math.Max(payloadLength, 1));
            try
            {
                int payloadRead = payloadLength == 0 ? 0 : ReadExact(fs, payload.AsSpan(0, payloadLength));
                if (payloadRead < payloadLength)
                    throw InvalidSealedWal(path, "record payload is truncated");

                uint expectedPayloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24, 4));
                uint actualPayloadCrc = Crc32.HashToUInt32(payload.AsSpan(0, payloadLength));
                if (expectedPayloadCrc != actualPayloadCrc)
                    throw InvalidSealedWal(path, "record payload CRC is invalid");

                if (!TryReadPayload(
                    kind,
                    payload.AsSpan(0, payloadLength),
                    out byte[] key,
                    out byte[]? value,
                    out DateTimeOffset? expiresAtUtc))
                {
                    throw InvalidSealedWal(path, "record payload is invalid");
                }

                lastSequence = sequence;
                recordCount++;
                if (sequence == long.MaxValue)
                    throw InvalidSealedWal(path, "record sequence exceeds the supported range");
                nextSequence = sequence + 1;
                yield return CreateRecord(kind, sequence, key, value, expiresAtUtc);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }

        if (recordCount == 0)
            throw InvalidSealedWal(path, "file contains no records");
        if (lastSequence != filenameEndSequence)
        {
            throw InvalidSealedWal(
                path,
                $"filename end sequence {filenameEndSequence} does not match final record {lastSequence}");
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
            _length = checked(_length + RecordHeaderSize + payloadLength);
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

    private static long ReadAndValidateHeader(FileStream fs)
        => ReadAndValidateHeaderInfo(fs).FirstSequence;

    private static KvWalHeaderInfo ReadAndValidateHeaderInfo(FileStream fs)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        fs.Position = 0;
        if (ReadExact(fs, header) < HeaderSize)
            throw new InvalidDataException("KV WAL header is truncated.");

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(60, 4));
        uint actualCrc = Crc32.HashToUInt32(header[..60]);
        int version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        long createdUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(16, 8));
        long firstSequence = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(24, 8));
        if (!header[..8].SequenceEqual(Magic) ||
            version is < 1 or > CurrentVersion ||
            BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4)) != HeaderSize ||
            firstSequence <= 0 ||
            expectedCrc != actualCrc)
        {
            throw new InvalidDataException("KV WAL header is invalid.");
        }

        return new KvWalHeaderInfo(firstSequence, createdUtcTicks);
    }

    private static (long LastSequence, long LastValidOffset) ScanForLastValidRecord(
        FileStream fs,
        long firstSequence,
        Action<KvWalRecord>? replayRecord,
        bool requireSequenceContinuity,
        bool allowLegacyFirstRecordSequence)
    {
        fs.Position = HeaderSize;
        long lastSequence = -1;
        long lastValidOffset = HeaderSize;
        long expectedSequence = firstSequence;
        bool firstRecord = true;

        byte[] headerBuffer = new byte[RecordHeaderSize];
        while (true)
        {
            Span<byte> header = headerBuffer;
            int headerRead = ReadExact(fs, header);
            if (headerRead < RecordHeaderSize)
                break;

            if (!TryParseRecordHeader(header, fs.Length - fs.Position, out var kind, out long sequence, out int payloadLength))
                break;
            if (sequence != expectedSequence)
            {
                if (allowLegacyFirstRecordSequence && firstRecord)
                {
                    expectedSequence = sequence;
                }
                else
                {
                    if (requireSequenceContinuity)
                    {
                        throw new InvalidDataException(
                            $"KV active WAL record sequence is discontinuous: expected {expectedSequence}, " +
                            $"found {sequence}.");
                    }

                    break;
                }
            }

            byte[] payload = ArrayPool<byte>.Shared.Rent(Math.Max(payloadLength, 1));
            try
            {
                int payloadRead = payloadLength == 0 ? 0 : ReadExact(fs, payload.AsSpan(0, payloadLength));
                if (payloadRead < payloadLength)
                    break;

                uint expectedPayloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24, 4));
                uint actualPayloadCrc = Crc32.HashToUInt32(payload.AsSpan(0, payloadLength));
                if (expectedPayloadCrc != actualPayloadCrc)
                {
                    ThrowIfActiveWalHasLaterBytes(fs, "payload CRC mismatch");
                    break;
                }

                if (!TryReadPayload(
                    kind,
                    payload.AsSpan(0, payloadLength),
                    out byte[] key,
                    out byte[]? value,
                    out DateTimeOffset? expiresAtUtc))
                {
                    ThrowIfActiveWalHasLaterBytes(fs, "invalid payload");
                    break;
                }

                lastSequence = sequence;
                lastValidOffset = fs.Position;
                replayRecord?.Invoke(CreateRecord(kind, sequence, key, value, expiresAtUtc));
                firstRecord = false;
                if (sequence == long.MaxValue)
                    break;
                expectedSequence = sequence + 1;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }

        return (lastSequence, lastValidOffset);
    }

    private static void ThrowIfActiveWalHasLaterBytes(FileStream fs, string reason)
    {
        if (fs.Position < fs.Length)
        {
            throw new InvalidDataException(
                $"KV active WAL contains {reason} before the physical tail; refusing to discard later records.");
        }
    }

    private static bool LooksLikeSealedWalPath(string path)
    {
        string fileName = System.IO.Path.GetFileName(path);
        return fileName.StartsWith("sealed-", StringComparison.Ordinal)
            && fileName.EndsWith(".SDBKVWAL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseSealedWalEndSequence(string path, out long sequence)
    {
        const string Prefix = "sealed-";
        sequence = 0;
        if (!LooksLikeSealedWalPath(path))
            return false;

        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        return long.TryParse(
            name.AsSpan(Prefix.Length),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out sequence)
            && sequence > 0;
    }

    private static InvalidDataException InvalidSealedWal(string path, string reason)
        => new($"KV sealed WAL '{System.IO.Path.GetFileName(path)}' is invalid: {reason}.");

    private static KvWalRecord CreateRecord(
        KvWalRecordKind kind,
        long sequence,
        byte[] key,
        byte[]? value,
        DateTimeOffset? expiresAtUtc)
        => new(
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

internal readonly record struct KvWalReplayInfo(long FirstSequence, long LastSequence, long RecordCount)
{
    public long NextSequence => checked(LastSequence + 1);
}

internal readonly record struct KvWalHeaderInfo(long FirstSequence, long CreatedUtcTicks);
