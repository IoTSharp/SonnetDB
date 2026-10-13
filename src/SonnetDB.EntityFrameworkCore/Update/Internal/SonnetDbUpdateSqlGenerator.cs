using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using SonnetDB.EntityFrameworkCore.Storage.Internal;

namespace SonnetDB.EntityFrameworkCore.Update.Internal;

/// <summary>
/// SonnetDB DML SQL 生成器。
/// </summary>
public sealed class SonnetDbUpdateSqlGenerator : UpdateSqlGenerator
{
    private static readonly StringTypeMapping JsonPathLiteralMapping = new("STRING", System.Data.DbType.String);

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
        RejectJsonAggregateWrites(command, allowScalarUpdate: false);
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
        RejectJsonAggregateWrites(command, allowScalarUpdate: true);
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

    /// <inheritdoc />
    protected override void AppendUpdateColumnValue(
        ISqlGenerationHelper updateSqlGeneratorHelper,
        IColumnModification columnModification,
        StringBuilder stringBuilder,
        string name,
        string? schema)
    {
        if (columnModification.JsonPath is not null and not "$")
        {
            if (!TryGetScalarUpdatePath(columnModification, out var path))
                throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);

            stringBuilder.Append("json_set_value(")
                .Append(updateSqlGeneratorHelper.DelimitIdentifier(columnModification.ColumnName))
                .Append(", ").Append(JsonPathLiteralMapping.GenerateSqlLiteral(path)).Append(", ");
            base.AppendUpdateColumnValue(updateSqlGeneratorHelper, columnModification, stringBuilder, name, schema);
            stringBuilder.Append(')');
            return;
        }

        base.AppendUpdateColumnValue(updateSqlGeneratorHelper, columnModification, stringBuilder, name, schema);
    }

    private static bool TryGetScalarUpdatePath(IColumnModification operation, out string path)
    {
        path = string.Empty;
        if (operation.Property is not { DeclaringType: IEntityType entityType } property
            || !entityType.IsMappedToJson()
            || entityType.FindOwnership() is not { IsUnique: true } ownership
            || !string.Equals(entityType.GetContainerColumnName(), operation.ColumnName, StringComparison.Ordinal)
            || operation.TypeMapping is null || operation.TypeMapping.Converter is not null)
        {
            return false;
        }

        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if ((type != typeof(string) && type != typeof(bool) && type != typeof(byte) && type != typeof(short)
                && type != typeof(int) && type != typeof(long) && type != typeof(float) && type != typeof(double))
            || (operation.Value is not null && operation.Value.GetType() != type)
            || operation.Value is float single && !float.IsFinite(single)
            || operation.Value is double number && !double.IsFinite(number))
        {
            return false;
        }

        // EF 的 JsonPath 以点连接原始名称，名称本身也可以含点；沿唯一 ownership 链
        // 从模型构造带引号的固定属性路径，不猜测点的含义，也不接受集合索引。
        var jsonName = property.GetJsonPropertyName();
        if (string.IsNullOrWhiteSpace(jsonName) || jsonName.Length > 1019)
            return false;

        var names = new List<string> { jsonName };
        for (var depth = 0; ownership.PrincipalEntityType.IsMappedToJson() && depth < 64; depth++)
        {
            var ownedName = entityType.GetJsonPropertyName();
            if (names.Count >= 64 || string.IsNullOrWhiteSpace(ownedName) || ownedName.Length > 1019)
                return false;
            names.Add(ownedName);
            entityType = ownership.PrincipalEntityType;
            if (entityType.FindOwnership() is not { IsUnique: true } parentOwnership)
                return false;
            ownership = parentOwnership;
        }

        if (ownership.PrincipalEntityType.IsMappedToJson()
            || !string.Equals(entityType.GetContainerColumnName(), operation.ColumnName, StringComparison.Ordinal))
            return false;

        names.Reverse();
        var quotedPath = new StringBuilder("$");
        var efPath = new StringBuilder("$");
        foreach (var name in names)
        {
            quotedPath.Append("['").Append(name.Replace("'", "''", StringComparison.Ordinal)).Append("']");
            if (quotedPath.Length > 1024)
                return false;
            efPath.Append('.').Append(name);
        }

        path = quotedPath.ToString();
        return string.Equals(operation.JsonPath, efPath.ToString(), StringComparison.Ordinal);
    }

    private static void RejectJsonAggregateWrites(IReadOnlyModificationCommand command, bool allowScalarUpdate)
    {
        if (command.ColumnModifications.Any(operation => operation.IsWrite
            && (operation.JsonPath is not null || operation.TypeMapping is SonnetDbJsonTypeMapping)
            && (operation.JsonPath != "$"
                || operation.TypeMapping is not SonnetDbJsonTypeMapping
                || operation.Value is not null and not string)
            && !(allowScalarUpdate && TryGetScalarUpdatePath(operation, out _))))
        {
            throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
        }
    }
}
