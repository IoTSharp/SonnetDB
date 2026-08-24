using System.Text.Json;
using SonnetDB.Graphs;
using SonnetDB.Json;

namespace SonnetDB.Server.Graphs;

internal interface IGraphMaintenanceAuditStore
{
    void Append(GraphMaintenanceApprovalDto entry);

    GraphMaintenanceApprovalDto? GetLatest(Guid approvalId);

    IReadOnlyList<GraphMaintenanceApprovalDto> List(string database, string graph, int maxEntries);
}

internal sealed class FileGraphMaintenanceAuditStore : IGraphMaintenanceAuditStore
{
    private const string FileName = "graph-maintenance-audit.ndjson";
    private readonly object _sync = new();
    private readonly string _path;
    private readonly List<GraphMaintenanceApprovalDto> _entries = [];

    internal FileGraphMaintenanceAuditStore(string systemDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDirectory);
        Directory.CreateDirectory(systemDirectory);
        _path = Path.Combine(systemDirectory, FileName);
        LoadExisting();
    }

    public void Append(GraphMaintenanceApprovalDto entry)
    {
        Validate(entry, lineNumber: null);
        lock (_sync)
        {
            using var buffer = new MemoryStream();
            JsonSerializer.Serialize(buffer, entry, ServerJsonContext.Default.GraphMaintenanceApprovalDto);
            buffer.WriteByte((byte)'\n');

            using var stream = new FileStream(
                _path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            long originalLength = stream.Length;
            stream.Position = originalLength;
            try
            {
                buffer.Position = 0;
                buffer.CopyTo(stream);
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                try
                {
                    stream.SetLength(originalLength);
                    stream.Flush(flushToDisk: true);
                }
                catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                {
                    // 保留原始写异常；下次启动会拒绝损坏的审计文件。
                }

                throw;
            }

            _entries.Add(entry);
        }
    }

    public GraphMaintenanceApprovalDto? GetLatest(Guid approvalId)
    {
        if (approvalId == Guid.Empty)
            return null;
        lock (_sync)
            return _entries.LastOrDefault(entry => entry.ApprovalId == approvalId);
    }

    public IReadOnlyList<GraphMaintenanceApprovalDto> List(string database, string graph, int maxEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(graph);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        lock (_sync)
        {
            return _entries
                .Where(entry => string.Equals(entry.Database, database, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Graph, graph, StringComparison.Ordinal))
                .TakeLast(maxEntries)
                .Reverse()
                .ToArray();
        }
    }

    private void LoadExisting()
    {
        if (!File.Exists(_path))
            return;

        byte[] data = File.ReadAllBytes(_path);
        int offset = 0;
        int lineNumber = 0;
        while (offset < data.Length)
        {
            lineNumber++;
            ReadOnlySpan<byte> remaining = data.AsSpan(offset);
            int newline = remaining.IndexOf((byte)'\n');
            bool terminated = newline >= 0;
            int end = terminated ? offset + newline : data.Length;
            ReadOnlySpan<byte> line = TrimAsciiWhitespace(data.AsSpan(offset, end - offset));
            if (line.Length == 0)
            {
                if (!terminated)
                    RepairTornTail(offset, appendNewline: false);
                if (!terminated)
                    break;
                offset = end + 1;
                continue;
            }

            GraphMaintenanceApprovalDto entry;
            try
            {
                entry = JsonSerializer.Deserialize(
                        line,
                        ServerJsonContext.Default.GraphMaintenanceApprovalDto)
                    ?? throw new InvalidDataException("Graph 维护审计记录不能为 null。");
            }
            catch (JsonException exception)
            {
                // A process can terminate between the final write and its newline.
                // Only an unterminated final JSON record is repairable; a terminated
                // malformed line remains a hard corruption signal.
                if (!terminated)
                {
                    RepairTornTail(offset, appendNewline: false);
                    break;
                }
                throw new InvalidDataException($"Graph 维护审计文件第 {lineNumber} 行损坏。", exception);
            }
            Validate(entry, lineNumber);
            _entries.Add(entry);
            if (!terminated)
            {
                RepairTornTail(data.Length, appendNewline: true);
                break;
            }
            offset = end + 1;
        }

        RecoverApplyingEntries();
    }

    private void RecoverApplyingEntries()
    {
        GraphMaintenanceApprovalDto[] applying = _entries
            .Where(static entry => string.Equals(entry.State, "applying", StringComparison.Ordinal))
            .ToArray();
        foreach (GraphMaintenanceApprovalDto entry in applying)
        {
            Append(entry with
            {
                OccurredAtUtc = DateTimeOffset.UtcNow,
                State = "interrupted",
                ErrorCode = "graph_maintenance_interrupted",
                Reason = "进程在 Graph 维护执行期间终止；维护 sidecar 可由下一次审批继续处理。",
            });
        }
    }

    private void RepairTornTail(long offset, bool appendNewline)
    {
        using var stream = new FileStream(
            _path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        if (appendNewline)
        {
            stream.Position = stream.Length;
            stream.WriteByte((byte)'\n');
        }
        else
        {
            stream.SetLength(offset);
            stream.Position = offset;
        }
        stream.Flush(flushToDisk: true);
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
    {
        int start = 0;
        while (start < value.Length && value[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            start++;
        int end = value.Length;
        while (end > start && value[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            end--;
        return value[start..end];
    }

    private static void Validate(GraphMaintenanceApprovalDto entry, int? lineNumber)
    {
        bool valid = entry.ApprovalId != Guid.Empty
            && entry.OccurredAtUtc != default
            && !string.IsNullOrWhiteSpace(entry.Database)
            && !string.IsNullOrWhiteSpace(entry.Graph)
            && Enum.IsDefined(entry.Action)
            && !string.IsNullOrWhiteSpace(entry.State)
            && !string.IsNullOrWhiteSpace(entry.Principal)
            && entry.ExpiresAtUtc != default
            && entry.MaxWorkUnits is >= 1 and <= 4_096;
        if (valid)
            return;

        string location = lineNumber is null ? string.Empty : $"第 {lineNumber.Value} 行";
        throw new InvalidDataException($"Graph 维护审计记录{location}字段无效。");
    }
}

internal sealed class GraphMaintenanceApprovalService
{
    private static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(10);
    private readonly object _sync = new();
    private readonly IGraphMaintenanceAuditStore _audit;
    private readonly TimeProvider _timeProvider;

    internal GraphMaintenanceApprovalService(
        IGraphMaintenanceAuditStore audit,
        TimeProvider timeProvider)
    {
        _audit = audit;
        _timeProvider = timeProvider;
    }

    internal GraphMaintenanceApprovalDto Stage(
        string database,
        string graph,
        GraphMaintenanceStageRequest request,
        string principal)
    {
        ValidateRequest(request);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var entry = new GraphMaintenanceApprovalDto
        {
            ApprovalId = Guid.NewGuid(),
            OccurredAtUtc = now,
            Database = database,
            Graph = graph,
            Action = request.Action,
            State = "staged",
            Principal = principal,
            ExpiresAtUtc = now.Add(ApprovalLifetime),
            CompactOnCompletion = request.CompactOnCompletion,
            MaxWorkUnits = request.MaxWorkUnits,
        };
        lock (_sync)
            _audit.Append(entry);
        return entry;
    }

    internal GraphMaintenanceApprovalDto Approve(
        string database,
        string graph,
        Guid approvalId,
        string principal,
        GraphStore store,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            GraphMaintenanceApprovalDto staged = ResolvePending(database, graph, approvalId);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (now > staged.ExpiresAtUtc)
            {
                GraphMaintenanceApprovalDto expired = staged with
                {
                    OccurredAtUtc = now,
                    Principal = principal,
                    State = "expired",
                    ErrorCode = "graph_maintenance_approval_expired",
                    Reason = "Graph 维护审批已过期，请重新暂存。",
                };
                _audit.Append(expired);
                throw new GraphMaintenanceApprovalException(expired.ErrorCode, expired.Reason);
            }

            _audit.Append(staged with
            {
                OccurredAtUtc = now,
                Principal = principal,
                State = "applying",
            });

            try
            {
                GraphMaintenanceExecutionDto result = Execute(store, staged, cancellationToken);
                string state = result.IsComplete ? "completed" : "paused";
                var completed = staged with
                {
                    OccurredAtUtc = _timeProvider.GetUtcNow(),
                    Principal = principal,
                    State = state,
                    Result = result,
                };
                _audit.Append(completed);
                return completed;
            }
            catch (Exception exception) when (exception is not GraphMaintenanceApprovalException)
            {
                string code = exception is OperationCanceledException
                    ? "graph_maintenance_cancelled"
                    : "graph_maintenance_failed";
                _audit.Append(staged with
                {
                    OccurredAtUtc = _timeProvider.GetUtcNow(),
                    Principal = principal,
                    State = "failed",
                    ErrorCode = code,
                    Reason = LimitReason(exception.Message),
                });
                throw;
            }
        }
    }

    internal GraphMaintenanceApprovalDto Reject(
        string database,
        string graph,
        Guid approvalId,
        string principal,
        string? reason)
    {
        if (reason?.Length > 512)
            throw new GraphMaintenanceApprovalException("bad_request", "拒绝原因不能超过 512 个字符。");
        lock (_sync)
        {
            GraphMaintenanceApprovalDto staged = ResolvePending(database, graph, approvalId);
            var rejected = staged with
            {
                OccurredAtUtc = _timeProvider.GetUtcNow(),
                Principal = principal,
                State = "rejected",
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            };
            _audit.Append(rejected);
            return rejected;
        }
    }

    internal IReadOnlyList<GraphMaintenanceApprovalDto> List(
        string database,
        string graph,
        int maxEntries)
        => _audit.List(database, graph, maxEntries);

    private GraphMaintenanceApprovalDto ResolvePending(
        string database,
        string graph,
        Guid approvalId)
    {
        GraphMaintenanceApprovalDto? latest = _audit.GetLatest(approvalId);
        if (latest is null
            || !string.Equals(latest.Database, database, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(latest.Graph, graph, StringComparison.Ordinal))
        {
            throw new GraphMaintenanceApprovalException(
                "graph_maintenance_approval_not_found",
                "未找到当前数据库与 Graph 的维护审批。");
        }
        if (!string.Equals(latest.State, "staged", StringComparison.Ordinal))
        {
            throw new GraphMaintenanceApprovalException(
                "graph_maintenance_approval_not_pending",
                $"Graph 维护审批当前状态为 '{latest.State}'，不能重复决策。");
        }
        return latest;
    }

    private static GraphMaintenanceExecutionDto Execute(
        GraphStore store,
        GraphMaintenanceApprovalDto approval,
        CancellationToken cancellationToken)
    {
        return approval.Action switch
        {
            GraphMaintenanceAction.RepairRebuild => MapMaintenance(
                approval.Action,
                store.RunMaintenance(
                    new GraphMaintenanceOptions
                    {
                        MaxWorkUnits = approval.MaxWorkUnits,
                        CompactOnCompletion = approval.CompactOnCompletion,
                    },
                    cancellationToken)),
            GraphMaintenanceAction.Checkpoint => new GraphMaintenanceExecutionDto
            {
                Action = approval.Action,
                IsComplete = true,
                Sequence = store.Checkpoint(),
            },
            GraphMaintenanceAction.Compact => new GraphMaintenanceExecutionDto
            {
                Action = approval.Action,
                IsComplete = true,
                Sequence = store.Compact(),
            },
            _ => throw new GraphMaintenanceApprovalException("bad_request", "未知的 Graph 维护动作。"),
        };
    }

    private static GraphMaintenanceExecutionDto MapMaintenance(
        GraphMaintenanceAction action,
        GraphMaintenanceResult result)
        => new()
        {
            Action = action,
            IsComplete = result.IsComplete,
            OperationId = result.OperationId,
            Phase = result.Phase.ToString(),
            Sequence = result.Sequence,
            ScannedRecords = result.ScannedRecords,
            RepairedEntries = result.RepairedEntries,
            RemovedEntries = result.RemovedEntries,
            WorkUnits = result.WorkUnits,
        };

    private static void ValidateRequest(GraphMaintenanceStageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Action))
            throw new GraphMaintenanceApprovalException("bad_request", "未知的 Graph 维护动作。");
        if (request.MaxWorkUnits is < 1 or > 4_096)
            throw new GraphMaintenanceApprovalException("bad_request", "MaxWorkUnits 必须在 1 到 4,096 之间。");
        if (request.Action != GraphMaintenanceAction.RepairRebuild && request.CompactOnCompletion)
            throw new GraphMaintenanceApprovalException("bad_request", "CompactOnCompletion 只适用于 repair/rebuild。");
    }

    private static string LimitReason(string message)
        => message.Length <= 512 ? message : message[..512];
}

internal sealed class GraphMaintenanceApprovalException(string code, string message)
    : InvalidOperationException(message)
{
    internal string Code { get; } = code;
}
