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
                    payload.AsSpan(0, payloadLength),
                    out byte[] key,
                    out byte[]? value,
                    out DateTimeOffset? expiresAtUtc))
                    yield break;

                yield return new KvWalRecord(kind, sequence, key, kind == KvWalRecordKind.Put ? value : null, expiresAtUtc);
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
        int valueLength = kind == KvWalRecordKind.Put ? value.Length : -1;
        int payloadLength = PayloadPrefixBytesV2 + key.Length + Math.Max(valueLength, 0);

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

            if (!TryParseRecordHeader(header, fs.Length - fs.Position, out _, out long sequence, out int payloadLength))
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

                if (!TryReadPayload(payload.AsSpan(0, payloadLength), out _, out _, out _))
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
        if (rawKind != (byte)KvWalRecordKind.Put && rawKind != (byte)KvWalRecordKind.Delete)
            return false;

        kind = (KvWalRecordKind)rawKind;
        sequence = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(8, 8));
        return sequence > 0;
    }

    private static bool TryReadPayload(
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
        if (keyLength <= 0 || valueLength < -1)
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
