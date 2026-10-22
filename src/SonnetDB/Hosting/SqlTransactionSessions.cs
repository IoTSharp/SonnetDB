using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Hosting;

/// <summary>为远程 ADO 轻事务保留有界、短期的服务端上下文。</summary>
internal sealed class SqlTransactionSessions : IDisposable
{
    private const int MaxSessions = 128;
    private const int MaxRetainedSessions = 8192;
    private const int MaxSweepEntries = 8;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AcquireWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Timer _sweeper;
    private readonly ILogger<SqlTransactionSessions> _logger;
    private int _activeCount;
    private int _retainedCount;
    private int _sweepRunning;

    public SqlTransactionSessions(ILogger<SqlTransactionSessions> logger)
    {
        _logger = logger;
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public bool TryCreate(Tsdb database, string databaseName, string authorization, out Session? session,
        SqlTransactionIsolationLevel isolationLevel = SqlTransactionIsolationLevel.ReadCommitted,
        string? connectionSessionId = null)
    {
        session = null;
        if (Interlocked.Increment(ref _activeCount) > MaxSessions)
        {
            Interlocked.Decrement(ref _activeCount);
            return false;
        }
        if (Interlocked.Increment(ref _retainedCount) > MaxRetainedSessions)
        {
            Interlocked.Decrement(ref _activeCount);
            Interlocked.Decrement(ref _retainedCount);
            return false;
        }

        var created = new Session(
            Guid.NewGuid().ToString("N"), database, databaseName, PrincipalKey(authorization),
            new SqlTransactionContext(isolationLevel), DateTimeOffset.UtcNow + Lease, connectionSessionId);
        if (!_sessions.TryAdd(created.Id, created))
        {
            Interlocked.Decrement(ref _activeCount);
            Interlocked.Decrement(ref _retainedCount);
            return false;
        }
        session = created;
        return true;
    }

    public async Task<Session?> AcquireAsync(
        string id, Tsdb database, string databaseName, string authorization, CancellationToken cancellationToken,
        string? connectionSessionId = null)
    {
        if (!_sessions.TryGetValue(id, out var session)
            || !ReferenceEquals(session.Database, database)
            || !string.Equals(session.DatabaseName, databaseName, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(
                session.PrincipalKey, PrincipalKey(authorization)))
            return null;

        if (!await session.Gate.WaitAsync(AcquireWait, cancellationToken).ConfigureAwait(false))
            throw new SqlTransactionSessionBusyException();
        if (!_sessions.TryGetValue(id, out var current) || !ReferenceEquals(current, session))
        {
            session.Gate.Release();
            return null;
        }
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            TryExpire(session);
            session.Gate.Release();
            return null;
        }
        if (session.ConnectionSessionId is null)
            session.ConnectionSessionId = connectionSessionId;
        else if (!string.Equals(session.ConnectionSessionId, connectionSessionId, StringComparison.Ordinal))
        {
            session.Gate.Release();
            return null;
        }
        if (session.Terminal is null)
            session.ExpiresAt = DateTimeOffset.UtcNow + Lease;
        return session;
    }

    public void Complete(Session session, string state)
    {
        if (session.Terminal is null)
            Interlocked.Decrement(ref _activeCount);
        session.Terminal = state;
        session.ExpiresAt = DateTimeOffset.UtcNow + TerminalRetention;
    }

    private void Sweep()
    {
        if (Interlocked.Exchange(ref _sweepRunning, 1) != 0)
            return;
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int visited = 0;
            foreach (var session in _sessions.Values)
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
                        TryExpire(session);
                }
                finally
                {
                    session.Gate.Release();
                }
            }
        }
        finally
        {
            Volatile.Write(ref _sweepRunning, 0);
        }
    }

    private void Expire(Session session)
    {
        if (!_sessions.TryRemove(new KeyValuePair<string, Session>(session.Id, session)))
            return;
        Interlocked.Decrement(ref _retainedCount);
        if (session.Terminal is null)
            Interlocked.Decrement(ref _activeCount);
        if (!session.Transaction.IsCompleted)
            SqlExecutor.ExecuteStatement(session.Database, session.DatabaseName,
                new RollbackTransactionStatement(), null, session.Transaction);
    }

    private void TryExpire(Session session)
    {
        try { Expire(session); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "远程轻事务 {SessionId} 过期回滚失败。", session.Id);
        }
    }

    internal bool TryTerminateConnectionSession(string connectionSessionId, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionSessionId);
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Session[] bound = _sessions.Values
            .Where(session => string.Equals(
                session.ConnectionSessionId, connectionSessionId, StringComparison.Ordinal))
            .Take(MaxSessions)
            .ToArray();
        foreach (Session session in bound)
        {
            TimeSpan remaining = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            bool acquired = timeout == TimeSpan.Zero
                ? session.Gate.Wait(0)
                : remaining > TimeSpan.Zero && session.Gate.Wait(remaining);
            if (!acquired)
                return false;
            try
            {
                Expire(session);
            }
            finally
            {
                session.Gate.Release();
            }
        }
        return true;
    }

    private static byte[] PrincipalKey(string authorization)
        => SHA256.HashData(Encoding.UTF8.GetBytes(authorization));

    public void Dispose()
    {
        _sweeper.Dispose();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var session in _sessions.Values.Take(MaxSessions))
        {
            TimeSpan remaining = ShutdownBudget - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                break;
            TimeSpan wait = remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5);
            if (!session.Gate.Wait(wait))
            {
                _logger.LogWarning("关闭服务时等待远程轻事务 {SessionId} 超时。", session.Id);
                continue;
            }
            try { TryExpire(session); }
            finally { session.Gate.Release(); }
        }
    }

    internal sealed class Session(
        string id, Tsdb database, string databaseName, byte[] principalKey,
        SqlTransactionContext transaction, DateTimeOffset expiresAt, string? connectionSessionId)
    {
        public string Id { get; } = id;
        public Tsdb Database { get; } = database;
        public string DatabaseName { get; } = databaseName;
        public byte[] PrincipalKey { get; } = principalKey;
        public SqlTransactionContext Transaction { get; } = transaction;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public string? Terminal { get; set; }
        public string? ConnectionSessionId { get; set; } = connectionSessionId;
    }
}

internal sealed class SqlTransactionSessionBusyException()
    : TimeoutException("等待远程轻事务会话准入超时。");
