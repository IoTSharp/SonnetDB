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
        DateTimeOffset sampledAtUtc)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(snapshot);

        int written = 0;
        foreach (ModbusTableBinding binding in bindings)
        {
            TableStore store = database.Tables.Open(binding.TableName);
            TableSchema schema = store.Schema;
            var values = new object?[schema.Columns.Count];
            var assigned = new bool[schema.Columns.Count];

            foreach (ModbusColumnMapping mapping in binding.Columns)
            {
                if (mapping.Access == ModbusAccessMode.Write)
                    continue;

                TableColumn column = schema.TryGetColumn(mapping.ColumnName)
                    ?? throw new InvalidOperationException(
                        $"Modbus table '{binding.TableName}' 不存在映射列 '{mapping.ColumnName}'。");
                ushort[] rented = ArrayPool<ushort>.Shared.Rent(mapping.RegisterCount);
                try
                {
                    Span<ushort> raw = rented.AsSpan(0, mapping.RegisterCount);
                    snapshot.CopyTo(mapping.Area, mapping.PduAddress, raw);
                    values[column.Ordinal] = ModbusValueCodec.Decode(
                        raw,
                        mapping.Area,
                        mapping.ValueType,
                        mapping.StringLength,
                        mapping.BitIndex ?? 0,
                        mapping.ByteOrder,
                        mapping.WordOrder,
                        mapping.Scale,
                        mapping.Offset);
                    assigned[column.Ordinal] = true;
                }
                finally
                {
                    ArrayPool<ushort>.Shared.Return(rented, clearArray: true);
                }
            }

            if (binding.SampleTimeColumn is not null)
            {
                TableColumn sampleTimeColumn = schema.TryGetColumn(binding.SampleTimeColumn)
                    ?? throw new InvalidOperationException(
                        $"Modbus table '{binding.TableName}' 不存在 SAMPLE_TIME 列 '{binding.SampleTimeColumn}'。");
                values[sampleTimeColumn.Ordinal] = sampledAtUtc;
                assigned[sampleTimeColumn.Ordinal] = true;
            }

            CompleteUnmappedValues(binding, schema, values, assigned, sampledAtUtc);
            if (binding.TableMode == ModbusTableMode.Latest)
                store.Upsert(values);
            else
                store.Insert(values);
            written++;
        }

        return written;
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
                + "SAMPLE_TIME、自动主键或 LATEST 固定键生成。");
        }
    }
}
