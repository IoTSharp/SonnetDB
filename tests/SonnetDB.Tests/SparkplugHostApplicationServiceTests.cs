using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using SonnetDB.Configuration;
using SonnetDB.Hosting;
using SonnetDB.Mqtt;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// 验证 Sparkplug Rebirth worker 在阻塞、持续异常和宿主停止下的有界存活合同。
/// </summary>
public sealed class SparkplugHostApplicationServiceTests
{
    /// <summary>首次 ONLINE 未能发布时，后台 worker 仍应继续消费后续 Rebirth 请求。</summary>
    [Fact]
    public async Task ExecuteAsync_WhenInitialOnlinePublishFails_ContinuesWithRebirthQueue()
    {
        var publisher = new InitialStateFailThenRebirthPublisher();
        var lifecycle = new SparkplugLifecycleStore();
        var metrics = new ServerMetrics();
        using var service = CreateService(
            publisher,
            lifecycle,
            metrics,
            publishTimeoutMilliseconds: 500,
            publishHostState: true);

        await service.StartAsync(CancellationToken.None);
        try
        {
            MarkRebirthRequired(lifecycle, "edge-after-online-failure");
            Assert.True(service.RequestRebirth("factory", "edge-after-online-failure"));

            string topic = await publisher.RebirthTopic.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(
                () => metrics.SparkplugRebirthCommands == 1,
                TimeSpan.FromSeconds(3));

            Assert.Equal("spBv1.0/factory/NCMD/edge-after-online-failure", topic);
            Assert.Equal(1, publisher.InitialStateFailures);
            Assert.Equal(0, metrics.SparkplugRebirthPublishFailures);
        }
        finally
        {
            await StopServiceAsync(service);
        }
    }

    /// <summary>首次 ONLINE 的未知程序错误必须使服务启动失败，不能伪装成 broker 暂不可用。</summary>
    [Fact]
    public async Task ExecuteAsync_WhenInitialOnlineThrowsUnknownError_FaultsService()
    {
        var publisher = new InitialStateProgrammingErrorPublisher();
        using var service = CreateService(
            publisher,
            new SparkplugLifecycleStore(),
            new ServerMetrics(),
            publishTimeoutMilliseconds: 500,
            publishHostState: true);

        await service.StartAsync(CancellationToken.None);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Equal("测试注入的未知发布错误。", error.Message);
        Assert.Equal(2, publisher.Attempts);
    }

    /// <summary>首次 ONLINE 尚在等待时，宿主停止必须取消发布并完成有界 OFFLINE 收尾。</summary>
    [Fact]
    public async Task StopAsync_DuringInitialOnlinePublish_CancelsPromptly()
    {
        var publisher = new BlockingInitialStatePublisher();
        using var service = CreateService(
            publisher,
            new SparkplugLifecycleStore(),
            new ServerMetrics(),
            SparkplugHostApplicationService.MaxPublishTimeoutMilliseconds,
            publishHostState: true);

        await service.StartAsync(CancellationToken.None);
        await publisher.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();
        await StopServiceAsync(service);
        stopwatch.Stop();

        await publisher.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"停止耗时 {stopwatch.Elapsed}。");
        Assert.Equal(2, publisher.Attempts);
    }

    /// <summary>初始发布在停止竞态中转成 unavailable 时，StopAsync 仍须正常完成。</summary>
    [Fact]
    public async Task StopAsync_WhenInitialOnlineBecomesUnavailableDuringCancellation_Completes()
    {
        var publisher = new UnavailableOnCancellationInitialStatePublisher();
        using var service = CreateService(
            publisher,
            new SparkplugLifecycleStore(),
            new ServerMetrics(),
            SparkplugHostApplicationService.MaxPublishTimeoutMilliseconds,
            publishHostState: true);

        await service.StartAsync(CancellationToken.None);
        await publisher.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await StopServiceAsync(service);

        await publisher.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(service.ExecuteTask!.IsFaulted);
        Assert.Equal(2, publisher.Attempts);
    }

    /// <summary>验证单条阻塞发布达到 deadline 后释放节点，并继续发布下一请求。</summary>
    [Fact]
    public async Task ExecuteAsync_WhenPublishBlocksUntilDeadline_DropsCurrentAndContinues()
    {
        var publisher = new BlockFirstPublisher();
        var lifecycle = new SparkplugLifecycleStore();
        var metrics = new ServerMetrics();
        using var service = CreateService(publisher, lifecycle, metrics, publishTimeoutMilliseconds: 100);

        await service.StartAsync(CancellationToken.None);
        try
        {
            MarkRebirthRequired(lifecycle, "edge-blocked");
            Assert.True(service.RequestRebirth("factory", "edge-blocked"));
            Assert.True(service.RequestRebirth("factory", "edge-next"));

            string successfulTopic = await publisher.SuccessfulTopic.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(
                () => metrics.SparkplugRebirthPublishFailures == 1
                    && metrics.SparkplugRebirthCommands == 1,
                TimeSpan.FromSeconds(3));

            Assert.Equal("spBv1.0/factory/NCMD/edge-next", successfulTopic);
            Assert.True(publisher.FirstCancellationObserved.Task.IsCompletedSuccessfully);
            Assert.Equal(1, metrics.SparkplugRebirthQueueDiscarded);
            Assert.Equal(0, metrics.SparkplugRebirthQueueDepth);
            Assert.False(lifecycle.GetState("factory", "edge-blocked").RebirthRequested);
        }
        finally
        {
            await StopServiceAsync(service);
        }
    }

    /// <summary>验证连续即时异常只淘汰各自请求，不会使单 worker 永久退出。</summary>
    [Fact]
    public async Task ExecuteAsync_WhenPublisherKeepsThrowing_RecordsFailuresAndReachesLaterRequest()
    {
        const int failureCount = 3;
        var publisher = new FailThenSucceedPublisher(failureCount);
        var lifecycle = new SparkplugLifecycleStore();
        var metrics = new ServerMetrics();
        using var service = CreateService(publisher, lifecycle, metrics, publishTimeoutMilliseconds: 500);

        await service.StartAsync(CancellationToken.None);
        try
        {
            for (int index = 0; index < failureCount + 1; index++)
                Assert.True(service.RequestRebirth("factory", $"edge-{index}"));

            string successfulTopic = await publisher.SuccessfulTopic.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(
                () => metrics.SparkplugRebirthPublishFailures == failureCount
                    && metrics.SparkplugRebirthCommands == 1,
                TimeSpan.FromSeconds(3));

            Assert.Equal("spBv1.0/factory/NCMD/edge-3", successfulTopic);
            Assert.Equal(failureCount + 1, publisher.Attempts);
            Assert.Equal(failureCount, metrics.SparkplugRebirthQueueDiscarded);
            Assert.Equal(0, metrics.SparkplugRebirthQueueDepth);
        }
        finally
        {
            await StopServiceAsync(service);
        }
    }

    /// <summary>验证宿主停止直接取消 in-flight，不等待较长的请求 deadline。</summary>
    [Fact]
    public async Task StopAsync_WithBlockedPublish_CancelsInFlightAndReleasesLifecycle()
    {
        var publisher = new BlockingPublisher();
        var lifecycle = new SparkplugLifecycleStore();
        var metrics = new ServerMetrics();
        using var service = CreateService(
            publisher,
            lifecycle,
            metrics,
            SparkplugHostApplicationService.MaxPublishTimeoutMilliseconds);
        bool stopped = false;

        await service.StartAsync(CancellationToken.None);
        try
        {
            MarkRebirthRequired(lifecycle, "edge-stop");
            Assert.True(service.RequestRebirth("factory", "edge-stop"));
            await publisher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var stopwatch = Stopwatch.StartNew();
            await StopServiceAsync(service);
            stopwatch.Stop();
            stopped = true;

            await publisher.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"停止耗时 {stopwatch.Elapsed}。");
            Assert.Equal(0, metrics.SparkplugRebirthPublishFailures);
            Assert.Equal(1, metrics.SparkplugRebirthQueueDiscarded);
            Assert.Equal(0, metrics.SparkplugRebirthQueueDepth);
            Assert.False(lifecycle.GetState("factory", "edge-stop").RebirthRequested);
        }
        finally
        {
            if (!stopped)
                await StopServiceAsync(service);
        }
    }

    /// <summary>创建关闭 Host STATE 发布的测试服务，使断言只观察 Rebirth worker。</summary>
    private static SparkplugHostApplicationService CreateService(
        ISparkplugInternalPublisher publisher,
        SparkplugLifecycleStore lifecycle,
        ServerMetrics metrics,
        int publishTimeoutMilliseconds,
        bool publishHostState = false)
    {
        var options = new ServerOptions
        {
            Mqtt = new MqttBrokerOptions
            {
                Sparkplug = new SparkplugOptions
                {
                    PublishHostState = publishHostState,
                    RebirthQueueCapacity = 8,
                    RebirthPublishTimeoutMilliseconds = publishTimeoutMilliseconds,
                },
            },
        };

        return new SparkplugHostApplicationService(
            publisher,
            lifecycle,
            Options.Create(options),
            metrics,
            NullLogger<SparkplugHostApplicationService>.Instance);
    }

    /// <summary>把节点推进到序列缺口状态，模拟控制器提交 Rebirth 请求前的生命周期状态。</summary>
    private static void MarkRebirthRequired(SparkplugLifecycleStore lifecycle, string edgeNodeId)
    {
        SparkplugTopicRoute birth = Parse($"spBv1.0/factory/NBIRTH/{edgeNodeId}");
        SparkplugTopicRoute data = Parse($"spBv1.0/factory/NDATA/{edgeNodeId}");
        Assert.True(lifecycle.Process(birth, 0, 1).Accepted);
        Assert.True(lifecycle.Process(data, 2, null).RequiresRebirth);
    }

    /// <summary>在双重超时保护下停止服务，防止失败测试遗留后台任务。</summary>
    private static async Task StopServiceAsync(SparkplugHostApplicationService service)
    {
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.StopAsync(stopTimeout.Token).WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <summary>在最多 100 次检查和总墙钟超时内等待异步指标收敛。</summary>
    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        const int maxAttempts = 100;
        TimeSpan delay = TimeSpan.FromMilliseconds(Math.Max(1, timeout.TotalMilliseconds / maxAttempts));
        using var timeoutSource = new CancellationTokenSource(timeout);

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (predicate())
                return;

            await Task.Delay(delay, timeoutSource.Token);
        }

        throw new TimeoutException($"条件在 {maxAttempts} 次检查和 {timeout} 内未满足。");
    }

    /// <summary>解析测试 topic，并在构造错误时直接给出协议原因。</summary>
    private static SparkplugTopicRoute Parse(string topic)
    {
        Assert.True(SparkplugTopicParser.TryParse(topic, out SparkplugTopicRoute route, out string error), error);
        return route;
    }

    /// <summary>首次 Host STATE 失败、后续 Rebirth 与 OFFLINE 成功的发布器。</summary>
    private sealed class InitialStateFailThenRebirthPublisher : ISparkplugInternalPublisher
    {
        private int _initialStateFailures;

        public int InitialStateFailures => Volatile.Read(ref _initialStateFailures);

        public TaskCompletionSource<string> RebirthTopic { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>只拒绝首次 ONLINE，确保测试不会把停止阶段 OFFLINE 误认为 Rebirth 成功。</summary>
        public Task PublishInternalAsync(
            string topic,
            ReadOnlyMemory<byte> payload,
            MqttQualityOfServiceLevel qualityOfService,
            bool retain,
            CancellationToken cancellationToken)
        {
            if (retain && Interlocked.CompareExchange(ref _initialStateFailures, 1, 0) == 0)
                throw new SparkplugPublisherUnavailableException("测试注入的首次 ONLINE 发布失败。");

            if (!retain)
                RebirthTopic.TrySetResult(topic);
            return Task.CompletedTask;
        }
    }

    /// <summary>首次 ONLINE 抛出未知程序错误，OFFLINE 收尾正常完成的发布器。</summary>
    private sealed class InitialStateProgrammingErrorPublisher : ISparkplugInternalPublisher
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        /// <summary>仅首次调用返回未知错误，确保测试能观察原始异常且不被收尾覆盖。</summary>
        public Task PublishInternalAsync(
            string topic,
            ReadOnlyMemory<byte> payload,
            MqttQualityOfServiceLevel qualityOfService,
            bool retain,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
                return Task.FromException(new InvalidOperationException("测试注入的未知发布错误。"));

            return Task.CompletedTask;
        }
    }

    /// <summary>首次 ONLINE 阻塞到宿主取消，后续 OFFLINE 立即完成的发布器。</summary>
    private sealed class BlockingInitialStatePublisher : ISparkplugInternalPublisher
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>首调最多等待 30 秒但应由宿主停止取消，第二次用于 OFFLINE 收尾。</summary>
        public async Task PublishInternalAsync(
            string topic,
            ReadOnlyMemory<byte> payload,
            MqttQualityOfServiceLevel qualityOfService,
            bool retain,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) != 1)
                return;

            Started.TrySetResult(true);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult(true);
                throw;
            }
        }
    }

    /// <summary>首次 ONLINE 在取消边界转为专用 unavailable，第二次 OFFLINE 正常完成的发布器。</summary>
    private sealed class UnavailableOnCancellationInitialStatePublisher : ISparkplugInternalPublisher
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>首调阻塞到取消后模拟 bridge 的 deadline 转换结果，第二次立即完成。</summary>
        public async Task PublishInternalAsync(
            string topic,
            ReadOnlyMemory<byte> payload,
            MqttQualityOfServiceLevel qualityOfService,
            bool retain,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) != 1)
                return;

            Started.TrySetResult(true);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException ex)
            {
                CancellationObserved.TrySetResult(true);
                throw new SparkplugPublisherUnavailableException("测试模拟停止边界 broker 不可用。", ex);
            }
        }
    }

    /// <summary>首条请求阻塞到取消、后续请求成功的发布器。</summary>
    private sealed class BlockFirstPublisher : ISparkplugInternalPublisher
    {
        private int _attempts;

        public TaskCompletionSource<bool> FirstCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> SuccessfulTopic { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>首条调用等待最多 30 秒但应由请求 deadline 提前取消，第二条立即成功。</summary>
        public async Task PublishInternalAsync(
            string topic,
            ReadOnlyMemory<byte> payload,
            MqttQualityOfServiceLevel qualityOfService,
            bool retain,
            CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref _attempts);
            if (attempt == 1)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstCancellationObserved.TrySetResult(true);
                    throw;
                }
            }

            SuccessfulTopic.TrySetResult(topic);
        }
    }

    /// <summary>前若干次持续抛出 broker 异常、最后一次成功的发布器。</summary>
    private sealed class FailThenSucceedPublisher : ISparkplugInternalPublisher
    {
        private readonly int _failureCount;
        private int _attempts;

        /// <summary>创建具有固定失败次数的有界故障脚本。</summary>
        public FailThenSucceedPublisher(int failureCount)
        {
            _failureCount = failureCount;
        }

        public int Attempts => Volatile.Read(ref _attempts);

        public TaskCompletionSource<string> SuccessfulTopic { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>在固定次数内抛出异常，随后记录成功 topic。</summary>
        public Task PublishInternalAsync(
            string topic,
            ReadOnlyMemory<byte> payload,
            MqttQualityOfServiceLevel qualityOfService,
            bool retain,
            CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= _failureCount)
                throw new InvalidOperationException("测试注入的持续 broker 异常。");

            SuccessfulTopic.TrySetResult(topic);
            return Task.CompletedTask;
        }
    }

    /// <summary>始终阻塞到调用方取消的发布器，用于验证宿主停止优先级。</summary>
    private sealed class BlockingPublisher : ISparkplugInternalPublisher
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>等待最多 30 秒，并在收到停止取消后记录观察结果。</summary>
        public async Task PublishInternalAsync(
            string topic,
            ReadOnlyMemory<byte> payload,
            MqttQualityOfServiceLevel qualityOfService,
            bool retain,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult(true);
                throw;
            }
        }
    }
}
