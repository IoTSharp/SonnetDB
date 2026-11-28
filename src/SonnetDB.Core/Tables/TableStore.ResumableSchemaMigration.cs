using System.Buffers.Binary;
using SonnetDB.Kv;

namespace SonnetDB.Tables;

public sealed partial class TableStore
{
    internal long MigrationSequence => _keyspace.LastSequence;
    internal bool UsesLsmForMigration => _keyspace.LsmBackend is not null;
    internal long? MigrationSharedStorageId => _keyspace.SharedStore is null ? null : _keyspace.SharedStorageId;

    internal void SyncMigrationSource(CancellationToken cancellation)
        => _keyspace.SyncWalForMaintenance(cancellation);

    internal void CloseMigrationSource()
    {
        _automaticStatisticsRefresh?.Cancellation.Cancel();
        _keyspace.CloseForMigrationMove();
    }

    internal void InstallMigrationTarget(TableSchema schema, KvKeyspace target)
    {
        try
        {
            int count = target.LoadTableRowCount();
            _keyspace = target;
            _schema = schema;
            _rowCount = count;
            _statistics = null;
            _statisticsLoadFailureReason = "schema_changed";
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    internal KvBoundedReadPage ReadMigrationRows(byte[]? cursor, int rows, int bytes, CancellationToken cancellation)
        => _keyspace.ReadSchemaMigrationPage((byte)'r', cursor, rows, bytes, cancellation);

    internal static TableRow DecodeMigrationRow(TableSchema schema, KvEntry entry)
        => new(TableRowCodec.Decode(schema, entry.Value.Span),
            TableIndexCodec.DecodePrimaryKeyFromRowKey(entry.Key).ToArray());

    internal TableRow? ReadMigrationRow(byte[] key)
        => TryGetByRowKeyLocked(_schema, TableIndexCodec.EncodePrimaryRowKey(key));

    internal IReadOnlyList<KvBatchMutation> BuildMigrationRow(TableSchema targetSchema, TableRow source,
        IReadOnlyList<object?> values, bool preservesPrimaryKey, KvKeyspace target)
    {
        ValidateRow(targetSchema, values);
        byte[] primaryKey = targetSchema.PrimaryKey.Count != 0 ? TableKeyCodec.EncodePrimaryKey(targetSchema, values)
            : _schema.PrimaryKey.Count == 0 ? source.PrimaryKey.ToArray() : TableRowIdentity.Create();
        if (preservesPrimaryKey && !source.PrimaryKey.Span.SequenceEqual(primaryKey))
            throw new TableConstraintException(TableConstraintException.SchemaEvolutionUnsupported,
                targetSchema.Name, "PRIMARY KEY", "非主键 schema 迁移不能修改现有主键编码。");

        byte[] rowKey = TableIndexCodec.EncodePrimaryRowKey(primaryKey);
        byte[] payload = TableRowCodec.Encode(targetSchema, values);
        target.ValidateWrite(rowKey, payload);
        var mutations = new List<KvBatchMutation>(1 + targetSchema.Indexes.Count) { new(rowKey, payload) };
        foreach (TableIndex index in targetSchema.Indexes)
        {
            byte[]? key = TableIndexCodec.TryEncodeIndexEntryKey(index, values, targetSchema, primaryKey);
            if (key is null) continue;
            byte[] value = TableIndexCodec.EncodeIndexEntryValue(index, targetSchema, values, primaryKey);
            target.ValidateWrite(key, value);
            mutations.Add(new KvBatchMutation(key, value));
        }
        return mutations;
    }

    internal static TableConstraintException MigrationKeyCollision(TableSchema schema, byte[] key,
        IReadOnlyList<object?> values, byte[] primaryKey)
    {
        if (key[0] == (byte)'i')
        {
            foreach (TableIndex index in schema.Indexes)
            {
                if (index.IsUnique && key.AsSpan().SequenceEqual(
                    TableIndexCodec.TryEncodeIndexEntryKey(index, values, schema, primaryKey)))
                    return UniqueViolation(schema, index, "迁移目标值存在重复。");
            }
        }
        return new TableConstraintException(TableConstraintException.UniqueViolation,
            schema.Name, "PRIMARY KEY", $"table '{schema.Name}' 的迁移目标主键存在重复。");
    }

    internal void CopyMigrationIdentityState(TableSchema targetSchema, KvKeyspace target, long rowCount,
        CancellationToken cancellation)
    {
        byte[]? autoIncrement = _keyspace.ReadSchemaMigrationValue(_autoIncrementStateKey, static _ => { }, cancellation);
        if (_schema.AutoIncrementColumn is null && targetSchema.AutoIncrementColumn is not null)
        {
            autoIncrement = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(autoIncrement, rowCount);
        }
        if (autoIncrement is not null)
            ApplySchemaMigrationMutations(target, [new KvBatchMutation(_autoIncrementStateKey, autoIncrement)], cancellation);
        target.SharedStore?.InitializeRowCount(target.SharedStorageId, checked((int)rowCount));
    }
}
