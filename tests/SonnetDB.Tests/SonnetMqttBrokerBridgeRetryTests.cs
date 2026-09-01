using System.Diagnostics;
using SonnetDB.Mqtt;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>MQTT 内部发布等待 broker 就绪时的次数、取消和异常合同测试。</summary>
public sealed class SonnetMqttBrokerBridgeRetryTests
{
    /// <summary>持续未就绪必须在精确最大次数后失败，不能形成无界重试。</summary>
    [Fact]
    public async Task ExecuteWithBoundedBrokerReadinessRetryAsync_AlwaysUnavailable_StopsAtLimit()
    {
        var readinessChecks = 0;
        var operationAttempts = 0;

        SparkplugPublisherUnavailableException error = await Assert.ThrowsAsync<SparkplugPublisherUnavailableException>(() =>
            SonnetMqttBrokerBridge.ExecuteWithBoundedBrokerReadinessRetryAsync(
                () =>
                {
                    readinessChecks++;
                    return false;
                },
                _ =>
                {
                    operationAttempts++;
                    return Task.CompletedTask;
                },
                CancellationToken.None,
                maxAttempts: 3,
                retryDelay: TimeSpan.Zero));

        Assert.Equal(3, readinessChecks);
        Assert.Equal(0, operationAttempts);
        Assert.Null(error.InnerException);
    }

    /// <summary>broker 在预算内恢复时应立即成功，不继续消耗剩余尝试次数。</summary>
    [Fact]
    public async Task ExecuteWithBoundedBrokerReadinessRetryAsync_EventuallyReady_ReturnsEarly()
    {
        var readinessChecks = 0;
        var operationAttempts = 0;

        await SonnetMqttBrokerBridge.ExecuteWithBoundedBrokerReadinessRetryAsync(
            () => ++readinessChecks >= 3,
            _ =>
            {
                operationAttempts++;
                return Task.CompletedTask;
            },
            CancellationToken.None,
            maxAttempts: 10,
            retryDelay: TimeSpan.Zero);

        Assert.Equal(3, readinessChecks);
        Assert.Equal(1, operationAttempts);
    }

    /// <summary>调用方取消必须立即穿透，不能被未就绪重试转换为普通失败。</summary>
    [Fact]
    public async Task ExecuteWithBoundedBrokerReadinessRetryAsync_Cancelled_StopsImmediately()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var readinessChecks = 0;
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SonnetMqttBrokerBridge.ExecuteWithBoundedBrokerReadinessRetryAsync(
                () =>
                {
                    readinessChecks++;
                    return true;
                },
                _ =>
                {
                    attempts++;
                    return Task.CompletedTask;
                },
                cancellation.Token,
                maxAttempts: 10,
                retryDelay: TimeSpan.FromSeconds(1)));

        Assert.Equal(0, attempts);
        Assert.Equal(0, readinessChecks);
    }

    /// <summary>operation 运行中耗尽 deadline 时必须取消当前尝试，不能等到 operation 自行结束。</summary>
    [Fact]
    public async Task ExecuteWithBoundedBrokerReadinessRetryAsync_DeadlineExpiresDuringOperation_StopsBoundedly()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var attempts = 0;
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SonnetMqttBrokerBridge.ExecuteWithBoundedBrokerReadinessRetryAsync(
                static () => true,
                async token =>
                {
                    attempts++;
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                },
                deadline.Token,
                maxAttempts: 10,
                retryDelay: TimeSpan.FromSeconds(1)));
        stopwatch.Stop();

        Assert.Equal(1, attempts);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"运行中 deadline 取消耗时 {stopwatch.Elapsed}，未按预算终止。");
    }

    /// <summary>broker 已就绪后的未知操作异常必须立即穿透，不能伪装成未就绪。</summary>
    [Fact]
    public async Task ExecuteWithBoundedBrokerReadinessRetryAsync_OperationFails_PropagatesImmediately()
    {
        var attempts = 0;

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SonnetMqttBrokerBridge.ExecuteWithBoundedBrokerReadinessRetryAsync(
                static () => true,
                _ =>
                {
                    attempts++;
                    throw new InvalidOperationException("publisher bug");
                },
                CancellationToken.None,
                maxAttempts: 10,
                retryDelay: TimeSpan.Zero));

        Assert.Equal("publisher bug", error.Message);
        Assert.Equal(1, attempts);
    }
}
