using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Contracts;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

/// <summary>
/// 保存有界的 ServerRelay 事件日志，避免续流请求重复执行工具。
/// 配置共享 journal 后，独占运行租约防止重复执行，其他实例可以只读跟随活跃事件。
/// 租约所有者退出后封闭 interrupted 终态，不重新执行 provider 或本地工具。
/// </summary>
internal sealed class CopilotServerRelayRunStore : IDisposable
{
    private const int MaxActiveRuns = 64;
    private const int MaxReplayRuns = 64;
    private const int MaxTrackedRunIdentities = 2048;
    private const long MaxJournalFileBytes = (MaxActiveRuns + MaxReplayRuns) * 4L * 1024 * 1024 + 4 * 1024 * 1024;
    private static readonly TimeSpan DefaultActiveRunTimeToLive = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReplayTimeToLive = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TombstoneTimeToLive = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly TimeSpan _activeRunTimeToLive;
    private readonly string? _journalPath;
    private readonly string? _journalLockPath;
    private readonly Dictionary<CopilotServerRelayRunKey, CopilotServerRelayRun> _activeRuns = [];
    private readonly Dictionary<CopilotServerRelayRunKey, CopilotServerRelayRun> _replayRuns = [];
    private readonly Dictionary<CopilotServerRelayRunKey, CopilotServerRelayRun> _followers = [];
    private readonly Dictionary<CopilotServerRelayRunKey, FileStream> _leases = [];
    private readonly Dictionary<CopilotServerRelayRunKey, DateTimeOffset> _tombstones = [];
    private FileStream? _journalTransaction;
    private int _journalTransactionDepth;
    private bool _disposing;
    private bool _disposed;

    public CopilotServerRelayRunStore(
        TimeSpan? activeRunTimeToLive = null,
        string? journalPath = null)
    {
        _activeRunTimeToLive = activeRunTimeToLive ?? DefaultActiveRunTimeToLive;
        if (_activeRunTimeToLive <= TimeSpan.Zero || _activeRunTimeToLive > DefaultActiveRunTimeToLive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeRunTimeToLive),
                _activeRunTimeToLive,
                "ServerRelay active run TTL 必须大于零且不超过 10 分钟。");
        }

        if (!string.IsNullOrWhiteSpace(journalPath))
        {
            _journalPath = Path.GetFullPath(journalPath);
            _journalLockPath = _journalPath + ".lock";
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            lock (_gate)
                LoadJournalLocked(DateTimeOffset.UtcNow);
        }
    }

    public CopilotServerRelayAttachResult Attach(
        string runId,
        string? cursor,
        CopilotServerRelayRunBinding binding)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentNullException.ThrowIfNull(binding);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _disposing, this);
            using var transaction = AcquireJournalLock();
            var now = DateTimeOffset.UtcNow;
            LoadJournalLocked(now);
            PruneExpiredEntries(now);
            var key = new CopilotServerRelayRunKey(binding.Owner, runId);

            if (_activeRuns.TryGetValue(key, out var active))
                return AttachExisting(active, cursor, binding);
            if (_replayRuns.TryGetValue(key, out var completed))
                return AttachExisting(completed, cursor, binding);
            if (_followers.TryGetValue(key, out var follower))
                return AttachExisting(follower, cursor, binding);
            if (_tombstones.ContainsKey(key))
                return new CopilotServerRelayAttachResult(CopilotServerRelayAttachStatus.Expired, null, 0);
            if (!string.IsNullOrWhiteSpace(cursor))
                return new CopilotServerRelayAttachResult(CopilotServerRelayAttachStatus.Unknown, null, 0);
            if (_activeRuns.Count + _followers.Count >= MaxActiveRuns || TrackedIdentityCount >= MaxTrackedRunIdentities)
                return new CopilotServerRelayAttachResult(CopilotServerRelayAttachStatus.CapacityExceeded, null, 0);

            var lease = TryAcquireRunLease(key);
            if (_journalPath is not null && lease is null)
                throw new IOException("ServerRelay run 的所有权租约不可用，拒绝重复执行。");

            var run = new CopilotServerRelayRun(
                runId,
                binding,
                now + _activeRunTimeToLive,
                OnRunCompleted,
                OnRunChanged);
            _activeRuns.Add(key, run);
            if (lease is not null)
                _leases.Add(key, lease);
            try
            {
                PersistJournalLocked(now);
            }
            catch
            {
                _activeRuns.Remove(key);
                _leases.Remove(key);
                lease?.Dispose();
                run.Dispose();
                throw;
            }
            return new CopilotServerRelayAttachResult(CopilotServerRelayAttachStatus.Created, run, 0);
        }
    }

    private static CopilotServerRelayAttachResult AttachExisting(
        CopilotServerRelayRun run,
        string? cursor,
        CopilotServerRelayRunBinding binding)
    {
        if (run.Binding != binding)
            return new CopilotServerRelayAttachResult(CopilotServerRelayAttachStatus.Conflict, null, 0);
        if (!run.TryResolveCursor(cursor, out var afterSequence))
            return new CopilotServerRelayAttachResult(CopilotServerRelayAttachStatus.CursorInvalid, null, 0);

        return new CopilotServerRelayAttachResult(
            CopilotServerRelayAttachStatus.Attached,
            run,
            afterSequence);
    }

    private void OnRunCompleted(CopilotServerRelayRun run)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var now = DateTimeOffset.UtcNow;
            var key = new CopilotServerRelayRunKey(run.Binding.Owner, run.RunId);
            if (_activeRuns.TryGetValue(key, out var active) && ReferenceEquals(active, run))
                _activeRuns.Remove(key);

            run.SetReplayExpiresAt(now + ReplayTimeToLive);
            _replayRuns[key] = run;
            try
            {
                // Complete 已封闭本地 run；即使共享日志锁超时，也不能把已完成的 run
                // 永久留在 active 槽位或持有 lease。持久化失败仍向调用方的故障日志传播。
                using var transaction = AcquireJournalLock();
                PruneExpiredEntries(now);
                TrimReplayRuns(now);
                PersistJournalLocked(now);
            }
            catch
            {
                // 未成功持久化的终态不能在后续无关写入中覆盖其他实例恢复的终态。
                // 再次 Attach 时必须从 journal 重建，并由已释放的 lease 判定 owner-loss。
                _replayRuns.Remove(key);
                run.Dispose();
                throw;
            }
            finally
            {
                if (_leases.Remove(key, out var lease))
                    lease.Dispose();
            }
        }
    }

    private void OnRunChanged(CopilotServerRelayRun run)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || !_activeRuns.TryGetValue(new(run.Binding.Owner, run.RunId), out var active) ||
                    !ReferenceEquals(active, run))
                    return;
                PersistJournalLocked(DateTimeOffset.UtcNow);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "ServerRelay durable journal update failed for {0}: {1}",
                run.RunId,
                exception);
        }
    }

    private void LoadJournalLocked(DateTimeOffset now)
    {
        if (_journalPath is null || !File.Exists(_journalPath))
            return;

        using var journalLock = AcquireJournalLock();
        CopilotServerRelayJournalDocument? document;
        try
        {
            using var stream = new FileStream(
                _journalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxJournalFileBytes)
                throw new InvalidDataException("ServerRelay journal exceeds the bounded file size.");
            document = JsonSerializer.Deserialize(
                stream,
                CopilotServerRelayJournalJsonContext.Default.CopilotServerRelayJournalDocument);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new InvalidDataException("ServerRelay durable journal could not be loaded; refusing a new owner.", exception);
        }

        ValidateJournalBounds(document);

        foreach (var tombstone in document.Tombstones ?? [])
        {
            if (tombstone is null ||
                string.IsNullOrWhiteSpace(tombstone.Owner) ||
                string.IsNullOrWhiteSpace(tombstone.RunId) ||
                tombstone.ExpiresAtUtc <= now)
                continue;

            _tombstones[new CopilotServerRelayRunKey(tombstone.Owner, tombstone.RunId)] = tombstone.ExpiresAtUtc;
            if (_followers.Remove(new(tombstone.Owner, tombstone.RunId), out var expiredFollower))
                expiredFollower.Dispose();
            if (_replayRuns.Remove(new(tombstone.Owner, tombstone.RunId), out var expiredReplay))
                expiredReplay.Dispose();
        }

        foreach (var persisted in document.Runs ?? [])
        {
            var key = new CopilotServerRelayRunKey(persisted.Binding.Owner, persisted.RunId);
            if (_activeRuns.ContainsKey(key) || _replayRuns.ContainsKey(key) || _tombstones.ContainsKey(key))
                continue;

            var replayExpiresAt = persisted.ReplayExpiresAtUtc ?? now + ReplayTimeToLive;
            if (replayExpiresAt <= now)
            {
                AddTombstone(key, now + TombstoneTimeToLive);
                continue;
            }

            try
            {
                // An open lease identifies a live owner. Followers never overwrite its
                // snapshot and never start another provider/tool invocation.
                using var abandonedLease = persisted.Completed ? null : TryAcquireRunLease(key);
                if (!persisted.Completed && abandonedLease is null)
                {
                    if (_followers.TryGetValue(key, out var existingFollower))
                        existingFollower.UpdateReplica(persisted);
                    else
                        _followers.Add(key, CopilotServerRelayRun.RestoreFollower(persisted, RefreshFollower));
                    continue;
                }

                var run = persisted.Completed || persisted.Events.LastOrDefault()?.Type == "done"
                    ? CopilotServerRelayRun.RestoreCompleted(
                        persisted.RunId,
                        persisted.Binding,
                        persisted.ActiveExpiresAtUtc,
                        persisted.Events)
                    : CopilotServerRelayRun.RestoreInterrupted(
                        persisted.RunId,
                        persisted.Binding,
                        persisted.ActiveExpiresAtUtc,
                        persisted.Events,
                        "ServerRelay 所在进程已重启；运行已中断，仅允许重放已记录事件。");
                run.SetReplayExpiresAt(replayExpiresAt);
                if (_followers.Remove(key, out var follower))
                {
                    follower.UpdateReplica(run.CreateJournalSnapshot());
                    run.Dispose();
                    run = follower;
                }
                _replayRuns.Add(key, run);
                if (!persisted.Completed)
                    PersistJournalLocked(now);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    "ServerRelay durable journal run {0} was rejected: {1}",
                    persisted.RunId,
                    exception);
                AddTombstone(key, now + TombstoneTimeToLive);
                if (_followers.Remove(key, out var rejectedFollower))
                    rejectedFollower.Dispose();
            }
        }

        TrimReplayRuns(now);
    }

    private void PersistJournalLocked(DateTimeOffset now)
    {
        if (_journalPath is null)
            return;

        using var journalLock = AcquireJournalLock();
        var persisted = new Dictionary<CopilotServerRelayRunKey, CopilotServerRelayJournalRun>();
        var tombstones = new Dictionary<CopilotServerRelayRunKey, DateTimeOffset>();
        LoadExternalJournalInto(persisted, tombstones, now);
        foreach (var tombstone in _tombstones)
        {
            if (tombstone.Value > now)
                tombstones[tombstone.Key] = tombstone.Value;
        }
        foreach (var run in _activeRuns.Values.Concat(_replayRuns.Values))
        {
            var snapshot = run.CreateJournalSnapshot();
            if (snapshot.ReplayExpiresAtUtc is DateTimeOffset replayExpiry && replayExpiry <= now)
                continue;
            var key = new CopilotServerRelayRunKey(snapshot.Binding.Owner, snapshot.RunId);
            if (!tombstones.ContainsKey(key))
                persisted[key] = snapshot;
        }
        foreach (var key in tombstones.Keys)
            persisted.Remove(key);

        var document = new CopilotServerRelayJournalDocument(
            persisted.Values.ToArray(),
            tombstones.Select(static item => new CopilotServerRelayJournalTombstone(
                item.Key.Owner,
                item.Key.RunId,
                item.Value)).ToArray());
        ValidateJournalBounds(document);
        var temporaryPath = _journalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(
                    stream,
                    document,
                    CopilotServerRelayJournalJsonContext.Default.CopilotServerRelayJournalDocument);
                stream.Flush(true);
            }
            File.Move(temporaryPath, _journalPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // The replacement already succeeded; a stale temp file is harmless
                // and will be removed by the next writer.
            }
        }
    }

    private void LoadExternalJournalInto(
        Dictionary<CopilotServerRelayRunKey, CopilotServerRelayJournalRun> target,
        Dictionary<CopilotServerRelayRunKey, DateTimeOffset> tombstones,
        DateTimeOffset now)
    {
        if (_journalPath is null || !File.Exists(_journalPath))
            return;

        try
        {
            using var stream = new FileStream(
                _journalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxJournalFileBytes)
                throw new InvalidDataException("ServerRelay journal exceeds the bounded file size.");
            var document = JsonSerializer.Deserialize(
                stream,
                CopilotServerRelayJournalJsonContext.Default.CopilotServerRelayJournalDocument);
            ValidateJournalBounds(document);

            foreach (var tombstone in document.Tombstones ?? [])
            {
                if (tombstone is null ||
                    string.IsNullOrWhiteSpace(tombstone.Owner) ||
                    string.IsNullOrWhiteSpace(tombstone.RunId))
                    continue;
                var key = new CopilotServerRelayRunKey(tombstone.Owner, tombstone.RunId);
                if (tombstone.ExpiresAtUtc > now)
                    tombstones[key] = tombstone.ExpiresAtUtc;
            }

            foreach (var run in document.Runs ?? [])
            {
                var expiry = run.ReplayExpiresAtUtc;
                var key = new CopilotServerRelayRunKey(run.Binding.Owner, run.RunId);
                if ((expiry is null || expiry > now) && !tombstones.ContainsKey(key))
                    target[key] = run;
            }

        }
        catch (Exception exception)
        {
            throw new InvalidDataException("ServerRelay durable journal merge failed; refusing to overwrite evidence.", exception);
        }
    }

    private static void ValidateJournalBounds([System.Diagnostics.CodeAnalysis.NotNull] CopilotServerRelayJournalDocument? document)
    {
        if (document is null || document.Runs is null ||
            document.Runs.Length > MaxActiveRuns + MaxReplayRuns ||
            (document.Tombstones?.Length ?? 0) > MaxTrackedRunIdentities)
            throw new InvalidDataException("ServerRelay journal exceeds the bounded identity count.");
        var identities = new HashSet<CopilotServerRelayRunKey>();
        var started = Stopwatch.GetTimestamp();
        foreach (var run in document.Runs)
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(3) ||
                run is null || run.Binding is null || run.Events is null || run.Events.Length > 256 ||
                string.IsNullOrWhiteSpace(run.RunId) || string.IsNullOrWhiteSpace(run.Binding.Owner) ||
                // 空字符串代表未选择业务库的控制面对话；非空纯空白不是合法数据库身份。
                run.Binding.DatabaseName is null ||
                (run.Binding.DatabaseName.Length > 0 && string.IsNullOrWhiteSpace(run.Binding.DatabaseName)) ||
                string.IsNullOrWhiteSpace(run.Binding.RequestFingerprint) ||
                !identities.Add(new(run.Binding.Owner, run.RunId)))
                throw new InvalidDataException("ServerRelay journal contains an invalid or duplicate run identity.");
        }
        foreach (var tombstone in document.Tombstones ?? [])
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(3) ||
                tombstone is null || string.IsNullOrWhiteSpace(tombstone.Owner) ||
                string.IsNullOrWhiteSpace(tombstone.RunId) ||
                !identities.Add(new(tombstone.Owner, tombstone.RunId)))
                throw new InvalidDataException("ServerRelay journal contains an invalid or duplicate tombstone identity.");
        }
    }

    private IDisposable? AcquireJournalLock()
    {
        if (_journalLockPath is null)
            return null;
        if (_journalTransaction is not null)
        {
            _journalTransactionDepth++;
            return new JournalTransaction(this);
        }

        IOException? last = null;
        var started = Stopwatch.GetTimestamp();
        for (var attempt = 0; attempt < 100 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3); attempt++)
        {
            try
            {
                _journalTransaction = new FileStream(
                    _journalLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    // 保留唯一 journal 锁文件；Unix 删除路径会让延迟 flock 的进程
                    // 锁住旧 inode，而后来的事务同时锁住新 inode。
                    options: FileOptions.None);
                _journalTransactionDepth = 1;
                return new JournalTransaction(this);
            }
            catch (IOException exception)
            {
                last = exception;
                Thread.Sleep(25);
            }
        }

        throw new IOException("ServerRelay durable journal lock could not be acquired.", last);
    }

    private FileStream? TryAcquireRunLease(CopilotServerRelayRunKey key)
    {
        if (_journalPath is null)
            return null;
        if (_journalTransaction is null)
            throw new InvalidOperationException("ServerRelay run 租约必须在 journal 事务内获取。");

        // 所有 run lease 的 open/flock 均由稳定的 journal 锁串行化。即使旧 owner
        // 在 journal 外释放租约，当前获取者仍持有 journal，其他实例不能同时创建
        // 新 inode owner；保留短期删除，避免任意 runId 导致锁文件永久累积。
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Owner + "\0" + key.RunId)));
        try
        {
            return new FileStream(_journalPath + ".run-" + identity + ".lock",
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void RefreshFollower(CopilotServerRelayRun follower)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LoadJournalLocked(DateTimeOffset.UtcNow);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed || _disposing)
                return;
            _disposing = true;
            try
            {
                foreach (var run in _activeRuns.Values.ToArray())
                {
                    run.Fail("ServerRelay 所在进程已停止或重启；运行已中断，仅允许重放已记录事件。");
                    run.Complete();
                    run.Dispose();
                }
                foreach (var run in _followers.Values.Concat(_replayRuns.Values))
                    run.Dispose();
            }
            finally
            {
                foreach (var lease in _leases.Values)
                    lease.Dispose();
                _leases.Clear();
                _disposed = true;
            }
        }
    }

    private sealed class JournalTransaction(CopilotServerRelayRunStore owner) : IDisposable
    {
        public void Dispose()
        {
            if (--owner._journalTransactionDepth == 0)
            {
                owner._journalTransaction!.Dispose();
                owner._journalTransaction = null;
            }
        }
    }

    private void PruneExpiredEntries(DateTimeOffset now)
    {
        foreach (var pair in _activeRuns.ToArray())
        {
            if (now >= pair.Value.ActiveExpiresAtUtc)
                pair.Value.Expire();
        }

        foreach (var pair in _replayRuns.ToArray())
        {
            if (pair.Value.ReplayExpiresAtUtc is DateTimeOffset expiresAt && now >= expiresAt)
            {
                _replayRuns.Remove(pair.Key);
                pair.Value.Dispose();
                AddTombstone(pair.Key, now + TombstoneTimeToLive);
            }
        }

        foreach (var pair in _tombstones.ToArray())
        {
            if (now >= pair.Value)
                _tombstones.Remove(pair.Key);
        }
    }

    private void TrimReplayRuns(DateTimeOffset now)
    {
        while (_replayRuns.Count > MaxReplayRuns)
        {
            var oldest = _replayRuns.MinBy(static pair => pair.Value.ReplayExpiresAtUtc);
            _replayRuns.Remove(oldest.Key);
            oldest.Value.Dispose();
            AddTombstone(oldest.Key, now + TombstoneTimeToLive);
        }
    }

    private void AddTombstone(CopilotServerRelayRunKey key, DateTimeOffset expiresAtUtc)
    {
        _tombstones[key] = expiresAtUtc;
    }

    private int TrackedIdentityCount => _activeRuns.Count + _followers.Count + _replayRuns.Count + _tombstones.Count;
}

internal readonly record struct CopilotServerRelayRunKey(string Owner, string RunId);

internal sealed record CopilotServerRelayRunBinding(
    string Owner,
    string DatabaseName,
    string RequestFingerprint);

internal enum CopilotServerRelayAttachStatus
{
    Created,
    Attached,
    Unknown,
    Expired,
    Conflict,
    CursorInvalid,
    CapacityExceeded,
}

internal readonly record struct CopilotServerRelayAttachResult(
    CopilotServerRelayAttachStatus Status,
    CopilotServerRelayRun? Run,
    long AfterSequence);

/// <summary>
/// 单个 relay run 的有界事件日志。它只保存 source-generated JSON 合同对象，不持有 HttpContext。
/// </summary>
internal sealed class CopilotServerRelayRun : IDisposable
{
    private const int MaxEvents = 256;
    private const int MaxJournalBytes = 4 * 1024 * 1024;
    private const int TerminalReserveBytes = 16 * 1024;
    private const int MaxFailureMessageLength = 1024;

    private readonly object _gate = new();
    private readonly List<CopilotChatEvent> _events = [];
    private readonly Dictionary<string, CopilotServerRelayToolCallState> _toolCalls =
        new(StringComparer.Ordinal);
    private readonly Action<CopilotServerRelayRun> _onCompleted;
    private readonly Action<CopilotServerRelayRun>? _onChanged;
    private readonly CancellationTokenSource _deadlineCancellation = new();
    private readonly CancellationTokenSource _disposalCancellation = new();
    private readonly CancellationToken _deadlineToken;
    private readonly CancellationToken _disposalToken;
    private readonly Timer _deadlineTimer;
    private TaskCompletionSource _changed = CreateSignal();
    private string? _activeToolCallId;
    private int _journalBytes;
    private long _nextToolCallId;
    private bool _completed;
    private bool _doneSeen;
    private bool _outcomeSeen;
    private DateTimeOffset? _replayExpiresAtUtc;
    private Action<CopilotServerRelayRun>? _refreshReplica;
    private bool _disposed;

    public CopilotServerRelayRun(
        string runId,
        CopilotServerRelayRunBinding binding,
        DateTimeOffset activeExpiresAtUtc,
        Action<CopilotServerRelayRun> onCompleted,
        Action<CopilotServerRelayRun>? onChanged = null,
        bool startDeadlineTimer = true)
    {
        RunId = runId;
        Binding = binding;
        ActiveExpiresAtUtc = activeExpiresAtUtc;
        _onCompleted = onCompleted;
        _onChanged = onChanged;
        _deadlineToken = _deadlineCancellation.Token;
        _disposalToken = _disposalCancellation.Token;
        var dueTime = activeExpiresAtUtc - DateTimeOffset.UtcNow;
        _deadlineTimer = new Timer(
            static state =>
            {
                try
                {
                    ((CopilotServerRelayRun)state!).Expire();
                }
                catch (Exception exception)
                {
                    ReportFailure("ServerRelay deadline timer callback failed.", exception);
                }
            },
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        try
        {
            if (startDeadlineTimer)
            {
                _ = _deadlineTimer.Change(
                    dueTime > TimeSpan.Zero ? dueTime : TimeSpan.Zero,
                    Timeout.InfiniteTimeSpan);
            }
        }
        catch
        {
            _deadlineTimer.Dispose();
            throw;
        }
    }

    public string RunId { get; }

    public CopilotServerRelayRunBinding Binding { get; }

    public DateTimeOffset ActiveExpiresAtUtc { get; }

    public CancellationToken DeadlineToken => _deadlineToken;

    public DateTimeOffset? ReplayExpiresAtUtc
    {
        get
        {
            lock (_gate)
                return _replayExpiresAtUtc;
        }
    }

    public CopilotChatEvent Publish(CopilotChatEvent candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        CopilotChatEvent mapped;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_refreshReplica is not null)
                throw new InvalidOperationException("ServerRelay follower 只允许订阅，拒绝追加事件。");
            if (_completed || _doneSeen)
                throw new InvalidOperationException("ServerRelay run 已结束，拒绝追加终态后的事件。");
            if (_outcomeSeen && !string.Equals(candidate.Type, "done", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ServerRelay run 已产生 final/error，拒绝追加 done 之外的事件。");
            if (string.Equals(candidate.Type, "done", StringComparison.OrdinalIgnoreCase) && !_outcomeSeen)
                throw new InvalidOperationException("ServerRelay run 尚未产生 final/error，拒绝提前接受 done。");
            if (string.Equals(candidate.Type, "final", StringComparison.OrdinalIgnoreCase) &&
                _activeToolCallId is not null)
            {
                throw new InvalidOperationException(
                    "ServerRelay run 仍有未完成的 tool call，拒绝以 final 封闭成功结果。");
            }

            var toolCallTransition = ResolveToolCallTransition(candidate);
            var sequence = checked((long)_events.Count + 1);
            mapped = candidate with
            {
                RunId = RunId,
                Sequence = sequence,
                Cursor = CreateCursor(RunId, sequence),
                ToolCallId = toolCallTransition.ToolCallId,
            };

            AppendCore(mapped);
            ApplyToolCallTransition(toolCallTransition);
            if (string.Equals(mapped.Type, "final", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mapped.Type, "error", StringComparison.OrdinalIgnoreCase))
            {
                _outcomeSeen = true;
                _activeToolCallId = null;
            }
            if (string.Equals(mapped.Type, "done", StringComparison.OrdinalIgnoreCase))
                _doneSeen = true;
        }

        NotifyChanged();
        return mapped;
    }

    public void Fail(string message)
    {
        try
        {
            var normalized = string.IsNullOrWhiteSpace(message)
                ? "ServerRelay run 未返回可确认的终态。"
                : message.Trim();
            if (normalized.Length > MaxFailureMessageLength)
                normalized = normalized[..MaxFailureMessageLength];

            var notify = false;
            lock (_gate)
            {
                if (_disposed || _completed || _doneSeen)
                    return;

                if (!_outcomeSeen)
                {
                    AppendTerminalCore(new CopilotChatEvent("error", Message: normalized));
                    _outcomeSeen = true;
                }
                AppendTerminalCore(new CopilotChatEvent("done", Message: "completed"));
                _doneSeen = true;

                notify = true;
            }

            if (notify)
                NotifyChanged();
        }
        catch (Exception exception)
        {
            ReportFailure("ServerRelay run failed while sealing an error outcome.", exception);
        }
    }

    public void Complete()
    {
        try
        {
            var notify = false;
            lock (_gate)
            {
                if (_disposed || _completed)
                    return;
                if (!_doneSeen)
                {
                    if (!_outcomeSeen)
                    {
                        AppendTerminalCore(new CopilotChatEvent(
                            "error",
                            Message: "ServerRelay run 在 done 事件前结束，已拒绝不完整结果。"));
                        _outcomeSeen = true;
                    }
                    AppendTerminalCore(new CopilotChatEvent("done", Message: "completed"));
                    _doneSeen = true;
                }

                _completed = true;
                PulseChanged();
                notify = true;
            }

            if (notify)
            {
                _deadlineTimer.Dispose();
                _onCompleted(this);
            }
        }
        catch (Exception exception)
        {
            ReportFailure("ServerRelay run failed while completing.", exception);
        }
    }

    public void Expire()
    {
        try
        {
            if (!_deadlineCancellation.IsCancellationRequested)
                _deadlineCancellation.Cancel();
        }
        catch (Exception exception)
        {
            ReportFailure("ServerRelay deadline cancellation callback failed.", exception);
        }
        finally
        {
            Fail("ServerRelay run 已超过绝对 TTL，运行已停止并封闭为错误终态。");
            Complete();
        }
    }

    public void SetReplayExpiresAt(DateTimeOffset expiresAtUtc)
    {
        lock (_gate)
            _replayExpiresAtUtc = expiresAtUtc;
    }

    internal CopilotServerRelayJournalRun CreateJournalSnapshot()
    {
        lock (_gate)
        {
            return new CopilotServerRelayJournalRun(
                RunId,
                Binding,
                ActiveExpiresAtUtc,
                _replayExpiresAtUtc,
                _completed,
                _events.ToArray());
        }
    }

    internal static CopilotServerRelayRun RestoreCompleted(
        string runId,
        CopilotServerRelayRunBinding binding,
        DateTimeOffset activeExpiresAtUtc,
        IReadOnlyList<CopilotChatEvent> events)
    {
        var run = new CopilotServerRelayRun(
            runId,
            binding,
            activeExpiresAtUtc,
            static _ => { },
            startDeadlineTimer: false);
        run.RestoreEvents(events, completed: true);
        return run;
    }

    internal static CopilotServerRelayRun RestoreInterrupted(
        string runId,
        CopilotServerRelayRunBinding binding,
        DateTimeOffset activeExpiresAtUtc,
        IReadOnlyList<CopilotChatEvent> events,
        string message)
    {
        var run = new CopilotServerRelayRun(
            runId,
            binding,
            activeExpiresAtUtc,
            static _ => { },
            startDeadlineTimer: false);
        run.RestoreEvents(events, completed: false);
        run.Fail(message);
        run.Complete();
        return run;
    }

    internal static CopilotServerRelayRun RestoreFollower(
        CopilotServerRelayJournalRun snapshot,
        Action<CopilotServerRelayRun> refresh)
    {
        var run = new CopilotServerRelayRun(snapshot.RunId, snapshot.Binding,
            snapshot.ActiveExpiresAtUtc, static _ => { }, startDeadlineTimer: false)
        {
            _refreshReplica = refresh,
        };
        run.UpdateReplica(snapshot);
        return run;
    }

    internal void UpdateReplica(CopilotServerRelayJournalRun snapshot)
    {
        if (snapshot.RunId != RunId || snapshot.Binding != Binding || snapshot.ActiveExpiresAtUtc != ActiveExpiresAtUtc)
            throw new InvalidDataException("ServerRelay follower 的持久身份发生变化。");
        lock (_gate)
        {
            if (snapshot.Events.Length < _events.Count)
                throw new InvalidDataException("ServerRelay follower 拒绝回退事件序号。");
            for (int index = 0; index < _events.Count; index++)
            {
                var typeInfo = CopilotServerRelayJournalJsonContext.Default.CopilotChatEvent;
                if (JsonSerializer.Serialize(_events[index], typeInfo) != JsonSerializer.Serialize(snapshot.Events[index], typeInfo))
                    throw new InvalidDataException("ServerRelay follower 拒绝修改已发布事件。");
            }
            if (snapshot.Events.Length == _events.Count && snapshot.Completed == _completed &&
                snapshot.ReplayExpiresAtUtc == _replayExpiresAtUtc)
                return;

            // Validate the entire bounded snapshot before changing the visible tail.
            bool complete = snapshot.Completed || snapshot.Events.LastOrDefault()?.Type == "done";
            using var validated = new CopilotServerRelayRun(RunId, Binding, ActiveExpiresAtUtc,
                static _ => { }, startDeadlineTimer: false);
            validated.RestoreEvents(snapshot.Events, complete);
            _events.Clear();
            _events.AddRange(validated._events);
            _completed = validated._completed;
            _doneSeen = validated._doneSeen;
            _outcomeSeen = validated._outcomeSeen;
            _journalBytes = validated._journalBytes;
            _replayExpiresAtUtc = snapshot.ReplayExpiresAtUtc;
            PulseChanged();
            if (_completed)
                _deadlineTimer.Dispose();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource disposalCancellation;
        CancellationTokenSource deadlineCancellation;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _deadlineTimer.Dispose();
            disposalCancellation = _disposalCancellation;
            deadlineCancellation = _deadlineCancellation;
        }
        // Cancellation callbacks can call back into the run/store; never invoke
        // them while holding the run lock.
        CancelAndDispose(disposalCancellation);
        CancelAndDispose(deadlineCancellation);
    }

    private static void CancelAndDispose(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception)
        {
            ReportFailure("ServerRelay disposal cancellation callback failed.", exception);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void RestoreEvents(IReadOnlyList<CopilotChatEvent> events, bool completed)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count > MaxEvents)
            throw new InvalidDataException("ServerRelay journal event count exceeds the bounded replay limit.");

        lock (_gate)
        {
            foreach (var item in events)
            {
                if (item is null ||
                    item.Sequence is not long sequence || sequence != _events.Count + 1 ||
                    !string.Equals(item.RunId, RunId, StringComparison.Ordinal) ||
                    !string.Equals(item.Cursor, CreateCursor(RunId, sequence), StringComparison.Ordinal))
                    throw new InvalidDataException("ServerRelay journal contains an invalid event sequence.");

                string type = item.Type.Trim();
                if (_doneSeen || (_outcomeSeen && !string.Equals(type, "done", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("ServerRelay journal contains an event after a terminal outcome.");
                if (string.Equals(type, "done", StringComparison.OrdinalIgnoreCase) && !_outcomeSeen)
                    throw new InvalidDataException("ServerRelay journal contains done before final/error.");
                if (string.Equals(type, "final", StringComparison.OrdinalIgnoreCase) && _activeToolCallId is not null)
                    throw new InvalidDataException("ServerRelay journal contains final before the active tool call completed.");

                var transition = ResolveToolCallTransition(item);
                int bytes = JsonSerializer.SerializeToUtf8Bytes(
                    item,
                    CopilotServerRelayJournalJsonContext.Default.CopilotChatEvent).Length;
                if (bytes > MaxJournalBytes - _journalBytes)
                    throw new InvalidDataException("ServerRelay journal event bytes exceed the bounded replay limit.");

                _events.Add(item);
                _journalBytes += bytes;
                ApplyToolCallTransition(transition);
                if (string.Equals(type, "final", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    _outcomeSeen = true;
                    _activeToolCallId = null;
                }
                if (string.Equals(type, "done", StringComparison.OrdinalIgnoreCase))
                    _doneSeen = true;
            }

            if (completed)
            {
                if (!_outcomeSeen || !_doneSeen)
                    throw new InvalidDataException("Completed ServerRelay journal run has no final/error followed by done.");
                _completed = true;
                _deadlineTimer.Dispose();
            }
            else if (_doneSeen ||
                _events.Count > MaxEvents - 2 ||
                _journalBytes > MaxJournalBytes - TerminalReserveBytes)
            {
                throw new InvalidDataException("Interrupted ServerRelay journal run cannot safely append its terminal events.");
            }
        }
    }

    private void NotifyChanged()
    {
        try
        {
            _onChanged?.Invoke(this);
        }
        catch (Exception exception)
        {
            ReportFailure("ServerRelay run change callback failed.", exception);
        }
    }

    public bool TryResolveCursor(string? cursor, out long afterSequence)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(cursor))
            {
                afterSequence = 0;
                return true;
            }

            var separator = cursor.LastIndexOf(':');
            if (separator <= 0 ||
                !string.Equals(cursor[..separator], RunId, StringComparison.Ordinal) ||
                !long.TryParse(cursor[(separator + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out afterSequence) ||
                afterSequence <= 0 ||
                afterSequence > _events.Count)
            {
                afterSequence = 0;
                return false;
            }

            return string.Equals(_events[checked((int)afterSequence - 1)].Cursor, cursor, StringComparison.Ordinal);
        }
    }

    public async IAsyncEnumerable<CopilotChatEvent> ReadAfterAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposalToken);
        readCancellation.CancelAfter(TimeSpan.FromMinutes(11));
        var readToken = readCancellation.Token;
        var nextIndex = checked((int)afterSequence);
        // 6000 polling intervals cover the absolute 10-minute owner TTL;
        // the remaining iterations accommodate all 256 bounded event batches.
        for (int iteration = 0; iteration < 6400; iteration++)
        {
            readToken.ThrowIfCancellationRequested();
            bool refresh;
            lock (_gate)
                refresh = !_completed;
            if (refresh)
                _refreshReplica?.Invoke(this);
            CopilotChatEvent[] available;
            Task? waitTask;
            var completed = false;
            lock (_gate)
            {
                if (nextIndex < _events.Count)
                {
                    available = _events.Skip(nextIndex).ToArray();
                    nextIndex = _events.Count;
                    waitTask = null;
                }
                else if (_completed)
                {
                    available = [];
                    waitTask = null;
                    completed = true;
                }
                else
                {
                    available = [];
                    waitTask = _changed.Task;
                }
            }

            foreach (var item in available)
                yield return item;

            if (completed)
                yield break;
            if (waitTask is not null)
            {
                if (_refreshReplica is not null)
                    await Task.Delay(TimeSpan.FromMilliseconds(100), readToken).ConfigureAwait(false);
                else
                    await waitTask.WaitAsync(readToken).ConfigureAwait(false);
            }
        }
        throw new TimeoutException("ServerRelay 订阅超过有界跟随次数，连接已停止。");
    }

    private CopilotServerRelayToolCallTransition ResolveToolCallTransition(
        CopilotChatEvent candidate)
    {
        var type = candidate.Type.Trim();
        var explicitId = string.IsNullOrWhiteSpace(candidate.ToolCallId) ? null : candidate.ToolCallId.Trim();
        if (string.Equals(type, "tool_call", StringComparison.OrdinalIgnoreCase))
        {
            var toolName = RequireToolName(candidate);
            if (_activeToolCallId is not null)
            {
                throw new InvalidOperationException(
                    "ServerRelay run 已有 active tool call，拒绝并发追加另一个 tool_call。");
            }

            var nextToolCallId = checked(_nextToolCallId + 1);
            var toolCallId = explicitId ?? $"{RunId}:tool:{nextToolCallId}";
            if (_toolCalls.TryGetValue(toolCallId, out var existing))
            {
                if (!existing.Completed)
                {
                    throw new InvalidOperationException(
                        "ServerRelay tool_call 复用了 active toolCallId，已拒绝该事件。");
                }
                if (!string.Equals(existing.ToolName, toolName, StringComparison.Ordinal) ||
                    !HaveEquivalentJson(existing.ToolArguments, candidate.ToolArguments))
                {
                    throw new InvalidOperationException(
                        "ServerRelay tool_call replay 的名称或参数与已完成调用冲突，已拒绝该事件。");
                }

                return new CopilotServerRelayToolCallTransition(
                    toolCallId,
                    toolName,
                    candidate.ToolArguments,
                    ToolResult: null,
                    CopilotServerRelayToolCallTransitionKind.Replay,
                    GeneratedSequence: null);
            }

            return new CopilotServerRelayToolCallTransition(
                toolCallId,
                toolName,
                candidate.ToolArguments,
                ToolResult: null,
                CopilotServerRelayToolCallTransitionKind.Start,
                explicitId is null ? nextToolCallId : null);
        }

        if (string.Equals(type, "tool_retry", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
        {
            var toolCallId = explicitId ?? _activeToolCallId;
            if (toolCallId is null || !_toolCalls.TryGetValue(toolCallId, out var state))
                throw new InvalidOperationException($"ServerRelay {candidate.Type} 事件引用了未知 toolCallId，已拒绝该事件。");
            if (state.Completed)
            {
                throw new InvalidOperationException(
                    $"ServerRelay {candidate.Type} 事件引用了已完成的 toolCallId，已拒绝该事件。");
            }

            var toolName = RequireToolName(candidate);
            if (!string.Equals(state.ToolName, toolName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"ServerRelay {candidate.Type} 事件的 tool name 与 tool_call 不一致，已拒绝该事件。");
            }
            if (string.Equals(type, "tool_retry", StringComparison.OrdinalIgnoreCase) &&
                state.HasResult)
            {
                throw new InvalidOperationException(
                    "ServerRelay cached tool_call replay 只接受等价 tool_result，拒绝追加 tool_retry。");
            }
            if (string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase) &&
                state.HasResult &&
                !HaveEquivalentJson(state.ToolResult, candidate.ToolResult))
            {
                throw new InvalidOperationException(
                    "ServerRelay tool_result replay 与已完成调用的结果冲突，已拒绝该事件。");
            }

            return new CopilotServerRelayToolCallTransition(
                toolCallId,
                toolName,
                ToolArguments: null,
                candidate.ToolResult,
                string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase)
                    ? CopilotServerRelayToolCallTransitionKind.Complete
                    : CopilotServerRelayToolCallTransitionKind.Retry,
                GeneratedSequence: null);
        }

        return new CopilotServerRelayToolCallTransition(
            explicitId,
            ToolName: null,
            ToolArguments: null,
            ToolResult: null,
            CopilotServerRelayToolCallTransitionKind.None,
            GeneratedSequence: null);
    }

    private void ApplyToolCallTransition(CopilotServerRelayToolCallTransition transition)
    {
        switch (transition.Kind)
        {
            case CopilotServerRelayToolCallTransitionKind.Start:
                _toolCalls.Add(
                    transition.ToolCallId!,
                    new CopilotServerRelayToolCallState(
                        transition.ToolName!,
                        transition.ToolArguments,
                        ToolResult: null,
                        HasResult: false,
                        Completed: false));
                _activeToolCallId = transition.ToolCallId;
                if (transition.GeneratedSequence is long generatedSequence)
                    _nextToolCallId = generatedSequence;
                break;
            case CopilotServerRelayToolCallTransitionKind.Replay:
                CopilotServerRelayToolCallState replay = _toolCalls[transition.ToolCallId!];
                _toolCalls[transition.ToolCallId!] = replay with { Completed = false };
                _activeToolCallId = transition.ToolCallId;
                break;
            case CopilotServerRelayToolCallTransitionKind.Complete:
                CopilotServerRelayToolCallState completed = _toolCalls[transition.ToolCallId!];
                _toolCalls[transition.ToolCallId!] = completed with
                {
                    ToolResult = completed.HasResult ? completed.ToolResult : transition.ToolResult,
                    HasResult = true,
                    Completed = true,
                };
                if (string.Equals(_activeToolCallId, transition.ToolCallId, StringComparison.Ordinal))
                    _activeToolCallId = null;
                break;
        }
    }

    private void AppendCore(CopilotChatEvent item)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(item, ServerJsonContext.Default.CopilotChatEvent).Length;
        var reserve = GetTerminalReserve(item);
        if (_events.Count > MaxEvents - reserve.EventCount - 1 ||
            bytes > MaxJournalBytes - reserve.Bytes - _journalBytes)
            throw new InvalidOperationException("ServerRelay run 事件日志超过固定容量，已停止该运行。");

        _events.Add(item);
        _journalBytes += bytes;
        PulseChanged();
    }

    private void AppendTerminalCore(CopilotChatEvent candidate)
    {
        var sequence = checked((long)_events.Count + 1);
        var mapped = candidate with
        {
            RunId = RunId,
            Sequence = sequence,
            Cursor = CreateCursor(RunId, sequence),
        };
        AppendCore(mapped);
    }

    private CopilotServerRelayTerminalReserve GetTerminalReserve(CopilotChatEvent item)
    {
        if (string.Equals(item.Type, "done", StringComparison.OrdinalIgnoreCase))
            return default;

        var doneSequence = checked(item.Sequence!.Value + 1);
        var done = new CopilotChatEvent("done", Message: "completed")
        {
            RunId = RunId,
            Sequence = doneSequence,
            Cursor = CreateCursor(RunId, doneSequence),
        };
        var doneBytes = JsonSerializer.SerializeToUtf8Bytes(
            done,
            ServerJsonContext.Default.CopilotChatEvent).Length;
        if (IsOutcome(item))
            return new CopilotServerRelayTerminalReserve(EventCount: 1, Bytes: doneBytes);

        return new CopilotServerRelayTerminalReserve(EventCount: 2, Bytes: TerminalReserveBytes);
    }

    private void PulseChanged()
    {
        var previous = _changed;
        _changed = CreateSignal();
        previous.TrySetResult();
    }

    private static bool IsOutcome(CopilotChatEvent item)
        => string.Equals(item.Type, "final", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Type, "error", StringComparison.OrdinalIgnoreCase);

    private static string RequireToolName(CopilotChatEvent candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.ToolName))
        {
            throw new InvalidOperationException(
                $"ServerRelay {candidate.Type} 事件缺少 tool name，已拒绝该事件。");
        }

        return candidate.ToolName.Trim();
    }

    private static bool HaveEquivalentJson(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
            return true;
        if (left is null || right is null)
            return false;

        try
        {
            using var leftDocument = JsonDocument.Parse(left);
            using var rightDocument = JsonDocument.Parse(right);
            return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ReportFailure(string message, Exception exception)
        => System.Diagnostics.Trace.TraceError("{0} {1}", message, exception);

    private static string CreateCursor(string runId, long sequence)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{runId}:{sequence}");

    private static TaskCompletionSource CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal readonly record struct CopilotServerRelayTerminalReserve(int EventCount, int Bytes);

internal readonly record struct CopilotServerRelayToolCallState(
    string ToolName,
    string? ToolArguments,
    string? ToolResult,
    bool HasResult,
    bool Completed);

internal readonly record struct CopilotServerRelayToolCallTransition(
    string? ToolCallId,
    string? ToolName,
    string? ToolArguments,
    string? ToolResult,
    CopilotServerRelayToolCallTransitionKind Kind,
    long? GeneratedSequence);

internal enum CopilotServerRelayToolCallTransitionKind
{
    None,
    Start,
    Replay,
    Retry,
    Complete,
}

/// <summary>
/// ServerRelay durable journal document. It stores only bounded, already
/// validated transport events; provider credentials and HTTP state never enter
/// this file.
/// </summary>
internal sealed record CopilotServerRelayJournalDocument(
    CopilotServerRelayJournalRun[] Runs,
    CopilotServerRelayJournalTombstone[]? Tombstones = null);

internal sealed record CopilotServerRelayJournalRun(
    string RunId,
    CopilotServerRelayRunBinding Binding,
    DateTimeOffset ActiveExpiresAtUtc,
    DateTimeOffset? ReplayExpiresAtUtc,
    bool Completed,
    CopilotChatEvent[] Events);

internal sealed record CopilotServerRelayJournalTombstone(
    string Owner,
    string RunId,
    DateTimeOffset ExpiresAtUtc);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CopilotServerRelayJournalDocument))]
[JsonSerializable(typeof(CopilotServerRelayJournalRun))]
[JsonSerializable(typeof(CopilotServerRelayJournalTombstone))]
[JsonSerializable(typeof(CopilotServerRelayRunBinding))]
[JsonSerializable(typeof(CopilotChatEvent))]
internal sealed partial class CopilotServerRelayJournalJsonContext : JsonSerializerContext;
