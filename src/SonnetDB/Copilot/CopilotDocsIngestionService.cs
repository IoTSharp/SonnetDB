using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;

namespace SonnetDB.Copilot;

/// <summary>
/// 服务端启动后的后台增量文档摄入服务（PR #64）。
/// 仅当 <see cref="CopilotOptions.Enabled"/> 与 <see cref="CopilotDocsOptions.AutoIngestOnStartup"/>
/// 同时为 <c>true</c>，且 <see cref="CopilotReadiness"/> 报告 embedding 已就绪时才会运行。
/// 任何摄入异常都被吞掉并记录日志，不影响服务端启动。
/// </summary>
internal sealed class CopilotDocsIngestionService : BackgroundService
{
    private readonly CopilotOptions _copilot;
    private readonly CopilotReadiness _readiness;
    private readonly DocsIngestor _ingestor;
    private readonly ILogger<CopilotDocsIngestionService> _logger;

    public CopilotDocsIngestionService(
        IOptions<ServerOptions> serverOptions,
        CopilotReadiness readiness,
        DocsIngestor ingestor,
        ILogger<CopilotDocsIngestionService> logger)
    {
        ArgumentNullException.ThrowIfNull(serverOptions);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(ingestor);
        ArgumentNullException.ThrowIfNull(logger);
        _copilot = serverOptions.Value.Copilot;
        _readiness = readiness;
        _ingestor = ingestor;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_copilot.Enabled)
            return;
        if (!_copilot.Docs.AutoIngestOnStartup)
            return;

        var snapshot = _readiness.Evaluate();
        if (!snapshot.EmbeddingReady)
        {
            _logger.LogInformation(
                "Copilot docs auto-ingest skipped: embedding provider not ready (reason={Reason}).",
                snapshot.Reason);
            return;
        }

        try
        {
            var stats = await _ingestor.IngestAsync(_copilot.Docs.Roots, force: false, dryRun: false, stoppingToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Copilot docs auto-ingest completed: scanned={Scanned}, indexed={Indexed}, skipped={Skipped}, deleted={Deleted}, chunks={Chunks}.",
                stats.ScannedFiles,
                stats.IndexedFiles,
                stats.SkippedFiles,
                stats.DeletedFiles,
                stats.WrittenChunks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 优雅关停。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot docs auto-ingest failed; continuing startup.");
        }
    }
}
