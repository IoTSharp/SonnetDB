using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Cdc;

/// <summary>有界、可重开的单分区本地 CDC 快照接收端。</summary>
/// <remarks>
/// <para>调用方必须提供固定读视图及其准确行数和位点。快照按稳定键顺序分页复制，
/// 完整复制后才能读取物化结果或应用增量。复制期间的增量应保留在独立的 CDC spool 中。</para>
/// <para>增量位点必须从快照位点连续递增。每批行变更和位点写入同一个原子替换文件；
/// 最后一个增量可按相同事件内容重试。该边界不提供远程复制、跨分区原子性或任意历史去重。</para>
/// </remarks>
public sealed class CdcSnapshotReplica : IDisposable, IAsyncDisposable
{
    private const int FormatVersion = 1;
    private const int HeaderBytes = 48;
    private static readonly byte[] Magic = "SDBCSR01"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);
    private readonly string _directory;
    private readonly CdcSnapshotReplicaOptions _options;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _disposeLock = new();
    private FileStream? _lease;
    private CdcSnapshotReplicaDocument _document;
    private bool _disposed;
    private Exception? _fault;

    private CdcSnapshotReplica(
        string path,
        CdcSnapshotReplicaOptions options,
        FileStream lease,
        CdcSnapshotReplicaDocument document)
    {
        FilePath = path;
        _directory = Path.GetDirectoryName(path)!;
        _options = options;
        _lease = lease;
        _document = document;
    }

    /// <summary>物化行和恢复位点共同保存的绝对文件路径。</summary>
    public string FilePath { get; }

    /// <summary>创建新的快照接收端；已有状态文件不会被覆盖。</summary>
    /// <param name="path">接收端状态文件路径。</param>
    /// <param name="descriptor">固定读视图身份、总行数和增量边界。</param>
    /// <param name="options">容量与批次限制。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已经持久化初始状态并持有独占 lease 的接收端。</returns>
    public static async ValueTask<CdcSnapshotReplica> CreateAsync(
        string path,
        CdcSnapshotDescriptor descriptor,
        CdcSnapshotReplicaOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CdcSnapshotReplicaOptions limits = options ?? new();
        limits.Validate();
        ValidateDescriptor(descriptor, limits);
        string fullPath = GetPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        FileStream lease = AcquireLease(fullPath);
        CdcSnapshotReplica? replica = null;
        try
        {
            if (File.Exists(fullPath))
                throw new IOException("CDC 快照状态文件已经存在，请使用 Open 恢复。");
            var document = new CdcSnapshotReplicaDocument(
                FormatVersion, descriptor, CdcSnapshotPhase.Snapshot, 0, null,
                descriptor.Checkpoint.Offset, null, new Dictionary<string, string>(StringComparer.Ordinal));
            replica = new CdcSnapshotReplica(fullPath, limits, lease, document);
            await replica.SaveAsync(document, overwrite: false, cancellationToken).ConfigureAwait(false);
            return replica;
        }
        catch
        {
            if (replica is not null)
                replica.Dispose();
            else
                lease.Dispose();
            throw;
        }
    }

    /// <summary>重开接收端，并校验持久状态与预期固定读视图完全一致。</summary>
    /// <param name="path">已有状态文件路径；文件缺失时不会创建空状态。</param>
    /// <param name="expectedDescriptor">必须与持久状态一致的快照身份和边界。</param>
    /// <param name="options">容量与批次限制。</param>
    /// <returns>已校验且持有独占 lease 的接收端。</returns>
    /// <exception cref="InvalidDataException">状态损坏、容量越界或快照身份不匹配。</exception>
    public static CdcSnapshotReplica Open(
        string path,
        CdcSnapshotDescriptor expectedDescriptor,
        CdcSnapshotReplicaOptions? options = null)
    {
        CdcSnapshotReplicaOptions limits = options ?? new();
        limits.Validate();
        ValidateDescriptor(expectedDescriptor, limits);
        string fullPath = GetPath(path);
        FileStream lease = AcquireLease(fullPath);
        try
        {
            CdcSnapshotReplicaDocument document = Load(fullPath, limits);
            if (document.Descriptor != expectedDescriptor)
                throw new InvalidDataException("CDC 快照身份、schema、行数或 checkpoint 与预期不一致。");
            return new CdcSnapshotReplica(fullPath, limits, lease, document);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>读取当前已经提交的一致恢复状态。</summary>
    /// <returns>快照进度、阶段、物化行数和已应用位点。</returns>
    public CdcSnapshotReplicaState GetState()
    {
        lock (_stateLock)
        {
            EnsureOperational();
            return State(_document);
        }
    }

    /// <summary>原子保存一页按键严格递增的快照行。</summary>
    /// <param name="expectedRowOrdinal">本页预期起始序号，必须等于已复制行数。</param>
    /// <param name="rows">来自同一固定读视图的非空快照页。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提交后的恢复状态。</returns>
    public async ValueTask<CdcSnapshotReplicaState> WriteSnapshotPageAsync(
        long expectedRowOrdinal,
        IReadOnlyList<CdcSnapshotRow> rows,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowOrdinal);
        ValidateBatchCount(rows.Count);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperational();
            CdcSnapshotReplicaDocument current = _document;
            if (current.Phase != CdcSnapshotPhase.Snapshot)
                throw new InvalidOperationException("CDC 快照已经完成，不能继续写入快照页。");
            if (expectedRowOrdinal != current.SnapshotRowsCopied)
                throw new ArgumentException("快照页起始序号与持久恢复进度不一致。", nameof(expectedRowOrdinal));
            if (rows.Count > current.Descriptor.RowCount - current.SnapshotRowsCopied)
                throw new ArgumentException("快照页超过固定读视图声明的总行数。", nameof(rows));

            var values = new Dictionary<string, string>(current.Rows, StringComparer.Ordinal);
            string? lastKey = current.LastSnapshotKey;
            long bytes = 0;
            foreach (CdcSnapshotRow row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytes += ValidateRow(row);
                if (bytes > _options.MaxBatchBytes)
                    throw new InvalidOperationException("CDC 快照页超过批次字节上限。");
                if (lastKey is not null && string.CompareOrdinal(row.Key, lastKey) <= 0)
                    throw new ArgumentException("快照键必须跨页严格递增，不能重复或倒退。", nameof(rows));
                values.Add(row.Key, row.ValueJson);
                lastKey = row.Key;
            }

            CdcSnapshotReplicaDocument candidate = current with
            {
                Rows = values,
                SnapshotRowsCopied = checked(current.SnapshotRowsCopied + rows.Count),
                LastSnapshotKey = lastKey,
            };
            await CommitAsync(candidate, cancellationToken).ConfigureAwait(false);
            return State(candidate);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>在总行数完整时原子进入增量阶段；完成操作可重复调用。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>增量阶段的恢复状态。</returns>
    public async ValueTask<CdcSnapshotReplicaState> CompleteSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperational();
            CdcSnapshotReplicaDocument current = _document;
            if (current.Phase == CdcSnapshotPhase.Incremental)
                return State(current);
            if (current.SnapshotRowsCopied != current.Descriptor.RowCount)
                throw new InvalidOperationException("CDC 快照尚未复制完整，不能切换到增量阶段。");
            CdcSnapshotReplicaDocument candidate = current with { Phase = CdcSnapshotPhase.Incremental };
            await CommitAsync(candidate, cancellationToken).ConfigureAwait(false);
            return State(candidate);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>原子应用一批同源、同 schema、同分区且位点连续的完整后像增量。</summary>
    /// <param name="events">非空事件批次；仅最后一个已应用事件支持内容相同的重复提交。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提交后的物化状态和恢复位点。</returns>
    public async ValueTask<CdcSnapshotReplicaState> ApplyIncrementalAsync(
        IReadOnlyList<CdcEvent> events,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(events);
        ValidateBatchCount(events.Count);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperational();
            CdcSnapshotReplicaDocument current = _document;
            if (current.Phase != CdcSnapshotPhase.Incremental)
                throw new InvalidOperationException("CDC 快照尚未完成，增量必须保留在源 spool 中。");
            var values = new Dictionary<string, string>(current.Rows, StringComparer.Ordinal);
            long offset = current.AppliedOffset;
            string? lastHash = current.LastEventHash;
            long bytes = 0;
            foreach (CdcEvent value in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] encoded = CdcEventCodec.Encode(value);
                bytes += encoded.Length;
                if (bytes > _options.MaxBatchBytes)
                    throw new InvalidOperationException("CDC 增量批次超过字节上限。");
                ValidateEventIdentity(value, current.Descriptor);
                string hash = Convert.ToHexString(SHA256.HashData(encoded));
                long next = value.Metadata.Checkpoint.Offset;
                if (next == offset && lastHash is not null && hash == lastHash)
                    continue;
                if (offset == long.MaxValue || next != offset + 1)
                    throw new ArgumentException("CDC 增量 checkpoint 缺失、倒退或重复事件内容不一致。", nameof(events));

                if (value.Metadata.Operation == CdcOperation.Delete)
                    values.Remove(value.Key);
                else
                {
                    if (value.AfterJson is null)
                        throw new ArgumentException("本地物化增量必须包含完整 after JSON。", nameof(events));
                    values[value.Key] = value.AfterJson;
                    if (values.Count > _options.MaxRows)
                        throw new InvalidOperationException("CDC 物化行数超过容量上限。");
                }
                offset = next;
                lastHash = hash;
            }

            if (offset == current.AppliedOffset)
                return State(current);
            CdcSnapshotReplicaDocument candidate = current with
            {
                Rows = values,
                AppliedOffset = offset,
                LastEventHash = lastHash,
            };
            await CommitAsync(candidate, cancellationToken).ConfigureAwait(false);
            return State(candidate);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>按键顺序读取一个有界物化批次；复制未完成时不暴露部分快照。</summary>
    /// <param name="afterKey">排他的起始键；为空时从首行读取。</param>
    /// <param name="maxRows">本批最多返回的行数；为空时使用批次上限。</param>
    /// <param name="maxBytes">本批最多返回的键和值的 UTF-8 字节数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>物化行、后续键和本次读取对应的位点；首行超限时抛出异常。</returns>
    public async ValueTask<CdcSnapshotRowBatch> ReadRowsAsync(
        string? afterKey = null,
        int? maxRows = null,
        int? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int rowLimit = maxRows ?? _options.MaxBatchRows;
        int byteLimit = maxBytes ?? _options.MaxBatchBytes;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowLimit, _options.MaxBatchRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteLimit, _options.MaxBatchBytes);
        if (afterKey is not null)
            ValidateIdentifier(afterKey);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperational();
            CdcSnapshotReplicaDocument current = _document;
            if (current.Phase != CdcSnapshotPhase.Incremental)
                throw new InvalidOperationException("CDC 快照尚未完成，物化结果不可见。");
            var rows = new List<CdcSnapshotRow>();
            int bytes = 0;
            bool hasMore = false;
            foreach ((string key, string value) in current.Rows.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (afterKey is not null && string.CompareOrdinal(key, afterKey) <= 0)
                    continue;
                int rowBytes = checked(StrictUtf8.GetByteCount(key) + StrictUtf8.GetByteCount(value));
                if (rows.Count >= rowLimit || rowBytes > byteLimit - bytes)
                {
                    if (rows.Count == 0)
                        throw new InvalidOperationException("CDC 读取字节上限无法容纳下一行。");
                    hasMore = true;
                    break;
                }
                rows.Add(new CdcSnapshotRow(key, value));
                bytes += rowBytes;
            }
            return new CdcSnapshotRowBatch(
                new ReadOnlyCollection<CdcSnapshotRow>(rows), rows.Count == 0 ? null : rows[^1].Key,
                hasMore, bytes, new CdcCheckpoint(current.Descriptor.Checkpoint.Partition, current.AppliedOffset));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>关闭接收端并释放独占 lease；持久状态文件予以保留。</summary>
    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed)
                return;
            if (!_operationGate.Wait(DisposeTimeout))
                throw new TimeoutException("CDC 快照接收端等待存储操作结束超时，请稍后重试关闭。");
            try
            {
                lock (_stateLock)
                    _disposed = true;
                _lease?.Dispose();
                _lease = null;
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    /// <summary>关闭接收端并释放独占 lease。</summary>
    /// <returns>已经完成的关闭操作。</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask CommitAsync(CdcSnapshotReplicaDocument candidate, CancellationToken cancellationToken)
    {
        await SaveAsync(candidate, overwrite: true, cancellationToken).ConfigureAwait(false);
        lock (_stateLock)
            _document = candidate;
    }

    private async ValueTask SaveAsync(
        CdcSnapshotReplicaDocument document,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new LimitedMemoryStream(_options.MaxBytes - HeaderBytes);
        JsonSerializer.Serialize(buffer, document, CdcSnapshotReplicaJsonContext.Default.CdcSnapshotReplicaDocument);
        byte[] payload = buffer.ToArray();
        byte[] header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), payload.Length);
        SHA256.HashData(payload).CopyTo(header, 16);
        string temporaryPath = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        bool publishAttempted = false;
        try
        {
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await file.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await file.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            publishAttempted = true;
            File.Move(temporaryPath, FilePath, overwrite);
            SonnetDB.Wal.DirectoryFsync.FlushRequired(_directory);
        }
        catch (Exception exception) when (publishAttempted)
        {
            lock (_stateLock)
                _fault = exception;
            throw;
        }
        finally
        {
            // 临时文件不是恢复输入；清理错误不得覆盖取消、写入或发布的主异常。
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static CdcSnapshotReplicaDocument Load(string path, CdcSnapshotReplicaOptions options)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < HeaderBytes || file.Length > options.MaxBytes)
            throw new InvalidDataException("CDC 快照状态文件长度越界或被截断。");
        byte[] header = new byte[HeaderBytes];
        file.ReadExactly(header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4));
        if (!header.AsSpan(0, 8).SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4)) != FormatVersion
            || length <= 0 || length != file.Length - HeaderBytes)
            throw new InvalidDataException("CDC 快照状态文件 magic、版本或正文长度无效。");
        byte[] payload = new byte[length];
        file.ReadExactly(payload);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), header.AsSpan(16, 32)))
            throw new InvalidDataException("CDC 快照状态文件校验和不匹配。");
        try
        {
            CdcSnapshotReplicaDocument document = JsonSerializer.Deserialize(
                payload, CdcSnapshotReplicaJsonContext.Default.CdcSnapshotReplicaDocument)
                ?? throw new InvalidDataException("CDC 快照状态正文为空。");
            ValidateDocument(document, options);
            return document;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or CdcFormatException)
        {
            throw new InvalidDataException("CDC 快照状态正文不符合本地恢复合同。", exception);
        }
    }

    private static void ValidateDocument(CdcSnapshotReplicaDocument document, CdcSnapshotReplicaOptions options)
    {
        ValidateDescriptor(document.Descriptor, options);
        if (document.FormatVersion != FormatVersion || !Enum.IsDefined(document.Phase)
            || document.Rows is null || document.Rows.Count > options.MaxRows
            || document.SnapshotRowsCopied < 0 || document.SnapshotRowsCopied > document.Descriptor.RowCount
            || document.AppliedOffset < document.Descriptor.Checkpoint.Offset)
            throw new InvalidDataException("CDC 快照状态字段越界。");
        foreach ((string key, string value) in document.Rows)
            ValidateRow(new CdcSnapshotRow(key, value));
        if ((document.SnapshotRowsCopied == 0) != (document.LastSnapshotKey is null))
            throw new InvalidDataException("CDC 快照恢复序号与末尾键不一致。");
        if (document.LastSnapshotKey is not null)
            ValidateIdentifier(document.LastSnapshotKey);
        if (document.Phase == CdcSnapshotPhase.Snapshot)
        {
            string? lastKey = document.Rows.Keys.OrderBy(static key => key, StringComparer.Ordinal).LastOrDefault();
            if (document.Rows.Count != document.SnapshotRowsCopied || lastKey != document.LastSnapshotKey
                || document.AppliedOffset != document.Descriptor.Checkpoint.Offset || document.LastEventHash is not null)
                throw new InvalidDataException("CDC 部分快照包含不一致的行或增量位点。");
        }
        else if (document.SnapshotRowsCopied != document.Descriptor.RowCount)
            throw new InvalidDataException("CDC 增量阶段没有完整快照。");
        if (document.AppliedOffset == document.Descriptor.Checkpoint.Offset)
        {
            if (document.LastEventHash is not null)
                throw new InvalidDataException("CDC 快照边界不应包含增量事件摘要。");
        }
        else if (document.LastEventHash is not { Length: 64 }
            || document.LastEventHash.Any(static value => !char.IsAsciiHexDigit(value)))
            throw new InvalidDataException("CDC 已应用位点缺少有效事件摘要。");
    }

    private static void ValidateDescriptor(CdcSnapshotDescriptor descriptor, CdcSnapshotReplicaOptions options)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateIdentifier(descriptor.SnapshotId);
        ValidateIdentifier(descriptor.Source);
        ValidateIdentifier(descriptor.Entity);
        ValidateIdentifier(descriptor.Schema);
        if (descriptor.SchemaVersion != CdcEventCodec.CurrentSchemaVersion)
            throw new CdcUnsupportedSchemaVersionException(descriptor.Schema, descriptor.SchemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(descriptor.Checkpoint.Partition);
        ArgumentOutOfRangeException.ThrowIfNegative(descriptor.Checkpoint.Offset);
        ArgumentOutOfRangeException.ThrowIfNegative(descriptor.RowCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(descriptor.RowCount, options.MaxRows);
    }

    private static void ValidateEventIdentity(CdcEvent value, CdcSnapshotDescriptor descriptor)
    {
        if (value.Source != descriptor.Source || value.Entity != descriptor.Entity
            || value.Metadata.Schema != descriptor.Schema || value.Metadata.SchemaVersion != descriptor.SchemaVersion
            || value.Metadata.Checkpoint.Partition != descriptor.Checkpoint.Partition)
            throw new ArgumentException("CDC 增量源、实体、schema 或分区与固定快照不一致。", nameof(value));
    }

    private void ValidateBatchCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _options.MaxBatchRows);
    }

    private static int ValidateRow(CdcSnapshotRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ValidateIdentifier(row.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.ValueJson);
        int bytes = StrictUtf8.GetByteCount(row.ValueJson);
        if (bytes > CdcEventCodec.MaxPayloadBytes)
            throw new ArgumentException("CDC 物化行 JSON 超过单值上限。", nameof(row));
        using JsonDocument parsed = JsonDocument.Parse(row.ValueJson, new JsonDocumentOptions { MaxDepth = 32 });
        return checked(bytes + StrictUtf8.GetByteCount(row.Key));
    }

    private static void ValidateIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (StrictUtf8.GetByteCount(value) > CdcEventCodec.MaxStringBytes)
            throw new ArgumentException("CDC 快照标识或键超过 UTF-8 字节上限。", nameof(value));
    }

    private static string GetPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static FileStream AcquireLease(string path)
        => new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);

    private static CdcSnapshotReplicaState State(CdcSnapshotReplicaDocument document)
        => new(document.Descriptor, document.Phase, document.SnapshotRowsCopied, document.LastSnapshotKey,
            new CdcCheckpoint(document.Descriptor.Checkpoint.Partition, document.AppliedOffset), document.Rows.Count);

    private void EnsureOperational()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fault is not null)
            throw new InvalidOperationException("CDC 状态发布结果未知，必须关闭并重开核对。", _fault);
    }

    private sealed class LimitedMemoryStream(int maxBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityLimit(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityLimit(buffer.Length);
            base.Write(buffer);
        }

        private void EnsureCapacityLimit(int count)
        {
            if (count > maxBytes - Position)
                throw new InvalidOperationException("CDC 快照物化状态超过文件字节容量上限。");
        }
    }
}
