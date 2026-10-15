using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DotNetCore.CAP;
using DotNetCore.CAP.Persistence;
using Microsoft.Extensions.Options;
using SonnetDB.Data.Documents;
using SonnetDB.Exceptions;

namespace SonnetDB.CAP.Storage.Document;

internal sealed class CapDocumentStore : IStorageInitializer, IDisposable
{
    private readonly bool _ownsClient;
    private readonly string _version;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public CapDocumentStore(IOptions<SonnetDbCapDocumentOptions> options, IOptions<CapOptions> capOptions)
    {
        Options = options.Value;
        _version = capOptions.Value.Version;
        _ownsClient = Options.Client is null;
        Client = Options.Client ?? new SndbDocumentClient(Options.ConnectionString);
    }

    public SonnetDbCapDocumentOptions Options { get; }
    public SndbDocumentClient Client { get; }
    public SemaphoreSlim SchedulerGate { get; } = new(1, 1);
    public long Now => Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    public string Collection => Options.CollectionName;
    public string GetPublishedTableName() => Collection + ".published";
    public string GetReceivedTableName() => Collection + ".received";
    public string GetLockTableName() => Collection + ".locks";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;
            await Client.CreateCollectionAsync(Collection, cancellationToken: cancellationToken).ConfigureAwait(false);
            await Client.EnsureIndexAsync(Collection, "cap_bucket_v1", ["$.bucket"], cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _initializeGate.Release(); }
    }

    public string Bucket(string kind, string state) => $"{kind}:{_version}:{state}";

    public async IAsyncEnumerable<(SndbDocument Row, CapStoredMessage Message)> ReadBucketAsync(string kind, string state, [EnumeratorCancellation] CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        string? after = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            string prefix = $"cap:{kind}:";
            var predicates = new List<SndbDocumentFilter>
            {
                Field("$.bucket", "eq", Bucket(kind, state)),
                Field("_id", "gte", prefix), Field("_id", "lt", prefix + "~")
            };
            if (after is not null)
                predicates.Add(Field("_id", "gt", after));
            var filter = new SndbDocumentFilter(And: predicates);
            IReadOnlyList<SndbDocument> page = await Client.FindAsync(Collection,
                new SndbDocumentFindOptions(Filter: filter, Sort: [new SndbDocumentSort("_id")], Limit: 128), token).ConfigureAwait(false);
            if (page.Count == 0)
                yield break;
            foreach (SndbDocument row in page)
            {
                token.ThrowIfCancellationRequested();
                CapStoredMessage message = Read(row);
                if (message.Kind != kind || message.Version != _version || message.StatusName != state)
                    throw new InvalidDataException("CAP 消息 bucket 与元数据不一致。");
                yield return (row, message);
            }
            after = page[^1].Id;
        }
    }

    public CapStoredMessage Read(SndbDocument row)
    {
        CapStoredMessage message = JsonSerializer.Deserialize(row.Json, CapDocumentJsonContext.Default.CapStoredMessage)
            ?? throw new InvalidDataException("CAP 消息文档为空。");
        if (message.FormatVersion != 1 || message.Bucket != $"{message.Kind}:{message.Version}:{message.StatusName}")
            throw new InvalidDataException("CAP 消息文档版本或 bucket 不兼容。");
        return message;
    }

    public async Task<(SndbDocument Row, CapStoredMessage Message)> RequireMessageAsync(string kind, string id)
    {
        await InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        SndbDocument row = await Client.FindOneAsync(Collection, MessageKey(kind, id)).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("CAP 消息不存在。");
        CapStoredMessage message = Read(row);
        if (message.Kind != kind || message.Version != _version || message.DbId != id)
            throw new InvalidDataException("CAP 消息身份或版本不匹配。");
        return (row, message);
    }

    public async Task InsertAsync(CapStoredMessage message, object? transaction = null)
    {
        string id = MessageKey(message.Kind, message.DbId);
        string json = JsonSerializer.Serialize(message, CapDocumentJsonContext.Default.CapStoredMessage);
        if (transaction is null)
        {
            await InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            Check(await Client.InsertOneAsync(Collection, id, json).ConfigureAwait(false));
        }
        else
            RequireBatch(transaction).Add(new SndbDocumentBulkWriteOperation(SndbDocumentBulkWriteOperationType.InsertOne, id, json));
    }

    public async Task ReplaceAsync(SndbDocument row, CapStoredMessage message, object? transaction = null)
    {
        string json = JsonSerializer.Serialize(message, CapDocumentJsonContext.Default.CapStoredMessage);
        var operation = new SndbDocumentBulkWriteOperation(SndbDocumentBulkWriteOperationType.ReplaceOne, row.Id, json, ExpectedVersion: row.Version);
        if (transaction is null)
            Check(await Client.BulkWriteAsync(Collection, [operation]).ConfigureAwait(false));
        else
            RequireBatch(transaction).Add(operation);
    }

    public DocumentWriteBatch RequireBatch(object transaction)
    {
        if (transaction is not DocumentWriteBatch batch || !ReferenceEquals(batch.Owner, this))
            throw new NotSupportedException("事务必须来自当前 CAP 文档存储的单 collection 批提交，不能传入 SQL、EF 或 MongoDB session。");
        batch.ThrowIfCompleted();
        return batch;
    }

    public static void Check(SndbDocumentWriteResult result) => CheckErrors(result.Errors);
    public static void Check(SndbDocumentFindOneAndUpdateResult result) => CheckErrors(result.Errors);
    public static void Check(SndbDocumentBulkWriteResult result)
    {
        CheckErrors(result.Errors);
        if (!result.Committed)
            throw new SonnetDbCapStorageException("not_committed");
    }

    private static void CheckErrors(IReadOnlyList<SndbDocumentWriteError>? errors)
    {
        SndbDocumentWriteError? error = errors?.FirstOrDefault(item => item.Severity == SndbDocumentWriteErrorSeverity.Error);
        if (error is not null)
            throw new SonnetDbCapStorageException(error.Code);
    }

    public static string MessageKey(string kind, string id)
    {
        long value = long.Parse(id, NumberStyles.None, CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return $"cap:{kind}:{value.ToString("D19", CultureInfo.InvariantCulture)}";
    }

    public static SndbDocumentFilter Field(string path, string op, string value)
        => new(path, op, JsonSerializer.SerializeToElement(value, CapDocumentJsonContext.Default.String));
    public static SndbDocumentFilter Field(string path, string op, long value)
        => new(path, op, JsonSerializer.SerializeToElement(value, CapDocumentJsonContext.Default.Int64));

    public void Dispose()
    {
        SchedulerGate.Dispose();
        _initializeGate.Dispose();
        if (_ownsClient)
            Client.Dispose();
    }
}

internal sealed class DocumentWriteBatch(CapDocumentStore owner)
{
    private readonly List<SndbDocumentBulkWriteOperation> _operations = [];
    private readonly string _requestId = Guid.NewGuid().ToString("N");
    private bool _completed;
    private bool _commitStarted;
    public CapDocumentStore Owner { get; } = owner;

    public void Add(SndbDocumentBulkWriteOperation operation)
    {
        ThrowIfCompleted();
        if (_commitStarted)
            throw new InvalidOperationException("提交开始后不能更改文档事务；未知远程结果仅允许重试原批次。");
        if (_operations.Count >= 1000)
            throw new InvalidOperationException("文档事务最多包含 1000 个操作。");
        _operations.Add(operation);
    }

    public async Task CommitAsync(CancellationToken token)
    {
        ThrowIfCompleted();
        token.ThrowIfCancellationRequested();
        _commitStarted = true;
        await Owner.InitializeAsync(token).ConfigureAwait(false);
        if (_operations.Count != 0)
            CapDocumentStore.Check(await Owner.Client.BulkWriteAsync(Owner.Collection, _operations, ordered: true, requestId: _requestId, cancellationToken: token).ConfigureAwait(false));
        _completed = true;
        _operations.Clear();
    }

    public void Rollback()
    {
        ThrowIfCompleted();
        _completed = true;
        _operations.Clear();
    }

    public void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException("文档事务已结束。");
    }
}
