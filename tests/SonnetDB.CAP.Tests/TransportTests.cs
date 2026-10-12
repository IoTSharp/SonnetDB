using System.Text;
using DotNetCore.CAP.Messages;
using DotNetCore.CAP.Transport;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.CAP.Transport;
using SonnetDB.Data.Mq;
using Xunit;

namespace SonnetDB.CAP.Tests;

public sealed class TransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumer_WithInboxFailure_RedeliversBeforeAdvancingOffset(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        var transport = context.Provider.GetRequiredService<ITransport>();
        var factory = context.Provider.GetRequiredService<IConsumerClientFactory>();
        var connection = context.Provider.GetRequiredService<CapMqConnection>();
        const string name = "订单.created / v1";
        foreach (string id in new[] { "201", "202" })
            Assert.True((await transport.SendAsync(Envelope(id, name, "中文正文"))).Succeeded);
        await using IConsumerClient consumer = await factory.CreateAsync("group A", 1);
        await consumer.SubscribeAsync([name]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var deliveries = new List<string>();
        object? stale = null;
        consumer.OnMessageCallback = async (message, sender) =>
        {
            deliveries.Add(message.GetId());
            Assert.Equal("group A", message.GetGroup());
            Assert.Equal("中文正文", Encoding.UTF8.GetString(message.Body.Span));
            if (deliveries.Count == 1)
            {
                stale = sender;
                await consumer.RejectAsync(sender);
                Assert.Equal(2, (await connection.Client.PullAsync(connection.Topic(name), connection.Group("group A"), 2)).Count);
                return;
            }
            await Assert.ThrowsAsync<ArgumentException>(() => consumer.CommitAsync(stale));
            await context.Storage.StoreReceivedMessageAsync(name, "group A", CapTestContext.Message(message.GetId(), name));
            await consumer.CommitAsync(sender);
            await consumer.CommitAsync(sender);
            if (message.GetId() == "202")
                await cancellation.CancelAsync();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.ListeningAsync(TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.Equal(new[] { "201", "201", "202" }, deliveries);
        Assert.Empty(await connection.Client.PullAsync(connection.Topic(name), connection.Group("group A"), 1));
        Assert.Equal(2, (await context.Storage.GetMonitoringApi().GetMessagesAsync(new()
        {
            MessageType = MessageType.Subscribe,
            PageSize = 100
        })).Totals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumer_WithMultipleGroups_ReceivesIndependentCopies(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        var transport = context.Provider.GetRequiredService<ITransport>();
        var factory = context.Provider.GetRequiredService<IConsumerClientFactory>();
        Assert.True((await transport.SendAsync(Envelope("210", "groups", "body"))).Succeeded);
        foreach (string group in new[] { "one", "two" })
        {
            await using IConsumerClient consumer = await factory.CreateAsync(group, 1);
            await consumer.SubscribeAsync(["groups"]);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            int received = 0;
            consumer.OnMessageCallback = async (message, sender) =>
            {
                Assert.Equal("210", message.GetId());
                received++;
                await consumer.CommitAsync(sender);
                await cancellation.CancelAsync();
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.ListeningAsync(TimeSpan.FromSeconds(1), cancellation.Token));
            Assert.Equal(1, received);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumer_WithInvalidConcurrencyAndSubscriptions_RejectsAndReleasesGroup(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        var factory = context.Provider.GetRequiredService<IConsumerClientFactory>();
        await Assert.ThrowsAsync<NotSupportedException>(() => factory.CreateAsync("group", 2));
        await using IConsumerClient first = await factory.CreateAsync("group", 1);
        await using IConsumerClient second = await factory.CreateAsync("group", 1);
        await Assert.ThrowsAsync<NotSupportedException>(() => first.SubscribeAsync(["orders.*"]));
        await first.SubscribeAsync(["orders"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.SubscribeAsync(["orders"]));
        await first.DisposeAsync();
        await second.SubscribeAsync(["orders"]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        second.OnMessageCallback = (_, _) => Task.CompletedTask;
        Task listening = second.ListeningAsync(TimeSpan.FromSeconds(1), cancellation.Token);
        await second.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listening);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_WithOversizedEnvelope_ReturnsFailureWithoutPublish(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        var connection = context.Provider.GetRequiredService<CapMqConnection>();
        var transport = context.Provider.GetRequiredService<ITransport>();
        connection.Options.MaxEnvelopeBytes = 100;
        var result = await transport.SendAsync(Envelope("220", "oversized", new string('x', 200)));
        Assert.False(result.Succeeded);
        Assert.IsType<ArgumentOutOfRangeException>(result.Exception);
        Assert.Empty(await connection.Client.PullAsync(connection.Topic("oversized"), connection.Group("group"), 1));
    }

    private static TransportMessage Envelope(string id, string name, string body) => new(
        new Dictionary<string, string?> { [Headers.MessageId] = id, [Headers.MessageName] = name }, Encoding.UTF8.GetBytes(body));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_WithReopenedStore_PreservesMessagesAndGroupOffset(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        var transport = context.Provider.GetRequiredService<ITransport>();
        var connection = context.Provider.GetRequiredService<CapMqConnection>();
        Assert.Equal(0, await connection.Client.EnsureConsumerGroupAsync(connection.Topic("recovery"), connection.Group("slow-group")));
        Assert.Equal(0, await connection.Client.EnsureConsumerGroupAsync(connection.Topic("recovery"), connection.Group("slow-group")));
        Assert.True((await transport.SendAsync(Envelope("231", "recovery", "first"))).Succeeded);
        Assert.True((await transport.SendAsync(Envelope("232", "recovery", "second"))).Succeeded);
        var first = Assert.Single(await connection.Client.PullAsync(connection.Topic("recovery"), connection.Group("persisted-group"), 1));
        await connection.Client.AckAsync(connection.Topic("recovery"), connection.Group("persisted-group"), first.Offset);
        await context.ReopenStoreAsync();
        connection = context.Provider.GetRequiredService<CapMqConnection>();
        Assert.Equal(first.Offset + 1, await connection.Client.EnsureConsumerGroupAsync(connection.Topic("recovery"), connection.Group("persisted-group")));
        var second = Assert.Single(await connection.Client.PullAsync(connection.Topic("recovery"), connection.Group("persisted-group"), 1));
        Assert.Equal(first.Offset + 1, second.Offset);
        Assert.Equal(2, (await connection.Client.PullAsync(connection.Topic("recovery"), connection.Group("slow-group"), 10)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureConsumerGroup_WithConcurrentCallsAndCancellation_IsIdempotentAndRequiresWrite(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        var connection = context.Provider.GetRequiredService<CapMqConnection>();
        string topic = connection.Topic("ensure-group");
        string group = connection.Group("ensure-group");
        long[] offsets = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => connection.Client.EnsureConsumerGroupAsync(topic, group))));
        Assert.All(offsets, offset => Assert.Equal(0, offset));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.Client.EnsureConsumerGroupAsync(topic, "cancelled-group", cancellation.Token));
        Assert.DoesNotContain("cancelled-group", (await connection.Client.GetDiagnosticsAsync(topic)).ConsumerLag.Keys);
        if (remote)
        {
            using var reader = new SndbMqClient($"Data Source={context.BaseUrl}/captests;Token=cap-reader-test;Protocol=Rest");
            await Assert.ThrowsAnyAsync<Exception>(() => reader.EnsureConsumerGroupAsync(topic, "reader-group"));
            Assert.DoesNotContain("reader-group", (await connection.Client.GetDiagnosticsAsync(topic)).ConsumerLag.Keys);
        }
    }
}
