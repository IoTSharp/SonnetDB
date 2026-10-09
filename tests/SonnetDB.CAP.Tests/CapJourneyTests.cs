using DotNetCore.CAP;
using DotNetCore.CAP.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.CAP.Storage.Document;
using Xunit;

namespace SonnetDB.CAP.Tests;

public sealed class CapJourneyTests
{
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
