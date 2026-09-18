using System.Security.Cryptography;
using SonnetDB.Engine;
using SonnetDB.Generations;

namespace SonnetDB.SemanticContent;

public sealed partial class RagIngestionWriter
{
    internal static SemaphoreSlim ManagementGate(Tsdb database)
        => Gates.GetValue(database, static _ => new SemaphoreSlim(1, 1));

    internal static void ValidateManagementOptions(RagIngestionWriterOptions options)
    {
        ValidateOptions(options);
        if (options.MaxCheckpointBytes > 64 * 1024 * 1024 || options.MaxTextCharacters > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "管理操作的快照与正文预算最多 64 MiB/字符。");
    }

    internal static RagIngestionWriterCheckpoint? ReadManagementPending(Tsdb database, string stream,
        RagIngestionWriterOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(Path.Combine(database.Keyspaces.KeyspacesDirectory, JobsKeyspaceName)))
            return null;
        string key = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(stream)));
        return DecodeManagementCheckpoint(database.Keyspaces.Open(JobsKeyspaceName).Get(key), stream, options, token);
    }

    internal static RagIngestionWriterCheckpoint? ReadManagementPublished(Tsdb database, string stream,
        DatabaseGenerationQueryLease? active, RagIngestionWriterOptions options, CancellationToken token)
    {
        if (active is null)
            return null;
        return ReadManagementPublished(database, stream, active.Generation, options, token);
    }

    private static RagIngestionWriterCheckpoint ReadManagementPublished(Tsdb database, string stream,
        DatabaseGeneration generation, RagIngestionWriterOptions options, CancellationToken token)
    {
        ValidateManagementResources(generation);
        string name = generation.Resources.Single(static resource => resource.Role == SnapshotResourceRole).Name;
        if (!Directory.Exists(Path.Combine(database.Keyspaces.KeyspacesDirectory, name)))
            throw new InvalidDataException("已发布 RAG 快照缺失。");
        var checkpoint = DecodeManagementCheckpoint(database.Keyspaces.Open(name).Get("snapshot"), stream, options, token)
            ?? throw new InvalidDataException("已发布 RAG 快照缺失。");
        if (checkpoint.GenerationId != generation.GenerationId
            || checkpoint.ExpectedRevision != generation.Revision - 1 || name != ResourceName(checkpoint))
            throw new InvalidDataException("已发布 RAG 快照身份不一致。");
        return checkpoint;
    }

    internal static void ValidateManagementResources(DatabaseGeneration generation)
    {
        string name = "rag_" + generation.GenerationId;
        if (!Guid.TryParseExact(generation.GenerationId, "N", out _) || generation.Resources.Count != 3
            || !generation.Resources.Any(resource => resource.Role == ChunksResourceRole
                && resource.Kind == DatabaseGenerationResourceKind.DocumentCollection && resource.Name == name)
            || !generation.Resources.Any(resource => resource.Role == SnapshotResourceRole
                && resource.Kind == DatabaseGenerationResourceKind.KvKeyspace && resource.Name == name)
            || !generation.Resources.Any(resource => resource.Role == "rag.fulltext"
                && resource.Kind == DatabaseGenerationResourceKind.DocumentFullTextIndex
                && resource.Name == FullTextIndexName && resource.ParentName == name))
            throw new InvalidDataException("generation 不属于独占 RAG 资源合同，不能通过 RAG 治理入口操作。");
    }

    private void ValidateRetainedProfiles(long expectedRevision, CancellationToken token)
    {
        _database.Generations.ExecuteAtRevision(_stream, expectedRevision, () =>
        {
            // 只对仍受 catalog 管理的快照执行合同检查；已清理历史不构成永久 profile 注册表。
            foreach (var generation in _database.Generations.ListForManagement(_stream, 1024, requireComplete: true))
            {
                token.ThrowIfCancellationRequested();
                var retained = ReadManagementPublished(_database, _stream, generation, _options, token);
                if (retained.Profile.Id == _profile.Id && !ProfilesEqual(retained.Profile, _profile))
                    throw new DatabaseGenerationException(RagIngestionManagementErrorCodes.ProfileMismatch,
                        "保留 generation 中已有相同 profile ID 的不同模型合同；模型换代必须使用新的 profile ID。");
            }
            return true;
        });
    }

    internal static bool IsManagementCheckpointPublished(Tsdb database, string stream, RagIngestionWriterCheckpoint checkpoint)
    {
        if (database.Generations.TryGet(stream, checkpoint.ExpectedRevision + 1)?.GenerationId == checkpoint.GenerationId)
            return true;
        string name = ResourceName(checkpoint);
        // 即使已发布描述符资源损坏或外部发布使用了不同 revision，也不能删除受管资源。
        return database.Generations.IsManagedResource(new(ChunksResourceRole, DatabaseGenerationResourceKind.DocumentCollection, name))
            || database.Generations.IsManagedResource(new(SnapshotResourceRole, DatabaseGenerationResourceKind.KvKeyspace, name));
    }

    internal async ValueTask<RagIngestionWriteResult> RebuildManagedAsync(long expectedRevision,
        Func<SemanticContentChunk, CancellationToken, ValueTask<float[]>> embedAsync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embedAsync);
        using var deadline = CreateDeadline(cancellationToken);
        CancellationToken token = deadline.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using DatabaseGenerationQueryLease? active = AcquireActive();
            CheckManagedRevision(active, expectedRevision);
            if (active is null)
                throw new DatabaseGenerationException(DatabaseGenerationErrorCodes.NoActiveGeneration, "没有可重建的已发布 RAG generation。");
            var pending = ReadPending(token);
            if (pending is not null && !IsManagementCheckpointPublished(_database, _stream, pending))
                throw new DatabaseGenerationException(RagIngestionManagementErrorCodes.PendingConflict, "请先续跑或丢弃未发布任务。");
            var previous = ReadPublished(active, token)!;
            if (previous.Profile.Id == _profile.Id && !ProfilesEqual(previous.Profile, _profile))
                throw new DatabaseGenerationException(RagIngestionManagementErrorCodes.ProfileMismatch, "模型换代必须使用新的 profile ID。");
            ValidateRetainedProfiles(expectedRevision, token);
            var manifests = new SemanticContentManifest[previous.Snapshot.Manifests.Count];
            for (int i = 0; i < manifests.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                manifests[i] = previous.Snapshot.Manifests[i] with { EmbeddingProfileId = _profile.Id };
            }
            var checkpoint = new RagIngestionWriterCheckpoint
            {
                Stream = _stream,
                GenerationId = Guid.NewGuid().ToString("N"),
                ExpectedRevision = expectedRevision,
                Profile = _profile,
                Snapshot = Freeze(new(manifests), token),
                UpdatedContents = manifests.Length,
                RebuildAllVectors = true,
            };
            byte[] bytes = EncodeCheckpoint(checkpoint);
            token.ThrowIfCancellationRequested();
            Jobs.Put(JobKey, bytes);
            Jobs.CreateSnapshot();
            return await ExecuteAsync(checkpoint, active, previous, embedAsync, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async ValueTask<RagIngestionWriteResult?> ResumeManagedAsync(string pendingGenerationId, long expectedRevision,
        Func<SemanticContentChunk, CancellationToken, ValueTask<float[]>> embedAsync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embedAsync);
        using var deadline = CreateDeadline(cancellationToken);
        CancellationToken token = deadline.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using DatabaseGenerationQueryLease? active = AcquireActive();
            CheckManagedRevision(active, expectedRevision);
            var checkpoint = RequireManagedPending(pendingGenerationId, token);
            if (checkpoint.ExpectedRevision != expectedRevision)
                throw new DatabaseGenerationException(DatabaseGenerationErrorCodes.RevisionConflict, "任务冻结版本与预期版本不一致。");
            if (!ProfilesEqual(checkpoint.Profile, _profile))
                throw new DatabaseGenerationException(RagIngestionManagementErrorCodes.ProfileMismatch, "续跑必须使用持久任务中的完整 profile。");
            ValidateRetainedProfiles(expectedRevision, token);
            checkpoint = checkpoint with { Snapshot = Freeze(checkpoint.Snapshot, token) };
            return await ExecuteAsync(checkpoint, active, ReadPublished(active, token), embedAsync, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async ValueTask<bool> DiscardManagedAsync(string pendingGenerationId, long expectedRevision, CancellationToken cancellationToken)
    {
        using var deadline = CreateDeadline(cancellationToken);
        CancellationToken token = deadline.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return _database.Generations.ExecuteAtRevision(_stream, expectedRevision, () =>
            {
                var checkpoint = RequireManagedPending(pendingGenerationId, token);
                token.ThrowIfCancellationRequested();
                _database.Documents.Drop(ResourceName(checkpoint));
                _database.Keyspaces.Drop(ResourceName(checkpoint));
                Jobs.Delete(JobKey);
                Jobs.CreateSnapshot();
                return true;
            });
        }
        finally { _gate.Release(); }
    }

    private RagIngestionWriterCheckpoint RequireManagedPending(string generationId, CancellationToken token)
    {
        var checkpoint = ReadPending(token);
        if (checkpoint is null || IsManagementCheckpointPublished(_database, _stream, checkpoint))
            throw new DatabaseGenerationException(RagIngestionManagementErrorCodes.PendingUnavailable, "没有未发布 RAG 任务。");
        if (checkpoint.GenerationId != generationId)
            throw new DatabaseGenerationException(RagIngestionManagementErrorCodes.PendingConflict, "未发布任务身份已变化。");
        return checkpoint;
    }

    private static void CheckManagedRevision(DatabaseGenerationQueryLease? active, long expectedRevision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        if ((active?.Generation.Revision ?? 0) != expectedRevision)
            throw new DatabaseGenerationException(DatabaseGenerationErrorCodes.RevisionConflict, "active revision 与调用方预期不一致。");
    }
}
