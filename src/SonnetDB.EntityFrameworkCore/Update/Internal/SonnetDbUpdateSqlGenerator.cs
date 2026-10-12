using System.Text;
using Microsoft.EntityFrameworkCore.Update;
using SonnetDB.EntityFrameworkCore.Storage.Internal;

namespace SonnetDB.EntityFrameworkCore.Update.Internal;

/// <summary>
/// SonnetDB DML SQL 生成器。
/// </summary>
public sealed class SonnetDbUpdateSqlGenerator : UpdateSqlGenerator
{
    /// <summary>
    /// 创建 SonnetDB DML SQL 生成器。
    /// </summary>
    /// <param name="dependencies">DML SQL 生成器依赖。</param>
    public SonnetDbUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    public override ResultSetMapping AppendInsertOperation(
        StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command,
        int commandPosition,
        out bool requiresTransaction)
    {
        RejectJsonAggregateWrites(command);
        return AppendInsertReturningOperation(
            commandStringBuilder,
            command,
            commandPosition,
            out requiresTransaction);
    }

    /// <inheritdoc />
    public override ResultSetMapping AppendUpdateOperation(
        StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command,
        int commandPosition,
        out bool requiresTransaction)
    {
        RejectJsonAggregateWrites(command);
        requiresTransaction = false;
        var operations = command.ColumnModifications;
        AppendUpdateCommand(
            commandStringBuilder,
            command.TableName,
            command.Schema,
            operations.Where(static operation => operation.IsWrite).ToList(),
            [],
            operations.Where(static operation => operation.IsCondition).ToList(),
            appendReturningOneClause: false);
        // SonnetDbModificationCommandBatch 从 SndbDataReader.RecordsAffected 校验实际影响行数。
        return ResultSetMapping.NoResults;
    }

    /// <inheritdoc />
    public override ResultSetMapping AppendDeleteOperation(
        StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command,
        int commandPosition,
        out bool requiresTransaction)
    {
        requiresTransaction = false;
        var operations = command.ColumnModifications;
        AppendDeleteCommand(
            commandStringBuilder,
            command.TableName,
            command.Schema,
            [],
            operations.Where(static operation => operation.IsCondition).ToList(),
            appendReturningOneClause: false);
        // SonnetDbModificationCommandBatch 从 SndbDataReader.RecordsAffected 校验实际影响行数。
        return ResultSetMapping.NoResults;
    }

    private static void RejectJsonAggregateWrites(IReadOnlyModificationCommand command)
    {
        if (command.ColumnModifications.Any(static operation => operation.IsWrite
            && (operation.JsonPath is not null || operation.TypeMapping is SonnetDbJsonTypeMapping)
            && (operation.JsonPath != "$"
                || operation.TypeMapping is not SonnetDbJsonTypeMapping
                || operation.Value is not null and not string)))
        {
            throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
        }
    }
}
