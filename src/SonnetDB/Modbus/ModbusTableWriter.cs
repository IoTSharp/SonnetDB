using System.Buffers;
using SonnetDB.Engine;
using SonnetDB.Tables;

namespace SonnetDB.Modbus;

internal static class ModbusTableWriter
{
    internal static int WriteSuccessfulSample(
        Tsdb database,
        IReadOnlyList<ModbusTableBinding> bindings,
        ModbusReadSnapshot snapshot,
        DateTimeOffset sampledAtUtc,
        ModbusTableWriterState? state = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(snapshot);

        var prepared = new List<PreparedRow>(bindings.Count);
        foreach (ModbusTableBinding binding in bindings)
        {
            TableStore store = database.Tables.Open(binding.TableName);
            TableSchema schema = store.Schema;
            TableRow? previous = HasWriteOnlyMapping(binding)
                ? GetMostRecentRow(store, binding, state)
                : null;
            var values = new object?[schema.Columns.Count];
            var assigned = new bool[schema.Columns.Count];

            foreach (ModbusColumnMapping mapping in binding.Columns)
            {
                TableColumn column = GetMappedColumn(binding, schema, mapping);
                if (mapping.Access == ModbusAccessMode.Write)
                {
                    CopyPreviousValue(previous, column, values, assigned);
                    continue;
                }

                values[column.Ordinal] = Decode(snapshot, mapping);
                assigned[column.Ordinal] = true;
            }

            AssignSampleMetadata(
                binding,
                schema,
                values,
                assigned,
                sampledAtUtc,
                ModbusSampleQuality.Good);
            CompleteUnmappedValues(binding, schema, values, assigned, sampledAtUtc);
            prepared.Add(new PreparedRow(store, binding.TableMode, values));
        }

        return WritePreparedRows(prepared, state, rememberAsSuccessful: true);
    }

    internal static int WriteFailedSample(
        Tsdb database,
        IReadOnlyList<ModbusTableBinding> bindings,
        ModbusReadSnapshot snapshot,
        DateTimeOffset sampledAtUtc,
        ModbusTableWriterState? state = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(snapshot);

        var prepared = new List<PreparedRow>(bindings.Count);
        foreach (ModbusTableBinding binding in bindings)
        {
            if (binding.ErrorPolicy == ModbusErrorPolicy.Skip)
                continue;

            TableStore store = database.Tables.Open(binding.TableName);
            TableSchema schema = store.Schema;
            TableRow? previous = binding.ErrorPolicy == ModbusErrorPolicy.KeepLast
                                 || HasWriteOnlyMapping(binding)
                ? GetMostRecentRow(store, binding, state)
                : null;
            var values = new object?[schema.Columns.Count];
            var assigned = new bool[schema.Columns.Count];
            ModbusSampleQuality quality;

            switch (binding.ErrorPolicy)
            {
                case ModbusErrorPolicy.KeepLast:
                    if (previous is null)
                        continue;
                    for (int index = 0; index < previous.Values.Count; index++)
                        values[index] = previous.Values[index];
                    Array.Fill(assigned, true);
                    ResetHistoryIdentity(binding, schema, values, sampledAtUtc);
                    quality = ModbusSampleQuality.Stale;
                    break;

                case ModbusErrorPolicy.Null:
                    quality = PopulateNullValues(binding, schema, previous, values, assigned);
                    break;

                case ModbusErrorPolicy.MarkBad:
                    quality = PopulateAvailableValues(
                        binding,
                        schema,
                        previous,
                        snapshot,
                        values,
                        assigned);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(bindings),
                        binding.ErrorPolicy,
                        "未知的 Modbus 采集错误策略。");
            }

            AssignSampleMetadata(binding, schema, values, assigned, sampledAtUtc, quality);
            CompleteUnmappedValues(binding, schema, values, assigned, sampledAtUtc);
            prepared.Add(new PreparedRow(store, binding.TableMode, values));
        }

        return WritePreparedRows(prepared, state, rememberAsSuccessful: false);
    }

    private static ModbusSampleQuality PopulateNullValues(
        ModbusTableBinding binding,
        TableSchema schema,
        TableRow? previous,
        object?[] values,
        bool[] assigned)
    {
        bool hasReadableMapping = false;
        foreach (ModbusColumnMapping mapping in binding.Columns)
        {
            TableColumn column = GetMappedColumn(binding, schema, mapping);
            if (mapping.Access == ModbusAccessMode.Write)
            {
                CopyPreviousValue(previous, column, values, assigned);
                continue;
            }

            hasReadableMapping = true;
            values[column.Ordinal] = null;
            assigned[column.Ordinal] = true;
        }

        return hasReadableMapping
            ? ModbusSampleQuality.Bad | ModbusSampleQuality.NoValue
            : ModbusSampleQuality.Bad;
    }

    private static ModbusSampleQuality PopulateAvailableValues(
        ModbusTableBinding binding,
        TableSchema schema,
        TableRow? previous,
        ModbusReadSnapshot snapshot,
        object?[] values,
        bool[] assigned)
    {
        int readable = 0;
        int decoded = 0;
        foreach (ModbusColumnMapping mapping in binding.Columns)
        {
            TableColumn column = GetMappedColumn(binding, schema, mapping);
            if (mapping.Access == ModbusAccessMode.Write)
            {
                CopyPreviousValue(previous, column, values, assigned);
                continue;
            }

            readable++;
            if (TryDecode(snapshot, mapping, out object? value))
            {
                values[column.Ordinal] = value;
                decoded++;
            }
            else
            {
                values[column.Ordinal] = null;
            }
            assigned[column.Ordinal] = true;
        }

        ModbusSampleQuality quality = ModbusSampleQuality.Bad;
        if (decoded < readable)
            quality |= ModbusSampleQuality.NoValue;
        if (decoded > 0 && decoded < readable)
            quality |= ModbusSampleQuality.Partial;
        return quality;
    }

    private static object? Decode(ModbusReadSnapshot snapshot, ModbusColumnMapping mapping)
    {
        ushort[] rented = ArrayPool<ushort>.Shared.Rent(mapping.RegisterCount);
        try
        {
            Span<ushort> raw = rented.AsSpan(0, mapping.RegisterCount);
            snapshot.CopyTo(mapping.Area, mapping.PduAddress, raw);
            return ModbusValueCodec.Decode(
                raw,
                mapping.Area,
                mapping.ValueType,
                mapping.StringLength,
                mapping.BitIndex ?? 0,
                mapping.ByteOrder,
                mapping.WordOrder,
                mapping.Scale,
                mapping.Offset);
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(rented, clearArray: true);
        }
    }

    private static bool TryDecode(
        ModbusReadSnapshot snapshot,
        ModbusColumnMapping mapping,
        out object? value)
    {
        try
        {
            value = Decode(snapshot, mapping);
            return true;
        }
        catch (InvalidDataException)
        {
            value = null;
            return false;
        }
        catch (OverflowException)
        {
            value = null;
            return false;
        }
    }

    private static void AssignSampleMetadata(
        ModbusTableBinding binding,
        TableSchema schema,
        object?[] values,
        bool[] assigned,
        DateTimeOffset sampledAtUtc,
        ModbusSampleQuality quality)
    {
        if (binding.SampleTimeColumn is not null)
        {
            TableColumn sampleTimeColumn = schema.TryGetColumn(binding.SampleTimeColumn)
                ?? throw new InvalidOperationException(
                    $"Modbus table '{binding.TableName}' 不存在 SAMPLE_TIME 列 '{binding.SampleTimeColumn}'。");
            values[sampleTimeColumn.Ordinal] = sampledAtUtc;
            assigned[sampleTimeColumn.Ordinal] = true;
        }

        if (binding.QualityColumn is not null)
        {
            TableColumn qualityColumn = schema.TryGetColumn(binding.QualityColumn)
                ?? throw new InvalidOperationException(
                    $"Modbus table '{binding.TableName}' 不存在 QUALITY 列 '{binding.QualityColumn}'。");
            values[qualityColumn.Ordinal] = (long)quality;
            assigned[qualityColumn.Ordinal] = true;
        }
    }

    private static TableColumn GetMappedColumn(
        ModbusTableBinding binding,
        TableSchema schema,
        ModbusColumnMapping mapping)
        => schema.TryGetColumn(mapping.ColumnName)
           ?? throw new InvalidOperationException(
               $"Modbus table '{binding.TableName}' 不存在映射列 '{mapping.ColumnName}'。");

    private static void CopyPreviousValue(
        TableRow? previous,
        TableColumn column,
        object?[] values,
        bool[] assigned)
    {
        if (previous is null)
            return;
        values[column.Ordinal] = previous.Values[column.Ordinal];
        assigned[column.Ordinal] = true;
    }

    private static TableRow? GetMostRecentRow(
        TableStore store,
        ModbusTableBinding binding,
        ModbusTableWriterState? state)
    {
        if (binding.TableMode == ModbusTableMode.Latest)
            return store.GetByPrimaryKey([0L]);
        object?[]? cached = state?.Get(store);
        if (cached is not null)
            return new TableRow(cached);

        IReadOnlyList<TableRow> rows = store.Scan();
        TableRow? latest = rows.Count == 0 ? null : rows[^1];
        if (latest is not null)
            state?.Remember(store, latest.Values);
        return latest;
    }

    private static bool HasWriteOnlyMapping(ModbusTableBinding binding)
        => binding.Columns.Any(static mapping => mapping.Access == ModbusAccessMode.Write);

    private static void ResetHistoryIdentity(
        ModbusTableBinding binding,
        TableSchema schema,
        object?[] values,
        DateTimeOffset sampledAtUtc)
    {
        if (binding.TableMode != ModbusTableMode.History)
            return;

        foreach (string primaryKeyName in schema.PrimaryKey)
        {
            TableColumn column = schema.TryGetColumn(primaryKeyName)
                ?? throw new InvalidOperationException(
                    $"Modbus table '{binding.TableName}' 的主键列 '{primaryKeyName}' 不存在。");
            if (column.IsAutoIncrement)
                values[column.Ordinal] = null;
            else if (schema.PrimaryKey.Count == 1 && column.DataType == TableColumnType.DateTime)
                values[column.Ordinal] = sampledAtUtc;
            else
                throw new InvalidOperationException(
                    $"Modbus HISTORY table '{binding.TableName}' 无法生成新的采样主键。");
        }
    }

    private static int WritePreparedRows(
        IReadOnlyList<PreparedRow> prepared,
        ModbusTableWriterState? state,
        bool rememberAsSuccessful)
    {
        foreach (PreparedRow row in prepared)
        {
            if (row.TableMode == ModbusTableMode.Latest)
                row.Store.Upsert(row.Values);
            else
                row.Store.Insert(row.Values);
            if (rememberAsSuccessful)
                state?.Remember(row.Store, row.Values);
        }
        return prepared.Count;
    }

    private static void CompleteUnmappedValues(
        ModbusTableBinding binding,
        TableSchema schema,
        object?[] values,
        bool[] assigned,
        DateTimeOffset sampledAtUtc)
    {
        foreach (TableColumn column in schema.Columns)
        {
            if (assigned[column.Ordinal])
                continue;
            if (column.IsAutoIncrement || column.IsNullable)
                continue;

            bool singlePrimaryKey = column.IsPrimaryKey && schema.PrimaryKey.Count == 1;
            if (singlePrimaryKey
                && binding.TableMode == ModbusTableMode.Latest
                && column.DataType == TableColumnType.Int64)
            {
                values[column.Ordinal] = 0L;
                continue;
            }
            if (singlePrimaryKey
                && binding.TableMode == ModbusTableMode.History
                && column.DataType == TableColumnType.DateTime)
            {
                values[column.Ordinal] = sampledAtUtc;
                continue;
            }

            throw new InvalidOperationException(
                $"Modbus table '{binding.TableName}' 的必填列 '{column.Name}' 无法由映射、"
                + "SAMPLE_TIME、QUALITY、自动主键或 LATEST 固定键生成。");
        }
    }

    private sealed record PreparedRow(
        TableStore Store,
        ModbusTableMode TableMode,
        object?[] Values);
}

internal sealed class ModbusTableWriterState
{
    private readonly Dictionary<string, CachedRow> _lastSuccessfulRows = new(StringComparer.Ordinal);

    internal object?[]? Get(TableStore store)
    {
        string tableName = store.Schema.Name;
        if (!_lastSuccessfulRows.TryGetValue(tableName, out CachedRow? cached))
            return null;
        if (ReferenceEquals(cached.Store, store))
            return cached.Values;

        _lastSuccessfulRows.Remove(tableName);
        return null;
    }

    internal void Remember(TableStore store, IReadOnlyList<object?> values)
        => _lastSuccessfulRows[store.Schema.Name] = new CachedRow(store, values.ToArray());

    private sealed record CachedRow(TableStore Store, object?[] Values);
}
