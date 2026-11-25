using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using SonnetDB.Data;

namespace SonnetDB.EntityFrameworkCore.Update.Internal;

internal sealed class SonnetDbDmlModificationCommandBatch(
    ModificationCommandBatchFactoryDependencies dependencies,
    int capacity) : ModificationCommandBatch
{
    private readonly List<IReadOnlyModificationCommand> _commands = new(capacity);
    private readonly List<SonnetDbModificationCommandBatch> _singletons = new(capacity);
    private bool? _moreBatchesExpected;

    public override IReadOnlyList<IReadOnlyModificationCommand> ModificationCommands => _commands;

    public override bool RequiresTransaction => _singletons.Count > 1
        || (_singletons.Count == 1 && _singletons[0].RequiresTransaction);

    public override bool AreMoreBatchesExpected => _moreBatchesExpected!.Value;

    public override bool TryAddCommand(IReadOnlyModificationCommand modificationCommand)
    {
        if (_moreBatchesExpected.HasValue)
            throw new InvalidOperationException("EF Core 修改命令批次已完成。");
        if (_commands.Count == capacity
            || (_singletons.Count != 0 && (!_singletons[0].SupportsDmlBatch || modificationCommand.StoreStoredProcedure is not null)))
            return false;

        var singleton = new SonnetDbModificationCommandBatch(dependencies);
        try
        {
            if (!singleton.TryAddCommand(modificationCommand))
                return false;
        }
        catch (NotSupportedException) when (_commands.Count > 0)
        {
            // 让 EF 先执行前缀，再由单条命令报告 SQL 生成错误。
            foreach (var column in modificationCommand.ColumnModifications)
                column.ResetParameterNames();
            return false;
        }

        _commands.Add(modificationCommand);
        _singletons.Add(singleton);
        return true;
    }

    public override void Complete(bool moreBatchesExpected)
    {
        _moreBatchesExpected = moreBatchesExpected;
        for (var index = 0; index < _singletons.Count; index++)
            _singletons[index].Complete(moreBatchesExpected || index < _singletons.Count - 1);
    }

    public override void Execute(IRelationalConnection connection)
        => ExecuteCoreAsync(connection, asynchronous: false, CancellationToken.None).GetAwaiter().GetResult();

    public override Task ExecuteAsync(
        IRelationalConnection connection,
        CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(connection, asynchronous: true, cancellationToken);

    private async Task ExecuteCoreAsync(
        IRelationalConnection connection, bool asynchronous, CancellationToken cancellationToken)
    {
        if (_singletons.Count < 2
            || connection.CurrentTransaction?.GetDbTransaction() is not SndbTransaction { SupportsDmlBatch: true } transaction
            || _singletons.Any(static singleton => !singleton.SupportsDmlBatch))
        {
            await ExecuteSingletonsAsync(connection, 0, _singletons.Count, asynchronous, cancellationToken).ConfigureAwait(false);
            return;
        }

        var commands = new SndbCommand?[_singletons.Count];
        var statements = new SndbDmlBatchStatement[_singletons.Count];
        var current = 0;
        try
        {
            var prepared = 0;
            ExceptionDispatchInfo? preparationFailure = null;
            for (; prepared < commands.Length; prepared++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var command = _singletons[prepared].CreateBatchCommand(connection, Guid.NewGuid());
                    commands[prepared] = command;
                    statements[prepared] = new SndbDmlBatchStatement(command.CommandText, command.Parameters, 1, command.CommandTimeout);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not UnreachableException)
                {
                    // 参数绑定错误的条目保留在当前位置，先处理已准备的有效前缀。
                    preparationFailure = ExceptionDispatchInfo.Capture(exception);
                    break;
                }
            }

            for (; current < prepared;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = statements.AsSpan(current, prepared - current).ToArray();
                var results = await ExecuteRequestAsync(connection, transaction, remaining, asynchronous, cancellationToken)
                    .ConfigureAwait(false);
                if (results is null)
                {
                    await ExecuteSingletonsAsync(connection, current, prepared, asynchronous, cancellationToken).ConfigureAwait(false);
                    current = prepared;
                    break;
                }

                try
                {
                    if (results.Count == 0
                        || results.Count > remaining.Length
                        || (results.Count < remaining.Length
                            && results[^1] is { Error: null, Reader.RecordsAffected: 1 }))
                        throw new InvalidDataException("SonnetDB 修改命令批次返回无效的结果数量。");

                    foreach (var result in results)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (result.Error is not null)
                            throw result.Error;
                        if (result.Reader is null)
                            throw new InvalidDataException("SonnetDB 修改命令批次未返回修改结果。");

                        if (asynchronous)
                        {
                            await _singletons[current].ConsumeBatchResultAsync(
                                connection, commands[current]!, result.Reader, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            _singletons[current].ConsumeBatchResult(connection, commands[current]!, result.Reader);
                        }
                        current++;
                    }
                    // 服务器在并发不符时停止。只有 EF 拦截器抑制了并发异常，才发送尚未执行的后缀。
                }
                finally
                {
                    foreach (var result in results)
                    {
                        if (asynchronous && result.Reader is not null)
                            await result.Reader.DisposeAsync().ConfigureAwait(false);
                        else
                            result.Reader?.Dispose();
                    }
                }
            }

            preparationFailure?.Throw();
        }
        catch (Exception exception) when (exception is not DbUpdateException and not OperationCanceledException and not UnreachableException)
        {
            throw new DbUpdateException("将实体更改保存到 SonnetDB 时发生错误。", exception,
                _commands[Math.Min(current, _commands.Count - 1)].Entries.ToList());
        }
        finally
        {
            foreach (var command in commands)
            {
                if (asynchronous && command is not null)
                    await command.DisposeAsync().ConfigureAwait(false);
                else
                    command?.Dispose();
            }
        }
    }

    private async Task ExecuteSingletonsAsync(
        IRelationalConnection connection, int start, int end, bool asynchronous, CancellationToken cancellationToken)
    {
        for (var index = start; index < end; index++)
        {
            if (asynchronous)
                await _singletons[index].ExecuteAsync(connection, cancellationToken).ConfigureAwait(false);
            else
                _singletons[index].Execute(connection);
        }
    }

    private async Task<IReadOnlyList<SndbDmlBatchResult>?> ExecuteRequestAsync(
        IRelationalConnection connection,
        SndbTransaction transaction,
        SndbDmlBatchStatement[] statements,
        bool asynchronous,
        CancellationToken cancellationToken)
    {
        var logger = dependencies.Logger;
        var started = DateTimeOffset.UtcNow;
        if (!logger.ShouldLogCommandExecute(started))
        {
            var request = transaction.ExecuteDmlBatchAsync(statements, cancellationToken);
            return asynchronous ? await request.ConfigureAwait(false) : request.GetAwaiter().GetResult();
        }

        // 一个命令耗时对应一个网络请求。SQL 模板和参数仍遵循 EF 的敏感数据日志设置。
        var text = string.Join(Environment.NewLine, statements.Select(static statement => statement.Sql));
        await using var command = new SndbCommand(text, (SndbConnection)connection.DbConnection)
        {
            Transaction = transaction,
            CommandTimeout = statements[0].CommandTimeout,
        };
        foreach (var statement in statements)
        {
            foreach (SndbParameter parameter in statement.Parameters)
                command.Parameters.Add(parameter);
        }

        var commandId = Guid.NewGuid();
        var connectionId = connection.ConnectionId;
        var context = dependencies.CurrentContext.Context;
        var stopwatch = new Stopwatch();

        IReadOnlyList<SndbDmlBatchResult>? results;
        try
        {
            var request = transaction.ExecuteDmlBatchAsync(statements, cancellationToken, OnSending);
            results = asynchronous ? await request.ConfigureAwait(false) : request.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (stopwatch.IsRunning)
        {
            if (asynchronous)
            {
                await logger.CommandCanceledAsync(connection, command, text, context, DbCommandMethod.ExecuteNonQuery,
                    commandId, connectionId, started, stopwatch.Elapsed, CommandSource.SaveChanges, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                logger.CommandCanceled(connection, command, text, context, DbCommandMethod.ExecuteNonQuery,
                    commandId, connectionId, started, stopwatch.Elapsed, CommandSource.SaveChanges);
            }
            throw;
        }
        catch (Exception exception) when (stopwatch.IsRunning)
        {
            await LogErrorAsync(exception).ConfigureAwait(false);
            throw;
        }

        if (results is not null)
        {
            try
            {
                if (results.Count > 0 && results[^1].Error is { } error)
                {
                    await LogErrorAsync(error).ConfigureAwait(false);
                }
                else if (asynchronous)
                {
                    await logger.CommandNonQueryExecutedAsync(connection, command, text, context, commandId, connectionId,
                        CountAffected(results), started, stopwatch.Elapsed, CommandSource.SaveChanges, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    logger.CommandNonQueryExecuted(connection, command, text, context, commandId, connectionId,
                        CountAffected(results), started, stopwatch.Elapsed, CommandSource.SaveChanges);
                }
            }
            catch
            {
                foreach (var result in results)
                    result.Reader?.Dispose();
                throw;
            }
        }

        return results;

        static int CountAffected(IReadOnlyList<SndbDmlBatchResult> batchResults)
            => batchResults.Sum(static result => result.Reader?.RecordsAffected ?? 0);

        async ValueTask OnSending()
        {
            started = DateTimeOffset.UtcNow;
            stopwatch.Start();
            if (asynchronous)
            {
                await logger.CommandNonQueryExecutingAsync(connection, command, text, context, commandId, connectionId,
                    started, CommandSource.SaveChanges, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                logger.CommandNonQueryExecuting(connection, command, text, context, commandId, connectionId,
                    started, CommandSource.SaveChanges);
            }
        }

        async ValueTask LogErrorAsync(Exception exception)
        {
            if (asynchronous)
            {
                await logger.CommandErrorAsync(connection, command, text, context, DbCommandMethod.ExecuteNonQuery,
                    commandId, connectionId, exception, started, stopwatch.Elapsed, CommandSource.SaveChanges, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                logger.CommandError(connection, command, text, context, DbCommandMethod.ExecuteNonQuery,
                    commandId, connectionId, exception, started, stopwatch.Elapsed, CommandSource.SaveChanges);
            }
        }
    }
}
