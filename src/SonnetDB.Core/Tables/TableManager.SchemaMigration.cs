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
        IReadOnlyList<TableSchema> previousSchemas = Catalog.Snapshot();
        var children = PrepareSchemaMigrationChildren(current, updated, previousSchemas);
        var updatedByName = children.ToDictionary(static child => child.Updated.Name,
            static child => child.Updated, StringComparer.Ordinal);
        updatedByName.Add(updated.Name, updated);
        if (updatedByName.Count > TableSchemaMigrationJournal.MaxTables)
            throw SchemaMigrationUnsupported(current.Name, "迁移涉及的关系表超过 32 张上限。");
        TableSchema[] candidateSchemas = previousSchemas.Select(schema =>
            updatedByName.GetValueOrDefault(schema.Name, schema)).ToArray();
        Action publishCatalog = Catalog.PrepareReplacement(candidateSchemas);
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
                TableSchema schema = Catalog.TryGet(name)
                    ?? throw new InvalidDataException($"主键迁移引用了不存在的 table '{name}'。");
                TableStore store = OpenStoreLocked(schema);
                Monitor.Enter(store.SynchronizationRoot);
                lockedStores.Add(store);
                store.EnsureNoOnlineIndexBuild();
            }
            var budget = new TableSchemaMigrationBudget();
            var plans = new Dictionary<string, (TableStore Store, TableStore.SchemaMigrationPlan Plan)>(StringComparer.Ordinal);
            TableStore principalStore = _stores[current.Name];
            TableStore.SchemaMigrationPlan principalPlan = principalStore.PrepareSchemaMigration(updated, transform,
                (schema, values) =>
                {
                    ValidateSchemaMigrationRow(schema, values);
                    validate?.Invoke(schema, values);
                }, TableRowIdentity.Create, budget);
            plans.Add(current.Name, (principalStore, principalPlan));
            budget.Reserve(principalPlan.Rows.Count * 80L);
            var principalByOriginalKey = principalPlan.Rows.ToDictionary(static row => row.OriginalPrimaryKey,
                static row => row.UpdatedRow, KvKeyComparer.Instance);
            foreach (var child in children)
            {
                TableStore store = _stores[child.Original.Name];
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
        TableSchema current, TableSchema updated, IReadOnlyList<TableSchema> schemas)
    {
        var changedTypes = current.Columns.Where(column =>
            column.DataType != updated.Columns[column.Ordinal].DataType).Select(static column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (current.ForeignKeys.Any(foreignKey => foreignKey.Columns.Any(changedTypes.Contains)))
            throw SchemaMigrationUnsupported(current.Name, "主键类型迁移暂不支持同时承担出站外键的列。");
        var children = new List<SchemaMigrationChild>();
        foreach (var child in schemas)
        {
            TableForeignKey[] references = child.ForeignKeys.Where(foreignKey =>
                string.Equals(foreignKey.PrincipalTable, current.Name, StringComparison.Ordinal)).ToArray();
            if (references.Length == 0) continue;
            if (string.Equals(child.Name, current.Name, StringComparison.Ordinal))
                throw SchemaMigrationUnsupported(current.Name, "主键迁移暂不支持自引用外键。");
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
                    if (column.IsAutoIncrement || column.IsRowVersion)
                        throw SchemaMigrationUnsupported(child.Name, "主键迁移不能改写子表的生成列。");
                    if (column.IsPrimaryKey && HasInboundForeignKeyLocked(child.Name))
                        throw SchemaMigrationUnsupported(child.Name, "主键迁移暂不支持继续级联到被引用的子表主键。");
                }
            }
            TableSchema childUpdated = child;
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

    private static IReadOnlyList<object?> TransformSchemaMigrationChildRow(
        TableSchema current, TableSchema updated, SchemaMigrationChild child, TableRow row,
        IReadOnlyDictionary<byte[], TableRow> principalByOriginalKey)
    {
        object?[] values = row.Values.ToArray();
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
                        ? changed : Catalog.TryGet(foreignKey.PrincipalTable)!;
                    if (!foreignKey.PrincipalColumns.SequenceEqual(principal.PrimaryKey, StringComparer.Ordinal))
                        throw SchemaMigrationUnsupported(schema.Name, "迁移后的外键必须引用完整的目标主键。");
                    IReadOnlyList<object?>? values = ExtractForeignKeyValues(schema, migrationRow.UpdatedRow, foreignKey);
                    if (values is null) continue;
                    bool exists = primaryKeys.TryGetValue(principal.Name, out var preparedKeys)
                        ? preparedKeys.Contains(TableKeyCodec.EncodePrimaryKeyValues(principal, values))
                        : _stores[principal.Name].GetByPrimaryKey(values) is not null;
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
            TableSchemaCodec.SaveWithRecoveryBarrier(SchemaPath, previousSchemas);
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
