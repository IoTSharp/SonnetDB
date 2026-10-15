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
        RejectJsonAggregateWrites(command.ColumnModifications, allowPartialUpdate: false);
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
        requiresTransaction = false;
        var operations = command.ColumnModifications;
        var writes = operations.Where(static operation => operation.IsWrite).ToList();
        for (var index = 0; index < writes.Count; index++)
        {
            var operation = writes[index];
            if (operation.Property is null && operation.JsonPath is not null and not "$"
                && operation.TypeMapping is SonnetDbJsonTypeMapping && operation.Value is null or string
                && TryGetContainerUpdatePath(command, operation, out var path))
                writes[index] = new JsonFragmentModification(operation, path);
        }
        RejectJsonAggregateWrites(writes, allowPartialUpdate: true);
        AppendUpdateCommand(
            commandStringBuilder,
            command.TableName,
            command.Schema,
            writes,
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
            if (!TryGetJsonUpdatePath(columnModification, out var path))
                throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);

            stringBuilder.Append("json_set_value(")
                .Append(updateSqlGeneratorHelper.DelimitIdentifier(columnModification.ColumnName))
                .Append(", ").Append(JsonPathLiteralMapping.GenerateSqlLiteral(path)).Append(", ");
            base.AppendUpdateColumnValue(updateSqlGeneratorHelper, columnModification, stringBuilder, name, schema);
            if (columnModification.TypeMapping is SonnetDbJsonTypeMapping)
                stringBuilder.Append(", TRUE");
            stringBuilder.Append(')');
            return;
        }

        base.AppendUpdateColumnValue(updateSqlGeneratorHelper, columnModification, stringBuilder, name, schema);
    }

    private static bool TryGetJsonUpdatePath(IColumnModification operation, out string path)
    {
        path = string.Empty;
        if (operation is JsonFragmentModification fragment)
        {
            path = fragment.Path;
            return true;
        }

        if (operation.Property is not { DeclaringType: IEntityType entityType } property
            || !entityType.IsMappedToJson()
            || entityType.FindOwnership() is not { IsUnique: true }
            || !string.Equals(entityType.GetContainerColumnName(), operation.ColumnName, StringComparison.Ordinal)
            || operation.TypeMapping is null || operation.TypeMapping.Converter is not null)
        {
            return false;
        }

        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (operation.TypeMapping is SonnetDbJsonTypeMapping)
        {
            if (operation.TypeMapping is not SonnetDbJsonTypeMapping || operation.Value is not null and not string)
                return false;
        }
        else if ((type != typeof(string) && type != typeof(bool) && type != typeof(byte) && type != typeof(short)
                && type != typeof(int) && type != typeof(long) && type != typeof(float) && type != typeof(double))
            || (operation.Value is not null && operation.Value.GetType() != type)
            || operation.Value is float single && !float.IsFinite(single)
            || operation.Value is double number && !double.IsFinite(number))
        {
            return false;
        }

        var jsonName = property.GetJsonPropertyName();
        if (string.IsNullOrWhiteSpace(jsonName) || jsonName.Length > 1019)
            return false;

        return TryGetOwnedPath(entityType, jsonName, operation.JsonPath, operation.ColumnName, out path);
    }

    private static bool TryGetOwnedPath(IEntityType entityType, string? leafName, string? jsonPath, string columnName, out string path)
    {
        path = string.Empty;
        if (entityType.FindOwnership() is not { IsUnique: true } ownership)
            return false;
        // EF 的 JsonPath 以点连接原始名称。使用模型名称重建路径，不猜测分隔点或数组索引。
        List<string> names = leafName is null ? [] : [leafName];
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
            || !string.Equals(entityType.GetContainerColumnName(), columnName, StringComparison.Ordinal))
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
        return string.Equals(jsonPath, efPath.ToString(), StringComparison.Ordinal);
    }

    private static bool TryGetContainerUpdatePath(IReadOnlyModificationCommand command, IColumnModification operation, out string path)
    {
        path = string.Empty;
        if (command.Entries.Count > 1024)
            return false;
        var visited = new HashSet<IEntityType>();
        foreach (var entry in command.Entries)
        {
            var entityType = entry.EntityType;
            for (var depth = 0; depth < 64 && entityType.IsMappedToJson() && visited.Add(entityType); depth++)
            {
                if (visited.Count > 1024)
                    return false;
                if (TryGetOwnedPath(entityType, null, operation.JsonPath, operation.ColumnName, out var candidate))
                {
                    if (path.Length != 0 && !string.Equals(path, candidate, StringComparison.Ordinal))
                        return false;
                    path = candidate;
                }
                if (entityType.FindOwnership() is not { } ownership)
                    break;
                entityType = ownership.PrincipalEntityType;
            }
        }
        return path.Length != 0;
    }

    private sealed class JsonFragmentModification(IColumnModification source, string path)
        : ColumnModification(new ColumnModificationParameters(source.ColumnName, source.Value, property: null,
            source.TypeMapping!.StoreType, source.TypeMapping,
            source.JsonPath ?? throw new InvalidOperationException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage),
            read: false, write: true, key: false, condition: false, sensitiveLoggingEnabled: false)
        {
            GenerateParameterName = source.UseCurrentValueParameter ? () => source.ParameterName! : null,
        })
    {
        internal string Path { get; } = path;
    }

    private static void RejectJsonAggregateWrites(IReadOnlyList<IColumnModification> operations, bool allowPartialUpdate)
    {
        if (operations.Any(operation => operation.IsWrite
            && (operation.JsonPath is not null || operation.TypeMapping is SonnetDbJsonTypeMapping)
            && (operation.JsonPath != "$"
                || operation.TypeMapping is not SonnetDbJsonTypeMapping
                || operation.Value is not null and not string)
            && !(allowPartialUpdate && TryGetJsonUpdatePath(operation, out _))))
        {
            throw new NotSupportedException(SonnetDbJsonTypeMapping.UnsupportedOperationMessage);
        }
    }
}
