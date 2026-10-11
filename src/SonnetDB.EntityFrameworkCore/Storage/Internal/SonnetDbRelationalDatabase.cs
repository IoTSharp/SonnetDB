using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using SonnetDB.Data;
using SonnetDB.Tables;

namespace SonnetDB.EntityFrameworkCore.Storage.Internal;

/// <summary>
/// 将轻事务提交阶段的关系表约束错误转换为包含实际修改条目的 EF Core 保存异常。
/// </summary>
internal sealed class SonnetDbRelationalDatabase(
    DatabaseDependencies dependencies,
    RelationalDatabaseDependencies relationalDependencies)
    : RelationalDatabase(dependencies, relationalDependencies)
{
    /// <inheritdoc />
    public override int SaveChanges(IList<IUpdateEntry> entries)
    {
        try
        {
            return base.SaveChanges(entries);
        }
        catch (TableConstraintException exception) when (IsUpdateConstraint(exception.ErrorCode))
        {
            throw CreateUpdateException(exception, exception.ErrorCode, entries);
        }
        catch (SndbServerException exception) when (IsUpdateConstraint(exception.Error))
        {
            throw CreateUpdateException(exception, exception.Error, entries);
        }
    }

    /// <inheritdoc />
    public override async Task<int> SaveChangesAsync(
        IList<IUpdateEntry> entries,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        catch (TableConstraintException exception) when (IsUpdateConstraint(exception.ErrorCode))
        {
            throw CreateUpdateException(exception, exception.ErrorCode, entries);
        }
        catch (SndbServerException exception) when (IsUpdateConstraint(exception.Error))
        {
            throw CreateUpdateException(exception, exception.Error, entries);
        }
    }

    private static bool IsUpdateConstraint(string errorCode)
        => errorCode is TableConstraintException.UniqueViolation
            or TableConstraintException.ForeignKeyViolation
            or TableConstraintException.CheckViolation
            or TableConstraintException.ConcurrencyConflict;

    private static DbUpdateException CreateUpdateException(
        Exception exception,
        string errorCode,
        IList<IUpdateEntry> entries)
    {
        // 命令批处理已包装的 EF 异常不进入这些 catch；这里只补提交阶段的异常边界。
        var affectedEntries = entries.ToArray();
        return errorCode == TableConstraintException.ConcurrencyConflict
            ? new DbUpdateConcurrencyException("SonnetDB 提交保存更改时检测到乐观并发冲突。", exception, affectedEntries)
            : new DbUpdateException("SonnetDB 提交保存更改时违反关系表约束。", exception, affectedEntries);
    }
}
