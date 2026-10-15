using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

public sealed partial class SqlTransactionContext
{
    // 所有隔离级别的缓冲行都绑定首次解释其列序和键语义的不可变 schema。
    // 纯 INSERT 不一定建立 Serializable 读令牌，因此不能用读集代替此写集身份。
    private readonly Dictionary<string, TableSchema> _mutationSchemas = new(StringComparer.Ordinal);

    private void RecordMutationSchema(TableSchema schema)
    {
        ValidateMutationSchema(schema);
        if (_mutationSchemas.TryAdd(schema.Name, schema))
            _undo.Add(() => _mutationSchemas.Remove(schema.Name));
    }

    private void ValidateMutationSchema(TableSchema schema)
    {
        if (_mutationSchemas.TryGetValue(schema.Name, out TableSchema? expected)
            && !ReferenceEquals(expected, schema))
            throw MutationSchemaConflict(schema.Name);
    }

    // 调用方持有 TableManager 锁；在级联解释旧键前及最终提交前分别校验。
    internal void ValidateMutationSchemas(TableCatalog catalog)
    {
        foreach (var (name, expected) in _mutationSchemas)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            CheckCommitDeadline();
            if (!ReferenceEquals(expected, catalog.TryGet(name)))
                throw MutationSchemaConflict(name);
        }
    }

    // 仅供执行计划判断是否必须进入 overlay 路径；消费行时必须提供 schema 校验身份。
    internal bool HasBufferedMutations(string tableName)
        => !_completed && _tableMutations.TryGetValue(tableName, out var buffer) && buffer.Rows.Count > 0;

    private static TableConstraintException MutationSchemaConflict(string name)
        => new(TableConstraintException.SerializationConflict, name, null,
            $"table '{name}' 在事务缓冲写入后发生 schema 变化，旧行不能按新主键或列定义继续解释。", "transaction", hint: null);
}
