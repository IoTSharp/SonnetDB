using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Storage;

namespace SonnetDB.EntityFrameworkCore.Metadata.Conventions.Internal;

/// <summary>
/// 将字符串最大长度转换为模型内的关系表 CHECK 约束，使迁移与数据库写入共同维护长度合同。
/// </summary>
internal sealed class SonnetDbStringLengthConvention(ITypeMappingSource typeMappingSource) : IModelFinalizingConvention
{
    /// <inheritdoc />
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        var entityTypes = modelBuilder.Metadata.GetEntityTypes().ToArray();
        var columns = new Dictionary<(StoreObjectIdentifier Table, string Column), LengthConstraint>();
        foreach (var entityType in entityTypes)
        {
            var table = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);
            if (table is null || entityType.IsMappedToJson())
            {
                continue;
            }

            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType != typeof(string)
                    || property.GetMaxLength() is not > 0
                    || property.GetColumnName(table.Value) is not { } columnName)
                {
                    continue;
                }

                // 模型终结尚未缓存类型映射时，主动解析配置中的转换器（包括转换器类型）。
                var mapping = property.FindTypeMapping() ?? typeMappingSource.FindMapping((IProperty)property);
                var providerType = mapping?.Converter?.ProviderClrType
                    ?? property.GetProviderClrType()
                    ?? property.GetValueConverter()?.ProviderClrType
                    ?? property.ClrType;
                if (providerType != typeof(string)
                    // 默认类型映射在模型终结期间可能尚未写回；只有明确的非字符串列类型才排除。
                    || ((property.GetColumnType(table.Value) ?? (mapping as RelationalTypeMapping)?.StoreType) is { } columnType
                        && !string.Equals(columnType, "STRING", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(columnType, "TEXT", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var key = (table.Value, columnName);
                var maximumLength = property.GetMaxLength()!.Value;
                if (!columns.TryGetValue(key, out var existing) || maximumLength < existing.MaximumLength)
                {
                    columns[key] = new LengthConstraint(entityType, maximumLength);
                }
            }
        }

        foreach (var (key, constraint) in columns)
        {
            var name = $"CK_{key.Table.Name}_{key.Column}_MaxLength";
            var column = "\"" + key.Column.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            var sql = $"{column} IS NULL OR char_length({column}) <= {constraint.MaximumLength.ToString(CultureInfo.InvariantCulture)}";
            var existingConstraints = entityTypes
                .Where(entityType => StoreObjectIdentifier.Create(entityType, StoreObjectType.Table) == key.Table)
                .SelectMany(entityType => entityType.GetCheckConstraints())
                .Where(existing => existing.ModelName == name || existing.GetName(key.Table) == name)
                .ToArray();
            if (existingConstraints.Length != 0)
            {
                if (existingConstraints.Any(existing => !string.Equals(existing.Sql, sql, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"SonnetDB 字符串长度约束 '{name}' 与已有 CHECK 约束冲突；请为自定义约束使用不同名称。");
                }

                continue;
            }

            constraint.EntityType.AddCheckConstraint(name, sql);
        }
    }

    private sealed record LengthConstraint(IConventionEntityType EntityType, int MaximumLength);
}
