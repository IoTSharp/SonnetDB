using System.Threading.Channels;
using SonnetDB.Hosting;

namespace SonnetDB.Mqtt;

/// <summary>
/// 为 Sparkplug Rebirth 命令提供按 edge node 去重的有界单消费者队列。
/// </summary>
internal sealed class SparkplugRebirthQueue
{
    public const int MinCapacity = 1;
    public const int MaxCapacity = 65_536;

    private readonly object _syncRoot = new();
    private readonly Channel<SparkplugRebirthRequest> _channel;
    private readonly HashSet<SparkplugRebirthRequest> _outstanding = [];
    private readonly ServerMetrics _metrics;
    private bool _accepting = true;
    private int _queuedCount;

    /// <summary>
    /// 创建固定容量队列；容量同时约束等待项和正在发布的唯一节点数。
    /// </summary>
    public SparkplugRebirthQueue(int capacity, ServerMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (capacity is < MinCapacity or > MaxCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                $"Sparkplug Rebirth 队列容量必须位于 {MinCapacity}..{MaxCapacity}。");
        }

        Capacity = capacity;
        _metrics = metrics;
        _channel = Channel.CreateBounded<SparkplugRebirthRequest>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }

    /// <summary>队列允许的最大未完成唯一节点数。</summary>
    public int Capacity { get; }

    /// <summary>读取等待发布和正在发布的唯一节点数。</summary>
    public int OutstandingCount
    {
        get
        {
            lock (_syncRoot)
                return _outstanding.Count;
        }
    }

    /// <summary>尝试加入请求；同一节点在发布完成前只保留一份。</summary>
    public SparkplugRebirthEnqueueResult TryEnqueue(string groupId, string edgeNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeNodeId);
        var request = new SparkplugRebirthRequest(groupId, edgeNodeId);

        lock (_syncRoot)
        {
            if (!_accepting)
            {
                _metrics.RecordSparkplugRebirthQueueRejected();
                return SparkplugRebirthEnqueueResult.RejectedStopped;
            }

            // 先判断重复项，使已满队列中的同节点请求仍可安全合并。
            if (_outstanding.Contains(request))
            {
                _metrics.RecordSparkplugRebirthQueueCoalesced();
                return SparkplugRebirthEnqueueResult.Coalesced;
            }

            if (_outstanding.Count >= Capacity)
            {
                _metrics.RecordSparkplugRebirthQueueRejected();
                return SparkplugRebirthEnqueueResult.RejectedFull;
            }

            _outstanding.Add(request);
            if (!_channel.Writer.TryWrite(request))
            {
                _outstanding.Remove(request);
                throw new InvalidOperationException("Sparkplug Rebirth 队列状态不一致，无法写入已预留的容量。");
            }

            _queuedCount++;
            _metrics.RecordSparkplugRebirthQueueEnqueued();
            return SparkplugRebirthEnqueueResult.Queued;
        }
    }

    /// <summary>异步读取下一项；写端关闭且已无等待项时返回 <see langword="null"/>。</summary>
    public async ValueTask<SparkplugRebirthRequest?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        if (!_channel.Reader.TryRead(out SparkplugRebirthRequest request))
            throw new InvalidOperationException("Sparkplug Rebirth 单消费者队列未能读取已就绪请求。");

        lock (_syncRoot)
        {
            if (_queuedCount <= 0 || !_outstanding.Contains(request))
                throw new InvalidOperationException("Sparkplug Rebirth 队列读取到未登记的请求。");

            _queuedCount--;
            _metrics.RecordSparkplugRebirthQueueDequeued();
        }

        return request;
    }

    /// <summary>发布结束后释放节点去重标记，使后续缺口可以再次请求。</summary>
    public void Complete(in SparkplugRebirthRequest request)
    {
        lock (_syncRoot)
            _outstanding.Remove(request);
    }

    /// <summary>停止接受新请求，并唤醒等待中的单消费者。</summary>
    public void StopAccepting()
    {
        lock (_syncRoot)
        {
            if (!_accepting)
                return;

            _accepting = false;
            _channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// 丢弃停止时尚未完成的请求并释放 Channel 中保存的引用；调用方负责恢复生命周期重试标记。
    /// </summary>
    public IReadOnlyList<SparkplugRebirthRequest> DiscardOutstanding()
    {
        lock (_syncRoot)
        {
            if (_outstanding.Count == 0)
                return [];

            SparkplugRebirthRequest[] discarded = [.. _outstanding];
            int queuedCount = _queuedCount;

            // Channel 中最多只有 Capacity 项，显式上界避免停止路径出现无界清空循环。
            for (int index = 0; index < Capacity && _channel.Reader.TryRead(out _); index++)
            {
            }

            _outstanding.Clear();
            _queuedCount = 0;
            _metrics.RecordSparkplugRebirthQueueDiscarded(discarded.Length, queuedCount);
            return discarded;
        }
    }
}

/// <summary>Sparkplug Rebirth 入队结果。</summary>
internal enum SparkplugRebirthEnqueueResult
{
    Queued,
    Coalesced,
    RejectedFull,
    RejectedStopped,
}

/// <summary>按 group 和 edge node 唯一标识一条 Rebirth 请求。</summary>
internal readonly record struct SparkplugRebirthRequest(string GroupId, string EdgeNodeId);
