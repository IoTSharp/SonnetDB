using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using SonnetDB.Auth;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Generations;
using SonnetDB.SemanticContent;

namespace SonnetDB.SemanticSearch;

internal sealed class RagManagementService(CopilotRagKnowledgeStore configuredProvider, IHttpContextAccessor httpContext)
{
    internal RagManagementProfiles Profiles()
    {
        EmbeddingProfile profile = configuredProvider.GetManagedProfile();
        return new([new(profile.Id, profile.Provider, profile.Model, profile.Revision, profile.Dimensions,
            profile.Normalization.ToString(), profile.Metric.ToString())]);
    }

    internal async Task<RagManagementStatus> StatusAsync(Tsdb database, string stream, CancellationToken token)
    {
        ValidateStream(stream);
        var status = await new RagIngestionManager(database, stream).GetStatusAsync(token).ConfigureAwait(false);
        return new(stream, status.Active?.Revision ?? 0, status.Active?.Profile.Id,
            status.Active?.ManifestCount ?? 0, status.Active?.ChunkCount ?? 0,
            status.Pending is not { } pending ? null : new(pending.GenerationId, pending.Profile.Id,
                pending.ExpectedRevision, pending.ManifestCount, pending.ChunkCount));
    }

    internal async Task<RagManagementResult> ExecuteAsync(Tsdb database, string operation,
        RagManagementRequest request, CancellationToken cancellationToken)
    {
        ValidateStream(request.Stream);
        if (request.ExpectedRevision is null or < 0) throw new ArgumentException("必须提供非负 expectedRevision。");
        if (request.MaxGenerations is < 1 or > 100) throw new ArgumentException("maxGenerations 必须为 1 至 100。");
        if (operation is "resume" or "discard" && !Guid.TryParseExact(request.PendingGenerationId, "N", out _))
            throw new ArgumentException("resume/discard 必须提供有效 pendingGenerationId。");
        if (operation == "cleanup" && request.RetiredBeforeUtc is null)
            throw new ArgumentException("cleanup 必须显式提供 retiredBeforeUtc。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        CancellationToken token = deadline.Token;
        var audit = new RagManagementAuditStore(database);
        HttpContext? context = httpContext.HttpContext;
        string principal = context is null ? "background" : BearerAuthMiddleware.GetUser(context)?.UserName
            ?? "role:" + (BearerAuthMiddleware.GetRole(context) ?? "unknown");
        var entry = new RagManagementAuditEntry(Guid.NewGuid(), DateTimeOffset.UtcNow,
            principal.Length <= 256 ? principal : principal[..256], operation,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Stream))), "started",
            request.ExpectedRevision.Value, null, null);
        audit.Write(entry, token);
        try
        {
            token.ThrowIfCancellationRequested();
            var manager = new RagIngestionManager(database, request.Stream,
                new RagIngestionWriterOptions { MaxDuration = TimeSpan.FromSeconds(120) });
            RagManagementResult result;
            if (operation is "rebuild" or "resume")
            {
                EmbeddingProfile profile = configuredProvider.GetManagedProfile();
                if (request.ProfileId != profile.Id) throw new RagManagementProfileException();
                RagIngestionWriteResult? written = operation == "rebuild"
                    ? await manager.RebuildAsync(profile, request.ExpectedRevision.Value, Embed, token).ConfigureAwait(false)
                    : await manager.ResumeAsync(request.PendingGenerationId!, request.ExpectedRevision.Value, profile, Embed, token).ConfigureAwait(false);
                result = new(operation, request.Stream, written is null ? "no_pending_task" : "completed",
                    written?.Generation.Revision ?? request.ExpectedRevision.Value, [], []);
                async ValueTask<float[]> Embed(SemanticContentChunk chunk, CancellationToken ct)
                {
                    try { return await configuredProvider.EmbedManagedAsync(database, profile, chunk.Text, ct).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { throw new RagManagementEmbeddingException(exception); }
                }
            }
            else if (operation == "discard")
            {
                bool removed = await manager.DiscardPendingAsync(request.PendingGenerationId!, request.ExpectedRevision.Value, token).ConfigureAwait(false);
                result = new(operation, request.Stream, removed ? "completed" : "no_pending_task", request.ExpectedRevision.Value, [], []);
            }
            else if (operation == "cleanup")
            {
                var cleaned = await manager.CleanupRetiredAsync(request.ExpectedRevision.Value, request.MaxGenerations,
                    request.RetiredBeforeUtc, token).ConfigureAwait(false);
                result = new(operation, request.Stream, "completed", request.ExpectedRevision.Value,
                    cleaned.RemovedRevisions, cleaned.DeferredRevisions.Concat(cleaned.RetentionDeferredRevisions).ToArray());
            }
            else throw new ArgumentException("不支持的 RAG 管理操作。");
            WriteTerminal("succeeded", result.Revision, null);
            return result;
        }
        catch (RagManagementAuditCompletionException) { throw; }
        catch (OperationCanceledException)
        {
            WriteTerminal("cancelled", null, cancellationToken.IsCancellationRequested ? "rag_cancelled" : "rag_timeout");
            if (!cancellationToken.IsCancellationRequested) throw new TimeoutException("RAG 管理操作超时。");
            throw;
        }
        catch (Exception exception)
        {
            WriteTerminal("failed", null, exception is DatabaseGenerationException generation ? generation.Code
                : exception is RagManagementProfileException ? "profile_mismatch" : "rag_operation_failed");
            throw;
        }
        void WriteTerminal(string status, long? revision, string? code)
        {
            using var auditDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { audit.Write(entry with { Status = status, Revision = revision, ErrorCode = code }, auditDeadline.Token); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
            {
                // 已提交的 mutation 不能因完成审计失败而反写为 failed；持久 started 记录提示调用方核对状态。
                throw new RagManagementAuditCompletionException(exception);
            }
        }
    }

    private static void ValidateStream(string stream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        if (stream.Length > 1024 || Encoding.UTF8.GetByteCount(stream) > 4096)
            throw new ArgumentException("stream 超过预算。");
    }
}
