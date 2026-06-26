using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;

namespace SonnetDB.Copilot;

/// <summary>
/// 服务端启动后的后台技能库增量摄入服务（PR #65）。
/// 仅当 <see cref="CopilotOptions.Enabled"/> 与 <see cref="CopilotSkillsOptions.AutoIngestOnStartup"/>
/// 同时为 <c>true</c>，且 <see cref="CopilotReadiness"/> 报告 embedding 已就绪时才会运行。
/// 任何摄入异常都被吞掉并记录日志，不影响服务端启动。
/// </summary>
internal sealed class CopilotSkillsIngestionService : BackgroundService
{
    private readonly CopilotOptions _copilot;
    private readonly CopilotReadiness _readiness;
    private readonly SkillRegistry _registry;
    private readonly ILogger<CopilotSkillsIngestionService> _logger;

    public CopilotSkillsIngestionService(
        IOptions<ServerOptions> serverOptions,
        CopilotReadiness readiness,
        SkillRegistry registry,
        ILogger<CopilotSkillsIngestionService> logger)
    {
        ArgumentNullException.ThrowIfNull(serverOptions);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _copilot = serverOptions.Value.Copilot;
        _readiness = readiness;
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_copilot.Enabled)
            return;
        if (!_copilot.Skills.AutoIngestOnStartup)
            return;

        var snapshot = _readiness.Evaluate();
        if (!snapshot.EmbeddingReady)
        {
            _logger.LogInformation(
                "Copilot skills auto-ingest skipped: embedding provider not ready (reason={Reason}).",
                snapshot.Reason);
            return;
        }

        try
        {
            var stats = await _registry.IngestAsync(_copilot.Skills.Root, force: false, dryRun: false, stoppingToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Copilot skills auto-ingest completed: scanned={Scanned}, indexed={Indexed}, skipped={Skipped}, deleted={Deleted}.",
                stats.ScannedSkills,
                stats.IndexedSkills,
                stats.SkippedSkills,
                stats.DeletedSkills);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 优雅关停。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot skills auto-ingest failed; continuing startup.");
        }
    }
}
