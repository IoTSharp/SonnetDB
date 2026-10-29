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
    private const int MaxSweepRollbacks = 8;
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
    private long _lastLimitDiagnostic;

    /// <summary>当前活动事务数，终态回执不占用该配额。</summary>
    internal int ActiveCount => Volatile.Read(ref _activeCount);

    /// <summary>当前保留的活动及终态会话总数。</summary>
    internal int RetainedCount => Volatile.Read(ref _retainedCount);

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
            ReportLimit("active");
            return false;
        }
        if (Interlocked.Increment(ref _retainedCount) > MaxRetainedSessions)
        {
            Interlocked.Decrement(ref _activeCount);
            Interlocked.Decrement(ref _retainedCount);
            ReportLimit("retained");
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

    /// <summary>有界扫描全部保留槽位；仅对需要回滚的活动事务限流，已完成终态不能积压到容量耗尽。</summary>
    internal void Sweep()
    {
        if (Interlocked.Exchange(ref _sweepRunning, 1) != 0)
            return;
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int inspected = 0;
            int rollbacks = 0;
            int expired = 0;
            foreach (var session in _sessions.Values)
            {
                if (inspected++ >= MaxRetainedSessions
                    || System.Diagnostics.Stopwatch.GetElapsedTime(started) >= SweepBudget)
                    break;
                if (session.ExpiresAt > DateTimeOffset.UtcNow || !session.Gate.Wait(0))
                    continue;
                try
                {
                    if (session.ExpiresAt > DateTimeOffset.UtcNow)
                        continue;
                    // 完成事务的回执移除无需存储 I/O；昂贵的回滚仍保持原八项及两秒预算。
                    if (!session.Transaction.IsCompleted)
                    {
                        if (rollbacks >= MaxSweepRollbacks)
                            continue;
                        rollbacks++;
                    }
                    TryExpire(session);
                    expired++;
                }
                finally
                {
                    session.Gate.Release();
                }
            }
            if (expired > 0)
                _logger.LogInformation("远程事务到期清理：检查 {Inspected}，处理 {Expired}，回滚候选 {Rollbacks}，当前活动 {Active}，当前保留 {Retained}。",
                    inspected, expired, rollbacks, ActiveCount, RetainedCount);
        }
        finally
        {
            Volatile.Write(ref _sweepRunning, 0);
        }
    }

    /// <summary>达到配额时每十五秒最多记录一次明确原因，避免把终态回执积压误诊为活动事务泄漏。</summary>
    private void ReportLimit(string kind)
    {
        long now = Environment.TickCount64;
        long previous = Volatile.Read(ref _lastLimitDiagnostic);
        if (now - previous < 15_000 || Interlocked.CompareExchange(ref _lastLimitDiagnostic, now, previous) != previous)
            return;
        _logger.LogWarning("远程事务准入拒绝：配额 {Kind}，当前活动 {Active}/{ActiveLimit}，当前保留 {Retained}/{RetainedLimit}。",
            kind, ActiveCount, MaxSessions, RetainedCount, MaxRetainedSessions);
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
