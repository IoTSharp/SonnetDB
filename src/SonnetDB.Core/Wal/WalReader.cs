using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using SonnetDB.Buffers;
using SonnetDB.IO;
using SonnetDB.Storage.Format;

namespace SonnetDB.Wal;

/// <summary>
/// WAL（Write-Ahead Log）读取器，支持顺序回放，对文件尾截断和 CRC 校验失败优雅停止。
/// </summary>
public sealed class WalReader : IDisposable
{
    private FileStream? _fileStream;
    private bool _disposed;
    private readonly int _formatVersion;
    private readonly AtomicPointWalRecoveryOptions? _recoveryOptions;
    private readonly CancellationToken _recoveryCancellation;
    private readonly long _recoveryStarted;

    /// <summary>WAL 文件的完整路径。</summary>
    public string Path { get; }

    /// <summary>文件头中记录的首条 LSN。</summary>
    public long FirstLsn { get; private set; }

    /// <summary>
    /// 回放完成后最后一条合法记录的 LSN；若无合法记录则等于 <see cref="FirstLsn"/> - 1。
    /// </summary>
    public long LastLsn { get; private set; }

    /// <summary>已读取的字节数（含文件头）。</summary>
    public long BytesRead { get; private set; }

    /// <summary>
    /// 最后一条合法记录之后的文件偏移量，供未来 WAL 修复使用。
    /// </summary>
    public long LastValidOffset { get; private set; }

    private WalReader(string path, FileStream fileStream, long firstLsn, int formatVersion,
        AtomicPointWalRecoveryOptions? recoveryOptions, CancellationToken recoveryCancellation, long recoveryStarted)
    {
        Path = path;
        _fileStream = fileStream;
        FirstLsn = firstLsn;
        LastLsn = firstLsn - 1;
        BytesRead = FormatSizes.WalFileHeaderSize;
        LastValidOffset = FormatSizes.WalFileHeaderSize;
        _formatVersion = formatVersion;
        _recoveryOptions = recoveryOptions;
        _recoveryCancellation = recoveryCancellation;
        _recoveryStarted = recoveryStarted;
    }

    /// <summary>
    /// 打开一个 WAL 文件用于读取。
    /// </summary>
    /// <param name="path">WAL 文件路径。</param>
    /// <returns>已初始化的 <see cref="WalReader"/> 实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> 为 null。</exception>
    /// <exception cref="InvalidDataException">文件头 magic 或版本不合法时抛出。</exception>
    public static WalReader Open(string path)
        => OpenCore(path, TsdbMagic.FormatVersion);

    /// <summary>显式打开 WAL v2 单 Point 原子记录读取器；v1 文件被拒绝。</summary>
    /// <param name="path">WAL v2 文件路径。</param>
    /// <param name="recoveryOptions">记录数、总文件字节和墙钟预算；省略时使用有界默认值。</param>
    /// <param name="cancellationToken">在读取和每个完整 Point 解码前后检查的取消令牌。</param>
    /// <returns>每个完整 Point 返回一个 AtomicPointRecord，损坏或截断的尾记录不返回。</returns>
    /// <remarks>单条不超过 8 MiB 的同步读取/解码不能在内部抢占；前后检查预算与取消。期限从 Open 开始，含调用方枚举间隔。</remarks>
    public static WalReader OpenAtomicPoints(string path, AtomicPointWalRecoveryOptions? recoveryOptions = null,
        CancellationToken cancellationToken = default)
        => OpenCore(path, WalFileHeader.AtomicPointFormatVersion, recoveryOptions ?? new(), cancellationToken);

    internal static WalReader OpenForRecovery(string path, AtomicPointWalRecoveryOptions? recoveryOptions = null,
        CancellationToken cancellationToken = default)
    {
        int formatVersion = ReadFormatVersion(path);
        return OpenCore(path, formatVersion,
            formatVersion == WalFileHeader.AtomicPointFormatVersion
                ? recoveryOptions ?? new()
                : null,
            cancellationToken);
    }

    internal static int ReadFormatVersion(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] bytes = new byte[FormatSizes.WalFileHeaderSize];
        if (ReadExact(stream, bytes, 0, bytes.Length) != bytes.Length)
            throw new InvalidDataException("WAL file is truncated: file header incomplete.");
        var reader = new SpanReader(bytes);
        WalFileHeader header = reader.ReadStruct<WalFileHeader>();
        if (!header.IsValidFor(TsdbMagic.FormatVersion) && !header.IsValidFor(WalFileHeader.AtomicPointFormatVersion))
            throw new InvalidDataException("WAL file header is invalid: magic or version mismatch.");
        return header.FormatVersion;
    }

    private static WalReader OpenCore(string path, int formatVersion,
        AtomicPointWalRecoveryOptions? recoveryOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        recoveryOptions?.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        long recoveryStarted = Stopwatch.GetTimestamp();

        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            if (recoveryOptions is not null && fs.Length > recoveryOptions.MaxReadBytes)
                throw new InvalidDataException("WAL v2 recovery exceeds its total file byte budget.");
            byte[] headerBuf = ArrayPool<byte>.Shared.Rent(FormatSizes.WalFileHeaderSize);
            try
            {
                int read = ReadExact(fs, headerBuf, 0, FormatSizes.WalFileHeaderSize);
                if (read < FormatSizes.WalFileHeaderSize)
                    throw new InvalidDataException("WAL file is truncated: file header incomplete.");

                var reader = new SpanReader(headerBuf.AsSpan(0, FormatSizes.WalFileHeaderSize));
                var fileHeader = reader.ReadStruct<WalFileHeader>();

                if (!fileHeader.IsValidFor(formatVersion))
                    throw new InvalidDataException("WAL file header is invalid: magic or version mismatch.");

                var result = new WalReader(path, fs, fileHeader.FirstLsn, formatVersion,
                    recoveryOptions, cancellationToken, recoveryStarted);
                result.CheckAtomicRecoveryBudget();
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headerBuf);
            }
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 顺序回放所有合法记录。遇到截断或 CRC 校验失败时停止迭代，不抛出异常。
    /// </summary>
    /// <returns>按 LSN 顺序的 <see cref="WalRecord"/> 序列。</returns>
    /// <exception cref="ObjectDisposedException">读取器已关闭时抛出。</exception>
    public IEnumerable<WalRecord> Replay()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WalReader));

        _fileStream!.Position = FormatSizes.WalFileHeaderSize;
        BytesRead = FormatSizes.WalFileHeaderSize;
        LastValidOffset = FormatSizes.WalFileHeaderSize;
        LastLsn = FirstLsn - 1;
        CheckAtomicRecoveryBudget();
        long replayEnd = _fileStream.Length;
        int records = 0;

        byte[] headerBuf = ArrayPool<byte>.Shared.Rent(FormatSizes.WalRecordHeaderSize);
        try
        {
            while (_fileStream.Position < replayEnd)
            {
                CheckAtomicRecoveryBudget();
                if (_recoveryOptions is not null && _fileStream.Position >= replayEnd)
                    yield break;
                // Read record header
                int headerRead = ReadExact(_fileStream, headerBuf, 0, FormatSizes.WalRecordHeaderSize);
                if (headerRead < FormatSizes.WalRecordHeaderSize)
                    yield break;

                ReadOnlySpan<byte> headerSpan = headerBuf.AsSpan(0, FormatSizes.WalRecordHeaderSize);
                var headerReader = new SpanReader(headerSpan);
                var header = headerReader.ReadStruct<WalRecordHeader>();

                if (!header.IsShapeValid(headerSpan))
                    yield break;

                if (header.PayloadLength < 0)
                    yield break;

                if (_formatVersion == WalFileHeader.AtomicPointFormatVersion &&
                    (header.PayloadLength > AtomicPointWalCodec.MaxPayloadBytes ||
                     header.Lsn == long.MaxValue || header.Lsn != LastLsn + 1 ||
                     !IsAtomicPointV2RecordType(header.RecordType)))
                    yield break;

                if (_recoveryOptions is not null && records >= _recoveryOptions.MaxRecords)
                    throw new InvalidDataException("WAL v2 recovery exceeds its record budget.");

                if (header.PayloadLength > (_recoveryOptions is null ? _fileStream.Length : replayEnd) - _fileStream.Position)
                    yield break;

                byte[] payloadBuf = ArrayPool<byte>.Shared.Rent(Math.Max(header.PayloadLength, 1));
                try
                {
                    int payloadRead = header.PayloadLength > 0
                        ? ReadExact(_fileStream, payloadBuf, 0, header.PayloadLength)
                        : 0;

                    if (payloadRead < header.PayloadLength)
                        yield break;

                    // Verify CRC32
                    uint actualCrc = Crc32.HashToUInt32(payloadBuf.AsSpan(0, header.PayloadLength));
                    if (actualCrc != header.PayloadCrc32)
                        yield break;
                    CheckAtomicRecoveryBudget();

                    // Decode payload — catch InvalidDataException without a yield inside try/catch
                    WalRecord? record = null;
                    bool shouldStop = false;
                    try
                    {
                        if (_formatVersion == WalFileHeader.AtomicPointFormatVersion && header.RecordType != WalRecordType.AtomicPoint)
                            AtomicPointWalCodec.ValidateControlPayload(payloadBuf.AsSpan(0, header.PayloadLength), header.RecordType, header.Lsn);
                        var payloadReader = new SpanReader(payloadBuf.AsSpan(0, header.PayloadLength));
                        record = header.RecordType switch
                        {
                            WalRecordType.WritePoint =>
                                WalPayloadCodec.ReadWritePointPayload(payloadReader, header.Lsn, header.Timestamp),
                            WalRecordType.CreateSeries =>
                                WalPayloadCodec.ReadCreateSeriesPayload(payloadReader, header.Lsn, header.Timestamp),
                            WalRecordType.Checkpoint =>
                                WalPayloadCodec.ReadCheckpointPayload(payloadReader, header.Lsn, header.Timestamp),
                            WalRecordType.Truncate =>
                                new TruncateRecord(header.Lsn, header.Timestamp),
                            WalRecordType.Delete =>
                                WalPayloadCodec.ReadDeletePayload(payloadReader, header.Lsn, header.Timestamp),
                            WalRecordType.AtomicPoint when _formatVersion == WalFileHeader.AtomicPointFormatVersion =>
                                AtomicPointWalCodec.Decode(payloadBuf.AsSpan(0, header.PayloadLength), header.Lsn, header.Timestamp),
                            _ => null,
                        };
                    }
                    catch (InvalidDataException)
                    {
                        shouldStop = true;
                    }
                    catch (Exception ex) when (_formatVersion == WalFileHeader.AtomicPointFormatVersion &&
                        ex is InvalidOperationException or ArgumentException or OverflowException or IndexOutOfRangeException)
                    {
                        shouldStop = true;
                    }
                    CheckAtomicRecoveryBudget();

                    // Update tracking — must happen outside the try/catch above
                    if (!shouldStop)
                    {
                        long recordSize = FormatSizes.WalRecordHeaderSize + header.PayloadLength;
                        BytesRead += recordSize;
                        LastValidOffset += recordSize;
                        LastLsn = header.Lsn;
                        records++;
                    }

                    if (shouldStop)
                        yield break;

                    if (record is not null)
                        yield return record;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payloadBuf);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuf);
        }
    }

    private static bool IsAtomicPointV2RecordType(WalRecordType recordType)
        => recordType is WalRecordType.AtomicPoint
            or WalRecordType.CreateSeries
            or WalRecordType.Checkpoint
            or WalRecordType.Delete
            or WalRecordType.Truncate;

    internal void CheckAtomicRecoveryBudget()
    {
        if (_recoveryOptions is null)
            return;
        _recoveryCancellation.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(_recoveryStarted) >= _recoveryOptions.EffectiveTimeout)
            throw new InvalidDataException("WAL v2 recovery exceeds its wall-clock budget.");
        if (_fileStream!.Length > _recoveryOptions.MaxReadBytes)
            throw new InvalidDataException("WAL v2 recovery exceeds its total file byte budget.");
    }

    /// <summary>
    /// 关闭读取器并释放文件句柄。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _fileStream?.Dispose();
        _fileStream = null;
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
