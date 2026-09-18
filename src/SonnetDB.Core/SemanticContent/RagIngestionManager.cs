using System.Text;
using SonnetDB.Engine;
using SonnetDB.Generations;

namespace SonnetDB.SemanticContent;

/// <summary>复用持久 writer、generation 与查询租约的有界 RAG 治理入口。</summary>
public sealed class RagIngestionManager
{
    private readonly Tsdb _database;
    private readonly string _stream;
    private readonly RagIngestionWriterOptions _options;

    /// <summary>绑定待管理的数据库与独占 RAG stream；不会创建第二套存储。</summary>
    /// <param name="database">调用方已获授权的数据库。</param>
    /// <param name="stream">RAG stream。</param>
    /// <param name="options">checkpoint、内容数量与完整操作的协作超时预算。</param>
    public RagIngestionManager(Tsdb database, string stream, RagIngestionWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        if (new UTF8Encoding(false, true).GetByteCount(stream) > 4096)
            throw new ArgumentOutOfRangeException(nameof(stream));
        _database = database;
        _stream = stream;
        _options = options ?? new();
        RagIngestionWriter.ValidateManagementOptions(_options);
    }

    /// <summary>读取 active 和真实未发布任务的完整 profile、身份与计数，不输出正文或向量。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>单个 stream 的有界状态快照。</returns>
    public async ValueTask<RagIngestionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CreateDeadline(cancellationToken);
        CancellationToken token = deadline.Token;
        SemaphoreSlim gate = RagIngestionWriter.ManagementGate(_database);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using DatabaseGenerationQueryLease? active = AcquireActive();
            var published = RagIngestionWriter.ReadManagementPublished(_database, _stream, active, _options, token);
            var pending = RagIngestionWriter.ReadManagementPending(_database, _stream, _options, token);
            RagGenerationStatus? activeStatus = published is null ? null : new(active!.Generation.GenerationId,
                active.Generation.Revision, active.Generation.PublishedAtUtc, FreezeProfile(published.Profile),
                published.Snapshot.Manifests.Count, CountChunks(published, token));
            RagPendingStatus? pendingStatus = pending is null || RagIngestionWriter.IsManagementCheckpointPublished(_database, _stream, pending)
                ? null : new(pending.GenerationId, pending.ExpectedRevision, FreezeProfile(pending.Profile),
                    pending.Snapshot.Manifests.Count, CountChunks(pending, token));
            token.ThrowIfCancellationRequested();
            return new(_stream, activeStatus, pendingStatus);
        }
        finally { gate.Release(); }
    }

    /// <summary>从持久 active 快照重建全部向量；新 profile ID 可用于模型换代，发布前失败保持 active。</summary>
    /// <param name="profile">目标完整模型合同；ID 在保留快照中不可更改合同，宿主负责可信 provider、外发策略与审计。</param>
    /// <param name="expectedRevision">必须匹配当前 active 的版本。</param>
    /// <param name="embedAsync">遵守目标 profile 和取消令牌的模型调用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完整发布的新 generation 与统计。</returns>
    public ValueTask<RagIngestionWriteResult> RebuildAsync(EmbeddingProfile profile, long expectedRevision,
        Func<SemanticContentChunk, CancellationToken, ValueTask<float[]>> embedAsync, CancellationToken cancellationToken = default)
        => new RagIngestionWriter(_database, _stream, profile, _options).RebuildManagedAsync(expectedRevision, embedAsync, cancellationToken);

    /// <summary>以明确任务身份、预期版本和完整 profile 续跑；不自动替换其他 pending。</summary>
    /// <param name="pendingGenerationId">状态接口返回的未发布任务身份。</param>
    /// <param name="expectedRevision">当前 active 及任务的预期版本。</param>
    /// <param name="profile">必须与持久任务完全一致的模型合同。</param>
    /// <param name="embedAsync">宿主批准的模型调用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>发布结果；无 pending 时抛出稳定错误。</returns>
    public ValueTask<RagIngestionWriteResult?> ResumeAsync(string pendingGenerationId, long expectedRevision, EmbeddingProfile profile,
        Func<SemanticContentChunk, CancellationToken, ValueTask<float[]>> embedAsync, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingGenerationId);
        return new RagIngestionWriter(_database, _stream, profile, _options).ResumeManagedAsync(pendingGenerationId, expectedRevision, embedAsync, cancellationToken);
    }

    /// <summary>只丢弃指定未发布任务；原子检查 active revision，永不删除已被 generation 管理的资源。</summary>
    /// <param name="pendingGenerationId">状态接口返回的未发布任务身份。</param>
    /// <param name="expectedRevision">调用方观察到的 active revision。</param>
    /// <param name="cancellationToken">取消令牌；开始物理删除后完成本次删除再退出。</param>
    /// <returns>是否丢弃了任务；缺失或身份变化抛出稳定错误。</returns>
    public async ValueTask<bool> DiscardPendingAsync(string pendingGenerationId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingGenerationId);
        using var deadline = CreateDeadline(cancellationToken);
        var status = await GetStatusAsync(deadline.Token).ConfigureAwait(false);
        if (status.Pending is null)
            throw new DatabaseGenerationException(RagIngestionManagementErrorCodes.PendingUnavailable, "没有未发布 RAG 任务。");
        return await new RagIngestionWriter(_database, _stream, status.Pending.Profile, _options)
            .DiscardManagedAsync(pendingGenerationId, expectedRevision, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>最多检查指定数量的 retired generation，在同一 catalog 锁内检查预期版本。</summary>
    /// <param name="expectedRevision">调用方观察到的 active revision。</param>
    /// <param name="maxGenerations">最多检查的候选数，一至一千零二十四；含因租约或保留时间延后的候选。</param>
    /// <param name="publishedBeforeUtc">可选 inclusive 发布时间 cutoff。</param>
    /// <param name="cancellationToken">协作取消令牌。</param>
    /// <returns>已删除和因租约或保留时间延后的版本。</returns>
    public async ValueTask<DatabaseGenerationCleanupResult> CleanupRetiredAsync(long expectedRevision, int maxGenerations,
        DateTimeOffset? publishedBeforeUtc = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxGenerations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxGenerations, 1024);
        using var deadline = CreateDeadline(cancellationToken);
        SemaphoreSlim gate = RagIngestionWriter.ManagementGate(_database);
        await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            return _database.Generations.ExecuteAtRevision(_stream, expectedRevision, () =>
            {
                using var active = _database.Generations.AcquireActive(_stream);
                _ = RagIngestionWriter.ReadManagementPublished(_database, _stream, active, _options, deadline.Token);
                foreach (var candidate in _database.Generations.ListForManagement(_stream, maxGenerations, requireComplete: false))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    RagIngestionWriter.ValidateManagementResources(candidate);
                }
                return _database.Generations.CleanupRetired(_stream, new(publishedBeforeUtc ?? DateTimeOffset.MaxValue)
                { MaxGenerations = maxGenerations, ExpectedRevision = expectedRevision }, deadline.Token);
            });
        }
        finally { gate.Release(); }
    }

    private DatabaseGenerationQueryLease? AcquireActive()
    {
        try { return _database.Generations.AcquireActive(_stream); }
        catch (DatabaseGenerationException exception) when (exception.Code == DatabaseGenerationErrorCodes.NoActiveGeneration) { return null; }
    }

    private CancellationTokenSource CreateDeadline(CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_options.MaxDuration);
        return deadline;
    }

    private static EmbeddingProfile FreezeProfile(EmbeddingProfile profile)
    {
        if (profile.SupportedModalities is null || profile.SupportedModalities.Count > 6 || profile.DataEgressPolicy is null
            || SemanticContentValidator.ValidateProfile(profile).Count != 0)
            throw new InvalidDataException("持久 RAG profile 无效。");
        return profile with { SupportedModalities = Array.AsReadOnly(profile.SupportedModalities.ToArray()) };
    }

    private static int CountChunks(RagIngestionWriterCheckpoint checkpoint, CancellationToken token)
    {
        int count = 0;
        foreach (var manifest in checkpoint.Snapshot.Manifests)
        {
            token.ThrowIfCancellationRequested();
            count = checked(count + manifest.Chunks.Count);
        }
        return count;
    }
}
