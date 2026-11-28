using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
using SonnetDB.Routines;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Wal;

namespace SonnetDB.Tables;

public sealed partial class TableManager
{
    private const int MigrationPageRows = 256;
    private const int MigrationPageBytes = 32 * 1024 * 1024;
    private const int MigrationWriteBytes = 64 * 1024 * 1024;
    private const int MigrationPagesPerAttempt = 200_000;
    private static readonly TimeSpan _migrationAttemptLimit = TimeSpan.FromMinutes(20);

    internal bool ForceResumableSchemaMigrationForTests { get; set; }
    internal Action<string>? ResumableSchemaMigrationPhaseTestHook { get; set; }

    private string ResumableMigrationDirectory => Path.Combine(
        _rootDirectory, "rowstore", TableResumableSchemaMigrationJournal.DirectoryName);

    private bool HasPendingResumableSchemaMigration()
        => File.Exists(Path.Combine(ResumableMigrationDirectory, TableResumableSchemaMigrationJournal.ManifestName));

    internal void DiscardUncommittedMigrationInDdlWorkspace()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            ResumableMigrationState? state = TableResumableSchemaMigrationJournal.Read(ResumableMigrationDirectory);
            if (state is not null) DiscardResumableMigration(ResumableMigrationDirectory, state);
        }
    }

    private void RecoverResumableSchemaMigrationBeforeOpeningStores()
    {
        string directory = ResumableMigrationDirectory;
        ResumableMigrationState? state = TableResumableSchemaMigrationJournal.Read(directory);
        if (state is null) return;
        if (TableSchemaCodec.ReadFormatVersion(SchemaPath) < TableSchemaCodec.ResumableMigrationVersion)
            throw new InvalidDataException("分批迁移缺少关系目录恢复屏障。");
        if (state.Discarding)
            TableResumableSchemaMigrationJournal.Discard(directory, state);
        else if (state.Committing)
            TableResumableSchemaMigrationJournal.Complete(directory, SchemaPath, state,
                phase => SchemaMigrationRecoveryPhaseTestHook?.Invoke(_rootDirectory, phase));
        else
        {
            for (int index = 0; index < state.Tables.Length; index++)
                TableResumableSchemaMigrationJournal.CheckStage(directory, state, index);
        }
    }

    private sealed record MigrationParticipant(TableSchema Source, TableSchema Target, TableStore Store,
        Func<TableRow, long, IReadOnlyList<object?>> Transform, Action<TableSchema, IReadOnlyList<object?>>? Validate,
        bool PreservesPrimaryKey);

    private void ApplyResumableSchemaMigration(
        TableSchema current, TableSchema updated, TableSchema principalTarget,
        IReadOnlyList<SchemaMigrationChild> children, SchemaMigrationChild? selfReference,
        bool preservesPrimaryKey, IReadOnlyList<TableSchema> previousSchemas, IReadOnlyList<TableSchema> candidateSchemas,
        IReadOnlyCollection<string> lockNames, Func<TableSchema, TableRow, IReadOnlyList<object?>> transform,
        Action<TableSchema, IReadOnlyList<object?>>? validate,
        Func<TableRow, long, IReadOnlyList<object?>>? backfill, object? backfillValue, Action publishCatalog)
    {
        CancellationToken cancellation = RoutineExecutionContext.Current?.Options.CancellationToken
            ?? SqlQueryResources.Current?.CancellationToken ?? default;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(_migrationAttemptLimit);
        cancellation = deadline.Token;
        long started = Stopwatch.GetTimestamp();
        TableStore principalStore = _stores[current.ObjectName];
        IReadOnlyList<object?> TransformPrincipal(TableRow row, long number)
            => backfill is null ? transform(current, row) : backfill(row, number);
        TableRow? FindPrincipal(byte[] key)
        {
            TableRow? row = principalStore.ReadMigrationRow(key);
            return row is null ? null : new TableRow(TransformPrincipal(row, 0).ToArray(), key);
        }

        var tables = new List<MigrationParticipant>(children.Count + 1)
        {
            new(current, principalTarget, principalStore,
                (row, number) =>
                {
                    IReadOnlyList<object?> values = TransformPrincipal(row, number);
                    return selfReference is null ? values
                        : TransformSchemaMigrationChildRow(current, updated, selfReference, row, FindPrincipal, values);
                }, validate, preservesPrimaryKey),
        };
        foreach (SchemaMigrationChild child in children)
        {
            tables.Add(new MigrationParticipant(child.Original, child.Updated, _stores[child.Original.ObjectName],
                (row, _) => TransformSchemaMigrationChildRow(current, updated, child, row, FindPrincipal),
                null, false));
        }

        string directory = ResumableMigrationDirectory;
        byte[] candidate = TableSchemaCodec.EncodeResumableMigrationCatalog(candidateSchemas);
        var budget = new TableSchemaMigrationBudget();
        budget.SetContext(current.Name, lockNames.Count);
        var sourceCatalog = new FileInfo(SchemaPath);
        if (!sourceCatalog.Exists)
            throw SchemaMigrationUnsupported(current.Name, "原关系目录不存在。");
        long catalogBytes = Math.Max(sourceCatalog.Length, candidate.LongLength);
        if (catalogBytes > TableSchemaMigrationJournal.MaxCatalogBytes)
            throw budget.CreateJournalExceeded(TableSchemaMigrationBudget.JournalCatalogBytesKind,
                sourceCatalog.Length, catalogBytes, TableSchemaMigrationJournal.MaxCatalogBytes);

        SqlExecutor.ThrowIfCancellationRequested();
        foreach (string name in lockNames)
            _stores[_catalog.TryGet(name)!.ObjectName].SyncMigrationSource(cancellation);
        ResumableMigrationState? state = TableResumableSchemaMigrationJournal.Read(directory);
        if (state is { Committing: true })
            throw InvalidateSchemaMigration(new InvalidDataException("分批迁移已进入持久切换。"),
                "分批迁移需要关闭数据库并重开以完成切换。");
        if (TableSchemaCodec.ReadFormatVersion(SchemaPath) < TableSchemaCodec.ResumableMigrationVersion)
            TableSchemaCodec.SaveWithRecoveryBarrier(SchemaPath, previousSchemas,
                minimumVersion: TableSchemaCodec.ResumableMigrationVersion);

        var sourceTables = new ResumableMigrationTable[tables.Count];
        string rowstore = Path.GetDirectoryName(directory)!;
        for (int index = 0; index < tables.Count; index++)
        {
            MigrationParticipant table = tables[index];
            string sourceDirectory = TableDirectory(table.Source.ObjectName);
            sourceTables[index] = new ResumableMigrationTable(Path.GetRelativePath(rowstore, sourceDirectory),
                table.Store.Generation, table.Store.MigrationSequence)
            {
                SourceLayoutHash = TableResumableSchemaMigrationJournal.ReadSourceLayoutHash(sourceDirectory),
            };
        }

        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (backfill is not null)
        {
            var values = new object?[updated.Columns.Count];
            values[^1] = backfillValue;
            identity.AppendData(TableRowCodec.Encode(updated, values));
        }
        Span<byte> revision = stackalloc byte[2 * sizeof(long)];
        foreach (string name in lockNames.Order(StringComparer.Ordinal))
        {
            TableStore locked = _stores[_catalog.TryGet(name)!.ObjectName];
            BinaryPrimitives.WriteInt64LittleEndian(revision, locked.Generation);
            BinaryPrimitives.WriteInt64LittleEndian(revision[sizeof(long)..], locked.MigrationSequence);
            identity.AppendData(revision);
        }

        byte[] requestIdentity = identity.GetHashAndReset();
        byte[] sourceHash = TableResumableSchemaMigrationJournal.ReadCatalogHash(SchemaPath);
        if (state is not null && (state.Discarding || !state.SourceCatalogHash.AsSpan().SequenceEqual(sourceHash)
            || !state.CandidateCatalog.AsSpan().SequenceEqual(candidate)
            || !state.RequestIdentity.AsSpan().SequenceEqual(requestIdentity)
            || !SameMigrationSources(state.Tables, sourceTables)))
        {
            DiscardResumableMigration(directory, state);
            state = null;
        }
        if (state is null)
        {
            state = new ResumableMigrationState(sourceHash, candidate, sourceTables, 0, null, false)
            {
                RequestIdentity = requestIdentity,
            };
            TableResumableSchemaMigrationJournal.Save(directory, state);
        }

        bool rejected = false;
        var targets = new List<KvKeyspace>(tables.Count);
        try
        {
            for (int i = 0; i < tables.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                string stage = TableResumableSchemaMigrationJournal.StageDirectory(directory, i);
                TableResumableSchemaMigrationJournal.PrepareStage(directory, state, i);
                KvKeyspace target = OpenMigrationTarget(tables[i], stage);
                targets.Add(target);
                target.SyncWalForMaintenance(cancellation);
                TableResumableSchemaMigrationJournal.RecordStageLayout(stage);
            }

            RecoverResumableMigrationPage(directory, state, targets, cancellation);
            for (int pages = 0; state.Step < tables.Count * 2; pages++)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                cancellation.ThrowIfCancellationRequested();
                if (pages >= MigrationPagesPerAttempt || Stopwatch.GetElapsedTime(started) >= _migrationAttemptLimit)
                    throw new TimeoutException("分批迁移到达本次执行预算。重新提交相同 ALTER 可继续。");

                int index = state.Step % tables.Count;
                state = state.Step < tables.Count
                    ? CopyResumableMigrationPage(directory, state, tables[index], targets[index], budget, cancellation)
                    : ValidateResumableMigrationPage(directory, state, tables, targets, index, cancellation);
            }
        }
        catch (TableConstraintException)
        {
            rejected = true;
            throw;
        }
        finally
        {
            foreach (KvKeyspace target in targets) target.Dispose();
            if (rejected)
            {
                foreach (KvKeyspace target in targets) target.EnsureMigrationDirectoryReleased();
                DiscardResumableMigration(directory, state);
            }
        }

        foreach (KvKeyspace target in targets) target.EnsureMigrationDirectoryReleased();

        ResumableSchemaMigrationPhaseTestHook?.Invoke("before-commit");
        SqlExecutor.ThrowIfCancellationRequested();
        cancellation.ThrowIfCancellationRequested();
        try
        {
            state = state with { Committing = true };
            TableResumableSchemaMigrationJournal.Save(directory, state);
            ResumableSchemaMigrationPhaseTestHook?.Invoke("commit-intent");
            long?[] originalStorageIds = tables.Select(table => table.Store.MigrationSharedStorageId).ToArray();
            foreach (MigrationParticipant table in tables) table.Store.CloseMigrationSource();

            TableResumableSchemaMigrationJournal.Complete(directory, SchemaPath, state,
                ResumableSchemaMigrationPhaseTestHook);
            foreach (MigrationParticipant table in tables)
            {
                KvKeyspace target = OpenTableKeyspace(KeyspaceName(table.Target.ObjectName), TableDirectory(table.Target.ObjectName));
                table.Store.InstallMigrationTarget(table.Target, target);
            }
            publishCatalog();
            ReleaseMigrationSharedStorage(originalStorageIds);
        }
        catch (Exception failure)
        {
            throw InvalidateSchemaMigration(failure, "分批迁移的切换结果需要关闭并重新打开数据库确认。");
        }
    }

    private static bool SameMigrationSources(ResumableMigrationTable[] previous, ResumableMigrationTable[] current)
    {
        if (previous.Length != current.Length) return false;
        for (int index = 0; index < previous.Length; index++)
        {
            ResumableMigrationTable left = previous[index], right = current[index];
            if (!string.Equals(left.RelativeDirectory, right.RelativeDirectory, StringComparison.Ordinal)
                || left.SourceGeneration != right.SourceGeneration || left.SourceSequence != right.SourceSequence
                || !left.SourceLayoutHash.AsSpan().SequenceEqual(right.SourceLayoutHash))
                return false;
        }
        return true;
    }

    private void DiscardResumableMigration(string directory, ResumableMigrationState state)
    {
        var ids = new long?[state.Tables.Length];
        for (int index = 0; index < ids.Length; index++)
            ids[index] = TryGetSharedStorageId(TableResumableSchemaMigrationJournal.StageDirectory(directory, index));
        TableResumableSchemaMigrationJournal.Discard(directory, state);
        ReleaseMigrationSharedStorage(ids);
    }

    private void ReleaseMigrationSharedStorage(IEnumerable<long?> ids)
    {
        foreach (long? id in ids)
            ReleaseSharedStorage(id);
    }

    private KvKeyspace OpenMigrationTarget(MigrationParticipant table, string stage)
    {
        string name = KeyspaceName(table.Target.ObjectName);
        if (TryGetSharedStorageId(stage) is not null
            || (table.Store.UsesLsmForMigration && _kvOptions.StorageEngine == KvStorageEngine.Lsm))
            return OpenTableKeyspace(name, stage);
        return KvKeyspace.Open(name, stage, _kvOptions with
        {
            StorageEngine = table.Store.UsesLsmForMigration ? KvStorageEngine.Lsm : KvStorageEngine.Legacy,
            LegacyMigration = KvLegacyMigrationMode.Disabled,
        }, _diskReadBudget);
    }

    private static void RecoverResumableMigrationPage(string directory, ResumableMigrationState state,
        IReadOnlyList<KvKeyspace> targets, CancellationToken cancellation)
    {
        ResumableMigrationPending? pending = TableResumableSchemaMigrationJournal.ReadPending(directory);
        if (pending is null) return;
        int relativeCursor = pending.Cursor.AsSpan().SequenceCompareTo(state.Cursor);
        if (pending.MigrationId != state.Id || pending.Step >= targets.Count
            || pending.Step > state.Step || pending.Step < state.Step - 1
            || (pending.Step == state.Step && relativeCursor > 0))
            throw new InvalidDataException("分批迁移单批补偿与 checkpoint 不一致。");
        if (pending.Step == state.Step && relativeCursor == 0)
        {
            TableStore.ApplySchemaMigrationMutations(targets[state.Step],
                pending.Keys.Select(static key => new KvBatchMutation(key, null)).ToArray(), cancellation);
        }
        TableResumableSchemaMigrationJournal.ClearPending(directory);
    }

    private ResumableMigrationState CopyResumableMigrationPage(string directory, ResumableMigrationState state,
        MigrationParticipant table, KvKeyspace target, TableSchemaMigrationBudget budget, CancellationToken cancellation)
    {
        budget.SetContext(table.Source.Name, budget.ParticipatingTables);
        KvBoundedReadPage page = table.Store.ReadMigrationRows(state.Cursor, MigrationPageRows, MigrationPageBytes, cancellation);
        long rows = state.RowsInStep;
        var mutations = new List<KvBatchMutation>();
        var keys = new HashSet<byte[]>(KvKeyComparer.Instance);
        long writeBytes = 0;
        long pendingBytes = (state.Cursor?.Length ?? 0) + 128L;
        byte[]? lastWrittenRow = null;
        bool partial = false;
        foreach (KvEntry entry in page.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            TableRow row = TableStore.DecodeMigrationRow(table.Source, entry);
            IReadOnlyList<object?> values = table.Transform(row, checked(rows + 1));
            ValidateSchemaMigrationRow(table.Target, values);
            table.Validate?.Invoke(table.Target, values);
            IReadOnlyList<KvBatchMutation> rowMutations = table.Store.BuildMigrationRow(
                table.Target, row, values, table.PreservesPrimaryKey, target);
            long rowBytes = rowMutations.Sum(static item => 32L + item.Key.Length + item.Value!.Length);
            long rowKeysBytes = rowMutations.Sum(static item => sizeof(int) + (long)item.Key.Length);
            bool exceedsWorkingBytes = writeBytes + rowBytes > MigrationWriteBytes;
            bool exceedsJournalBytes = pendingBytes + rowKeysBytes > TableResumableSchemaMigrationJournal.MaxPendingBytes;
            bool exceedsJournalEntries = mutations.Count + rowMutations.Count > TableResumableSchemaMigrationJournal.MaxPendingKeys;
            if (exceedsWorkingBytes || exceedsJournalBytes || exceedsJournalEntries)
            {
                if (mutations.Count == 0)
                {
                    if (exceedsWorkingBytes)
                        throw budget.CreateExceeded(TableSchemaMigrationBudget.WorkingBytesKind,
                            rowBytes, MigrationWriteBytes, rows, writeBytes);
                    if (exceedsJournalBytes)
                        throw budget.CreateJournalExceeded(TableSchemaMigrationBudget.JournalBytesKind,
                            pendingBytes, rowKeysBytes, TableResumableSchemaMigrationJournal.MaxPendingBytes);
                    throw budget.CreateJournalExceeded(TableSchemaMigrationBudget.JournalEntriesKind,
                        mutations.Count, rowMutations.Count, TableResumableSchemaMigrationJournal.MaxPendingKeys);
                }
                partial = true;
                break;
            }
            foreach (KvBatchMutation mutation in rowMutations)
            {
                if (!keys.Add(mutation.Key) || target.ReadSchemaMigrationValue(mutation.Key, static _ => { }, cancellation) is not null)
                    throw TableStore.MigrationKeyCollision(table.Target, mutation.Key, values,
                        TableIndexCodec.DecodePrimaryKeyFromRowKey(rowMutations[0].Key).ToArray());
                mutations.Add(mutation);
            }
            writeBytes += rowBytes;
            pendingBytes += rowKeysBytes;
            rows++;
            lastWrittenRow = entry.Key.ToArray();
        }

        if (mutations.Count != 0)
        {
            TableResumableSchemaMigrationJournal.SavePending(directory,
                new ResumableMigrationPending(state.Id, state.Step, state.Cursor, mutations.Select(static item => item.Key).ToArray()));
            ResumableSchemaMigrationPhaseTestHook?.Invoke("page-pending");
            cancellation.ThrowIfCancellationRequested();
            TableStore.ApplySchemaMigrationMutations(target, mutations, cancellation);
            ResumableSchemaMigrationPhaseTestHook?.Invoke("page-data-durable");
        }

        bool hasMore = partial || page.Truncated;
        byte[]? next = partial ? lastWrittenRow : page.NextKey;
        if (!hasMore)
        {
            table.Store.CopyMigrationIdentityState(table.Target, target, rows, cancellation);
            target.SyncWalForMaintenance(cancellation);
        }
        ResumableMigrationState checkpoint = SaveMigrationProgress(directory, state, hasMore, next, rows);
        ResumableSchemaMigrationPhaseTestHook?.Invoke("page-checkpointed");
        if (mutations.Count != 0) TableResumableSchemaMigrationJournal.ClearPending(directory);
        return checkpoint;
    }

    private ResumableMigrationState ValidateResumableMigrationPage(string directory, ResumableMigrationState state,
        IReadOnlyList<MigrationParticipant> tables, IReadOnlyList<KvKeyspace> targets, int index, CancellationToken cancellation)
    {
        MigrationParticipant table = tables[index];
        KvKeyspace target = targets[index];
        KvBoundedReadPage page = table.Target.ForeignKeys.Count == 0
            ? new KvBoundedReadPage([], false, null)
            : target.ReadSchemaMigrationPage((byte)'r', state.Cursor, MigrationPageRows, MigrationPageBytes, cancellation);
        foreach (KvEntry entry in page.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            TableRow row = TableStore.DecodeMigrationRow(table.Target, entry);
            foreach (TableForeignKey foreignKey in table.Target.ForeignKeys)
            {
                int principalIndex = -1;
                for (int candidate = 0; candidate < tables.Count; candidate++)
                {
                    if (tables[candidate].Target.ObjectName == foreignKey.PrincipalObjectName)
                    {
                        principalIndex = candidate;
                        break;
                    }
                }
                TableSchema principal = principalIndex >= 0 ? tables[principalIndex].Target
                    : _catalog.TryGet(foreignKey.PrincipalObjectName)
                        ?? throw new InvalidDataException($"分批迁移外键缺少父表 {foreignKey.PrincipalObjectName}。");
                if (!foreignKey.PrincipalColumns.SequenceEqual(principal.PrimaryKey, StringComparer.Ordinal))
                    throw SchemaMigrationUnsupported(table.Target.Name, "迁移后的外键必须引用完整的目标主键。");
                IReadOnlyList<object?>? values = ExtractForeignKeyValues(table.Target, row, foreignKey);
                if (values is null) continue;
                byte[] key = TableKeyCodec.EncodePrimaryKeyValues(principal, values);
                bool exists = principalIndex >= 0
                    ? targets[principalIndex].ReadSchemaMigrationValue(TableIndexCodec.EncodePrimaryRowKey(key),
                        static _ => { }, cancellation) is not null
                    : _stores[principal.ObjectName].ReadMigrationRow(key) is not null;
                if (!exists)
                    throw new TableConstraintException(TableConstraintException.ForeignKeyViolation, table.Target.Name,
                        foreignKey.Name, "主键迁移后的行不满足外键约束。");
            }
        }
        if (!page.Truncated)
        {
            if (target.LsmBackend is null)
                target.CheckpointMigrationTarget(cancellation);
            byte[] fingerprint = TableStoreMaintenanceFile.ComputeSchemaFingerprint(table.Target);
            TableStoreMaintenanceFile.MarkLegacyMigrationComplete(target, target.Generation, fingerprint);
            TableStoreMaintenanceFile.MarkIndexesClean(target, target.Generation, target.LastSequence, fingerprint);
            target.SyncWalForMaintenance(cancellation);
            DirectoryFsync.FlushRequired(target.RootDirectory);
        }
        return SaveMigrationProgress(directory, state, page.Truncated, page.NextKey, 0);
    }

    private static ResumableMigrationState SaveMigrationProgress(string directory, ResumableMigrationState state,
        bool hasMore, byte[]? next, long rows)
    {
        if (hasMore && (next is null || (state.Cursor is not null
            && state.Cursor.AsSpan().SequenceCompareTo(next) >= 0)))
            throw new InvalidDataException("分批迁移主键游标未推进。");
        ResumableMigrationState checkpoint = hasMore
            ? state with { Cursor = next, RowsInStep = rows }
            : state with { Step = state.Step + 1, Cursor = null, RowsInStep = 0 };
        TableResumableSchemaMigrationJournal.Save(directory, checkpoint);
        return checkpoint;
    }
}
