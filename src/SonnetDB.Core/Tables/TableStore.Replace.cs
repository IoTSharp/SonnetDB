using SonnetDB.Kv;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Tables;

public sealed partial class TableStore
{
    /// <summary>按原生主键和全部唯一索引读取替换候选，不扫描整张表；NULL 唯一键不构成冲突。</summary>
    internal IReadOnlyList<TableRow> FindReplacementConflicts(IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_sync)
        {
            ThrowIfDisposedLocked();
            TableSchema schema = _schema;
            ValidateRow(schema, values);
            var conflicts = new List<TableRow>();
            var seen = new HashSet<byte[]>(KvKeyComparer.Instance);

            if (schema.PrimaryKey.Count != 0)
                Add(TableKeyCodec.EncodePrimaryKey(schema, values));

            foreach (TableIndex index in schema.Indexes)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                if (!index.IsUnique)
                    continue;
                byte[]? key = TableIndexCodec.TryEncodeIndexEntryKey(index, values, schema, ReadOnlySpan<byte>.Empty);
                if (key is null)
                    continue;
                byte[]? entry = _keyspace.Get(key);
                if (entry is not null)
                    Add(TableIndexCodec.DecodeIndexPrimaryKey(index, entry).ToArray());
            }

            return conflicts;

            void Add(byte[] primaryKey)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                if (!seen.Add(primaryKey))
                    return;
                TableRow? row = TryGetByRowKeyLocked(schema, TableIndexCodec.EncodePrimaryRowKey(primaryKey));
                if (row is not null)
                    conflicts.Add(row);
            }
        }
    }

    /// <summary>先核对整批最终唯一键，再忽略本批已经移除的旧索引所有者。</summary>
    private void ValidateFinalUniqueIndexesLocked(TableSchema schema, IReadOnlyList<TableMutationOperation> operations)
    {
        var removedOwners = new HashSet<byte[]>(KvKeyComparer.Instance);
        var pendingKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (TableMutationOperation operation in operations)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (operation.OldRow is not null)
                removedOwners.Add(operation.OldRow.PrimaryKey.ToArray());
            if (operation.NewRow is not null)
                ValidatePendingUniqueIndexes(schema, operation.NewRow, pendingKeys);
        }

        foreach (TableMutationOperation operation in operations)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (operation.NewRow is not { } row)
                continue;
            foreach (TableIndex index in schema.Indexes)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                if (!index.IsUnique)
                    continue;
                byte[]? key = TableIndexCodec.TryEncodeIndexEntryKey(index, row.Values, schema, row.PrimaryKey.Span);
                if (key is null)
                    continue;
                byte[]? entry = _keyspace.Get(key);
                if (entry is null)
                    continue;
                byte[] owner = TableIndexCodec.DecodeIndexPrimaryKey(index, entry).ToArray();
                if (!owner.AsSpan().SequenceEqual(row.PrimaryKey.Span) && !removedOwners.Contains(owner))
                    throw UniqueViolation(schema, index);
            }
        }
    }

    /// <summary>先移除全部旧行和旧索引，再构造全部最终行，避免旧键清理覆盖本批新索引。</summary>
    private Dictionary<byte[], byte[]?> BuildFinalMutationValues(
        TableSchema schema, IReadOnlyList<TableMutationOperation> operations)
    {
        var desired = new Dictionary<byte[], byte[]?>(KvKeyComparer.Instance);
        foreach (TableMutationOperation operation in operations)
        {
            if (operation.OldRow is null)
                continue;
            foreach (IndexEntry entry in BuildMutationIndexEntries(schema, operation.OldRow))
                desired[entry.Key] = null;
            desired[operation.RowKey] = null;
        }
        foreach (TableMutationOperation operation in operations)
        {
            if (operation.NewRow is not { } row)
                continue;
            desired[TableIndexCodec.EncodePrimaryRowKey(row.PrimaryKey.Span)] = TableRowCodec.Encode(schema, row.Values);
            foreach (IndexEntry entry in BuildMutationIndexEntries(schema, row))
                desired[entry.Key] = entry.Value;
        }
        return desired;
    }
}
