using System.Diagnostics;
using System.Runtime.CompilerServices;
using SonnetDB.Exceptions;

namespace SonnetDB.Tables;

/// <summary>
/// 数据库实例内共享的关系锁表。申请原子完成；等待期间不持有 manager/store 的同步锁。
/// 该组件不读取行，调用方必须在锁定后重新检查行身份、版本与查询谓词。
/// </summary>
internal sealed class TableRowLockManager : IDisposable
{
    private static readonly object DatabaseToken = new();
    private readonly int _maxResources;
    private readonly int _maxWaiters;
    private readonly long _maxResourceBytes;
    private readonly object _sync = new();
    private readonly Dictionary<Resource, HeldLock> _held = [];
    private readonly HashSet<Owner> _owners = [];
    private readonly List<Request> _waiters = [];
    private TaskCompletionSource<bool> _changed = NewSignal();
    private long _heldBytes;
    private long _waitingBytes;
    private bool _disposed;

    internal TableRowLockManager(int maxResources = 100_000, int maxWaiters = 1_024,
        long maxResourceBytes = 64 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResources);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWaiters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResourceBytes);
        _maxResources = maxResources;
        _maxWaiters = maxWaiters;
        _maxResourceBytes = maxResourceBytes;
    }

    // 表身份是管理器拥有的稳定对象，不是表名、当前 schema 对象或可改写的主键声明。
    // Key=null 表示表级排他（DDL/主键迁移）；普通行键必须为复制后的规范存储身份。
    internal readonly struct Resource(object table, string? key) : IEquatable<Resource>
    {
        internal object Table { get; } = table;
        internal string? Key { get; } = key;
        internal bool IsDatabase => ReferenceEquals(Table, DatabaseToken);
        // 除键字符串外，为字典、请求数组和集合项预留固定空间；这是保守预算而非 CLR 堆测量。
        internal long BudgetBytes => 128L + (Key?.Length ?? 0) * sizeof(char);

        public bool Equals(Resource other) => ReferenceEquals(Table, other.Table) && Key == other.Key;
        public override bool Equals(object? obj) => obj is Resource other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Table), Key);

        internal static Resource Row(object table, ReadOnlySpan<byte> identity)
        {
            ArgumentNullException.ThrowIfNull(table);
            if (identity.IsEmpty)
                throw new ArgumentException("行锁要求非空的存储身份。", nameof(identity));
            return new Resource(table, Convert.ToHexString(identity));
        }

        internal static Resource TableExclusive(object table)
        {
            ArgumentNullException.ThrowIfNull(table);
            return new Resource(table, null);
        }

        internal static Resource DatabaseExclusive => new(DatabaseToken, null);
    }

    internal Owner CreateOwner()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var owner = new Owner(this);
            _owners.Add(owner);
            return owner;
        }
    }

    internal Lease Acquire(Owner owner, IReadOnlyList<Resource> resources, bool noWait,
        TimeSpan timeout, CancellationToken cancellationToken = default)
        => AcquireAsync(owner, resources, noWait, timeout, cancellationToken).GetAwaiter().GetResult();

    internal async Task<Lease> AcquireAsync(Owner owner, IReadOnlyList<Resource> resources, bool noWait,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(resources);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "行锁等待必须使用正数且有限的预算。");
        if (resources.Count > _maxResources)
            throw Limit();
        Resource[] requested = resources.Distinct().ToArray();
        foreach (var resource in requested)
            ArgumentNullException.ThrowIfNull(resource.Table);
        var request = new Request(owner, requested);
        if (request.BudgetBytes > _maxResourceBytes)
            throw Limit();
        long started = Stopwatch.GetTimestamp();
        bool queued = false;
        try
        {
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!ReferenceEquals(owner.Manager, this))
                        throw new ArgumentException("锁持有者不属于本数据库。", nameof(owner));
                    ObjectDisposedException.ThrowIf(owner.Completed, owner);
                    if (!_owners.Contains(owner))
                        throw new ArgumentException("锁持有者未在本数据库登记。", nameof(owner));
                    cancellationToken.ThrowIfCancellationRequested();
                    if (queued && Stopwatch.GetElapsedTime(started) >= timeout)
                        throw TimedOut();
                    var blockers = Blockers(request);
                    if (blockers.Count == 0)
                    {
                        int added = requested.Count(resource => !_held.ContainsKey(resource));
                        long addedBytes = requested.Where(resource => !_held.ContainsKey(resource)).Sum(resource => resource.BudgetBytes);
                        long retainedBytes = _heldBytes + _waitingBytes - (request.Budgeted ? request.BudgetBytes : 0);
                        if (_held.Count + added > _maxResources || addedBytes > _maxResourceBytes - retainedBytes)
                            throw Limit();
                        if (requested.Any(resource => _held.TryGetValue(resource, out var held) && held.References == int.MaxValue))
                            throw Limit();
                        foreach (var resource in requested)
                        {
                            if (_held.TryGetValue(resource, out var held))
                                held.References = checked(held.References + 1);
                            else
                            {
                                _held.Add(resource, new HeldLock(owner));
                                _heldBytes += resource.BudgetBytes;
                            }
                        }
                        Unreserve(request);
                        return new Lease(this, owner, requested);
                    }
                    if (noWait)
                        throw new SqlRowLockException(SqlRowLockException.NotAvailable,
                            "NOWAIT 目标当前不可锁定；本次申请没有取得任何新锁。");
                    if (!queued)
                    {
                        if (_waiters.Count >= _maxWaiters
                            || request.BudgetBytes > _maxResourceBytes - _heldBytes - _waitingBytes)
                            throw Limit();
                        if (_waiters.Any(waiter => ReferenceEquals(waiter.Owner, owner)))
                            throw new InvalidOperationException("同一事务不能同时发起多个行锁等待。");
                        _waiters.Add(request);
                        _waitingBytes += request.BudgetBytes;
                        request.Budgeted = true;
                        queued = true;
                    }
                    if (ReachesOwner(blockers, owner))
                        throw new SqlRowLockException(SqlRowLockException.Deadlock,
                            "行锁等待形成依赖环；请回滚当前事务后重新开始。");
                    changed = _changed.Task;
                }
                TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                    throw TimedOut();
                try
                {
                    await changed.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw TimedOut();
                }
            }
        }
        finally
        {
            if (queued)
            {
                lock (_sync)
                {
                    _waiters.Remove(request);
                    Unreserve(request);
                    Pulse();
                }
            }
        }
    }

    private HashSet<Owner> Blockers(Request request)
    {
        var blockers = new HashSet<Owner>();
        foreach (var resource in request.Resources)
        {
            if (resource.Key is null)
            {
                foreach (var (candidate, held) in _held)
                    if (Conflicts(resource, candidate) && !ReferenceEquals(held.Owner, request.Owner))
                        blockers.Add(held.Owner);
            }
            else
            {
                if (_held.TryGetValue(resource, out var held) && !ReferenceEquals(held.Owner, request.Owner))
                    blockers.Add(held.Owner);
                if (_held.TryGetValue(Resource.TableExclusive(resource.Table), out var table)
                    && !ReferenceEquals(table.Owner, request.Owner))
                    blockers.Add(table.Owner);
                if (_held.TryGetValue(Resource.DatabaseExclusive, out var database)
                    && !ReferenceEquals(database.Owner, request.Owner))
                    blockers.Add(database.Owner);
            }
        }
        // 仅阻挡会竞争同一目标的先到请求。持锁事务可越过正在等它的维护请求，
        // 否则“DDL 等待事务释放/事务等待 DDL 队列”会制造人为依赖环。
        foreach (var waiter in _waiters)
        {
            if (ReferenceEquals(waiter, request))
                break;
            if (!ReferenceEquals(waiter.Owner, request.Owner)
                && !_held.Any(pair => ReferenceEquals(pair.Value.Owner, request.Owner)
                    && waiter.ConflictsWith(pair.Key))
                && request.Resources.Any(resource => !IsOwned(request.Owner, resource) && waiter.ConflictsWith(resource)))
                blockers.Add(waiter.Owner);
        }
        return blockers;
    }

    private bool IsOwned(Owner owner, Resource resource)
        => _held.TryGetValue(resource, out var held) && ReferenceEquals(held.Owner, owner)
            || _held.TryGetValue(Resource.DatabaseExclusive, out held) && ReferenceEquals(held.Owner, owner)
            || _held.TryGetValue(Resource.TableExclusive(resource.Table), out held) && ReferenceEquals(held.Owner, owner);

    private bool ReachesOwner(IEnumerable<Owner> starting, Owner owner)
    {
        var pending = new Stack<Owner>(starting);
        var visited = new HashSet<Owner>();
        while (pending.TryPop(out var current))
        {
            if (ReferenceEquals(current, owner))
                return true;
            if (!visited.Add(current))
                continue;
            foreach (var waiter in _waiters.Where(waiter => ReferenceEquals(waiter.Owner, current)))
                foreach (var next in Blockers(waiter))
                    pending.Push(next);
        }
        return false;
    }

    private static bool Conflicts(Resource left, Resource right)
        => (left.IsDatabase || right.IsDatabase || ReferenceEquals(left.Table, right.Table))
            && (left.Key is null || right.Key is null || left.Key == right.Key);

    private void Release(Owner owner, IReadOnlyList<Resource> resources)
    {
        lock (_sync)
        {
            foreach (var resource in resources)
            {
                if (_held.TryGetValue(resource, out var held) && ReferenceEquals(held.Owner, owner)
                    && --held.References == 0)
                {
                    _held.Remove(resource);
                    _heldBytes -= resource.BudgetBytes;
                }
            }
            Pulse();
        }
    }

    private void Complete(Owner owner)
    {
        lock (_sync)
        {
            if (owner.Completed)
                return;
            owner.Completed = true;
            _owners.Remove(owner);
            foreach (var resource in _held.Where(pair => ReferenceEquals(pair.Value.Owner, owner)).Select(pair => pair.Key).ToArray())
            {
                _held.Remove(resource);
                _heldBytes -= resource.BudgetBytes;
            }
            foreach (var request in _waiters.Where(waiter => ReferenceEquals(waiter.Owner, owner)))
                Unreserve(request);
            _waiters.RemoveAll(waiter => ReferenceEquals(waiter.Owner, owner));
            Pulse();
        }
    }

    private void Pulse()
    {
        var signal = _changed;
        _changed = NewSignal();
        signal.TrySetResult(true);
    }

    private void Unreserve(Request request)
    {
        if (!request.Budgeted)
            return;
        _waitingBytes -= request.BudgetBytes;
        request.Budgeted = false;
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SqlRowLockException TimedOut()
        => new(SqlRowLockException.WaitTimeout, "行锁等待超时；本次申请没有取得任何新锁。");

    private static SqlRowLockException Limit()
        => new(SqlRowLockException.LimitExceeded, "行锁、等待者数量或键内存预算超过保护上限。");

    internal (int Held, int Waiting, int Owners) Inspect()
    {
        lock (_sync)
            return (_held.Count, _waiters.Count, _owners.Count);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var owner in _owners)
                owner.Completed = true;
            _owners.Clear();
            _held.Clear();
            _heldBytes = 0;
            foreach (var request in _waiters)
                Unreserve(request);
            _waiters.Clear();
            Pulse();
        }
    }

    private sealed class HeldLock(Owner owner)
    {
        internal Owner Owner { get; } = owner;
        internal int References { get; set; } = 1;
    }

    private sealed class Request(Owner owner, Resource[] resources)
    {
        private readonly HashSet<Resource> _resources = [.. resources];
        internal Owner Owner { get; } = owner;
        internal Resource[] Resources { get; } = resources;
        internal long BudgetBytes { get; } = resources.Sum(resource => resource.BudgetBytes);
        internal bool Budgeted { get; set; }
        internal bool ConflictsWith(Resource resource)
            => resource.Key is null ? Resources.Any(candidate => Conflicts(resource, candidate))
                : _resources.Contains(resource) || _resources.Contains(Resource.TableExclusive(resource.Table))
                    || _resources.Contains(Resource.DatabaseExclusive);
    }

    internal sealed class Owner(TableRowLockManager manager) : IDisposable
    {
        internal TableRowLockManager Manager { get; } = manager;
        internal bool Completed { get; set; }
        public void Dispose() => Manager.Complete(this);
    }

    internal sealed class Lease(TableRowLockManager manager, Owner owner, Resource[] resources) : IDisposable
    {
        private int _completed;

        // 语句成功后把锁保留到事务结束；失败时 Dispose 只释放本次申请的引用。
        internal void Retain() => Interlocked.Exchange(ref _completed, 1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                manager.Release(owner, resources);
        }
    }
}
