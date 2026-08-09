using System.Collections.Concurrent;

namespace SonnetDB.Modbus;

internal sealed class ModbusSourceOperationCoordinator
{
    private readonly ConcurrentDictionary<SourceKey, SemaphoreSlim> _locks = new();

    internal async ValueTask<Lease> AcquireAsync(
        string database,
        string source,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var key = new SourceKey(database, source);
        SemaphoreSlim semaphore = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(semaphore);
    }

    private readonly record struct SourceKey(string Database, string Source);

    internal readonly struct Lease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore;

        internal Lease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public ValueTask DisposeAsync()
        {
            _semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
