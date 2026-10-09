using DotNetCore.CAP;
using DotNetCore.CAP.Transport;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Data.Documents;

namespace SonnetDB.CAP.Storage.Document;

/// <summary>将业务文档和 Outbox 在同一 collection 中原子批提交，成功后才投递 CAP 消息。</summary>
public sealed class SonnetDbDocumentCapTransaction : CapTransactionBase
{
    private readonly DocumentWriteBatch _batch;
    private readonly ICapPublisher _publisher;
    private bool _disposed;

    internal SonnetDbDocumentCapTransaction(ICapPublisher publisher, CapDocumentStore store, IDispatcher dispatcher)
        : base(dispatcher)
    {
        _publisher = publisher;
        _batch = new DocumentWriteBatch(store);
        DbTransaction = _batch;
    }

    /// <summary>当前业务与消息批提交使用的 collection。</summary>
    public string CollectionName => _batch.Owner.Options.CollectionName;

    /// <summary>取得业务文档在共享 collection 中的实际 ID，避免与 CAP 内部记录冲突。</summary>
    /// <param name="id">应用业务 ID。</param>
    /// <returns>实际文档 ID。</returns>
    public static string GetBusinessDocumentId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return "business:" + id;
    }

    /// <summary>将业务文档插入暂存到当前原子批次，不立即写入。</summary>
    /// <param name="id">应用业务 ID。</param>
    /// <param name="json">业务 JSON 文档。</param>
    public void InsertBusinessDocument(string id, string json)
        => _batch.Add(new SndbDocumentBulkWriteOperation(SndbDocumentBulkWriteOperationType.InsertOne, GetBusinessDocumentId(id), json));

    /// <summary>将业务文档替换暂存到当前原子批次。</summary>
    /// <param name="id">应用业务 ID。</param>
    /// <param name="json">业务 JSON 文档。</param>
    /// <param name="expectedVersion">原文档预期版本，冲突时整个批次失败。</param>
    public void ReplaceBusinessDocument(string id, string json, long expectedVersion)
        => _batch.Add(new SndbDocumentBulkWriteOperation(SndbDocumentBulkWriteOperationType.ReplaceOne, GetBusinessDocumentId(id), json, ExpectedVersion: expectedVersion));

    /// <summary>将业务文档删除暂存到当前原子批次。</summary>
    /// <param name="id">应用业务 ID。</param>
    /// <param name="expectedVersion">原文档预期版本。</param>
    public void DeleteBusinessDocument(string id, long expectedVersion)
        => _batch.Add(new SndbDocumentBulkWriteOperation(SndbDocumentBulkWriteOperationType.DeleteOne, GetBusinessDocumentId(id), ExpectedVersion: expectedVersion));

    /// <inheritdoc />
    public override void Commit() => CommitAsync().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _batch.CommitAsync(cancellationToken).ConfigureAwait(false);
        await FlushAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void Rollback()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _batch.Rollback();
    }

    /// <inheritdoc />
    public override Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Rollback();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (ReferenceEquals(_publisher.Transaction, this))
            _publisher.Transaction = null;
        DbTransaction = null;
    }
}

/// <summary>CAP 的单 collection 文档事务扩展。</summary>
public static class SonnetDbDocumentCapTransactionExtensions
{
    /// <summary>创建业务文档与 CAP Outbox 共用的有界原子批次。</summary>
    /// <param name="publisher">CAP 发布器。</param>
    /// <returns>需要显式提交或回滚并释放的事务。</returns>
    public static SonnetDbDocumentCapTransaction BeginSonnetDbDocumentTransaction(this ICapPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        if (publisher.Transaction is not null)
            throw new InvalidOperationException("当前上下文已有 CAP 事务。");
        var transaction = new SonnetDbDocumentCapTransaction(publisher,
            publisher.ServiceProvider.GetRequiredService<CapDocumentStore>(),
            publisher.ServiceProvider.GetRequiredService<IDispatcher>());
        publisher.Transaction = transaction;
        return transaction;
    }
}
