namespace SonnetDB.Tables;

public sealed partial class TableManager
{
    /// <summary>删除目录中保存的真实约束；主键删除迁移现有行到稳定无主键身份。</summary>
    /// <param name="tableName">已绑定的表名。</param>
    /// <param name="constraintName">已绑定的约束名，精确匹配。</param>
    /// <returns>存在并删除约束时为 true。</returns>
    public bool DropConstraint(string tableName, string constraintName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintName);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfDisposed();
                _schemaMutationGuard?.Invoke(tableName, "ALTER TABLE");
                var current = Catalog.TryGet(tableName)
                    ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
                if (string.Equals(current.PrimaryKeyConstraintName, constraintName, StringComparison.Ordinal))
                {
                    if (Catalog.Snapshot().Any(schema => schema.ForeignKeys.Any(key =>
                        string.Equals(key.PrincipalTable, tableName, StringComparison.Ordinal))))
                        throw new InvalidOperationException($"主键 '{constraintName}' 仍被外键引用。");
                    ApplyPrimaryKeySchemaMigrationLocked(current, current.WithPrimaryKey([]), static (_, row) => row.Values);
                    return true;
                }
                return DropForeignKey(tableName, constraintName) || DropCheckConstraint(tableName, constraintName);
            }
    }

    /// <summary>持久设置自增下一值；不能回退到已分配的高水位，成功返回前同步 WAL。</summary>
    /// <param name="tableName">已绑定的表名。</param>
    /// <param name="nextValue">1 到 Int64.MaxValue 之间的下一值。</param>
    public void SetAutoIncrementNextValue(string tableName, long nextValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nextValue);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfDisposed();
                _schemaMutationGuard?.Invoke(tableName, "ALTER TABLE");
                var schema = Catalog.TryGet(tableName)
                    ?? throw new InvalidOperationException($"table '{tableName}' 不存在。");
                OpenStoreLocked(schema).SetAutoIncrementNextValue(nextValue);
            }
    }
}

public sealed partial class TableStore
{
    internal void SetAutoIncrementNextValue(long nextValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nextValue);
        lock (_sync)
        {
            ThrowIfDisposedLocked();
            if (_schema.AutoIncrementColumn is null)
                throw new InvalidOperationException($"table '{_schema.Name}' 没有 AUTO_INCREMENT 列。");
            long current = ReadAutoIncrementStateLocked();
            if (nextValue <= current)
                throw new InvalidOperationException($"AUTO_INCREMENT 下一值必须大于历史高水位 {current}。");
            WriteAutoIncrementStateLocked(nextValue - 1, nextValue - 1 != current);
            _keyspace.SyncWalForMaintenance();
        }
    }
}
