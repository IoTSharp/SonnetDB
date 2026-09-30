using SonnetDB.Engine;

namespace SonnetDB.Sql.Execution;

internal enum SqlSourceKind
{
    Table,
    Measurement,
    View,
    MaterializedView,
}

internal readonly record struct SqlSourceName(string Name, SqlSourceKind Kind);

internal static class SqlSourceNameResolver
{
    internal static SqlSourceName? Resolve(Tsdb tsdb, string name, bool quoted)
    {
        SqlSourceName? result = null;
        void Add(string? candidate, SqlSourceKind kind)
        {
            if (candidate is null)
                return;
            if (result is not null)
                throw new InvalidOperationException(
                    $"SQL 数据源 '{name}' 存在跨模型名称歧义；请使用双引号精确引用或显式迁移冲突对象。");
            result = new SqlSourceName(candidate, kind);
        }

        Add(tsdb.Tables.Catalog.Resolve(name, quoted)?.Name, SqlSourceKind.Table);
        Add(tsdb.Measurements.Resolve(name, quoted)?.Name, SqlSourceKind.Measurement);
        Add(tsdb.Views.Catalog.Resolve(name, quoted)?.Name, SqlSourceKind.View);
        Add(tsdb.MaterializedViews.Catalog.Resolve(name, quoted)?.Name, SqlSourceKind.MaterializedView);
        return result;
    }

    internal static void EnsureAvailable(Tsdb tsdb, string name, SqlSourceKind? excludedKind = null)
    {
        bool occupied = excludedKind != SqlSourceKind.Table
                && tsdb.Tables.Catalog.Snapshot().Any(schema => SameName(schema.Name, name))
            || excludedKind != SqlSourceKind.Measurement
                && tsdb.Measurements.Snapshot().Any(schema => SameName(schema.Name, name))
            || excludedKind != SqlSourceKind.View
                && tsdb.Views.Catalog.Snapshot().Any(view => SameName(view.Name, name))
            || excludedKind != SqlSourceKind.MaterializedView
                && tsdb.MaterializedViews.Catalog.Snapshot().Any(view => SameName(view.Name, name));
        if (occupied)
            throw new InvalidOperationException(
                $"SQL 数据源 '{name}' 已存在；新建名称不能与现有表、measurement 或视图仅大小写不同。");
    }

    private static bool SameName(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
