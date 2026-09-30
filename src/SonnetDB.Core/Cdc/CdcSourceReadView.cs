using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Kv;

namespace SonnetDB.Cdc;

/// <summary>
/// 单源、单实体、单分区的持久固定读视图。
/// </summary>
/// <remarks>
/// <para>
/// 视图由调用方在源端取得一致读快照后创建。创建过程中委托返回的行必须全部属于
/// <see cref="CdcSnapshotDescriptor.Checkpoint"/>，并且按业务键严格递增；该类会把完整
/// 行集一次原子发布到独立文件，随后分页读取不会观察源端变化。
/// </para>
/// <para>
/// 视图文件不可变，打开时校验 magic、版本、长度、SHA-256、身份、行数和键序。
/// 单个路径持有独占 lease，因此进程重启后只能通过 <see cref="Open"/> 恢复同一视图。
/// </para>
/// </remarks>
public sealed class CdcSourceReadView : IDisposable, IAsyncDisposable
{
    private const int FormatVersion = 1;
    private const int HeaderBytes = 48;
    private static readonly byte[] Magic = "SDBCSV01"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);
    private readonly CdcSnapshotReplicaOptions _options;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _disposeLock = new();
    private FileStream? _lease;
    private bool _disposed;

    private CdcSourceReadView(
        string filePath,
        CdcSnapshotReplicaOptions options,
        FileStream lease,
        CdcSourceReadViewDocument document)
    {
        FilePath = filePath;
        _options = options;
        _lease = lease;
        Document = document;
    }

    /// <summary>固定读视图文件的绝对路径。</summary>
    public string FilePath { get; }

    /// <summary>固定读视图的身份和源端位点。</summary>
    public CdcSnapshotDescriptor Descriptor => Document.Descriptor;

    /// <summary>视图中固定的行数。</summary>
    public long RowCount => Document.Rows.Count;

    private CdcSourceReadViewDocument Document { get; }

    /// <summary>
    /// 用已取得的完整固定行集创建源端读视图。
    /// </summary>
    /// <param name="path">视图状态文件路径。</param>
    /// <param name="descriptor">固定读视图身份、准确行数和 CDC 边界。</param>
    /// <param name="rows">固定读快照中的全部行，必须按键严格递增。</param>
    /// <param name="options">容量和页边界；为空时使用默认值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已原子发布且持有独占 lease 的视图。</returns>
    public static async ValueTask<CdcSourceReadView> CreateAsync(
        string path,
        CdcSnapshotDescriptor descriptor,
        IReadOnlyList<CdcSnapshotRow> rows,
        CdcSnapshotReplicaOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(rows);
        CdcSnapshotReplicaOptions limits = options ?? new();
        limits.Validate();
        ValidateDescriptor(descriptor, limits);
        ValidateRows(descriptor, rows, limits);
        string fullPath = GetPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        FileStream lease = AcquireLease(fullPath);
        try
        {
            if (File.Exists(fullPath))
                throw new IOException("CDC 源端固定读视图已经存在，请使用 Open 恢复。");

            var document = new CdcSourceReadViewDocument(FormatVersion, descriptor, rows.ToArray());
            var view = new CdcSourceReadView(fullPath, limits, lease, document);
            try
            {
                await view.SaveAsync(document, overwrite: false, cancellationToken).ConfigureAwait(false);
                return view;
            }
            catch
            {
                view.Dispose();
                throw;
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 从固定源分页委托创建不可变读视图。
    /// </summary>
    /// <param name="path">视图状态文件路径。</param>
    /// <param name="descriptor">固定读视图身份、准确行数和 CDC 边界。</param>
    /// <param name="pageReader">只读取同一源端固定快照的分页委托。</param>
    /// <param name="options">容量和页边界；为空时使用默认值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已原子发布且持有独占 lease 的视图。</returns>
    public static async ValueTask<CdcSourceReadView> CaptureAsync(
        string path,
        CdcSnapshotDescriptor descriptor,
        CdcSnapshotPageReader pageReader,
        CdcSnapshotReplicaOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageReader);
        CdcSnapshotReplicaOptions limits = options ?? new();
        limits.Validate();
        ValidateDescriptor(descriptor, limits);
        var rows = new List<CdcSnapshotRow>(checked((int)descriptor.RowCount));
        string? afterKey = null;
        while (rows.Count < descriptor.RowCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int remaining = checked((int)Math.Min(descriptor.RowCount - rows.Count, limits.MaxBatchRows));
            IReadOnlyList<CdcSnapshotRow> page = await pageReader(afterKey, remaining, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(page);
            if (page.Count == 0)
                throw new InvalidDataException("CDC 源端固定读视图在声明行数前提前结束。");
            if (page.Count > remaining)
                throw new InvalidDataException("CDC 源端分页超过请求的行数上限。");
            rows.AddRange(page);
            ValidateRows(descriptor, rows, limits, requireDeclaredCount: false);
            afterKey = rows[^1].Key;
        }

        if (rows.Count != descriptor.RowCount)
            throw new InvalidDataException("CDC 源端固定读视图返回的行数与 descriptor 不一致。");
        IReadOnlyList<CdcSnapshotRow> tail = await pageReader(afterKey, 1, cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(tail);
        if (tail.Count != 0)
            throw new InvalidDataException("CDC 源端固定读视图超过 descriptor 声明的总行数。");
        return await CreateAsync(path, descriptor, rows, limits, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从文档集合的固定 KV 读快照创建源端 CDC 视图。
    /// </summary>
    /// <param name="path">视图状态文件路径。</param>
    /// <param name="store">要读取的文档集合。</param>
    /// <param name="source">源数据库的稳定标识。</param>
    /// <param name="schemaVersion">文档 schema 版本。</param>
    /// <param name="partition">CDC 源分区编号。</param>
    /// <param name="snapshotId">可选固定视图标识；为空时由源和位点生成。</param>
    /// <param name="options">容量和页边界；为空时使用默认值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已经从固定快照原子发布的源端读视图。</returns>
    /// <remarks>
    /// 该方法在集合写锁内取得 KV 快照和 change-feed 位点。快照建立后源端可以继续写入，
    /// 但这些写入不会进入本视图；调用方必须把 checkpoint 之后的事件追加到独立
    /// <see cref="CdcEventSpool"/>，并在副本完成快照后按该位点连续应用。
    /// </remarks>
    public static async ValueTask<CdcSourceReadView> CaptureDocumentCollectionAsync(
        string path,
        DocumentCollectionStore store,
        string source,
        int schemaVersion = CdcEventCodec.CurrentSchemaVersion,
        long partition = 0,
        string? snapshotId = null,
        CdcSnapshotReplicaOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        CdcSnapshotReplicaOptions limits = options ?? new();
        limits.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(partition);
        if (schemaVersion != CdcEventCodec.CurrentSchemaVersion)
            throw new CdcUnsupportedSchemaVersionException("documents", schemaVersion);

        (KvReadSnapshot Snapshot, long Checkpoint) lease = store.AcquireCdcReadSnapshot();
        try
        {
            IReadOnlyList<CdcSnapshotRow> rows = ReadDocumentSnapshotRows(lease.Snapshot, limits, cancellationToken);
            int rowCount = rows.Count;
            string entity = store.Schema.Name;
            string id = snapshotId ?? $"snapshot-{source}-{entity}-{partition}-{lease.Checkpoint}";
            var descriptor = new CdcSnapshotDescriptor(
                id, source, entity, "documents", schemaVersion,
                new CdcCheckpoint(partition, lease.Checkpoint), rowCount);
            cancellationToken.ThrowIfCancellationRequested();
            return await CreateAsync(path, descriptor, rows, limits, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lease.Snapshot.Dispose();
        }
    }

    /// <summary>
    /// 重开并严格校验已有固定读视图。
    /// </summary>
    /// <param name="path">已有视图状态文件路径。</param>
    /// <param name="expectedDescriptor">必须与持久状态完全一致的 descriptor。</param>
    /// <param name="options">容量和页边界；为空时使用默认值。</param>
    /// <returns>已校验且持有独占 lease 的视图。</returns>
    public static CdcSourceReadView Open(
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
            CdcSourceReadViewDocument document = Load(fullPath, limits);
            if (document.Descriptor != expectedDescriptor)
                throw new InvalidDataException("CDC 源端固定读视图身份、schema、行数或 checkpoint 与预期不一致。");
            return new CdcSourceReadView(fullPath, limits, lease, document);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 在创建结果未知时，按调用方已知身份重开并读取持久固定视图的完整 descriptor。
    /// </summary>
    /// <param name="path">创建时使用的专用视图状态文件路径。</param>
    /// <param name="expectedSource">预期源标识。</param>
    /// <param name="expectedEntity">预期实体名称。</param>
    /// <param name="expectedSchema">预期 schema 名称。</param>
    /// <param name="expectedSchemaVersion">预期 schema 版本。</param>
    /// <param name="expectedPartition">预期源分区。</param>
    /// <param name="expectedSnapshotId">可选的预期快照标识。</param>
    /// <param name="options">容量和页边界；为空时使用默认值。</param>
    /// <returns>完整校验后持有独占 lease 的视图；其 Descriptor 包含持久化的行数和 checkpoint。</returns>
    public static CdcSourceReadView OpenExisting(
        string path,
        string expectedSource,
        string expectedEntity,
        string expectedSchema,
        int expectedSchemaVersion,
        long expectedPartition,
        string? expectedSnapshotId = null,
        CdcSnapshotReplicaOptions? options = null)
    {
        ValidateIdentifier(expectedSource);
        ValidateIdentifier(expectedEntity);
        ValidateIdentifier(expectedSchema);
        if (expectedSnapshotId is not null)
            ValidateIdentifier(expectedSnapshotId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedPartition);
        CdcSnapshotReplicaOptions limits = options ?? new();
        limits.Validate();
        string fullPath = GetPath(path);
        FileStream lease = AcquireLease(fullPath);
        try
        {
            CdcSourceReadViewDocument document = Load(fullPath, limits);
            CdcSnapshotDescriptor descriptor = document.Descriptor;
            if (descriptor.Source != expectedSource
                || descriptor.Entity != expectedEntity
                || descriptor.Schema != expectedSchema
                || descriptor.SchemaVersion != expectedSchemaVersion
                || descriptor.Checkpoint.Partition != expectedPartition
                || (expectedSnapshotId is not null && descriptor.SnapshotId != expectedSnapshotId))
                throw new InvalidDataException("CDC 源端固定读视图的持久身份与预期不一致。");
            return new CdcSourceReadView(fullPath, limits, lease, document);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 按键顺序读取固定视图的一页；返回位点始终是创建时的固定 checkpoint。
    /// </summary>
    /// <param name="afterKey">排他的起始键；为空时从首行开始。</param>
    /// <param name="maxRows">本页最多返回的行数；为空时使用默认页上限。</param>
    /// <param name="maxBytes">本页键和值的 UTF-8 总字节上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>固定视图行、continuation 和创建时 checkpoint。</returns>
    public async ValueTask<CdcSnapshotRowBatch> ReadPageAsync(
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
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = new List<CdcSnapshotRow>(rowLimit);
            int bytes = 0;
            bool hasMore = false;
            foreach (CdcSnapshotRow row in Document.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (afterKey is not null && string.CompareOrdinal(row.Key, afterKey) <= 0)
                    continue;
                int rowBytes = ValidateRow(row);
                if (result.Count >= rowLimit || rowBytes > byteLimit - bytes)
                {
                    if (result.Count == 0)
                        throw new InvalidOperationException("CDC 源端固定读视图页字节上限无法容纳下一行。");
                    hasMore = true;
                    break;
                }
                result.Add(row);
                bytes += rowBytes;
            }

            return new CdcSnapshotRowBatch(
                new ReadOnlyCollection<CdcSnapshotRow>(result),
                result.Count == 0 ? null : result[^1].Key,
                hasMore,
                bytes,
                Descriptor.Checkpoint);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>关闭视图并释放 lease；持久文件保留供进程重启后打开。</summary>
    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed)
                return;
            if (!_operationGate.Wait(DisposeTimeout))
                throw new TimeoutException("CDC 源端固定读视图等待读取结束超时。");
            try
            {
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

    /// <summary>异步关闭视图并释放 lease。</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask SaveAsync(
        CdcSourceReadViewDocument document,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new LimitedMemoryStream(_options.MaxBytes - HeaderBytes);
        JsonSerializer.Serialize(buffer, document, CdcSourceReadViewJsonContext.Default.CdcSourceReadViewDocument);
        byte[] payload = buffer.ToArray();
        byte[] header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), payload.Length);
        SHA256.HashData(payload).CopyTo(header, 16);
        string temporaryPath = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
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
            File.Move(temporaryPath, FilePath, overwrite);
            SonnetDB.Wal.DirectoryFsync.FlushRequired(Path.GetDirectoryName(FilePath)!);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static CdcSourceReadViewDocument Load(string path, CdcSnapshotReplicaOptions options)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < HeaderBytes || file.Length > options.MaxBytes)
            throw new InvalidDataException("CDC 源端固定读视图状态文件长度越界或被截断。");
        byte[] header = new byte[HeaderBytes];
        file.ReadExactly(header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4));
        if (!header.AsSpan(0, 8).SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4)) != FormatVersion
            || length <= 0 || length != file.Length - HeaderBytes)
            throw new InvalidDataException("CDC 源端固定读视图 magic、版本或正文长度无效。");
        byte[] payload = new byte[length];
        file.ReadExactly(payload);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), header.AsSpan(16, 32)))
            throw new InvalidDataException("CDC 源端固定读视图状态文件校验和不匹配。");
        try
        {
            CdcSourceReadViewDocument document = JsonSerializer.Deserialize(
                payload, CdcSourceReadViewJsonContext.Default.CdcSourceReadViewDocument)
                ?? throw new InvalidDataException("CDC 源端固定读视图正文为空。");
            ValidateDescriptor(document.Descriptor, options);
            ValidateRows(document.Descriptor, document.Rows, options);
            if (document.FormatVersion != FormatVersion)
                throw new InvalidDataException("CDC 源端固定读视图格式版本不受支持。");
            return document;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or CdcFormatException)
        {
            throw new InvalidDataException("CDC 源端固定读视图正文不符合恢复合同。", exception);
        }
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

    private static IReadOnlyList<CdcSnapshotRow> ReadDocumentSnapshotRows(
        KvReadSnapshot snapshot,
        CdcSnapshotReplicaOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var rows = new List<CdcSnapshotRow>();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = new byte[] { (byte)'d' },
            PageSize = options.MaxBatchRows,
            MaxPageBytes = options.MaxBatchBytes,
        });
        long totalBytes = 0;
        while (!cursor.IsExhausted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<KvEntry> page = cursor.ReadNextPage(cancellationToken);
            if (rows.Count > options.MaxRows - page.Count)
                throw new InvalidOperationException("CDC 文档源端固定读视图超过物化行数上限。");
            foreach (KvEntry entry in page)
            {
                var row = new CdcSnapshotRow(
                    DocumentIndexCodec.DecodeIdFromDocumentKey(entry.Key),
                    Encoding.UTF8.GetString(entry.Value.Span));
                totalBytes = checked(totalBytes + ValidateRow(row));
                if (totalBytes > options.MaxBytes - HeaderBytes)
                    throw new InvalidOperationException("CDC 文档源端固定读视图超过文件字节容量上限。");
                rows.Add(row);
            }
            if (page.Count == 0)
                break;
        }
        rows.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        return rows;
    }

    private static void ValidateRows(
        CdcSnapshotDescriptor descriptor,
        IReadOnlyList<CdcSnapshotRow> rows,
        CdcSnapshotReplicaOptions options,
        bool requireDeclaredCount = true)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (requireDeclaredCount && rows.Count != descriptor.RowCount)
            throw new InvalidDataException("CDC 源端固定读视图行数与 descriptor 不一致。");
        if (rows.Count > descriptor.RowCount)
            throw new InvalidDataException("CDC 源端固定读视图超过 descriptor 声明的行数。");
        if (rows.Count > options.MaxRows)
            throw new InvalidDataException("CDC 源端固定读视图超过行数容量。");
        string? previous = null;
        long totalBytes = 0;
        foreach (CdcSnapshotRow row in rows)
        {
            int bytes = ValidateRow(row);
            totalBytes = checked(totalBytes + bytes);
            if (previous is not null && string.CompareOrdinal(previous, row.Key) >= 0)
                throw new InvalidDataException("CDC 源端固定读视图键必须严格递增。");
            previous = row.Key;
            if (totalBytes > options.MaxBytes)
                throw new InvalidDataException("CDC 源端固定读视图超过文件容量。");
        }
    }

    private static int ValidateRow(CdcSnapshotRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ValidateIdentifier(row.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.ValueJson);
        int bytes = StrictUtf8.GetByteCount(row.ValueJson);
        if (bytes > CdcEventCodec.MaxPayloadBytes)
            throw new ArgumentException("CDC 源端固定读视图行 JSON 超过单值上限。", nameof(row));
        using JsonDocument parsed = JsonDocument.Parse(row.ValueJson, new JsonDocumentOptions { MaxDepth = 32 });
        return checked(bytes + StrictUtf8.GetByteCount(row.Key));
    }

    private static void ValidateIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (StrictUtf8.GetByteCount(value) > CdcEventCodec.MaxStringBytes)
            throw new ArgumentException("CDC 源端固定读视图标识或键超过 UTF-8 字节上限。", nameof(value));
    }

    private static string GetPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static FileStream AcquireLease(string path)
        => new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);

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
                throw new InvalidOperationException("CDC 源端固定读视图超过文件字节容量上限。");
        }
    }
}
