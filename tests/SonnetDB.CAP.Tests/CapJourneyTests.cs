using DotNetCore.CAP;
using DotNetCore.CAP.Internal;
using DotNetCore.CAP.Messages;
using DotNetCore.CAP.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SonnetDB.CAP.Storage.Document;
using Xunit;

namespace SonnetDB.CAP.Tests;

public sealed class CapJourneyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_WithCancelledStartupWait_RollsBackWithoutPersisting(bool remote)
    {
        var startup = new CapStartupGate();
        await using var context = await CapTestContext.CreateAsync(remote, startCap: true, startup: startup);
        try
        {
            await startup.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await context.Backend.InitializeAsync(CancellationToken.None);
            ICapPublisher publisher = context.Provider.GetRequiredService<ICapPublisher>();
            using var transaction = publisher.BeginSonnetDbDocumentTransaction();
            transaction.InsertBusinessDocument("cancelled-startup", """{"amount":42}""");
            await publisher.PublishAsync("cap.journey", "cancelled startup");
            using var cancellation = new CancellationTokenSource();

            Task commit = transaction.CommitAsync(cancellation.Token);
            Assert.False(commit.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit.WaitAsync(TimeSpan.FromSeconds(5)));
            await transaction.RollbackAsync();
            startup.Release.TrySetResult();
            var bootstrapper = Assert.IsAssignableFrom<BackgroundService>(context.Provider.GetRequiredService<IBootstrapper>());
            await bootstrapper.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Null(await context.Backend.Client.FindOneAsync("cap",
                SonnetDbDocumentCapTransaction.GetBusinessDocumentId("cancelled-startup")));
            Assert.Equal(0, (await context.Storage.GetMonitoringApi().GetMessagesAsync(new MessageQueryDto
            {
                MessageType = MessageType.Publish,
                PageSize = 1
            })).Totals);
            Assert.False(context.Provider.GetRequiredService<JourneySubscriber>().Received.Task.IsCompleted);
        }
        finally
        {
            startup.Release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_WithFaultedStartup_PropagatesErrorAndAllowsRollback(bool remote)
    {
        var startup = new CapStartupGate();
        await using var context = await CapTestContext.CreateAsync(remote, startCap: true, startup: startup);
        try
        {
            await startup.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await context.Backend.InitializeAsync(CancellationToken.None);
            ICapPublisher publisher = context.Provider.GetRequiredService<ICapPublisher>();
            using var transaction = publisher.BeginSonnetDbDocumentTransaction();
            transaction.InsertBusinessDocument("faulted-startup", """{"amount":42}""");
            await publisher.PublishAsync("cap.journey", "faulted startup");

            Task commit = transaction.CommitAsync();
            Assert.False(commit.IsCompleted);
            var failure = new InvalidOperationException("CAP startup test failure.");
            startup.Release.TrySetException(failure);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => commit.WaitAsync(TimeSpan.FromSeconds(5))));
            await transaction.RollbackAsync();

            Assert.Null(await context.Backend.Client.FindOneAsync("cap",
                SonnetDbDocumentCapTransaction.GetBusinessDocumentId("faulted-startup")));
            Assert.Equal(0, (await context.Storage.GetMonitoringApi().GetMessagesAsync(new MessageQueryDto
            {
                MessageType = MessageType.Publish,
                PageSize = 1
            })).Totals);
            Assert.False(context.Provider.GetRequiredService<JourneySubscriber>().Received.Task.IsCompleted);
        }
        finally
        {
            startup.Release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_WithUnstartedCap_PersistsScheduledOutboxWithoutStartingHost(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        var bootstrapper = Assert.IsAssignableFrom<BackgroundService>(context.Provider.GetRequiredService<IBootstrapper>());
        Assert.Null(bootstrapper.ExecuteTask);
        ICapPublisher publisher = context.Provider.GetRequiredService<ICapPublisher>();
        using var transaction = publisher.BeginSonnetDbDocumentTransaction();
        transaction.InsertBusinessDocument("unstarted-host", """{"amount":42}""");
        await publisher.PublishAsync("cap.journey", "unstarted host");

        await transaction.CommitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(bootstrapper.ExecuteTask);
        Assert.NotNull(await context.Backend.Client.FindOneAsync("cap",
            SonnetDbDocumentCapTransaction.GetBusinessDocumentId("unstarted-host")));
        var scheduled = await context.Storage.GetMonitoringApi().GetMessagesAsync(new MessageQueryDto
        {
            MessageType = MessageType.Publish,
            StatusName = nameof(StatusName.Scheduled),
            PageSize = 1
        });
        Assert.Equal(1, scheduled.Totals);
        Assert.Equal(nameof(StatusName.Scheduled), Assert.Single(scheduled.Items!).StatusName);
        Assert.Equal(0, (await context.Storage.GetMonitoringApi().GetMessagesAsync(new MessageQueryDto
        {
            MessageType = MessageType.Subscribe,
            PageSize = 1
        })).Totals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAsync_BeforeCapStartupCompletes_WaitsAndDelivers(bool remote)
    {
        var startup = new CapStartupGate();
        await using var context = await CapTestContext.CreateAsync(remote, startCap: true, startup: startup);
        try
        {
            await startup.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await context.Backend.InitializeAsync(CancellationToken.None);
            ICapPublisher publisher = context.Provider.GetRequiredService<ICapPublisher>();
            JourneySubscriber subscriber = context.Provider.GetRequiredService<JourneySubscriber>();
            using var transaction = publisher.BeginSonnetDbDocumentTransaction();
            transaction.InsertBusinessDocument("startup-order", """{"amount":42}""");
            await publisher.PublishAsync("cap.journey", "startup journey");

            Task commit = transaction.CommitAsync();
            Assert.False(commit.IsCompleted);
            Assert.False(subscriber.Received.Task.IsCompleted);
            Assert.Null(await context.Backend.Client.FindOneAsync("cap",
                SonnetDbDocumentCapTransaction.GetBusinessDocumentId("startup-order")));

            startup.Release.TrySetResult();
            await commit.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("startup journey", await subscriber.Received.Task.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.NotNull(await context.Backend.Client.FindOneAsync("cap",
                SonnetDbDocumentCapTransaction.GetBusinessDocumentId("startup-order")));
        }
        finally
        {
            startup.Release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_WithCommittedDocumentTransaction_DeliversAndPersistsBothStates(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote, startCap: true);
        ICapPublisher publisher = context.Provider.GetRequiredService<ICapPublisher>();
        JourneySubscriber subscriber = context.Provider.GetRequiredService<JourneySubscriber>();
        using (var transaction = publisher.BeginSonnetDbDocumentTransaction())
        {
            transaction.InsertBusinessDocument("journey-order", """{"amount":42}""");
            await publisher.PublishAsync("cap.journey", "中文 journey");
            Assert.False(subscriber.Received.Task.IsCompleted);
            await transaction.CommitAsync();
        }
        Assert.Equal("中文 journey", await subscriber.Received.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.NotNull(await context.Backend.Client.FindOneAsync("cap", SonnetDbDocumentCapTransaction.GetBusinessDocumentId("journey-order")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        StatisticsDto statistics;
        do
        {
            deadline.Token.ThrowIfCancellationRequested();
            statistics = await context.Storage.GetMonitoringApi().GetStatisticsAsync();
            if (statistics.PublishedSucceeded != 1 || statistics.ReceivedSucceeded != 1)
                await Task.Delay(25, deadline.Token);
        } while (statistics.PublishedSucceeded != 1 || statistics.ReceivedSucceeded != 1);
        Assert.Equal(0, statistics.PublishedFailed);
        Assert.Equal(0, statistics.ReceivedFailed);
    }
}
