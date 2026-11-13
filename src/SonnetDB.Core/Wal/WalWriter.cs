using System.Buffers;
using System.IO.Hashing;
using SonnetDB.Buffers;
using SonnetDB.IO;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Wal;

/// <summary>
/// WAL（Write-Ahead Log）写入器，支持 append-only 写入，含 CRC32 校验与 fsync 持久化。
/// </summary>
/// <remarks>
/// 写入流程：为每条记录计算 payload → 计算 CRC32 → 写 WalRecordHeader → 写 Payload。
/// 调用 <see cref="Sync"/> 或 <see cref="Dispose"/> 可确保数据落盘。
/// </remarks>
public sealed class WalWriter : IDisposable
{
    private const int MaxStackRecordSize = 1024;

    private FileStream? _fileStream;
    private BufferedStream? _stream;
    private long _nextLsn;
    private long _bytesWritten;
    private bool _footerDirty;
    private bool _disposed;
    private int _formatVersion;
    private Exception? _atomicAppendFault;

    internal Action? AfterAtomicPointWriteTestHook { get; set; }

    internal void FlushUnacknowledgedPointBytesToOsForTest() => _stream!.Flush();

    /// <summary>WAL 文件的完整路径。</summary>
    public string Path { get; }

    /// <summary>下一个将分配的 LSN（日志序列号）。</summary>
    public long NextLsn => _nextLsn;

    /// <summary>累计写入的字节数（包括文件头和所有记录，不包括可选 LastLsn footer）。</summary>
    public long BytesWritten => _bytesWritten;

    /// <summary>写入器是否处于打开状态。</summary>
    public bool IsOpen => !_disposed;

    internal bool OpenedUsingLastLsnFooter { get; }

    internal bool HasAppendFault => _atomicAppendFault is not null;

    internal int FormatVersion => _formatVersion;

    private WalWriter(
        string path,
        FileStream fileStream,
        BufferedStream stream,
        long nextLsn,
        long bytesWritten,
        bool footerDirty,
        bool openedUsingLastLsnFooter)
    {
        Path = path;
        _fileStream = fileStream;
        _stream = stream;
        _nextLsn = nextLsn;
        _bytesWritten = bytesWritten;
        _footerDirty = footerDirty;
        OpenedUsingLastLsnFooter = openedUsingLastLsnFooter;
    }

    /// <summary>
    /// 打开（或创建）一个 WAL 文件用于追加写入。
    /// </summary>
    /// <param name="path">WAL 文件路径（扩展名通常为 .SDBWAL）。</param>
    /// <param name="startLsn">新文件时的起始 LSN（默认为 1）。</param>
    /// <param name="bufferSize">写缓冲区大小（默认 64KB）。</param>
    /// <returns>已初始化的 <see cref="WalWriter"/> 实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> 为 null。</exception>
    /// <exception cref="InvalidDataException">文件存在但 magic 或版本不合法时抛出。</exception>
    public static WalWriter Open(string path, long startLsn = 1, int bufferSize = 64 * 1024)
        => OpenCore(path, startLsn, bufferSize, TsdbMagic.FormatVersion);

    /// <summary>显式打开单 Point 原子记录 WAL v2；旧 v1 reader/writer 拒绝此格式。不会升级已有 v1 文件。</summary>
    /// <param name="path">独立 WAL 文件路径。</param>
    /// <param name="startLsn">新文件的起始序列号。</param>
    /// <param name="bufferSize">写缓冲字节数。</param>
    /// <param name="recoveryOptions">v2 现有文件恢复的记录数、总文件字节和墙钟预算。</param>
    /// <param name="cancellationToken">恢复及截尾前检查的取消令牌，不改变后续追加取消语义。</param>
    /// <returns>显式 v2 写入器；支持整点与目录、检查点、删除控制记录，不自动 fsync。</returns>
    public static WalWriter OpenAtomicPoints(string path, long startLsn = 1, int bufferSize = 64 * 1024,
        AtomicPointWalRecoveryOptions? recoveryOptions = null, CancellationToken cancellationToken = default)
        => OpenCore(path, startLsn, bufferSize, WalFileHeader.AtomicPointFormatVersion, recoveryOptions ?? new(), cancellationToken);

    private static WalWriter OpenCore(string path, long startLsn, int bufferSize, int formatVersion,
        AtomicPointWalRecoveryOptions? recoveryOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        recoveryOptions?.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (formatVersion == WalFileHeader.AtomicPointFormatVersion && (startLsn <= 0 || startLsn == long.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(startLsn), "WAL v2 first LSN must be within 1..Int64.MaxValue-1.");

        bool fileExists = File.Exists(path) && new FileInfo(path).Length > 0;
        long nextLsn = startLsn;
        long bytesWritten = 0L;
        bool footerDirty = false;
        bool openedUsingLastLsnFooter = false;

        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            if (fileExists && fs.Length >= FormatSizes.WalFileHeaderSize)
            {
                // Read existing file header to validate
                byte[] headerBuf = ArrayPool<byte>.Shared.Rent(FormatSizes.WalFileHeaderSize);
                try
                {
                    fs.Position = 0;
                    ReadExact(fs, headerBuf, 0, FormatSizes.WalFileHeaderSize);
                    var reader = new SpanReader(headerBuf.AsSpan(0, FormatSizes.WalFileHeaderSize));
                    var fileHeader = reader.ReadStruct<WalFileHeader>();

                    if (!fileHeader.IsValidFor(formatVersion))
                        throw new InvalidDataException("WAL file header is invalid: magic or version mismatch.");

                    if (formatVersion == WalFileHeader.AtomicPointFormatVersion)
                    {
                        // v2 必须按完整 Point 校验恢复尾部；不能仅凭 footer 证明载荷有效。
                        using var atomicReader = WalReader.OpenAtomicPoints(path, recoveryOptions, cancellationToken);
                        foreach (WalRecord record in atomicReader.Replay())
                            _ = record;
                        bytesWritten = atomicReader.LastValidOffset;
                        nextLsn = checked(atomicReader.LastLsn + 1);
                        atomicReader.CheckAtomicRecoveryBudget();
                    }
                    else if (TryReadLastLsnFooter(fs, fileHeader, out var footer))
                    {
                        bytesWritten = footer.RecordsEndOffset;
                        nextLsn = footer.LastLsn + 1;
                        openedUsingLastLsnFooter = true;
                    }
                    else
                    {
                        // 旧 WAL 或坏 footer：扫描现有记录来确定下一个 LSN。
                        fs.Position = FormatSizes.WalFileHeaderSize;
                        bytesWritten = FormatSizes.WalFileHeaderSize;
                        long lastLsn = ScanForLastLsn(fs, fileHeader.FirstLsn - 1, ref bytesWritten);
                        nextLsn = lastLsn + 1;
                    }

                    // Open 进入 append 模式前移除旧 footer/坏尾部；关闭或 Sync 时会写入新的 footer。
                    cancellationToken.ThrowIfCancellationRequested();
                    fs.SetLength(bytesWritten);
                    footerDirty = true;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(headerBuf);
                }
            }
            else if (!fileExists || fs.Length == 0)
            {
                // New file: write header
                fs.Position = 0;
                WriteFileHeader(fs, startLsn, formatVersion);
                bytesWritten = FormatSizes.WalFileHeaderSize;
                nextLsn = startLsn;
                footerDirty = true;
            }
            else
            {
                throw new InvalidDataException("WAL file is truncated: header is incomplete.");
            }

            // Seek to records end for appending. The optional footer is rewritten on Flush/Sync/Dispose.
            fs.Position = bytesWritten;
            var bs = new BufferedStream(fs, bufferSize);
            return new WalWriter(path, fs, bs, nextLsn, bytesWritten, footerDirty, openedUsingLastLsnFooter)
            {
                _formatVersion = formatVersion,
            };
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 追加一条 WritePoint 记录，返回分配的 LSN。
    /// </summary>
    /// <param name="seriesId">序列唯一标识。</param>
    /// <param name="pointTimestamp">数据点时间戳（Unix 毫秒）。</param>
    /// <param name="fieldName">字段名称。</param>
    /// <param name="value">字段值。</param>
    /// <returns>分配的 LSN。</returns>
    /// <exception cref="ObjectDisposedException">写入器已关闭时抛出。</exception>
    public long AppendWritePoint(ulong seriesId, long pointTimestamp, string fieldName, FieldValue value)
    {
        ThrowIfDisposed();
        if (_formatVersion == WalFileHeader.AtomicPointFormatVersion)
            throw new InvalidOperationException("WAL v2 requires AppendAtomicPoint for field writes.");
        ArgumentNullException.ThrowIfNull(fieldName);

        int payloadSize = WalPayloadCodec.MeasureWritePoint(fieldName, value);
        return AppendRecord(WalRecordType.WritePoint, payloadSize, (ref SpanWriter w) =>
            WalPayloadCodec.WriteWritePointPayload(ref w, seriesId, pointTimestamp, fieldName, value));
    }

    /// <summary>在显式 WAL v2 中追加单个完整 Point，最多 1024 字段和 8 MiB 载荷；不自动 fsync。</summary>
    /// <param name="seriesId">序列标识。</param>
    /// <param name="pointTimestamp">Unix 毫秒时间戳。</param>
    /// <param name="fields">非空字段集合，编码及预算校验均在首次记录写入前完成。</param>
    /// <param name="cancellationToken">仅在首次写入前接受取消；开始写入后不能撤回结果。</param>
    /// <returns>单 Point 的 LSN。写入异常属于未知结果，写入器拒绝后续追加，须关闭重开对账。</returns>
    public long AppendAtomicPoint(ulong seriesId, long pointTimestamp,
        IReadOnlyDictionary<string, FieldValue> fields, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfAtomicAppendFault();
        if (_formatVersion != WalFileHeader.AtomicPointFormatVersion)
            throw new InvalidOperationException("Atomic Point records require explicit WAL v2.");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] payload = AtomicPointWalCodec.Encode(seriesId, pointTimestamp, fields);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return AppendRecord(WalRecordType.AtomicPoint, payload.Length,
                (ref SpanWriter writer) => writer.WriteBytes(payload));
        }
        catch (Exception ex)
        {
            _atomicAppendFault = ex;
            throw;
        }
    }

    /// <summary>
    /// 追加一条 CreateSeries 记录，返回分配的 LSN。
    /// </summary>
    /// <param name="seriesId">序列唯一标识。</param>
    /// <param name="measurement">Measurement 名称。</param>
    /// <param name="tags">Tag 键值对。</param>
    /// <returns>分配的 LSN。</returns>
    /// <exception cref="ObjectDisposedException">写入器已关闭时抛出。</exception>
    public long AppendCreateSeries(ulong seriesId, string measurement, IReadOnlyDictionary<string, string> tags)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(tags);
        if (_formatVersion == WalFileHeader.AtomicPointFormatVersion)
        {
            AtomicPointWalCodec.ValidateStrictUtf8(measurement, nameof(measurement));
            foreach (var tag in tags)
            {
                AtomicPointWalCodec.ValidateStrictUtf8(tag.Key, nameof(tags));
                AtomicPointWalCodec.ValidateStrictUtf8(tag.Value, nameof(tags));
            }
        }

        int payloadSize = WalPayloadCodec.MeasureCreateSeries(measurement, tags);
        return AppendRecord(WalRecordType.CreateSeries, payloadSize, (ref SpanWriter w) =>
            WalPayloadCodec.WriteCreateSeriesPayload(ref w, seriesId, measurement, tags));
    }

    /// <summary>
    /// 追加一条 Checkpoint 记录，返回分配的 LSN。
    /// </summary>
    /// <param name="checkpointLsn">检查点 LSN（截止该 LSN 的数据已落盘）。</param>
    /// <returns>分配的 LSN。</returns>
    /// <exception cref="ObjectDisposedException">写入器已关闭时抛出。</exception>
    public long AppendCheckpoint(long checkpointLsn)
    {
        ThrowIfDisposed();
        return AppendRecord(WalRecordType.Checkpoint, 8, (ref SpanWriter w) =>
            WalPayloadCodec.WriteCheckpointPayload(ref w, checkpointLsn));
    }

    /// <summary>
    /// 追加一条 Delete 记录，返回分配的 LSN。
    /// </summary>
    /// <param name="seriesId">序列唯一标识。</param>
    /// <param name="fieldName">字段名称。</param>
    /// <param name="fromTimestamp">删除时间窗起始时间戳（Unix 毫秒，闭区间）。</param>
    /// <param name="toTimestamp">删除时间窗结束时间戳（Unix 毫秒，闭区间）。</param>
    /// <returns>分配的 LSN。</returns>
    /// <exception cref="ObjectDisposedException">写入器已关闭时抛出。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> 为 null 时抛出。</exception>
    public long AppendDelete(ulong seriesId, string fieldName, long fromTimestamp, long toTimestamp)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(fieldName);
        if (_formatVersion == WalFileHeader.AtomicPointFormatVersion)
        {
            AtomicPointWalCodec.ValidateStrictUtf8(fieldName, nameof(fieldName));
            if (System.Text.Encoding.UTF8.GetByteCount(fieldName) > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(fieldName));
        }

        int payloadSize = WalPayloadCodec.MeasureDelete(fieldName);
        return AppendRecord(WalRecordType.Delete, payloadSize, (ref SpanWriter w) =>
            WalPayloadCodec.WriteDeletePayload(ref w, seriesId, fieldName, fromTimestamp, toTimestamp));
    }

    /// <summary>
    /// 把缓冲区刷到 OS（不强制 fsync）。
    /// </summary>
    /// <exception cref="ObjectDisposedException">写入器已关闭时抛出。</exception>
    public void Flush()
    {
        ThrowIfDisposed();
        FlushRequested(flushToDisk: false);
    }

    /// <summary>
    /// 强制 fsync，确保数据持久化到磁盘。
    /// </summary>
    /// <exception cref="ObjectDisposedException">写入器已关闭时抛出。</exception>
    public void Sync()
    {
        ThrowIfDisposed();
        FlushRequested(flushToDisk: true);
    }

    /// <summary>
    /// 关闭写入器并刷盘（fsync）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        Exception? firstError = null;
        try
        {
            FlushCore(flushToDisk: true);
        }
        catch (Exception ex)
        {
            firstError = ex;
        }
        finally
        {
            _disposed = true;
            try
            {
                _stream?.Dispose();
            }
            catch (Exception ex) when (firstError is null)
            {
                firstError = ex;
            }

            try
            {
                _fileStream?.Dispose();
            }
            catch (Exception ex) when (firstError is null)
            {
                firstError = ex;
            }

            _stream = null;
            _fileStream = null;
        }

        if (firstError is not null)
            throw firstError;
    }

    // ── 私有辅助 ─────────────────────────────────────────────────────────────

    private delegate void PayloadWriter(ref SpanWriter writer);

    private void ThrowIfAtomicAppendFault()
    {
        if (_atomicAppendFault is not null)
            throw new InvalidOperationException("WAL append result is unknown; close, reopen and reconcile before writing.", _atomicAppendFault);
    }

    private long AppendRecord(WalRecordType recordType, int payloadSize, PayloadWriter writePayload)
    {
        ThrowIfAtomicAppendFault();
        if (_formatVersion == WalFileHeader.AtomicPointFormatVersion && _nextLsn == long.MaxValue)
            throw new InvalidOperationException("WAL v2 LSN space is exhausted; no record was appended.");
        if (_formatVersion == WalFileHeader.AtomicPointFormatVersion && payloadSize > AtomicPointWalCodec.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payloadSize), "WAL v2 record exceeds its payload budget.");
        long lsn = _nextLsn;
        long now = DateTime.UtcNow.Ticks;
        int recordSize = FormatSizes.WalRecordHeaderSize + payloadSize;

        if (recordSize <= MaxStackRecordSize)
        {
            Span<byte> recordBuffer = stackalloc byte[recordSize];
            WriteRecordToBuffer(recordBuffer, recordType, payloadSize, now, lsn, writePayload);
            WriteEncodedRecord(recordBuffer, recordType);
        }
        else
        {
            byte[] recordBuffer = ArrayPool<byte>.Shared.Rent(recordSize);
            try
            {
                Span<byte> recordSpan = recordBuffer.AsSpan(0, recordSize);
                WriteRecordToBuffer(recordSpan, recordType, payloadSize, now, lsn, writePayload);
                WriteEncodedRecord(recordSpan, recordType);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(recordBuffer);
            }
        }

        _bytesWritten += recordSize;
        Sql.Execution.SqlExecutionTelemetry.RecordPhysicalWrite(recordSize);
        _nextLsn++;
        _footerDirty = true;
        return lsn;
    }

    private void WriteEncodedRecord(ReadOnlySpan<byte> recordBuffer, WalRecordType recordType)
    {
        try
        {
            _stream!.Write(recordBuffer);
            if (recordType == WalRecordType.AtomicPoint)
                AfterAtomicPointWriteTestHook?.Invoke();
        }
        catch (Exception ex) when (_formatVersion == WalFileHeader.AtomicPointFormatVersion)
        {
            _atomicAppendFault = ex;
            throw;
        }
    }

    private void WriteRecordToBuffer(
        Span<byte> recordBuffer,
        WalRecordType recordType,
        int payloadSize,
        long timestamp,
        long lsn,
        PayloadWriter writePayload)
    {
        Span<byte> payloadBuffer = recordBuffer.Slice(FormatSizes.WalRecordHeaderSize, payloadSize);
        var payloadWriter = new SpanWriter(payloadBuffer);
        writePayload(ref payloadWriter);
        if (_formatVersion == WalFileHeader.AtomicPointFormatVersion && recordType != WalRecordType.AtomicPoint)
            AtomicPointWalCodec.ValidateControlPayload(payloadBuffer, recordType, lsn);

        uint crc32 = Crc32.HashToUInt32(payloadBuffer);

        Span<byte> headerBuffer = recordBuffer[..FormatSizes.WalRecordHeaderSize];
        var header = WalRecordHeader.CreateNew(recordType, payloadSize, crc32, timestamp, lsn);
        header.Flags = WalRecordHeader.HeaderChecksumFlag;

        var headerWriter = new SpanWriter(headerBuffer);
        headerWriter.WriteStruct(in header);
        header.Reserved = WalRecordHeader.ComputeHeaderChecksum(headerBuffer);
        headerWriter = new SpanWriter(headerBuffer);
        headerWriter.WriteStruct(in header);
    }

    private void FlushCore(bool flushToDisk)
    {
        _stream!.Flush();
        // 未知 append 可能已有完整记录；不能用旧计数截断它或写一个虚假的 footer。
        if (_atomicAppendFault is null)
            WriteLastLsnFooterIfDirty();

        if (flushToDisk)
            _fileStream!.Flush(true);
    }

    private void FlushRequested(bool flushToDisk)
    {
        ThrowIfAtomicAppendFault();
        try
        {
            FlushCore(flushToDisk);
        }
        catch (Exception ex) when (_formatVersion == WalFileHeader.AtomicPointFormatVersion)
        {
            _atomicAppendFault = ex;
            throw;
        }
    }

    /// <summary>
    /// 把 LastLsn footer 直接写到底层 <see cref="_fileStream"/> 的 <see cref="_bytesWritten"/> 偏移处。
    /// <para>
    /// S13 不变式：本方法绕过 <see cref="_stream"/>（BufferedStream）直接定位 <see cref="_fileStream"/>，
    /// 因此**必须**保证 BufferedStream 已无缓冲数据，否则 footer 会写到错误偏移、损坏 WAL 帧。
    /// 为不依赖调用点的隐式顺序（原先仅靠 <see cref="FlushCore"/> 先调 <c>_stream.Flush()</c>），
    /// 本方法在写 footer 前自行 <c>_stream.Flush()</c>（FlushCore 已 flush 时此调用为廉价 no-op），
    /// 使不变式显式且自足。
    /// </para>
    /// </summary>
    private void WriteLastLsnFooterIfDirty()
    {
        if (!_footerDirty)
            return;

        // 显式收口 S13 不变式：确保 BufferedStream 已排空，footer 偏移才与 _bytesWritten 一致。
        _stream!.Flush();

        Span<byte> footerBuffer = stackalloc byte[FormatSizes.WalLastLsnFooterSize];
        var footer = WalLastLsnFooter.CreateNew(_nextLsn - 1, _bytesWritten);
        var writer = new SpanWriter(footerBuffer);
        writer.WriteStruct(in footer);

        footer.Crc32 = Crc32.HashToUInt32(footerBuffer[..WalLastLsnFooter.CrcCoveredLength]);
        writer = new SpanWriter(footerBuffer);
        writer.WriteStruct(in footer);

        FileStream fs = _fileStream!;
        fs.Position = _bytesWritten;
        fs.SetLength(_bytesWritten);
        fs.Write(footerBuffer);
        fs.SetLength(_bytesWritten + FormatSizes.WalLastLsnFooterSize);
        fs.Position = _bytesWritten;

        _footerDirty = false;
    }

    private static void WriteFileHeader(Stream stream, long firstLsn, int formatVersion)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(FormatSizes.WalFileHeaderSize);
        try
        {
            var header = WalFileHeader.CreateNewVersion(firstLsn, formatVersion);
            var writer = new SpanWriter(buf.AsSpan(0, FormatSizes.WalFileHeaderSize));
            writer.WriteStruct(in header);
            stream.Write(buf, 0, FormatSizes.WalFileHeaderSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static bool TryReadLastLsnFooter(
        FileStream fs,
        WalFileHeader fileHeader,
        out WalLastLsnFooter footer)
    {
        footer = default;

        if (fs.Length < FormatSizes.WalFileHeaderSize + FormatSizes.WalLastLsnFooterSize)
            return false;

        long footerOffset = fs.Length - FormatSizes.WalLastLsnFooterSize;
        Span<byte> footerBuffer = stackalloc byte[FormatSizes.WalLastLsnFooterSize];

        fs.Position = footerOffset;
        int read = ReadExact(fs, footerBuffer);
        if (read < FormatSizes.WalLastLsnFooterSize)
            return false;

        var reader = new SpanReader(footerBuffer);
        footer = reader.ReadStruct<WalLastLsnFooter>();

        if (!footer.IsShapeValid())
            return false;

        if (footer.RecordsEndOffset != footerOffset)
            return false;

        if (footer.RecordsEndOffset < FormatSizes.WalFileHeaderSize)
            return false;

        if (footer.LastLsn < fileHeader.FirstLsn - 1)
            return false;

        if (footer.LastLsn == long.MaxValue)
            return false;

        uint expectedCrc = Crc32.HashToUInt32(footerBuffer[..WalLastLsnFooter.CrcCoveredLength]);
        return footer.Crc32 == expectedCrc;
    }

    private static long ScanForLastLsn(FileStream fs, long initialLastLsn, ref long bytesWritten)
    {
        long lastLsn = initialLastLsn;
        byte[] headerBuf = ArrayPool<byte>.Shared.Rent(FormatSizes.WalRecordHeaderSize);
        try
        {
            while (true)
            {
                int headerRead = ReadExact(fs, headerBuf, 0, FormatSizes.WalRecordHeaderSize);
                if (headerRead < FormatSizes.WalRecordHeaderSize)
                    break;

                ReadOnlySpan<byte> headerSpan = headerBuf.AsSpan(0, FormatSizes.WalRecordHeaderSize);
                var headerReader = new SpanReader(headerSpan);
                var header = headerReader.ReadStruct<WalRecordHeader>();

                if (!header.IsShapeValid(headerSpan))
                    break;

                if (header.PayloadLength < 0)
                    break;

                if (header.PayloadLength > fs.Length - fs.Position)
                    break;

                if (!ReadAndVerifyPayloadCrc32(fs, header.PayloadLength, header.PayloadCrc32))
                    break;

                lastLsn = header.Lsn;
                bytesWritten += FormatSizes.WalRecordHeaderSize + header.PayloadLength;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuf);
        }
        return lastLsn;
    }

    private static bool ReadAndVerifyPayloadCrc32(Stream stream, int payloadLength, uint expectedCrc32)
    {
        var crc32 = new Crc32();
        Span<byte> buffer = stackalloc byte[4096];
        int remaining = payloadLength;

        while (remaining > 0)
        {
            int toRead = Math.Min(remaining, buffer.Length);
            int read = ReadExact(stream, buffer[..toRead]);
            if (read < toRead)
                return false;

            crc32.Append(buffer[..read]);
            remaining -= read;
        }

        return crc32.GetCurrentHashAsUInt32() == expectedCrc32;
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

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WalWriter));
    }
}
