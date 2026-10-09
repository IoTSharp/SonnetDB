using System.Globalization;
using System.Text.Json;
using DotNetCore.CAP;
using DotNetCore.CAP.Internal;
using DotNetCore.CAP.Messages;
using DotNetCore.CAP.Monitoring;
using DotNetCore.CAP.Persistence;
using DotNetCore.CAP.Serialization;
using Microsoft.Extensions.Options;
using SonnetDB.Data.Documents;
using static SonnetDB.CAP.Storage.Document.CapDocumentStore;

namespace SonnetDB.CAP.Storage.Document;

internal sealed partial class SonnetDbDocumentStorage(
    CapDocumentStore store, IOptions<CapOptions> options, ISerializer serializer, ISnowflakeId snowflake) : IDataStorage, IMonitoringApi
{
    private readonly CapOptions _options = options.Value;
    private static readonly string[] MessageStates = Enum.GetNames<StatusName>();

    public Task<MediumMessage> StoreMessageAsync(string name, Message content, object? transaction = null)
        => StoreMessage("p", content.GetId(), name, null, content, transaction);

    public Task<MediumMessage> StoreReceivedMessageAsync(string name, string group, Message content)
        => StoreMessage("r", snowflake.NextId().ToString(CultureInfo.InvariantCulture), name, group, content, null);

    private async Task<MediumMessage> StoreMessage(string kind, string id, string name, string? group, Message content, object? transaction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        string json = serializer.Serialize(content);
        var record = new CapStoredMessage(1, kind, _options.Version, store.Bucket(kind, nameof(StatusName.Scheduled)), id,
            name, group, json, 0, store.Now, null, nameof(StatusName.Scheduled));
        await store.InsertAsync(record, transaction).ConfigureAwait(false);
        return ToMedium(record, content);
    }

    public async Task StoreReceivedExceptionMessageAsync(string name, string group, string content)
    {
        var record = new CapStoredMessage(1, "r", _options.Version, store.Bucket("r", nameof(StatusName.Failed)),
            snowflake.NextId().ToString(CultureInfo.InvariantCulture), name, group, content, _options.FailedRetryCount,
            store.Now, checked(store.Now + (long)_options.FailedMessageExpiredAfter * 1000), nameof(StatusName.Failed));
        await store.InsertAsync(record).ConfigureAwait(false);
    }

    public Task ChangePublishStateAsync(MediumMessage message, StatusName state, object? transaction = null)
        => ChangeState("p", message, state, transaction);

    public Task ChangeReceiveStateAsync(MediumMessage message, StatusName state)
        => ChangeState("r", message, state, null);

    private async Task ChangeState(string kind, MediumMessage message, StatusName state, object? transaction)
    {
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        var current = await store.RequireMessageAsync(kind, message.DbId).ConfigureAwait(false);
        await store.ReplaceAsync(current.Row, current.Message with
        {
            Content = serializer.Serialize(message.Origin),
            Retries = message.Retries,
            ExpiresAt = message.ExpiresAt.HasValue ? ToTimestamp(message.ExpiresAt.Value) : null,
            StatusName = state.ToString(),
            Bucket = store.Bucket(kind, state.ToString())
        }, transaction).ConfigureAwait(false);
    }

    public async Task ChangePublishStateToDelayedAsync(string[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (string[] chunk in ids.Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            var batch = new DocumentWriteBatch(store);
            foreach (string id in chunk)
            {
                var current = await store.RequireMessageAsync("p", id).ConfigureAwait(false);
                await store.ReplaceAsync(current.Row, current.Message with
                {
                    StatusName = nameof(StatusName.Delayed),
                    Bucket = store.Bucket("p", nameof(StatusName.Delayed))
                }, batch).ConfigureAwait(false);
            }
            await batch.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public Task<IEnumerable<MediumMessage>> GetPublishedMessagesOfNeedRetry(TimeSpan lookbackSeconds)
        => GetRetry("p", lookbackSeconds);

    public Task<IEnumerable<MediumMessage>> GetReceivedMessagesOfNeedRetry(TimeSpan lookbackSeconds)
        => GetRetry("r", lookbackSeconds);

    private async Task<IEnumerable<MediumMessage>> GetRetry(string kind, TimeSpan lookback)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lookback, TimeSpan.Zero);
        long cutoff = checked(store.Now - (long)lookback.TotalMilliseconds);
        var result = new List<MediumMessage>();
        foreach (string state in new[] { nameof(StatusName.Failed), nameof(StatusName.Scheduled) })
        {
            await foreach (var item in store.ReadBucketAsync(kind, state).ConfigureAwait(false))
            {
                if (item.Message.Added >= cutoff || item.Message.Retries >= _options.FailedRetryCount)
                    continue;
                result.Add(ToMedium(item.Message));
                if (result.Count == 200)
                    return result;
            }
        }
        return result;
    }

    public async Task ScheduleMessagesOfDelayedAsync(Func<object, IEnumerable<MediumMessage>, Task> scheduleTask, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scheduleTask);
        await store.SchedulerGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long now = store.Now;
            var messages = new List<MediumMessage>();
            foreach (string state in new[] { nameof(StatusName.Delayed), nameof(StatusName.Queued) })
            {
                await foreach (var item in store.ReadBucketAsync("p", state, token).ConfigureAwait(false))
                {
                    long cutoff = state == nameof(StatusName.Delayed) ? now + 120_000 : now - 60_000;
                    if (item.Message.ExpiresAt is not { } expires || expires >= cutoff)
                        continue;
                    messages.Add(ToMedium(item.Message));
                    if (messages.Count == Math.Clamp(_options.SchedulerBatchSize, 1, 1000))
                        break;
                }
                if (messages.Count == Math.Clamp(_options.SchedulerBatchSize, 1, 1000))
                    break;
            }
            if (messages.Count == 0)
                return;
            var batch = new DocumentWriteBatch(store);
            await scheduleTask(batch, messages).ConfigureAwait(false);
            await batch.CommitAsync(token).ConfigureAwait(false);
        }
        finally { store.SchedulerGate.Release(); }
    }

    public async Task<int> DeleteExpiresAsync(string table, DateTime timeout, int batchCount = 1000, CancellationToken token = default)
    {
        string kind = table == store.GetPublishedTableName() ? "p"
            : table == store.GetReceivedTableName() ? "r" : throw new ArgumentException("只能清理 CAP published/received 逻辑集合。", nameof(table));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchCount);
        long cutoff = ToTimestamp(timeout);
        var operations = new List<SndbDocumentBulkWriteOperation>();
        foreach (string state in new[] { nameof(StatusName.Succeeded), nameof(StatusName.Failed) })
        {
            await foreach (var item in store.ReadBucketAsync(kind, state, token).ConfigureAwait(false))
            {
                if (item.Message.ExpiresAt is not { } expires || expires >= cutoff
                    || (state == nameof(StatusName.Failed) && item.Message.Retries < _options.FailedRetryCount))
                    continue;
                operations.Add(new SndbDocumentBulkWriteOperation(SndbDocumentBulkWriteOperationType.DeleteOne, item.Row.Id, ExpectedVersion: item.Row.Version));
                if (operations.Count == Math.Min(batchCount, 1000))
                    break;
            }
            if (operations.Count == Math.Min(batchCount, 1000))
                break;
        }
        token.ThrowIfCancellationRequested();
        if (operations.Count == 0)
            return 0;
        SndbDocumentBulkWriteResult result = await store.Client.BulkWriteAsync(store.Collection, operations, cancellationToken: token).ConfigureAwait(false);
        Check(result);
        return result.Deleted;
    }

    public Task<int> DeleteReceivedMessageAsync(long id) => DeleteMessage("r", id);
    public Task<int> DeletePublishedMessageAsync(long id) => DeleteMessage("p", id);

    private async Task<int> DeleteMessage(string kind, long id)
    {
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        string key = MessageKey(kind, id.ToString(CultureInfo.InvariantCulture));
        SndbDocument? row = await store.Client.FindOneAsync(store.Collection, key).ConfigureAwait(false);
        if (row is null)
            return 0;
        await store.RequireMessageAsync(kind, id.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        SndbDocumentBulkWriteResult result = await store.Client.BulkWriteAsync(store.Collection,
            [new SndbDocumentBulkWriteOperation(SndbDocumentBulkWriteOperationType.DeleteOne, key, ExpectedVersion: row.Version)]).ConfigureAwait(false);
        Check(result);
        return result.Deleted;
    }

    public async Task<bool> AcquireLockAsync(string key, TimeSpan ttl, string instance, CancellationToken token = default)
    {
        ValidateLock(key, instance, token);
        await store.InitializeAsync(token).ConfigureAwait(false);
        long now = store.Now;
        long expires = Expiry(now, ttl);
        string id = "cap:lock:" + key;
        if (await store.Client.FindOneAsync(store.Collection, id, token).ConfigureAwait(false) is null)
        {
            SndbDocumentWriteResult insertion = await store.Client.InsertOneAsync(store.Collection, id,
                JsonSerializer.Serialize(new CapStoredLock(1, "", 0), CapDocumentJsonContext.Default.CapStoredLock), token).ConfigureAwait(false);
            if (insertion.Errors?.Any(error => error.Severity == SndbDocumentWriteErrorSeverity.Error && error.Code != SndbDocumentWriteErrorCodes.DuplicateKey) == true)
                Check(insertion);
        }
        SndbDocumentFindOneAndUpdateResult result = await store.Client.FindOneAndUpdateAsync(store.Collection,
            new SndbDocumentFindOneAndUpdateOptions(LockUpdate(instance, expires), Id: id, Filter: Field("$.expiresAt", "lte", now)), token).ConfigureAwait(false);
        Check(result);
        return result.Matched > 0;
    }

    public async Task ReleaseLockAsync(string key, string instance, CancellationToken token = default)
    {
        ValidateLock(key, instance, token);
        await store.InitializeAsync(token).ConfigureAwait(false);
        SndbDocumentFindOneAndUpdateResult result = await store.Client.FindOneAndUpdateAsync(store.Collection,
            new SndbDocumentFindOneAndUpdateOptions(LockUpdate("", 0), Id: "cap:lock:" + key, Filter: Field("$.instance", "eq", instance)), token).ConfigureAwait(false);
        Check(result);
    }

    public async Task RenewLockAsync(string key, TimeSpan ttl, string instance, CancellationToken token = default)
    {
        ValidateLock(key, instance, token);
        await store.InitializeAsync(token).ConfigureAwait(false);
        long now = store.Now;
        var filter = new SndbDocumentFilter(And: [Field("$.instance", "eq", instance), Field("$.expiresAt", "gt", now)]);
        SndbDocumentFindOneAndUpdateResult result = await store.Client.FindOneAndUpdateAsync(store.Collection,
            new SndbDocumentFindOneAndUpdateOptions(LockUpdate(instance, Expiry(now, ttl)), Id: "cap:lock:" + key, Filter: filter), token).ConfigureAwait(false);
        Check(result);
        if (result.Matched == 0)
            throw new InvalidOperationException("CAP 存储锁已失效或归属其他实例。");
    }

    private static void ValidateLock(string key, string instance, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
    }

    private static long Expiry(long now, TimeSpan ttl)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ttl, TimeSpan.FromMilliseconds(1));
        return checked(now + (long)ttl.TotalMilliseconds);
    }

    private static SndbDocumentUpdate LockUpdate(string instance, long expires) => new(Set: new Dictionary<string, JsonElement>
    {
        ["$.instance"] = JsonSerializer.SerializeToElement(instance, CapDocumentJsonContext.Default.String),
        ["$.expiresAt"] = JsonSerializer.SerializeToElement(expires, CapDocumentJsonContext.Default.Int64)
    });

    private MediumMessage ToMedium(CapStoredMessage record, Message? origin = null) => new()
    {
        DbId = record.DbId,
        Content = record.Content,
        Origin = origin ?? serializer.Deserialize(record.Content) ?? throw new InvalidDataException("CAP 消息正文无法反序列化。"),
        Added = FromTimestamp(record.Added),
        ExpiresAt = record.ExpiresAt.HasValue ? FromTimestamp(record.ExpiresAt.Value) : null,
        Retries = record.Retries
    };

    private static long ToTimestamp(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds();
    private static DateTime FromTimestamp(long time) => DateTimeOffset.FromUnixTimeMilliseconds(time).LocalDateTime;
    public IMonitoringApi GetMonitoringApi() => this;
}
