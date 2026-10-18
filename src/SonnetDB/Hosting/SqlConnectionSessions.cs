using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Hosting;

/// <summary>保存远程逻辑连接拥有的临时关系对象，并按租约有界回收。</summary>
internal sealed class SqlConnectionSessions : IDisposable
{
    internal const string HeaderName = "X-SonnetDB-Connection-Session";
    private const int MaxSessions = 128;
    private const int MaxSweepEntries = 1;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AcquireWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TransactionDrainTimeout = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Session> _cleanup = new(StringComparer.Ordinal);
    private readonly object _lifecycleSync = new();
    private readonly ILogger<SqlConnectionSessions> _logger;
    private readonly SqlTransactionSessions _transactionSessions;
    private readonly Func<Tsdb, SqlSessionContext> _sessionFactory;
    private readonly TimeSpan _acquireWait;
    private readonly Timer _sweeper;
    private int _activeCount;
    private int _retainedCount;
    private int _disposed;
    private int _sweepRunning;

    internal int ActiveCount => Volatile.Read(ref _activeCount);
    internal int CleanupCount => _cleanup.Count;
    internal int RetainedCount => Volatile.Read(ref _retainedCount);

    public SqlConnectionSessions(
        ILogger<SqlConnectionSessions> logger,
        SqlTransactionSessions transactionSessions)
        : this(logger, transactionSessions, AcquireWait, static database => new SqlSessionContext(
            database,
            new SqlSessionOptions { CleanupTimeout = TimeSpan.FromSeconds(2) }))
    {
    }

    internal SqlConnectionSessions(
        ILogger<SqlConnectionSessions> logger,
        SqlTransactionSessions transactionSessions,
        TimeSpan acquireWait,
        Func<Tsdb, SqlSessionContext> sessionFactory)
    {
        if (acquireWait <= TimeSpan.Zero || acquireWait > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(acquireWait));
        _logger = logger;
        _transactionSessions = transactionSessions;
        _acquireWait = acquireWait;
        _sessionFactory = sessionFactory;
        _sweeper = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    public bool TryCreate(Tsdb database, string databaseName, string authorization, out Session? session)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            session = null;
            if (Volatile.Read(ref _retainedCount) >= MaxSessions)
                return false;

            var created = new Session(
                Guid.NewGuid().ToString("N"),
                database,
                databaseName,
                PrincipalKey(authorization),
                _sessionFactory(database),
                DateTimeOffset.UtcNow + Lease);
            if (!_sessions.TryAdd(created.Id, created))
            {
                created.Context.Dispose();
                return false;
            }

            Interlocked.Increment(ref _activeCount);
            Interlocked.Increment(ref _retainedCount);
            session = created;
            return true;
        }
    }

    public async Task<LeaseHandle?> AcquireAsync(
        string id,
        Tsdb database,
        string databaseName,
        string authorization,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_sessions.TryGetValue(id, out Session? session)
            || !ReferenceEquals(session.Database, database)
            || !string.Equals(session.DatabaseName, databaseName, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(session.PrincipalKey, PrincipalKey(authorization)))
        {
            return null;
        }

        if (!await session.Gate.WaitAsync(_acquireWait, cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("等待远程 SQL 连接会话准入超时。");
        if (!_sessions.TryGetValue(id, out Session? current) || !ReferenceEquals(current, session))
        {
            session.Gate.Release();
            return null;
        }
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            TryExpire(session, TimeSpan.Zero);
            session.Gate.Release();
            return null;
        }

        session.ExpiresAt = DateTimeOffset.UtcNow + Lease;
        return new LeaseHandle(session);
    }

    public void Close(LeaseHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        Session session = handle.Session;
        if (!_transactionSessions.TryTerminateConnectionSession(session.Id, TransactionDrainTimeout))
            throw new TimeoutException("等待连接会话的活动事务结束超时。");
        RemoveAndCleanup(session, reportFailure: true);
    }

    private void Sweep()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _sweepRunning, 1) != 0)
            return;
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int visited = 0;
            foreach (Session session in _sessions.Values)
            {
                if (visited >= MaxSweepEntries
                    || System.Diagnostics.Stopwatch.GetElapsedTime(started) >= SweepBudget)
                    break;
                if (session.ExpiresAt > DateTimeOffset.UtcNow || !session.Gate.Wait(0))
                    continue;
                visited++;
                try
                {
                    if (session.ExpiresAt <= DateTimeOffset.UtcNow)
                        TryExpire(session, TimeSpan.Zero);
                }
                finally
                {
                    session.Gate.Release();
                }
            }

            foreach (Session session in _cleanup.Values)
            {
                if (visited >= MaxSweepEntries
                    || System.Diagnostics.Stopwatch.GetElapsedTime(started) >= SweepBudget)
                    break;
                visited++;
                RetryCleanup(session);
            }
        }
        finally
        {
            Volatile.Write(ref _sweepRunning, 0);
        }
    }

    internal void SweepExpiredForTests() => Sweep();

    private void TryExpire(Session session, TimeSpan transactionWait)
    {
        if (!_transactionSessions.TryTerminateConnectionSession(session.Id, transactionWait))
        {
            session.ExpiresAt = DateTimeOffset.UtcNow + SweepInterval;
            return;
        }
        RemoveAndCleanup(session, reportFailure: false);
    }

    private void RemoveAndCleanup(Session session, bool reportFailure)
    {
        if (!_sessions.TryRemove(new KeyValuePair<string, Session>(session.Id, session)))
            return;
        Interlocked.Decrement(ref _activeCount);
        try
        {
            session.Context.Dispose();
            Interlocked.Decrement(ref _retainedCount);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            _cleanup[session.Id] = session;
            _logger.LogError(exception, "远程 SQL 连接会话 {SessionId} 清理失败，已进入有界重试队列。", session.Id);
            if (reportFailure)
                throw new SqlConnectionSessionCleanupException(session.Id, exception);
        }
    }

    private void RetryCleanup(Session session)
    {
        if (!session.Gate.Wait(0))
            return;
        try
        {
            if (!_cleanup.TryRemove(new KeyValuePair<string, Session>(session.Id, session)))
                return;
            try
            {
                session.Context.Dispose();
                Interlocked.Decrement(ref _retainedCount);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
            {
                _cleanup[session.Id] = session;
                _logger.LogError(exception, "远程 SQL 连接会话 {SessionId} 重试清理失败。", session.Id);
            }
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static byte[] PrincipalKey(string authorization)
        => SHA256.HashData(Encoding.UTF8.GetBytes(authorization));

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
        }
        _sweeper.Dispose();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (Session session in _sessions.Values.Take(MaxSessions))
        {
            TimeSpan remaining = ShutdownBudget - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                break;
            TimeSpan wait = remaining < ShutdownWait ? remaining : ShutdownWait;
            if (!session.Gate.Wait(wait))
            {
                _logger.LogWarning("关闭服务时等待 SQL 连接会话 {SessionId} 超时。", session.Id);
                continue;
            }
            try
            {
                remaining = ShutdownBudget - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                    TryExpire(session, remaining);
            }
            finally
            {
                session.Gate.Release();
            }
        }
        foreach (Session session in _cleanup.Values.Take(MaxSessions))
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= ShutdownBudget)
                break;
            RetryCleanup(session);
        }
        if (!_sessions.IsEmpty || !_cleanup.IsEmpty)
        {
            _logger.LogWarning(
                "关闭服务的 SQL 连接会话清理预算耗尽；active={ActiveCount}, cleanup={CleanupCount}。",
                _sessions.Count, _cleanup.Count);
        }
    }

    internal sealed class LeaseHandle : IDisposable
    {
        private Session? _session;

        internal LeaseHandle(Session session) => _session = session;

        internal Session Session => _session ?? throw new ObjectDisposedException(nameof(LeaseHandle));

        public SqlSessionContext Context => Session.Context;

        public string Id => Session.Id;

        public void Dispose()
        {
            Session? session = Interlocked.Exchange(ref _session, null);
            session?.Gate.Release();
        }
    }

    internal sealed class Session(
        string id,
        Tsdb database,
        string databaseName,
        byte[] principalKey,
        SqlSessionContext context,
        DateTimeOffset expiresAt)
    {
        public string Id { get; } = id;
        public Tsdb Database { get; } = database;
        public string DatabaseName { get; } = databaseName;
        public byte[] PrincipalKey { get; } = principalKey;
        public SqlSessionContext Context { get; } = context;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    }
}

internal sealed class SqlConnectionSessionCleanupException(string sessionId, Exception innerException)
    : Exception($"远程 SQL 连接会话 {sessionId} 已关闭，但临时目录清理正在有界重试。", innerException);
