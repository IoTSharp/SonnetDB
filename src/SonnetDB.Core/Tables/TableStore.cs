using SonnetDB.Kv;

namespace SonnetDB.Tables;

/// <summary>
/// 单个关系表的 KV-backed 行存储。
/// </summary>
public sealed class TableStore : IDisposable
{
    private readonly KvKeyspace _keyspace;

    internal TableStore(TableSchema schema, KvKeyspace keyspace)
    {
        Schema = schema;
        _keyspace = keyspace;
    }

    /// <summary>表 schema。</summary>
    public TableSchema Schema { get; }

    /// <summary>
    /// 插入或覆盖一行。
    /// </summary>
    /// <param name="values">按 schema 列顺序排列的行值。</param>
    public void Upsert(IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateRow(values);
        byte[] key = TableKeyCodec.EncodePrimaryKey(Schema, values);
        byte[] value = TableRowCodec.Encode(Schema, values);
        _keyspace.Put(key, value);
    }

    /// <summary>
    /// 插入一行；若主键已存在则抛出异常。
    /// </summary>
    /// <param name="values">按 schema 列顺序排列的行值。</param>
    public void Insert(IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateRow(values);
        byte[] key = TableKeyCodec.EncodePrimaryKey(Schema, values);
        if (_keyspace.Get(key) is not null)
            throw new InvalidOperationException($"table '{Schema.Name}' 中主键已存在。");

        byte[] value = TableRowCodec.Encode(Schema, values);
        _keyspace.Put(key, value);
    }

    /// <summary>
    /// 按主键读取一行。
    /// </summary>
    /// <param name="primaryKeyValues">主键值。</param>
    /// <returns>找到时返回行；否则返回 null。</returns>
    public TableRow? GetByPrimaryKey(IReadOnlyList<object?> primaryKeyValues)
    {
        byte[] key = TableKeyCodec.EncodePrimaryKeyValues(Schema, primaryKeyValues);
        byte[]? payload = _keyspace.Get(key);
        return payload is null ? null : new TableRow(TableRowCodec.Decode(Schema, payload));
    }

    /// <summary>
    /// 删除主键对应的行。
    /// </summary>
    /// <param name="primaryKeyValues">主键值。</param>
    /// <returns>存在并删除时返回 true。</returns>
    public bool DeleteByPrimaryKey(IReadOnlyList<object?> primaryKeyValues)
    {
        byte[] key = TableKeyCodec.EncodePrimaryKeyValues(Schema, primaryKeyValues);
        return _keyspace.Delete(key);
    }

    /// <summary>
    /// 扫描当前表的所有行，按主键字节序升序返回。
    /// </summary>
    /// <param name="limit">最多返回行数。</param>
    public IReadOnlyList<TableRow> Scan(int? limit = null)
    {
        var entries = _keyspace.ScanPrefix(ReadOnlySpan<byte>.Empty, limit ?? int.MaxValue);
        var rows = new List<TableRow>(entries.Count);
        foreach (var entry in entries)
            rows.Add(new TableRow(TableRowCodec.Decode(Schema, entry.Value.Span)));
        return rows;
    }

    internal void Compact() => _keyspace.Compact();

    /// <summary>
    /// 关闭底层 KV keyspace。
    /// </summary>
    public void Dispose() => _keyspace.Dispose();

    private void ValidateRow(IReadOnlyList<object?> values)
    {
        if (values.Count != Schema.Columns.Count)
            throw new ArgumentException("行值数量必须与表 schema 列数量一致。", nameof(values));

        for (int i = 0; i < Schema.Columns.Count; i++)
        {
            var column = Schema.Columns[i];
            if (values[i] is null && !column.IsNullable)
                throw new InvalidOperationException($"列 '{column.Name}' 不允许为 NULL。");
        }
    }
}
