using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Mqtt;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// 验证 Sparkplug Rebirth 队列的容量、合并、取消、停止释放与指标合同。
/// </summary>
public sealed class SparkplugRebirthQueueTests
{
    /// <summary>验证满载只拒绝新节点，同节点请求仍合并，并准确维护队列深度。</summary>
    [Fact]
    public async Task Queue_WhenFull_CoalescesSameNodeAndRejectsNewNode()
    {
        var metrics = new ServerMetrics();
        var queue = new SparkplugRebirthQueue(2, metrics);

        Assert.Equal(SparkplugRebirthEnqueueResult.Queued, queue.TryEnqueue("group-a", "edge-1"));
        Assert.Equal(SparkplugRebirthEnqueueResult.Coalesced, queue.TryEnqueue("group-a", "edge-1"));
        Assert.Equal(SparkplugRebirthEnqueueResult.Queued, queue.TryEnqueue("group-a", "edge-2"));
        Assert.Equal(SparkplugRebirthEnqueueResult.RejectedFull, queue.TryEnqueue("group-a", "edge-3"));
        Assert.Equal(2, queue.OutstandingCount);
        Assert.Equal(2, metrics.SparkplugRebirthQueueDepth);

        SparkplugRebirthRequest? next = await queue.ReadAsync(CancellationToken.None);
        Assert.True(next.HasValue);
        Assert.Equal(new SparkplugRebirthRequest("group-a", "edge-1"), next.Value);
        Assert.Equal(1, metrics.SparkplugRebirthQueueDepth);

        // 出队但尚未完成的请求仍需合并，避免慢 broker 导致同节点重复积压。
        Assert.Equal(SparkplugRebirthEnqueueResult.Coalesced, queue.TryEnqueue("group-a", "edge-1"));
        queue.Complete(next.Value);
        Assert.Equal(SparkplugRebirthEnqueueResult.Queued, queue.TryEnqueue("group-a", "edge-1"));

        queue.StopAccepting();
        IReadOnlyList<SparkplugRebirthRequest> discarded = queue.DiscardOutstanding();
        Assert.Equal(2, discarded.Count);
        Assert.Contains(new SparkplugRebirthRequest("group-a", "edge-1"), discarded);
        Assert.Contains(new SparkplugRebirthRequest("group-a", "edge-2"), discarded);
        Assert.Equal(SparkplugRebirthEnqueueResult.RejectedStopped, queue.TryEnqueue("group-a", "edge-4"));
        Assert.Null(await queue.ReadAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(3, metrics.SparkplugRebirthQueueEnqueued);
        Assert.Equal(2, metrics.SparkplugRebirthQueueCoalesced);
        Assert.Equal(2, metrics.SparkplugRebirthQueueRejected);
        Assert.Equal(2, metrics.SparkplugRebirthQueueDiscarded);
        Assert.Equal(0, metrics.SparkplugRebirthQueueDepth);
        Assert.Equal(0, queue.OutstandingCount);
    }

    /// <summary>验证并发生产者对同一实体最多生成一个未完成请求。</summary>
    [Fact]
    public void Queue_WithConcurrentSameNodeRequests_QueuesExactlyOnce()
    {
        const int requestCount = 100;
        var metrics = new ServerMetrics();
        var queue = new SparkplugRebirthQueue(4, metrics);
        var results = new SparkplugRebirthEnqueueResult[requestCount];

        Parallel.For(
            0,
            requestCount,
            index => results[index] = queue.TryEnqueue("group-a", "edge-shared"));

        Assert.Equal(1, results.Count(static result => result == SparkplugRebirthEnqueueResult.Queued));
        Assert.Equal(99, results.Count(static result => result == SparkplugRebirthEnqueueResult.Coalesced));
        Assert.Equal(1, queue.OutstandingCount);
        Assert.Equal(1, metrics.SparkplugRebirthQueueDepth);

        queue.StopAccepting();
        queue.DiscardOutstanding();
    }

    /// <summary>验证空队列读取遵守取消令牌，且取消后仍可完成有界清理。</summary>
    [Fact]
    public async Task Queue_ReadCancellation_StopsPromptlyAndReleasesState()
    {
        var metrics = new ServerMetrics();
        var queue = new SparkplugRebirthQueue(1, metrics);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.ReadAsync(cancellation.Token).AsTask());

        queue.StopAccepting();
        Assert.Empty(queue.DiscardOutstanding());
        Assert.Equal(0, metrics.SparkplugRebirthQueueDepth);
    }

    /// <summary>验证队列背压指标出现在 Prometheus 输出中，便于设置容量告警。</summary>
    [Fact]
    public void QueueMetrics_AreRenderedByPrometheusFormatter()
    {
        string root = Path.Combine(Path.GetTempPath(), "sonnetdb-sparkplug-queue-metrics-" + Guid.NewGuid().ToString("N"));
        var metrics = new ServerMetrics();
        var queue = new SparkplugRebirthQueue(1, metrics);

        try
        {
            Assert.Equal(SparkplugRebirthEnqueueResult.Queued, queue.TryEnqueue("group-a", "edge-1"));
            Assert.Equal(SparkplugRebirthEnqueueResult.RejectedFull, queue.TryEnqueue("group-a", "edge-2"));
            metrics.RecordSparkplugRebirthPublishFailure();
            using var registry = new TsdbRegistry(root);

            string prometheus = PrometheusFormatter.Render(metrics, registry);

            Assert.Contains("sonnetdb_sparkplug_rebirth_queue_enqueued_total 1", prometheus, StringComparison.Ordinal);
            Assert.Contains("sonnetdb_sparkplug_rebirth_queue_rejected_total 1", prometheus, StringComparison.Ordinal);
            Assert.Contains("sonnetdb_sparkplug_rebirth_queue_depth 1", prometheus, StringComparison.Ordinal);
            Assert.Contains("sonnetdb_sparkplug_rebirth_publish_failures_total 1", prometheus, StringComparison.Ordinal);
        }
        finally
        {
            queue.StopAccepting();
            queue.DiscardOutstanding();
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 测试清理采用 best effort，避免掩盖主要断言。
            }
        }
    }
}
