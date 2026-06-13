using Microsoft.Extensions.Hosting;

namespace SonnetDB.Caching;

internal sealed class SonnetDbCacheJanitor : BackgroundService
{
    private readonly SonnetDbCacheStore _store;

    public SonnetDbCacheJanitor(SonnetDbCacheStore store)
    {
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _store.Options.ExpirationScanInterval;
        if (interval <= TimeSpan.Zero)
            return;

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await _store.CleanExpiredAsync(_store.Options.ExpirationScanBatchSize, stoppingToken).ConfigureAwait(false);
    }
}
