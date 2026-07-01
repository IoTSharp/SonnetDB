using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SonnetDB.Caching;

internal sealed class SonnetDbCacheJanitor : BackgroundService
{
    private readonly ILogger<SonnetDbCacheJanitor> _logger;
    private readonly SonnetDbCacheStore _store;

    public SonnetDbCacheJanitor(SonnetDbCacheStore store, ILogger<SonnetDbCacheJanitor> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _store.Options.ExpirationScanInterval;
        if (interval <= TimeSpan.Zero)
            return;

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _store.CleanExpiredAsync(_store.Options.ExpirationScanBatchSize, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SonnetDB cache janitor cleanup failed; will retry on the next interval.");
            }
        }
    }
}
