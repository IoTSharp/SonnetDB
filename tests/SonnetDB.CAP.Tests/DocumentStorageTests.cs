using DotNetCore.CAP;
using DotNetCore.CAP.Internal;
using DotNetCore.CAP.Messages;
using DotNetCore.CAP.Monitoring;
using DotNetCore.CAP.Persistence;
using DotNetCore.CAP.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB.CAP.Storage.Document;
using SonnetDB.Data.Documents;
using SonnetDB.Exceptions;
using Xunit;

namespace SonnetDB.CAP.Tests;

public sealed class DocumentStorageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreMessage_WithPublishReceiveAndReopen_PreservesRecords(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        MediumMessage published = await context.Storage.StoreMessageAsync("orders.created", CapTestContext.Message("100", body: "中文正文"));
        MediumMessage received = await context.Storage.StoreReceivedMessageAsync("orders.created", "group-A", published.Origin);
        published.Retries = 1;
        published.ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(context.Backend.Now + 60_000).LocalDateTime;
        await context.Storage.ChangePublishStateAsync(published, StatusName.Failed);
        await context.ReopenStoreAsync();
        MediumMessage restored = Assert.IsType<MediumMessage>(await context.Storage.GetMonitoringApi().GetPublishedMessageAsync(100));
        Assert.Equal(published.Content, restored.Content);
        Assert.Equal(1, restored.Retries);
        Assert.NotNull(restored.ExpiresAt);
        Assert.NotNull(await context.Storage.GetMonitoringApi().GetReceivedMessageAsync(long.Parse(received.DbId)));
        Assert.Equal(1, await context.Storage.GetMonitoringApi().PublishedFailedCount());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetRetry_WithStatesAndThreshold_ReturnsOnlyEligibleMessages(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        MediumMessage pending = await context.Storage.StoreMessageAsync("orders.created", CapTestContext.Message("101"));
        MediumMessage exhausted = await context.Storage.StoreMessageAsync("orders.created", CapTestContext.Message("102"));
        MediumMessage succeeded = await context.Storage.StoreMessageAsync("orders.created", CapTestContext.Message("103"));
        exhausted.Retries = 3;
        await context.Storage.ChangePublishStateAsync(exhausted, StatusName.Failed);
        await context.Storage.ChangePublishStateAsync(succeeded, StatusName.Succeeded);
        context.Clock.Advance(TimeSpan.FromMinutes(5));
        MediumMessage retry = Assert.Single(await context.Storage.GetPublishedMessagesOfNeedRetry(TimeSpan.FromMinutes(4)));
        Assert.Equal(pending.DbId, retry.DbId);
        Assert.Empty(await context.Storage.GetReceivedMessagesOfNeedRetry(TimeSpan.Zero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteExpires_WithPendingAndTerminalMessages_DeletesOnlyExpiredTerminalRecords(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        MediumMessage pending = await context.Storage.StoreMessageAsync("orders.created", CapTestContext.Message("111"));
        MediumMessage succeeded = await context.Storage.StoreMessageAsync("orders.created", CapTestContext.Message("112"));
        MediumMessage failed = await context.Storage.StoreMessageAsync("orders.created", CapTestContext.Message("113"));
        MediumMessage retryable = await context.Storage.StoreMessageAsync("orders.created", CapTestContext.Message("114"));
        foreach (MediumMessage message in new[] { pending, succeeded, failed })
            message.ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(context.Backend.Now - 1000).LocalDateTime;
        await context.Storage.ChangePublishStateAsync(pending, StatusName.Scheduled);
        await context.Storage.ChangePublishStateAsync(succeeded, StatusName.Succeeded);
        failed.Retries = 3;
        await context.Storage.ChangePublishStateAsync(failed, StatusName.Failed);
        retryable.ExpiresAt = pending.ExpiresAt;
        await context.Storage.ChangePublishStateAsync(retryable, StatusName.Failed);
        IStorageInitializer initializer = context.Provider.GetRequiredService<IStorageInitializer>();
        DateTime cutoff = DateTimeOffset.FromUnixTimeMilliseconds(context.Backend.Now).LocalDateTime;
        Assert.Equal(1, await context.Storage.DeleteExpiresAsync(initializer.GetPublishedTableName(), cutoff, 1));
        Assert.Equal(1, await context.Storage.DeleteExpiresAsync(initializer.GetPublishedTableName(), cutoff));
        Assert.NotNull(await context.Storage.GetMonitoringApi().GetPublishedMessageAsync(111));
        Assert.NotNull(await context.Storage.GetMonitoringApi().GetPublishedMessageAsync(114));
        await Assert.ThrowsAsync<ArgumentException>(() => context.Storage.DeleteExpiresAsync("business", cutoff));
        Assert.Equal(1, await context.Storage.DeletePublishedMessageAsync(111));
        Assert.Equal(0, await context.Storage.DeletePublishedMessageAsync(111));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageLock_WithConcurrentOwnersRenewalAndExpiry_HasOneOwner(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        bool[] acquired = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            context.Storage.AcquireLockAsync("concurrent", TimeSpan.FromMinutes(1), "owner-" + index))));
        Assert.Single(acquired, value => value);
        string owner = "owner-" + Array.FindIndex(acquired, value => value);
        await context.Storage.ReleaseLockAsync("concurrent", "wrong-owner");
        Assert.False(await context.Storage.AcquireLockAsync("concurrent", TimeSpan.FromMinutes(1), "next"));
        context.Clock.Advance(TimeSpan.FromSeconds(30));
        await context.Storage.RenewLockAsync("concurrent", TimeSpan.FromMinutes(1), owner);
        context.Clock.Advance(TimeSpan.FromSeconds(40));
        Assert.False(await context.Storage.AcquireLockAsync("concurrent", TimeSpan.FromMinutes(1), "next"));
        context.Clock.Advance(TimeSpan.FromSeconds(21));
        Assert.True(await context.Storage.AcquireLockAsync("concurrent", TimeSpan.FromMinutes(1), "next"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Storage.RenewLockAsync("concurrent", TimeSpan.FromMinutes(1), owner));
        await context.Storage.ReleaseLockAsync("concurrent", "next");
        Assert.True(await context.Storage.AcquireLockAsync("concurrent", TimeSpan.FromMinutes(1), "last"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedScheduler_WithCallbackFailure_RollsBackQueuedState(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        MediumMessage message = await context.Storage.StoreMessageAsync("orders.created", CapTestContext.Message("120"));
        message.ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(context.Backend.Now + 30_000).LocalDateTime;
        await context.Storage.ChangePublishStateAsync(message, StatusName.Delayed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Storage.ScheduleMessagesOfDelayedAsync(async (transaction, messages) =>
        {
            await context.Storage.ChangePublishStateAsync(Assert.Single(messages), StatusName.Queued, transaction);
            throw new InvalidOperationException("injected scheduler failure");
        }));
        Assert.Equal(1, (await context.Storage.GetMonitoringApi().GetStatisticsAsync()).PublishedDelayed);
        await context.Storage.ScheduleMessagesOfDelayedAsync(async (transaction, messages) =>
            await context.Storage.ChangePublishStateAsync(Assert.Single(messages), StatusName.Queued, transaction));
        Assert.Equal(0, (await context.Storage.GetMonitoringApi().GetStatisticsAsync()).PublishedDelayed);
        int scheduled = 0;
        await context.Storage.ScheduleMessagesOfDelayedAsync((_, messages) => { scheduled += messages.Count(); return Task.CompletedTask; });
        Assert.Equal(0, scheduled);
        context.Clock.Advance(TimeSpan.FromMinutes(2));
        await context.Storage.ScheduleMessagesOfDelayedAsync((_, messages) => { scheduled += messages.Count(); return Task.CompletedTask; });
        Assert.Equal(1, scheduled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Monitoring_WithMoreThanOnePage_FiltersAndPaginatesWithoutDuplicates(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        for (int index = 1; index <= 140; index++)
            await context.Storage.StoreMessageAsync("bulk", CapTestContext.Message((1000 + index).ToString(), "bulk", "search-text"));
        PagedQueryResult<MessageDto> first = await context.Storage.GetMonitoringApi().GetMessagesAsync(new MessageQueryDto
        {
            MessageType = MessageType.Publish,
            Name = "bulk",
            Content = "search-text",
            CurrentPage = 0,
            PageSize = 100
        });
        PagedQueryResult<MessageDto> second = await context.Storage.GetMonitoringApi().GetMessagesAsync(new MessageQueryDto
        {
            MessageType = MessageType.Publish,
            Name = "bulk",
            CurrentPage = 1,
            PageSize = 100
        });
        Assert.Equal(140, first.Totals);
        Assert.Equal(100, first.Items!.Count);
        Assert.Equal(40, second.Items!.Count);
        Assert.Equal(140, first.Items.Concat(second.Items).Select(item => item.Id).Distinct().Count());
        Assert.Equal("1140", first.Items[0].Id);
        Assert.Equal(24, (await context.Storage.GetMonitoringApi().HourlyFailedJobs(MessageType.Publish)).Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => context.Storage.GetMonitoringApi().GetMessagesAsync(new MessageQueryDto { PageSize = 1001 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocumentTransaction_WithCommitRollbackAndConflict_PreservesAtomicity(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        ICapPublisher publisher = context.Provider.GetRequiredService<ICapPublisher>();
        using (var transaction = publisher.BeginSonnetDbDocumentTransaction())
        {
            transaction.InsertBusinessDocument("order-1", """{"amount":42}""");
            transaction.InsertBusinessDocument("bucket-overlap", """{"bucket":"p:v1:Scheduled"}""");
            await publisher.PublishAsync("orders.created", "order-1");
            Assert.Null(await context.Backend.Client.FindOneAsync("cap", SonnetDbDocumentCapTransaction.GetBusinessDocumentId("order-1")));
            Assert.Equal(0, (await context.Storage.GetMonitoringApi().GetMessagesAsync(new MessageQueryDto { MessageType = MessageType.Publish, PageSize = 100 })).Totals);
            await transaction.CommitAsync();
        }
        Assert.Null(publisher.Transaction);
        Assert.NotNull(await context.Backend.Client.FindOneAsync("cap", SonnetDbDocumentCapTransaction.GetBusinessDocumentId("order-1")));
        using (var transaction = publisher.BeginSonnetDbDocumentTransaction())
        {
            transaction.InsertBusinessDocument("order-rollback", "{}");
            await publisher.PublishAsync("orders.created", "rollback");
            await transaction.RollbackAsync();
        }
        Assert.Null(await context.Backend.Client.FindOneAsync("cap", SonnetDbDocumentCapTransaction.GetBusinessDocumentId("order-rollback")));
        using (var transaction = publisher.BeginSonnetDbDocumentTransaction())
        {
            transaction.InsertBusinessDocument("order-1", "{}");
            transaction.InsertBusinessDocument("order-conflict", "{}");
            await publisher.PublishAsync("orders.created", "conflict");
            await Assert.ThrowsAsync<SonnetDbCapStorageException>(() => transaction.CommitAsync());
        }
        Assert.Null(await context.Backend.Client.FindOneAsync("cap", SonnetDbDocumentCapTransaction.GetBusinessDocumentId("order-conflict")));
        Assert.Equal(1, (await context.Storage.GetMonitoringApi().GetMessagesAsync(new MessageQueryDto { MessageType = MessageType.Publish, PageSize = 100 })).Totals);
        await Assert.ThrowsAsync<NotSupportedException>(() => context.Storage.StoreMessageAsync("orders.created", CapTestContext.Message("999"), new object()));
        Assert.Null(await context.Storage.GetMonitoringApi().GetPublishedMessageAsync(999));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureIndex_WithRepetitionAndDifferentDefinition_IsIdempotentAndRejectsDrift(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        Assert.Equal("cap_bucket_v1", await context.Backend.Client.EnsureIndexAsync("cap", "cap_bucket_v1", ["$.bucket"]));
        await Assert.ThrowsAnyAsync<Exception>(() => context.Backend.Client.EnsureIndexAsync("cap", "cap_bucket_v1", ["$.other"]));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Storage.AcquireLockAsync("cancel", TimeSpan.FromMinutes(1), "owner", cancellation.Token));
        if (remote)
        {
            using var reader = new SndbDocumentClient($"Data Source={context.BaseUrl}/captests;Token=cap-reader-test;Protocol=Rest");
            await Assert.ThrowsAnyAsync<Exception>(() => reader.EnsureIndexAsync("cap", "reader-index", ["$.name"]));
            using var http = new HttpClient { BaseAddress = new Uri(context.BaseUrl!) };
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "cap-admin-test");
            using var request = new StringContent("""{"name":"null-paths","paths":null}""", System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("/v1/db/captests/documents/cap/indexes/ensure", request);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreReceivedException_WithThresholdAndDelayedBatch_PreservesErrorAndState(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        await context.Storage.StoreReceivedExceptionMessageAsync("broken", "group", "malformed-content");
        MessageDto exception = Assert.Single((await context.Storage.GetMonitoringApi().GetMessagesAsync(new MessageQueryDto
        {
            MessageType = MessageType.Subscribe,
            StatusName = "Failed",
            PageSize = 100
        })).Items!);
        Assert.Equal(3, exception.Retries);
        Assert.Equal("malformed-content", exception.Content);
        Assert.NotNull(exception.ExpiresAt);
        Assert.Empty(await context.Storage.GetReceivedMessagesOfNeedRetry(TimeSpan.Zero));
        await context.Storage.StoreMessageAsync("delayed", CapTestContext.Message("301", "delayed"));
        await context.Storage.StoreMessageAsync("delayed", CapTestContext.Message("302", "delayed"));
        await context.Storage.ChangePublishStateToDelayedAsync(["301", "302", "301"]);
        Assert.Equal(2, (await context.Storage.GetMonitoringApi().GetStatisticsAsync()).PublishedDelayed);
        var message = Assert.IsType<MediumMessage>(await context.Storage.GetMonitoringApi().GetPublishedMessageAsync(301));
        await context.Storage.ChangePublishStateAsync(message, StatusName.Succeeded);
        Assert.Equal(1, (await context.Storage.GetMonitoringApi().HourlySucceededJobs(MessageType.Publish)).Values.Sum());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageVersion_WithExistingRecords_DoesNotRetryOrMonitorOtherVersion(bool remote)
    {
        await using var context = await CapTestContext.CreateAsync(remote);
        await context.Storage.StoreMessageAsync("orders.created", CapTestContext.Message("190"));
        using var backend = new CapDocumentStore(Options.Create(new SonnetDbCapDocumentOptions { Client = context.Backend.Client }), Options.Create(new CapOptions { Version = "v2" }));
        var storage = new SonnetDbDocumentStorage(backend, Options.Create(new CapOptions { Version = "v2" }),
            context.Provider.GetRequiredService<ISerializer>(), context.Provider.GetRequiredService<ISnowflakeId>());
        Assert.Null(await storage.GetMonitoringApi().GetPublishedMessageAsync(190));
        Assert.Empty(await storage.GetPublishedMessagesOfNeedRetry(TimeSpan.Zero));
        Assert.Equal(0, (await storage.GetMonitoringApi().GetMessagesAsync(new MessageQueryDto { MessageType = MessageType.Publish, PageSize = 100 })).Totals);
    }
}
