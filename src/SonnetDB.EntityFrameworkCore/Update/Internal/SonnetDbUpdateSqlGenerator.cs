using System.Text;
using Microsoft.EntityFrameworkCore.Update;

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
        requiresTransaction = false;
        AppendInsertCommand(
            commandStringBuilder,
            command.TableName,
            command.Schema,
            command.ColumnModifications.Where(static operation => operation.IsWrite).ToList(),
            []);
        return ResultSetMapping.NoResults;
    }

    /// <inheritdoc />
    public override ResultSetMapping AppendUpdateOperation(
        StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command,
        int commandPosition,
        out bool requiresTransaction)
    {
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
        return ResultSetMapping.NoResults;
    }
}
