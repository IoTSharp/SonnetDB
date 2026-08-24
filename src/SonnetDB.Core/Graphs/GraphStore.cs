using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Kv;

namespace SonnetDB.Graphs;

/// <summary>
/// 单个原生属性图的存储句柄。
/// </summary>
/// <remarks>
/// 图存储身份由固定 marker 绑定到目录定义，打开时会先校验 marker，再获取底层 KV 生命周期租约。
/// </remarks>
public sealed class GraphStore : IDisposable
{
    /// <summary>固定 store marker 文件名。</summary>
    internal const string MarkerFileName = "store.sdbgraph";

    private const int MarkerFormatVersion = 1;
    private const int MarkerHeaderSize = 48;
    private const int MarkerNameCapacity = GraphDefinition.MaxNameBytes;
    private const int MarkerCrcOffset = MarkerHeaderSize + MarkerNameCapacity;
    private const int MarkerFooterOffset = MarkerCrcOffset + sizeof(uint);
    private const int MarkerSize = MarkerFooterOffset + 8;
    private static readonly byte[] MarkerMagic = "SDBGSTR1"u8.ToArray();
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    private readonly object _sync = new();
    private readonly object _maintenanceGate = new();
    private readonly object _offlineAlgorithmGate = new();
    private readonly object _commitGate;
    private readonly KvKeyspace _keyspace;
    private GraphStatistics? _statistics;
    private bool _disposed;

    /// <summary>测试在 transaction 已构造条件、进入 KV 条件提交前建立同步点。</summary>
    internal Action? BeforeTransactionConditionalCommitTestHook { get; set; }

    /// <summary>测试在 durable work unit 完成且已经释放提交门后建立同步点。</summary>
    internal Action<GraphMaintenancePhase, long>? AfterMaintenanceWorkUnitTestHook { get; set; }

    private GraphStore(
        GraphDefinition definition,
        string rootDirectory,
        KvKeyspace keyspace,
        object commitGate)
    {
        Definition = definition;
        RootDirectory = rootDirectory;
        _keyspace = keyspace;
        _commitGate = commitGate;
    }

    /// <summary>图的不可变目录定义。</summary>
    public GraphDefinition Definition { get; }

    /// <summary>图名称。</summary>
    public string Name => Definition.Name;

    /// <summary>图物理存储的稳定标识符。</summary>
    public Guid StorageId => Definition.StorageId;

    /// <summary>当前图记录与键布局版本。</summary>
    public int Version => Definition.RecordFormatVersion;

    /// <summary>图记录与键布局版本。</summary>
    public int RecordFormatVersion => Definition.RecordFormatVersion;

    /// <summary>图物理存储目录。</summary>
    internal string RootDirectory { get; }

    /// <summary>图目录 marker 的完整路径。</summary>
    internal string MarkerPath => Path.Combine(RootDirectory, MarkerFileName);

    /// <summary>可恢复维护 sidecar 的完整路径。</summary>
    internal string MaintenanceManifestPath => Path.Combine(RootDirectory, GraphMaintenanceManifestCodec.FileName);

    /// <summary>离线算法 durable workspace 根目录。</summary>
    internal string OfflineAlgorithmRootDirectory => Path.Combine(RootDirectory, "algorithms");

    /// <summary>
    /// 创建新的图存储 marker 并打开底层 KV keyspace。
    /// </summary>
    /// <param name="definition">图目录定义。</param>
    /// <param name="rootDirectory">图物理存储目录。</param>
    /// <param name="options">底层 KV 选项。</param>
    /// <param name="commitGate">与同一 manager 中其他 graph 共享的备份/提交门。</param>
    /// <returns>已打开的图存储。</returns>
    internal static GraphStore CreateNew(
        GraphDefinition definition,
        string rootDirectory,
        KvOptions options,
        object commitGate)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(commitGate);

        Directory.CreateDirectory(rootDirectory);
        string markerPath = Path.Combine(rootDirectory, MarkerFileName);
        if (File.Exists(markerPath))
            throw new InvalidOperationException($"图存储 marker 已存在：'{rootDirectory}'。");

        WriteMarker(markerPath, definition);
        try
        {
            KvKeyspace keyspace = KvKeyspace.Open(
                "graph." + definition.StorageId.ToString("N"),
                rootDirectory,
                options);
            return new GraphStore(definition, rootDirectory, keyspace, commitGate);
        }
        catch
        {
            // 调用方负责删除整个候选 store 目录；这里不吞掉 KV 打开异常。
            throw;
        }
    }

    /// <summary>
    /// 校验已有图存储 marker 后打开底层 KV keyspace。
    /// </summary>
    /// <param name="definition">目录中的预期图定义。</param>
    /// <param name="rootDirectory">图物理存储目录。</param>
    /// <param name="options">底层 KV 选项。</param>
    /// <param name="commitGate">与同一 manager 中其他 graph 共享的备份/提交门。</param>
    /// <returns>已打开的图存储。</returns>
    internal static GraphStore OpenExisting(
        GraphDefinition definition,
        string rootDirectory,
        KvOptions options,
        object commitGate)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(commitGate);

        if (!Directory.Exists(rootDirectory))
        {
            throw new InvalidDataException(
                $"图 '{definition.Name}' 的物理存储目录不存在：'{rootDirectory}'。");
        }

        GraphDefinition markerDefinition = ReadMarker(Path.Combine(rootDirectory, MarkerFileName));
        EnsureDefinitionMatches(definition, markerDefinition, rootDirectory);

        // marker 已完整校验后才获取 KV 生命周期租约；损坏或错配目录不会占用 WAL 文件。
        KvKeyspace keyspace = KvKeyspace.Open(
            "graph." + definition.StorageId.ToString("N"),
            rootDirectory,
            options);
        return new GraphStore(definition, rootDirectory, keyspace, commitGate);
    }

    /// <summary>
    /// 只读校验已有物理目录的 marker 身份，不获取底层 KV 生命周期租约。
    /// </summary>
    /// <param name="definition">目录中的预期图定义。</param>
    /// <param name="rootDirectory">图物理存储目录。</param>
    internal static void ValidateExistingMarker(GraphDefinition definition, string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rootDirectory);
        if (!Directory.Exists(rootDirectory))
        {
            throw new InvalidDataException(
                $"图 '{definition.Name}' 的物理存储目录不存在：'{rootDirectory}'。");
        }

        GraphDefinition markerDefinition = ReadMarker(Path.Combine(rootDirectory, MarkerFileName));
        EnsureDefinitionMatches(definition, markerDefinition, rootDirectory);
    }

    /// <summary>
    /// 创建当前图的 KV 一致快照。
    /// </summary>
    internal void CreateSnapshot()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _keyspace.CreateSnapshot();
        }
    }

    /// <summary>创建单 graph、单 keyspace 的乐观原子 transaction。</summary>
    /// <param name="requestId">用于未知提交结果解析和重复请求去重的稳定 ID。</param>
    /// <param name="limits">可选 transaction 写预算。</param>
    /// <returns>尚未提交的 transaction。</returns>
    public GraphTransaction BeginTransaction(
        Guid requestId,
        GraphTransactionLimits? limits = null)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return new GraphTransaction(this, requestId, limits);
        }
    }

    /// <summary>创建固定当前序列的 Graph statement 读快照。</summary>
    /// <returns>可执行点读、索引 seek 与流式邻接扩展的读会话。</returns>
    /// <remarks>
    /// 同一会话及其所有游标只观察创建时已经提交的状态；会话创建后的并发写仅对后续会话可见。
    /// 读快照不持有 Graph 提交锁，长遍历不会阻塞并发 writer。
    /// </remarks>
    public GraphReadSession BeginRead()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return new GraphReadSession(this, _keyspace.AcquireReadSnapshot());
        }
    }

    internal GraphStatistics? GetCachedStatistics()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _statistics;
        }
    }

    internal long CurrentSequence
    {
        get
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                return _keyspace.LastSequence;
            }
        }
    }

    internal void PublishStatistics(GraphStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_statistics is null || statistics.Sequence >= _statistics.Sequence)
                _statistics = statistics;
        }
    }

    /// <summary>按稳定快照有界重建图的邻接、label、property 和可识别 unique 派生索引。</summary>
    /// <param name="options">页大小和可选的唯一索引声明。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>扫描、修复、删除计数及最后序列号。</returns>
    /// <remarks>
    /// 冻结 V1 元素记录不包含 unique 声明。未提供声明时，方法只能从现存 unique key 推断声明；
    /// 若某个声明的全部 key 均已丢失，调用方必须通过 <paramref name="options"/> 重新提供该声明。
    /// </remarks>
    public GraphIndexRebuildResult RebuildIndexes(
        GraphIndexRebuildOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GraphIndexRebuildOptions();
        options.Validate();
        options = options with { UniqueIndexes = options.UniqueIndexes.ToArray() };
        var maintenanceOptions = new GraphMaintenanceOptions
        {
            UniqueIndexes = options.UniqueIndexes,
            PageSize = options.PageSize,
            MaxPageBytes = 32 * 1024 * 1024,
            MaxWorkUnits = int.MaxValue,
            MaxUniqueIndexDefinitions = options.MaxUniqueIndexDefinitions,
            CheckpointEveryWorkUnits = 64,
        };
        GraphMaintenanceResult result;
        lock (_maintenanceGate)
            result = RunMaintenanceCore(maintenanceOptions, cancellationToken);
        return new GraphIndexRebuildResult(
            result.Sequence,
            result.ScannedRecords,
            result.RepairedEntries,
            result.RemovedEntries,
            result.UniqueIndexDefinitionCount,
            options.UniqueIndexes.Count > 0);
    }

    /// <summary>
    /// 执行有限数量的 Graph repair/checkpoint work unit，并把续作位置持久化到 graph 目录。
    /// </summary>
    /// <param name="options">分页、续作、checkpoint 和可选 compaction 预算。</param>
    /// <param name="cancellationToken">取消令牌；已提交页保持 durable，下一次调用从最后保存的位置续作。</param>
    /// <returns>当前阶段、累计计数和稳定 operation ID。</returns>
    /// <remarks>
    /// 每个 work unit 单独获取 manager 提交门，页与页之间允许业务 writer 继续提交。任务完成前的
    /// sidecar 是唯一声明和 continuation 的修复来源；损坏时会明确拒绝，绝不静默从头覆盖。
    /// </remarks>
    public GraphMaintenanceResult RunMaintenance(
        GraphMaintenanceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new GraphMaintenanceOptions();
        options.Validate();
        options = options with { UniqueIndexes = options.UniqueIndexes.ToArray() };
        lock (_maintenanceGate)
            return RunMaintenanceCore(options, cancellationToken);
    }

    /// <summary>
    /// 在固定 statement snapshot 上执行 degree、弱连通分量、PageRank 和确定性 community，
    /// 并把结果发布到 Graph properties 或标准关系结果表。
    /// </summary>
    /// <param name="request">稳定 operation ID 与输出目标。</param>
    /// <param name="options">分页、内存、迭代和单次续作预算。</param>
    /// <param name="cancellationToken">取消令牌；最近完成的 work unit 保持 durable。</param>
    /// <returns>当前阶段、输入版本、收敛状态和发布进度。</returns>
    public GraphOfflineAlgorithmResult RunOfflineAlgorithms(
        GraphOfflineAlgorithmRequest request,
        GraphOfflineAlgorithmOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new GraphOfflineAlgorithmOptions();
        options.Validate();
        lock (_offlineAlgorithmGate)
            return GraphOfflineAlgorithmRunner.Run(this, request, options, cancellationToken);
    }

    /// <summary>为当前 Graph 创建完整 KV checkpoint，并回收已覆盖的 WAL。</summary>
    /// <returns>checkpoint 覆盖到的 KV sequence。</returns>
    public long Checkpoint()
    {
        lock (_commitGate)
            lock (_sync)
            {
                ThrowIfDisposed();
                return _keyspace.CreateSnapshot();
            }
    }

    /// <summary>将当前 Graph 压实为新的不可变 KV generation。</summary>
    /// <returns>compaction 覆盖到的 KV sequence。</returns>
    public long Compact()
    {
        lock (_commitGate)
            lock (_sync)
            {
                ThrowIfDisposed();
                return _keyspace.Compact();
            }
    }

    /// <summary>
    /// 在 manager 级备份/提交门和 store 生命周期锁内发布一个条件批次。
    /// 锁序固定为 commit gate、store、KV；备份用同一门覆盖 checkpoint 与文件复制。
    /// </summary>
    internal KvConditionalBatchResult ApplyTransactionBatch(
        IReadOnlyList<KvBatchMutation> mutations,
        IReadOnlyList<KvBatchPrecondition> preconditions,
        CancellationToken cancellationToken)
    {
        lock (_commitGate)
            lock (_sync)
            {
                ThrowIfDisposed();
                return _keyspace.ApplyConditionalBatch(mutations, preconditions, cancellationToken);
            }
    }

    /// <summary>当前图是否已经释放。</summary>
    internal bool IsDisposed
    {
        get
        {
            lock (_sync)
                return _disposed;
        }
    }

    /// <summary>访问底层 KV keyspace；仅供图存储实现使用。</summary>
    internal KvKeyspace Keyspace
    {
        get
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                return _keyspace;
            }
        }
    }

    private GraphMaintenanceResult RunMaintenanceCore(
        GraphMaintenanceOptions options,
        CancellationToken cancellationToken)
    {
        GraphMaintenanceState state;
        bool resumed;
        lock (_commitGate)
            lock (_sync)
            {
                ThrowIfDisposed();
                state = GraphMaintenanceRunner.LoadOrCreate(
                    MaintenanceManifestPath,
                    StorageId,
                    _keyspace,
                    options,
                    out resumed);
            }

        for (int index = 0;
             index < options.MaxWorkUnits && state.Phase != GraphMaintenancePhase.Completed;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_commitGate)
                lock (_sync)
                {
                    ThrowIfDisposed();
                    GraphMaintenanceRunner.RunNextWorkUnit(
                        _keyspace,
                        state,
                        options,
                        cancellationToken);
                    GraphMaintenanceRunner.RunPeriodicCheckpointIfDue(_keyspace, state, options);
                    if (state.Phase == GraphMaintenancePhase.Completed)
                        GraphMaintenanceManifestCodec.Delete(MaintenanceManifestPath);
                    else
                        GraphMaintenanceManifestCodec.Save(MaintenanceManifestPath, state);
                }
            AfterMaintenanceWorkUnitTestHook?.Invoke(state.Phase, state.WorkUnits);
        }

        return new GraphMaintenanceResult(state, resumed);
    }

    /// <summary>
    /// 释放图的底层 KV 资源。
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _keyspace.Dispose();
        }
    }

    private static void WriteMarker(string path, GraphDefinition definition)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("图 marker 路径必须包含父目录。", nameof(path));

        byte[] nameBytes = Utf8.GetBytes(definition.Name);
        if (nameBytes.Length > MarkerNameCapacity)
            throw new InvalidDataException("图 marker 名称超出固定容量。");

        byte[] marker = new byte[MarkerSize];
        Span<byte> span = marker;
        MarkerMagic.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], MarkerFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], MarkerHeaderSize);
        definition.StorageId.TryWriteBytes(span[16..32]);
        BinaryPrimitives.WriteInt64LittleEndian(span[32..], definition.CreatedAtUtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], definition.RecordFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(span[44..], nameBytes.Length);
        nameBytes.CopyTo(span[MarkerHeaderSize..]);

        uint crc = Crc32.HashToUInt32(span[..MarkerCrcOffset]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[MarkerCrcOffset..], crc);
        MarkerMagic.CopyTo(span[MarkerFooterOffset..]);

        string temporaryPath = path + ".tmp";
        try
        {
            using (var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: MarkerSize,
                options: FileOptions.WriteThrough))
            {
                destination.Write(marker);
                destination.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: false);
            SonnetDB.Wal.DirectoryFsync.FlushRequired(directory);
        }
        catch
        {
            TryDeleteTemporary(temporaryPath);
            throw;
        }
    }

    private static GraphDefinition ReadMarker(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"图存储 marker 缺失：'{path}'。");

        byte[] marker = new byte[MarkerSize];
        try
        {
            using var source = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: MarkerSize,
                options: FileOptions.SequentialScan);
            if (source.Length != MarkerSize)
                throw new InvalidDataException("图存储 marker 长度无效或已截断。");
            source.ReadExactly(marker);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException
            && exception is not InvalidDataException)
        {
            throw new InvalidDataException($"图存储 marker 无法读取：'{path}'。", exception);
        }

        ReadOnlySpan<byte> span = marker;
        if (!span[..MarkerMagic.Length].SequenceEqual(MarkerMagic))
            throw new InvalidDataException("图存储 marker magic 无效。");
        if (BinaryPrimitives.ReadInt32LittleEndian(span[8..]) != MarkerFormatVersion)
            throw new InvalidDataException("图存储 marker 版本不受支持。");
        if (BinaryPrimitives.ReadInt32LittleEndian(span[12..]) != MarkerHeaderSize)
            throw new InvalidDataException("图存储 marker header size 无效。");

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(span[MarkerCrcOffset..]);
        uint actualCrc = Crc32.HashToUInt32(span[..MarkerCrcOffset]);
        if (expectedCrc != actualCrc)
            throw new InvalidDataException("图存储 marker CRC32 不匹配。");
        if (!span[MarkerFooterOffset..].SequenceEqual(MarkerMagic))
            throw new InvalidDataException("图存储 marker footer magic 无效。");

        int nameLength = BinaryPrimitives.ReadInt32LittleEndian(span[44..]);
        if (nameLength <= 0 || nameLength > MarkerNameCapacity)
            throw new InvalidDataException("图存储 marker 名称长度无效。");
        ReadOnlySpan<byte> namePayload = span.Slice(MarkerHeaderSize, MarkerNameCapacity);
        for (int index = nameLength; index < namePayload.Length; index++)
        {
            if (namePayload[index] != 0)
                throw new InvalidDataException("图存储 marker 名称保留字节必须为零。");
        }

        string name;
        try
        {
            name = Utf8.GetString(namePayload[..nameLength]);
            GraphDefinition.ValidateName(name);
        }
        catch (Exception exception) when (exception is DecoderFallbackException or ArgumentException)
        {
            throw new InvalidDataException("图存储 marker 名称无效。", exception);
        }

        Guid storageId = new Guid(span[16..32]);
        long createdAtUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(span[32..]);
        int recordFormatVersion = BinaryPrimitives.ReadInt32LittleEndian(span[40..]);
        try
        {
            return GraphDefinition.Restore(
                name,
                storageId,
                createdAtUtcTicks,
                recordFormatVersion);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("图存储 marker 元数据无效。", exception);
        }
    }

    private static void EnsureDefinitionMatches(
        GraphDefinition expected,
        GraphDefinition actual,
        string rootDirectory)
    {
        if (!string.Equals(expected.Name, actual.Name, StringComparison.Ordinal)
            || expected.StorageId != actual.StorageId
            || expected.CreatedAtUtcTicks != actual.CreatedAtUtcTicks
            || expected.RecordFormatVersion != actual.RecordFormatVersion)
        {
            throw new InvalidDataException(
                $"图 '{expected.Name}' 的存储 marker 与目录定义不匹配：'{rootDirectory}'。");
        }
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve the original marker write failure.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
