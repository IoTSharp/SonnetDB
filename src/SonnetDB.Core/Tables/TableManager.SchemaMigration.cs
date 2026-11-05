using SonnetDB.Exceptions;
using SonnetDB.Kv;
using SonnetDB.Routines;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Tables;

public sealed partial class TableManager
{
    /// <summary>仅供测试在主键迁移的持久阶段暂停或注入失败。</summary>
    internal Action<string>? SchemaMigrationPhaseTestHook { get; set; }

    private sealed record SchemaMigrationChild(TableSchema Original, TableSchema Updated,
        IReadOnlyList<TableForeignKey> References);

    private void ApplyPrimaryKeySchemaMigrationLocked(
        TableSchema current, TableSchema updated,
        Func<TableSchema, TableRow, IReadOnlyList<object?>> transform,
        Action<TableSchema, IReadOnlyList<object?>>? validate = null)
    {
        SqlExecutor.ThrowIfCancellationRequested();
        IReadOnlyList<TableSchema> previousSchemas = _catalog.Snapshot();
        bool preservesPrimaryKey = HasUnchangedSchemaMigrationPrimaryKeyDefinition(current, updated);
        if (!preservesPrimaryKey && current.ForeignKeys.Any(key => key.PrincipalObjectName == current.ObjectName)
            && (current.SchemaName != TableObjectName.DefaultSchemaName
                || current.ForeignKeys.Any(key => key.PrincipalSchemaName != TableObjectName.DefaultSchemaName)
                || previousSchemas.Select(static schema => schema.Name).Distinct(StringComparer.Ordinal).Count() != previousSchemas.Count
                || previousSchemas.Any(schema => schema.ForeignKeys.Any(key => key.PrincipalTable == current.Name
                    && (schema.SchemaName != TableObjectName.DefaultSchemaName
                        || key.PrincipalSchemaName != TableObjectName.DefaultSchemaName)))))
        {
            // 既有 journal 仍按短表名查找恢复对象；在完成成对身份迁移前不得扩大其恢复合同。
            throw SchemaMigrationUnsupported(current.Name,
                "自引用主键迁移仅支持 main 内的相关表，且关系目录不得存在跨 schema 同名表。");
        }
        var children = PrepareSchemaMigrationChildren(current, updated, previousSchemas, preservesPrimaryKey);
        SchemaMigrationChild? selfReference = children.Find(child =>
            string.Equals(child.Original.Name, current.Name, StringComparison.Ordinal));
        TableSchema principalTarget = selfReference?.Updated ?? updated;
        if (selfReference is not null) children.Remove(selfReference);
        var updatedByName = children.ToDictionary(static child => child.Updated.Name,
            static child => child.Updated, StringComparer.Ordinal);
        updatedByName.Add(principalTarget.Name, principalTarget);
        if (updatedByName.Count > TableSchemaMigrationJournal.MaxTables)
            throw SchemaMigrationUnsupported(current.Name, "迁移涉及的关系表超过 32 张上限。");
        TableSchema[] candidateSchemas = previousSchemas.Select(schema =>
            updatedByName.GetValueOrDefault(schema.Name, schema)).ToArray();
        Action publishCatalog = _catalog.PrepareReplacement(candidateSchemas);
        var lockNames = new HashSet<string>(updatedByName.Keys, StringComparer.Ordinal);
        foreach (TableSchema schema in updatedByName.Values)
        {
            if (!string.Equals(schema.Name, current.Name, StringComparison.Ordinal))
                _schemaMutationGuard?.Invoke(schema.Name, "ALTER TABLE PRIMARY KEY");
            foreach (var foreignKey in schema.ForeignKeys) lockNames.Add(foreignKey.PrincipalTable);
            ValidateSchemaMigrationDefaults(schema);
        }
        if (lockNames.Count > TableSchemaMigrationJournal.MaxTables)
            throw SchemaMigrationUnsupported(current.Name, "迁移和外键校验涉及的关系表超过 32 张上限。");

        var lockedStores = new List<TableStore>(lockNames.Count);
        try
        {
            foreach (string name in lockNames.Order(StringComparer.Ordinal))
            {
                TableSchema schema = _catalog.TryGet(name)
                    ?? throw new InvalidDataException($"主键迁移引用了不存在的 table '{name}'。");
                TableStore store = OpenStoreLocked(schema);
                Monitor.Enter(store.SynchronizationRoot);
                lockedStores.Add(store);
                store.EnsureNoOnlineIndexBuild();
            }
            var budget = new TableSchemaMigrationBudget();
            var plans = new Dictionary<string, (TableStore Store, TableStore.SchemaMigrationPlan Plan)>(StringComparer.Ordinal);
            TableStore principalStore = _stores[current.ObjectName];
            TableStore.SchemaMigrationPlan principalPlan = principalStore.PrepareSchemaMigration(updated, transform,
                (schema, values) =>
                {
                    ValidateSchemaMigrationRow(schema, values);
                    validate?.Invoke(schema, values);
                }, TableRowIdentity.Create, budget);
            if (preservesPrimaryKey)
            {
                // 主键定义相同还不足以证明引用保持有效；迁移后的实际键集合也必须原样保留。
                foreach (var row in principalPlan.Rows)
                {
                    SqlExecutor.ThrowIfCancellationRequested();
                    if (!row.OriginalPrimaryKey.AsSpan().SequenceEqual(row.UpdatedRow.PrimaryKey.Span))
                        throw SchemaMigrationUnsupported(current.Name, "非主键 schema 迁移不能修改现有主键编码。");
                }
            }
            budget.Reserve(principalPlan.Rows.Count * 80L);
            var principalByOriginalKey = principalPlan.Rows.ToDictionary(static row => row.OriginalPrimaryKey,
                static row => row.UpdatedRow, KvKeyComparer.Instance);
            if (selfReference is not null)
            {
                // 先冻结所有旧键到新父行的映射，再在同一迁移计划中处理自引用。
                // 不依赖遍历顺序，循环引用也无需临时删除约束或写入半迁移状态。
                principalPlan = principalStore.PrepareSchemaMigration(principalTarget,
                    (_, row) => TransformSchemaMigrationChildRow(current, updated, selfReference, row,
                        principalByOriginalKey, principalByOriginalKey[row.PrimaryKey.ToArray()].Values),
                    (schema, values) =>
                    {
                        ValidateSchemaMigrationRow(schema, values);
                        validate?.Invoke(schema, values);
                    }, TableRowIdentity.Create, budget);
            }
            plans.Add(current.Name, (principalStore, principalPlan));
            foreach (var child in children)
            {
                TableStore store = _stores[child.Original.ObjectName];
                var plan = store.PrepareSchemaMigration(child.Updated,
                    (_, row) => TransformSchemaMigrationChildRow(current, updated, child, row, principalByOriginalKey),
                    ValidateSchemaMigrationRow, TableRowIdentity.Create, budget);
                plans.Add(child.Original.Name, (store, plan));
            }
            ValidateSchemaMigrationForeignKeys(plans, updatedByName);
            var catalogInfo = new FileInfo(SchemaPath);
            if (!catalogInfo.Exists || catalogInfo.Length is < 1 or > TableSchemaMigrationJournal.MaxCatalogBytes)
                throw SchemaMigrationUnsupported(current.Name, "原关系目录不存在或超过迁移恢复日志上限。");
            budget.Reserve(catalogInfo.Length * 2L);
            byte[] originalCatalog = File.ReadAllBytes(SchemaPath);
            TableSchemaMigrationUndo[] undoTables = plans.Values.Select(static entry => entry.Plan.Undo).ToArray();
            int payloadBytes = TableSchemaMigrationJournal.ValidatePlan(originalCatalog, undoTables);
            budget.Reserve(payloadBytes);
            SqlExecutor.ThrowIfCancellationRequested();
            CommitSchemaMigration(previousSchemas, candidateSchemas, originalCatalog, plans, undoTables, publishCatalog);
        }
        finally
        {
            for (int index = lockedStores.Count - 1; index >= 0; index--)
                Monitor.Exit(lockedStores[index].SynchronizationRoot);
        }
    }

    private List<SchemaMigrationChild> PrepareSchemaMigrationChildren(
        TableSchema current, TableSchema updated, IReadOnlyList<TableSchema> schemas, bool preservesPrimaryKey)
    {
        var changedTypes = current.Columns.Where(column =>
            column.DataType != updated.TryGetColumn(column.Name)?.DataType).Select(static column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (current.ForeignKeys.Any(foreignKey => foreignKey.Columns.Any(changedTypes.Contains)))
            throw SchemaMigrationUnsupported(current.Name, "主键类型迁移暂不支持同时承担出站外键的列。");
        // 原生非主键列仍需整表 journal 迁移，但不应改写引用同一组原主键的子表。
        // principal plan 的逐行键字节校验和现有 FK 校验在任何持久写入前继续执行。
        if (preservesPrimaryKey) return [];
        var children = new List<SchemaMigrationChild>();
        foreach (var child in schemas)
        {
            TableForeignKey[] references = child.ForeignKeys.Where(foreignKey =>
                string.Equals(foreignKey.PrincipalTable, current.Name, StringComparison.Ordinal)).ToArray();
            if (references.Length == 0) continue;
            bool isSelfReference = string.Equals(child.Name, current.Name, StringComparison.Ordinal);
            if (updated.PrimaryKey.Count == 0 || current.PrimaryKey.Count != updated.PrimaryKey.Count)
                throw SchemaMigrationUnsupported(current.Name, "存在入站外键时，新旧主键必须保留相同列数。");
            var mappedColumns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in references)
            {
                if (!reference.PrincipalColumns.SequenceEqual(current.PrimaryKey, StringComparer.Ordinal))
                    throw SchemaMigrationUnsupported(child.Name, "存量外键不是旧主键的完整有序引用。");
                foreach (string name in reference.Columns)
                {
                    if (!mappedColumns.Add(name)
                        || child.ForeignKeys.Any(other => !ReferenceEquals(other, reference)
                            && other.Columns.Contains(name, StringComparer.Ordinal)))
                        throw SchemaMigrationUnsupported(child.Name, "主键迁移暂不支持多个外键共享子表列。");
                    TableColumn column = child.TryGetColumn(name)!;
                    if (isSelfReference && (column.IsPrimaryKey
                        || updated.PrimaryKey.Contains(name, StringComparer.Ordinal)))
                        throw SchemaMigrationUnsupported(child.Name, "自引用外键源列不能与旧或新主键列重叠。");
                    if (column.IsAutoIncrement || column.IsRowVersion)
                        throw SchemaMigrationUnsupported(child.Name, "主键迁移不能改写子表的生成列。");
                    if (column.IsPrimaryKey && HasInboundForeignKeyLocked(child.Name))
                        throw SchemaMigrationUnsupported(child.Name, "主键迁移暂不支持继续级联到被引用的子表主键。");
                }
            }
            TableSchema childUpdated = isSelfReference ? updated : child;
            foreach (var reference in references)
            {
                childUpdated = childUpdated.WithoutForeignKey(reference.Name);
                for (int index = 0; index < reference.Columns.Count; index++)
                {
                    TableColumn source = childUpdated.TryGetColumn(reference.Columns[index])!;
                    TableColumn target = updated.TryGetColumn(updated.PrimaryKey[index])!;
                    childUpdated = childUpdated.WithAlteredColumn(source.Name, target.DataType,
                        source.IsNullable, source.DefaultExpressionSql,
                        target.DataType == TableColumnType.Decimal ? target.DecimalPrecision : null,
                        target.DataType == TableColumnType.Decimal ? target.DecimalScale : null,
                        target.HasExplicitDecimalDeclaration);
                    if (source.DataType != target.DataType && target.DataType == TableColumnType.String)
                        childUpdated = childUpdated.WithColumnLength(source.Name, target.StringLength);
                }
                childUpdated = childUpdated.WithForeignKey(new TableForeignKeyDefinition(reference.Name,
                    reference.Columns, current.Name, updated.PrimaryKey, reference.OnDelete));
            }
            children.Add(new SchemaMigrationChild(child, childUpdated, references));
        }
        return children;
    }

    private static bool HasUnchangedSchemaMigrationPrimaryKeyDefinition(TableSchema current, TableSchema updated)
    {
        if (!string.Equals(current.PrimaryKeyConstraintName, updated.PrimaryKeyConstraintName, StringComparison.Ordinal)
            || !current.PrimaryKey.SequenceEqual(updated.PrimaryKey, StringComparer.Ordinal))
            return false;
        foreach (string name in current.PrimaryKey)
        {
            TableColumn original = current.TryGetColumn(name)!;
            TableColumn target = updated.TryGetColumn(name)!;
            // 非主键列的增删可改变 ordinal；键编码按主键名称及声明次序提取值。
            // 其余完整列定义必须相同，包含类型、DECIMAL 约束、长度和生成语义。
            if ((original with { Ordinal = target.Ordinal }) != target) return false;
        }
        return true;
    }

    private static IReadOnlyList<object?> TransformSchemaMigrationChildRow(
        TableSchema current, TableSchema updated, SchemaMigrationChild child, TableRow row,
        IReadOnlyDictionary<byte[], TableRow> principalByOriginalKey,
        IReadOnlyList<object?>? transformedValues = null)
    {
        object?[] values = (transformedValues ?? row.Values).ToArray();
        foreach (var reference in child.References)
        {
            IReadOnlyList<object?>? oldForeignKey = ExtractForeignKeyValues(child.Original, row, reference);
            if (oldForeignKey is null)
            {
                // MATCH SIMPLE 的部分 NULL 行无需父行，但非 NULL 值仍须满足新列类型。
                foreach (string name in reference.Columns)
                {
                    TableColumn column = child.Updated.TryGetColumn(name)!;
                    values[column.Ordinal] = TableSqlExecutor.ConvertTableValueForSchemaChange(values[column.Ordinal], column);
                }
                continue;
            }
            byte[] oldKey = TableKeyCodec.EncodePrimaryKeyValues(current, oldForeignKey);
            if (!principalByOriginalKey.TryGetValue(oldKey, out TableRow? principal))
                throw new TableConstraintException(TableConstraintException.ForeignKeyViolation, child.Original.Name,
                    reference.Name, "主键迁移发现存量子行引用了不存在的父行。");
            for (int index = 0; index < reference.Columns.Count; index++)
            {
                TableColumn column = child.Updated.TryGetColumn(reference.Columns[index])!;
                TableColumn target = updated.TryGetColumn(updated.PrimaryKey[index])!;
                values[column.Ordinal] = principal.Values[target.Ordinal];
            }
        }
        return values;
    }

    private void ValidateSchemaMigrationForeignKeys(
        IReadOnlyDictionary<string, (TableStore Store, TableStore.SchemaMigrationPlan Plan)> plans,
        IReadOnlyDictionary<string, TableSchema> updatedByName)
    {
        var primaryKeys = plans.ToDictionary(static pair => pair.Key,
            static pair => pair.Value.Plan.Rows.Select(static row => row.UpdatedRow.PrimaryKey.ToArray())
                .ToHashSet(KvKeyComparer.Instance), StringComparer.Ordinal);
        foreach (var entry in plans.Values)
        {
            TableSchema schema = entry.Plan.UpdatedSchema;
            foreach (var migrationRow in entry.Plan.Rows)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                foreach (var foreignKey in schema.ForeignKeys)
                {
                    TableSchema principal = updatedByName.TryGetValue(foreignKey.PrincipalTable, out var changed)
                        ? changed : _catalog.TryGet(foreignKey.PrincipalTable)!;
                    if (!foreignKey.PrincipalColumns.SequenceEqual(principal.PrimaryKey, StringComparer.Ordinal))
                        throw SchemaMigrationUnsupported(schema.Name, "迁移后的外键必须引用完整的目标主键。");
                    IReadOnlyList<object?>? values = ExtractForeignKeyValues(schema, migrationRow.UpdatedRow, foreignKey);
                    if (values is null) continue;
                    bool exists = primaryKeys.TryGetValue(principal.Name, out var preparedKeys)
                        ? preparedKeys.Contains(TableKeyCodec.EncodePrimaryKeyValues(principal, values))
                        : _stores[principal.ObjectName].GetByPrimaryKey(values) is not null;
                    if (!exists)
                        throw new TableConstraintException(TableConstraintException.ForeignKeyViolation,
                            schema.Name, foreignKey.Name, "主键迁移后的行不满足外键约束。");
                }
            }
        }
    }

    private static void ValidateSchemaMigrationRow(TableSchema schema, IReadOnlyList<object?> values)
    {
        DecimalDeclarationValidator.ValidateForWrite(schema, values);
        ValidateAlteredRowCheckConstraints(schema, values);
    }

    private static void ValidateSchemaMigrationDefaults(TableSchema schema)
    {
        foreach (TableColumn column in schema.Columns)
        {
            if (column.DefaultExpressionSql is null) continue;
            var expression = SqlParser.ParsePredicate(column.DefaultExpressionSql);
            object? value = SqlSequenceRuntime.ValidateDefault(expression, column)
                ? 0L : TableSqlExecutor.EvaluateAndConvertSchemaDefault(expression, column);
            if (value is null && !column.IsNullable)
                throw SchemaMigrationUnsupported(schema.Name, $"NOT NULL 列 '{column.Name}' 的 DEFAULT 不能是 NULL。");
            StringLengthValidator.Validate(column, value, schema.Name);
            if (column.HasExplicitDecimalDeclaration && value is decimal decimalValue)
                DecimalDeclarationValidator.Validate(decimalValue, column.DecimalPrecision, column.DecimalScale, column.Name);
        }
    }

    private void CommitSchemaMigration(IReadOnlyList<TableSchema> previousSchemas,
        IReadOnlyList<TableSchema> candidateSchemas, byte[] originalCatalog,
        IReadOnlyDictionary<string, (TableStore Store, TableStore.SchemaMigrationPlan Plan)> plans,
        IReadOnlyList<TableSchemaMigrationUndo> undoTables, Action publishCatalog)
    {
        SqlExecutor.ThrowIfCancellationRequested();
        SchemaMigrationPhaseTestHook?.Invoke("before-barrier");
        bool journalPrepared = false;
        bool completing = false;
        try
        {
            // 屏障必须早于 pending 日志。任意一次失败或恢复都不降回允许旧引擎写入的版本。
            TableSchemaCodec.SaveWithRecoveryBarrier(SchemaPath, previousSchemas,
                minimumVersion: Math.Max(13, TableSchemaCodec.RequiredNativeVersion(candidateSchemas)));
            SchemaMigrationPhaseTestHook?.Invoke("after-barrier");
            TableSchemaMigrationJournal.Prepare(SchemaMigrationJournalPath, originalCatalog, undoTables);
            journalPrepared = true;
            SchemaMigrationPhaseTestHook?.Invoke("after-journal");
            CancellationToken cancellationToken = RoutineExecutionContext.Current?.Options.CancellationToken
                ?? SqlQueryResources.Current?.CancellationToken ?? default;
            foreach (var entry in plans.Values)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                entry.Store.ApplySchemaMigrationRaw(entry.Plan, cancellationToken);
                SchemaMigrationPhaseTestHook?.Invoke("after-table:" + entry.Plan.OriginalSchema.Name);
            }
            SqlExecutor.ThrowIfCancellationRequested();
            TableSchemaCodec.SaveWithRecoveryBarrier(SchemaPath, candidateSchemas);
            SchemaMigrationPhaseTestHook?.Invoke("after-catalog");
            SqlExecutor.ThrowIfCancellationRequested();
            completing = true;
            TableSchemaMigrationJournal.Complete(SchemaMigrationJournalPath);
            SchemaMigrationPhaseTestHook?.Invoke("after-complete");
            foreach (var entry in plans.Values) entry.Store.PublishSchemaMigration(entry.Plan);
            publishCatalog();
        }
        catch (Exception migrationFailure)
        {
            if (!journalPrepared || completing)
                throw InvalidateSchemaMigration(migrationFailure,
                    "主键迁移的屏障、日志或提交决定结果未知，必须关闭并重新打开数据库。");
            try
            {
                foreach (var entry in plans.Values) entry.Store.RestoreSchemaMigrationRaw(entry.Plan.Undo);
                TableSchemaCodec.SaveWithRecoveryBarrier(SchemaPath, previousSchemas, ".ddl-rollback.tmp");
                TableSchemaMigrationJournal.Complete(SchemaMigrationJournalPath);
            }
            catch (Exception rollbackFailure)
            {
                throw InvalidateSchemaMigration(new AggregateException(migrationFailure, rollbackFailure),
                    "主键迁移回滚失败，必须关闭并重新打开数据库完成恢复。");
            }
            throw;
        }
    }

    private TableTransactionRecoveryException InvalidateSchemaMigration(Exception failure, string message)
    {
        _transactionFailure = failure;
        foreach (TableStore store in _stores.Values) store.InvalidateTransaction(failure);
        return new TableTransactionRecoveryException(message, failure);
    }

    private static TableConstraintException SchemaMigrationUnsupported(string tableName, string message)
        => new(TableConstraintException.SchemaEvolutionUnsupported, tableName, "PRIMARY KEY", message);
}
