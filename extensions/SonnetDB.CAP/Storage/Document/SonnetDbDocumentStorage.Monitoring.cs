using System.Globalization;
using DotNetCore.CAP.Internal;
using DotNetCore.CAP.Messages;
using DotNetCore.CAP.Monitoring;
using DotNetCore.CAP.Persistence;

namespace SonnetDB.CAP.Storage.Document;

internal sealed partial class SonnetDbDocumentStorage
{
    public Task<MediumMessage?> GetPublishedMessageAsync(long id) => GetMessage("p", id);
    public Task<MediumMessage?> GetReceivedMessageAsync(long id) => GetMessage("r", id);

    private async Task<MediumMessage?> GetMessage(string kind, long id)
    {
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var row = await store.Client.FindOneAsync(store.Collection, CapDocumentStore.MessageKey(kind, id.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
        if (row is null)
            return null;
        CapStoredMessage record = store.Read(row);
        if (record.Version != _options.Version || record.Kind != kind)
            return null;
        return ToMedium(record);
    }

    public async Task<StatisticsDto> GetStatisticsAsync() => new()
    {
        PublishedSucceeded = await Count("p", nameof(StatusName.Succeeded)).ConfigureAwait(false),
        PublishedFailed = await Count("p", nameof(StatusName.Failed)).ConfigureAwait(false),
        PublishedDelayed = await Count("p", nameof(StatusName.Delayed)).ConfigureAwait(false),
        ReceivedSucceeded = await Count("r", nameof(StatusName.Succeeded)).ConfigureAwait(false),
        ReceivedFailed = await Count("r", nameof(StatusName.Failed)).ConfigureAwait(false)
    };

    private async Task<int> Count(string kind, string state)
    {
        int count = 0;
        await foreach (var item in store.ReadBucketAsync(kind, state).ConfigureAwait(false))
            count = checked(count + 1);
        return count;
    }

    public ValueTask<int> PublishedFailedCount() => new(Count("p", nameof(StatusName.Failed)));
    public ValueTask<int> PublishedSucceededCount() => new(Count("p", nameof(StatusName.Succeeded)));
    public ValueTask<int> ReceivedFailedCount() => new(Count("r", nameof(StatusName.Failed)));
    public ValueTask<int> ReceivedSucceededCount() => new(Count("r", nameof(StatusName.Succeeded)));

    public async Task<PagedQueryResult<MessageDto>> GetMessagesAsync(MessageQueryDto queryDto)
    {
        ArgumentNullException.ThrowIfNull(queryDto);
        ArgumentOutOfRangeException.ThrowIfNegative(queryDto.CurrentPage);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queryDto.PageSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(queryDto.PageSize, 1000);
        int skip = checked(queryDto.CurrentPage * queryDto.PageSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(skip, 10_000);
        string kind = queryDto.MessageType == MessageType.Publish ? "p" : "r";
        var heap = new PriorityQueue<CapStoredMessage, (long Added, long Id)>();
        int capacity = checked(skip + queryDto.PageSize);
        long count = 0;
        IEnumerable<string> states = string.IsNullOrEmpty(queryDto.StatusName) ? MessageStates : [queryDto.StatusName];
        foreach (string state in states)
        {
            await foreach (var item in store.ReadBucketAsync(kind, state).ConfigureAwait(false))
            {
                CapStoredMessage message = item.Message;
                if ((!string.IsNullOrEmpty(queryDto.Name) && message.Name != queryDto.Name)
                    || (!string.IsNullOrEmpty(queryDto.Group) && message.Group != queryDto.Group)
                    || (!string.IsNullOrEmpty(queryDto.Content) && !message.Content.Contains(queryDto.Content, StringComparison.Ordinal)))
                    continue;
                count++;
                heap.Enqueue(message, (message.Added, long.Parse(message.DbId, CultureInfo.InvariantCulture)));
                if (heap.Count > capacity)
                    heap.Dequeue();
            }
        }
        MessageDto[] messages = heap.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(item => item.Added).ThenByDescending(item => long.Parse(item.DbId, CultureInfo.InvariantCulture))
            .Skip(skip).Select(item => new MessageDto
            {
                Id = item.DbId,
                Version = item.Version,
                Group = item.Group,
                Name = item.Name,
                Content = item.Content,
                Added = FromTimestamp(item.Added),
                ExpiresAt = item.ExpiresAt.HasValue ? FromTimestamp(item.ExpiresAt.Value) : null,
                Retries = item.Retries,
                StatusName = item.StatusName
            }).ToArray();
        return new PagedQueryResult<MessageDto>
        {
            Items = messages,
            Totals = count,
            PageIndex = queryDto.CurrentPage,
            PageSize = queryDto.PageSize
        };
    }

    public Task<IDictionary<DateTime, int>> HourlySucceededJobs(MessageType type) => Hourly(type, nameof(StatusName.Succeeded));
    public Task<IDictionary<DateTime, int>> HourlyFailedJobs(MessageType type) => Hourly(type, nameof(StatusName.Failed));

    private async Task<IDictionary<DateTime, int>> Hourly(MessageType type, string state)
    {
        DateTime now = FromTimestamp(store.Now);
        DateTime hour = new(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        var result = Enumerable.Range(0, 24).ToDictionary(offset => hour.AddHours(-offset), _ => 0);
        await foreach (var item in store.ReadBucketAsync(type == MessageType.Publish ? "p" : "r", state).ConfigureAwait(false))
        {
            DateTime added = FromTimestamp(item.Message.Added);
            DateTime key = new(added.Year, added.Month, added.Day, added.Hour, 0, 0);
            if (result.TryGetValue(key, out int count))
                result[key] = checked(count + 1);
        }
        return result;
    }
}
