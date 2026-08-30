using System.Runtime.CompilerServices;
using System.Text.Json;
using SonnetDB.Contracts;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

/// <summary>
/// 保存单个 Server 进程内的短期 ServerRelay 事件日志，避免续流请求重复执行工具。
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
    private readonly Dictionary<CopilotServerRelayRunKey, CopilotServerRelayRun> _activeRuns = [];
    private readonly Dictionary<CopilotServerRelayRunKey, CopilotServerRelayRun> _replayRuns = [];
    private readonly Dictionary<CopilotServerRelayRunKey, DateTimeOffset> _tombstones = [];

    public CopilotServerRelayRunStore(TimeSpan? activeRunTimeToLive = null)
    {
        _activeRunTimeToLive = activeRunTimeToLive ?? DefaultActiveRunTimeToLive;
        if (_activeRunTimeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeRunTimeToLive),
                _activeRunTimeToLive,
                "ServerRelay active run TTL 必须大于零。");
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
                OnRunCompleted);
            _activeRuns.Add(key, run);
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
        Action<CopilotServerRelayRun> onCompleted)
    {
        RunId = runId;
        Binding = binding;
        ActiveExpiresAtUtc = activeExpiresAtUtc;
        _onCompleted = onCompleted;
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
            _ = _deadlineTimer.Change(
                dueTime > TimeSpan.Zero ? dueTime : TimeSpan.Zero,
                Timeout.InfiniteTimeSpan);
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
            var mapped = candidate with
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
            return mapped;
        }
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
            }
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
