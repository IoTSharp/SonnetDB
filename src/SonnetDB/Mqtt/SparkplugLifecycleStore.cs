using System.Collections.Concurrent;

namespace SonnetDB.Mqtt;

/// <summary>
/// 按 Sparkplug edge node 维护 birth/death、消息序列和设备在线状态。
/// </summary>
internal sealed class SparkplugLifecycleStore
{
    private readonly ConcurrentDictionary<EdgeKey, EdgeState> _edges = new();

    /// <summary>当前已知 edge node 数量。</summary>
    public int EdgeCount => _edges.Count;

    /// <summary>
    /// 校验一条上行生命周期或数据消息，并在序列缺口时只触发一次 Rebirth。
    /// </summary>
    public SparkplugLifecycleDecision Process(
        in SparkplugTopicRoute route,
        byte? sequence,
        ulong? birthDeathSequence)
    {
        var key = new EdgeKey(route.GroupId, route.EdgeNodeId);
        EdgeState state = _edges.GetOrAdd(key, static _ => new EdgeState());
        lock (state.SyncRoot)
        {
            if (route.MessageType == SparkplugMessageType.NBirth)
            {
                if (!birthDeathSequence.HasValue)
                    return RequireRebirth(state, "NBIRTH 缺少必需的 bdSeq metric。");
                if (!sequence.HasValue)
                    return RequireRebirth(state, "NBIRTH 缺少必需的 seq。");
                if (sequence.Value != 0)
                    return RequireRebirth(state, "NBIRTH seq 必须从 0 开始。");

                state.Online = true;
                state.BirthDeathSequence = birthDeathSequence;
                state.LastSequence = sequence;
                state.RebirthRequested = false;
                state.OnlineDevices.Clear();
                return SparkplugLifecycleDecision.AcceptedMessage;
            }

            if (route.MessageType == SparkplugMessageType.NDeath)
            {
                if (!birthDeathSequence.HasValue)
                    return new(false, false, "NDEATH 缺少必需的 bdSeq metric。");
                if (state.BirthDeathSequence.HasValue
                    && birthDeathSequence.HasValue
                    && state.BirthDeathSequence.Value != birthDeathSequence.Value)
                {
                    return new(false, false, "NDEATH bdSeq 与当前 birth 会话不匹配，已忽略过期死亡消息。");
                }

                state.Online = false;
                state.LastSequence = null;
                state.OnlineDevices.Clear();
                return SparkplugLifecycleDecision.AcceptedMessage;
            }

            if (route.MessageType == SparkplugMessageType.DDeath)
            {
                if (route.DeviceId is not null)
                    state.OnlineDevices.Remove(route.DeviceId);
                return SparkplugLifecycleDecision.AcceptedMessage;
            }

            if (!state.Online)
                return RequireRebirth(state, "edge node 尚无有效 NBIRTH 或已离线。");

            if (!sequence.HasValue)
                return RequireRebirth(state, "Sparkplug BIRTH/DATA 消息缺少必需的 seq。");

            if (sequence.HasValue)
            {
                if (state.LastSequence.HasValue)
                {
                    byte expected = unchecked((byte)(state.LastSequence.Value + 1));
                    if (sequence.Value != expected)
                    {
                        return RequireRebirth(
                            state,
                            $"Sparkplug seq 缺口：期望 {expected}，实际 {sequence.Value}。");
                    }
                }

                state.LastSequence = sequence.Value;
            }

            if (route.MessageType == SparkplugMessageType.DBirth && route.DeviceId is not null)
                state.OnlineDevices.Add(route.DeviceId);

            if (route.MessageType == SparkplugMessageType.DData
                && route.DeviceId is not null
                && !state.OnlineDevices.Contains(route.DeviceId))
            {
                return RequireRebirth(state, $"设备 '{route.DeviceId}' 尚无有效 DBIRTH。");
            }

            return SparkplugLifecycleDecision.AcceptedMessage;
        }
    }

    /// <summary>
    /// alias-only DATA 缺失 BIRTH 上下文时标记 edge node 需要重生，并进行请求去重。
    /// </summary>
    public bool MarkRebirthRequired(in SparkplugTopicRoute route)
    {
        var key = new EdgeKey(route.GroupId, route.EdgeNodeId);
        EdgeState state = _edges.GetOrAdd(key, static _ => new EdgeState());
        lock (state.SyncRoot)
        {
            if (state.RebirthRequested)
                return false;

            state.RebirthRequested = true;
            return true;
        }
    }

    /// <summary>读取节点当前状态，供测试和诊断使用。</summary>
    public SparkplugNodeState GetState(string groupId, string edgeNodeId)
    {
        if (!_edges.TryGetValue(new EdgeKey(groupId, edgeNodeId), out EdgeState? state))
            return new(false, null, null, false, 0);

        lock (state.SyncRoot)
        {
            return new(
                state.Online,
                state.BirthDeathSequence,
                state.LastSequence,
                state.RebirthRequested,
                state.OnlineDevices.Count);
        }
    }

    private static SparkplugLifecycleDecision RequireRebirth(EdgeState state, string reason)
    {
        bool publish = !state.RebirthRequested;
        state.RebirthRequested = true;
        return new(false, publish, reason);
    }

    private readonly record struct EdgeKey(string GroupId, string EdgeNodeId);

    private sealed class EdgeState
    {
        public object SyncRoot { get; } = new();
        public bool Online { get; set; }
        public ulong? BirthDeathSequence { get; set; }
        public byte? LastSequence { get; set; }
        public bool RebirthRequested { get; set; }
        public HashSet<string> OnlineDevices { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>生命周期校验结果。</summary>
internal readonly record struct SparkplugLifecycleDecision(
    bool Accepted,
    bool RequiresRebirth,
    string Reason)
{
    public static SparkplugLifecycleDecision AcceptedMessage { get; } = new(true, false, string.Empty);
}

/// <summary>用于诊断的 edge node 生命周期快照。</summary>
internal readonly record struct SparkplugNodeState(
    bool Online,
    ulong? BirthDeathSequence,
    byte? LastSequence,
    bool RebirthRequested,
    int OnlineDeviceCount);
