using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SonnetDB.Caching.EasyCaching;

internal sealed class SonnetDbEasyCachingJanitor : BackgroundService
{
    private readonly ILogger<SonnetDbEasyCachingJanitor> _logger;
    private readonly SonnetDbEasyCachingRegistration _registration;

    public SonnetDbEasyCachingJanitor(
        SonnetDbEasyCachingRegistration registration,
        ILogger<SonnetDbEasyCachingJanitor> logger)
    {
        _registration = registration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _registration.Store.Options.ExpirationScanInterval;
        if (interval <= TimeSpan.Zero)
            return;

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _registration.Store
                    .CleanExpiredAsync(_registration.Store.Options.ExpirationScanBatchSize, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SonnetDB EasyCaching expired-key cleanup failed; will retry on the next interval.");
            }
        }
    }
}
