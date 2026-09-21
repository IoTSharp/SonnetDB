using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Contracts;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

/// <summary>
/// 保存有界的 ServerRelay 事件日志，避免续流请求重复执行工具。
/// 配置 journal 路径后，完成事件还会跨进程/重启持久化；未完成 run 在新进程中只会
/// 被封闭为 interrupted 终态，不会尝试接管 provider 或本地工具。
/// </summary>
internal sealed class CopilotServerRelayRunStore
{
    private const int MaxActiveRuns = 64;
    private const int MaxReplayRuns = 64;
    private const int MaxTrackedRunIdentities = 2048;
    private static readonly TimeSpan DefaultActiveRunTimeToLive = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReplayTimeToLive = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TombstoneTimeToLive = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly TimeSpan _activeRunTimeToLive;
    private readonly string? _journalPath;
    private readonly string? _journalLockPath;
    private readonly Dictionary<CopilotServerRelayRunKey, CopilotServerRelayRun> _activeRuns = [];
    private readonly Dictionary<CopilotServerRelayRunKey, CopilotServerRelayRun> _replayRuns = [];
    private readonly Dictionary<CopilotServerRelayRunKey, DateTimeOffset> _tombstones = [];

    public CopilotServerRelayRunStore(
        TimeSpan? activeRunTimeToLive = null,
        string? journalPath = null)
    {
        _activeRunTimeToLive = activeRunTimeToLive ?? DefaultActiveRunTimeToLive;
        if (_activeRunTimeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeRunTimeToLive),
                _activeRunTimeToLive,
                "ServerRelay active run TTL 必须大于零。");
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
            var now = DateTimeOffset.UtcNow;
            LoadJournalLocked(now);
            PruneExpiredEntries(now);
            var key = new CopilotServerRelayRunKey(binding.Owner, runId);

            if (_activeRuns.TryGetValue(key, out var active))
                return AttachExisting(active, cursor, binding);
            if (_replayRuns.TryGetValue(key, out var completed))
                return AttachExisting(completed, cursor, binding);
            if (_tombstones.ContainsKey(key))
                return new CopilotServerRelayAttachResult(CopilotServerRelayAttachStatus.Expired, null, 0);
            if (!string.IsNullOrWhiteSpace(cursor))
                return new CopilotServerRelayAttachResult(CopilotServerRelayAttachStatus.Unknown, null, 0);
            if (_activeRuns.Count >= MaxActiveRuns || TrackedIdentityCount >= MaxTrackedRunIdentities)
                return new CopilotServerRelayAttachResult(CopilotServerRelayAttachStatus.CapacityExceeded, null, 0);

            var run = new CopilotServerRelayRun(
                runId,
                binding,
                now + _activeRunTimeToLive,
                OnRunCompleted,
                OnRunChanged);
            _activeRuns.Add(key, run);
            PersistJournalLocked(now);
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
            var now = DateTimeOffset.UtcNow;
            var key = new CopilotServerRelayRunKey(run.Binding.Owner, run.RunId);
            if (_activeRuns.TryGetValue(key, out var active) && ReferenceEquals(active, run))
                _activeRuns.Remove(key);

            run.SetReplayExpiresAt(now + ReplayTimeToLive);
            _replayRuns[key] = run;
            PruneExpiredEntries(now);
            TrimReplayRuns(now);
            PersistJournalLocked(now);
        }
    }

    private void OnRunChanged(CopilotServerRelayRun run)
    {
        try
        {
            lock (_gate)
                PersistJournalLocked(DateTimeOffset.UtcNow);
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
            document = JsonSerializer.Deserialize(
                stream,
                CopilotServerRelayJournalJsonContext.Default.CopilotServerRelayJournalDocument);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            System.Diagnostics.Trace.TraceError(
                "ServerRelay durable journal could not be loaded: {0}",
                exception);
            return;
        }

        if (document is null)
            return;

        foreach (var tombstone in document.Tombstones ?? [])
        {
            if (tombstone is null ||
                string.IsNullOrWhiteSpace(tombstone.Owner) ||
                string.IsNullOrWhiteSpace(tombstone.RunId) ||
                tombstone.ExpiresAtUtc <= now)
                continue;

            _tombstones[new CopilotServerRelayRunKey(tombstone.Owner, tombstone.RunId)] = tombstone.ExpiresAtUtc;
        }

        foreach (var persisted in document.Runs ?? [])
        {
            if (persisted is null || persisted.Binding is null || persisted.Events is null ||
                string.IsNullOrWhiteSpace(persisted.RunId) ||
                string.IsNullOrWhiteSpace(persisted.Binding.Owner) ||
                string.IsNullOrWhiteSpace(persisted.Binding.DatabaseName) ||
                string.IsNullOrWhiteSpace(persisted.Binding.RequestFingerprint))
                continue;

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
                // A process cannot safely continue a provider call after restart. The
                // persisted event tail is replayable, while an unfinished run is sealed
                // as interrupted instead of invoking the provider a second time.
                var run = persisted.Completed
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
                _replayRuns.Add(key, run);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    "ServerRelay durable journal run {0} was rejected: {1}",
                    persisted.RunId,
                    exception);
                AddTombstone(key, now + TombstoneTimeToLive);
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

        var document = new CopilotServerRelayJournalDocument(
            persisted.Values.ToArray(),
            tombstones.Select(static item => new CopilotServerRelayJournalTombstone(
                item.Key.Owner,
                item.Key.RunId,
                item.Value)).ToArray());
        var temporaryPath = _journalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(
                stream,
                document,
                CopilotServerRelayJournalJsonContext.Default.CopilotServerRelayJournalDocument);
            stream.Flush(true);
        }

        try
        {
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
            var document = JsonSerializer.Deserialize(
                stream,
                CopilotServerRelayJournalJsonContext.Default.CopilotServerRelayJournalDocument);
            if (document is null)
                return;

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
                if (run is null || run.Binding is null || run.Events is null ||
                    string.IsNullOrWhiteSpace(run.RunId) ||
                    string.IsNullOrWhiteSpace(run.Binding.Owner) ||
                    string.IsNullOrWhiteSpace(run.Binding.DatabaseName) ||
                    string.IsNullOrWhiteSpace(run.Binding.RequestFingerprint))
                    continue;
                var expiry = run.ReplayExpiresAtUtc;
                var key = new CopilotServerRelayRunKey(run.Binding.Owner, run.RunId);
                if ((expiry is null || expiry > now) && !tombstones.ContainsKey(key))
                    target[key] = run;
            }

            while (target.Count > MaxActiveRuns + MaxReplayRuns)
            {
                var oldest = target.MinBy(static pair =>
                    pair.Value.ReplayExpiresAtUtc ?? pair.Value.ActiveExpiresAtUtc);
                target.Remove(oldest.Key);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "ServerRelay durable journal merge failed: {0}",
                exception);
        }
    }

    private FileStream AcquireJournalLock()
    {
        if (_journalLockPath is null)
            throw new InvalidOperationException("ServerRelay durable journal path is not configured.");

        IOException? last = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                return new FileStream(
                    _journalLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.DeleteOnClose);
            }
            catch (IOException exception)
            {
                last = exception;
                Thread.Sleep(25);
            }
        }

        throw new IOException("ServerRelay durable journal lock could not be acquired.", last);
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
            AddTombstone(oldest.Key, now + TombstoneTimeToLive);
        }
    }

    private void AddTombstone(CopilotServerRelayRunKey key, DateTimeOffset expiresAtUtc)
    {
        _tombstones[key] = expiresAtUtc;
    }

    private int TrackedIdentityCount => _activeRuns.Count + _replayRuns.Count + _tombstones.Count;
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
internal sealed class CopilotServerRelayRun
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
    private readonly Timer _deadlineTimer;
    private TaskCompletionSource _changed = CreateSignal();
    private string? _activeToolCallId;
    private int _journalBytes;
    private long _nextToolCallId;
    private bool _completed;
    private bool _doneSeen;
    private bool _outcomeSeen;
    private DateTimeOffset? _replayExpiresAtUtc;

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

    public CancellationToken DeadlineToken => _deadlineCancellation.Token;

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
                if (_completed || _doneSeen)
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
                if (_completed)
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
        var nextIndex = checked((int)afterSequence);
        while (true)
        {
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
                await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
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
