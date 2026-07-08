using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SonnetDB.Caching.Distributed;

internal sealed class SonnetDbDistributedCacheJanitor : BackgroundService
{
    private readonly ILogger<SonnetDbDistributedCacheJanitor> _logger;
    private readonly IOptionsMonitor<SonnetDbDistributedCacheOptions> _options;

    public SonnetDbDistributedCacheJanitor(
        IOptionsMonitor<SonnetDbDistributedCacheOptions> options,
        ILogger<SonnetDbDistributedCacheJanitor> logger)
    {
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;
        if (options.ExpirationScanInterval <= TimeSpan.Zero)
            return;

        using var store = new SonnetDbDistributedCacheStore(options);
        using var timer = new PeriodicTimer(options.ExpirationScanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await store.CleanExpiredAsync(options.ExpirationScanBatchSize, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SonnetDB distributed cache expired-key cleanup failed; will retry on the next interval.");
            }
        }
    }
}
