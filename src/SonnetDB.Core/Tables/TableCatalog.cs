using System.Collections.Frozen;

namespace SonnetDB.Tables;

/// <summary>
/// 关系表 schema catalog。
/// </summary>
public sealed partial class TableCatalog
{
    private readonly object _sync = new();
    private Dictionary<TableObjectName, TableSchema> _mutable = new(TableObjectName.ExactComparer);
    private CatalogSnapshot _snapshot = new(
        new Dictionary<TableObjectName, TableSchema>(TableObjectName.ExactComparer)
            .ToFrozenDictionary(TableObjectName.ExactComparer),
        new Dictionary<TableObjectName, TableSchema?>(TableObjectName.UnquotedComparer)
            .ToFrozenDictionary(TableObjectName.UnquotedComparer));

    /// <summary>由所属 TableManager 安装的目录变更守卫；独立 catalog 默认不限制直接变更。</summary>
    internal Action<string, string>? MutationGuard { get; set; }

    /// <summary>当前表数量。</summary>
    public int Count
    {
        get
        {
            if (_session is not null)
                return Snapshot().Count;
            lock (_sync)
                return _mutable.Count;
        }
    }

    /// <summary>
    /// 新增表 schema。
    /// </summary>
    /// <param name="schema">schema。</param>
    public void Add(TableSchema schema)
    {
        RejectSessionMutation();
        ArgumentNullException.ThrowIfNull(schema);
        schema = CanonicalizeSchema(schema);
        GuardMutation(schema, "ADD");
        lock (_sync)
        {
            if (Volatile.Read(ref _snapshot).Unquoted.ContainsKey(schema.ObjectName))
                throw new InvalidOperationException($"table {schema.ObjectName} 已存在。");
            _mutable.Add(schema.ObjectName, schema);
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 加载或替换表 schema，主要用于启动恢复。
    /// </summary>
    /// <param name="schema">schema。</param>
    public void LoadOrReplace(TableSchema schema)
    {
        RejectSessionMutation();
        ArgumentNullException.ThrowIfNull(schema);
        schema = CanonicalizeSchema(schema);
        GuardMutation(schema, "LOAD OR REPLACE");
        lock (_sync)
        {
            if (!_mutable.ContainsKey(schema.ObjectName)
                && Volatile.Read(ref _snapshot).Unquoted.ContainsKey(schema.ObjectName))
                throw new InvalidOperationException($"table {schema.ObjectName} 已存在仅大小写不同的名称。");
            _mutable[schema.ObjectName] = schema;
            PublishSnapshot();
        }
    }

    /// <summary>恢复已有目录时保留历史大小写冲突，由名称解析报告歧义。</summary>
    internal void LoadExisting(TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        schema = CanonicalizeSchema(schema);
        lock (_sync)
        {
            _mutable.Add(schema.ObjectName, schema);
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 删除表 schema。
    /// </summary>
    /// <param name="name">表名。</param>
    /// <returns>存在并删除时返回 true。</returns>
    public bool Remove(string name)
        => Remove(new TableObjectName(name));

    /// <summary>按完整身份删除表 schema。</summary>
    /// <param name="objectName">schema 与表名组成的身份。</param>
    /// <returns>存在并删除时返回 true。</returns>
    public bool Remove(TableObjectName objectName)
    {
        RejectSessionMutation();
        lock (_sync)
        {
            if (!_mutable.TryGetValue(objectName, out TableSchema? schema))
                return false;
            GuardMutation(schema, "REMOVE");
            _mutable.Remove(objectName);
            PublishSnapshot();
            return true;
        }
    }

    /// <summary>
    /// 尝试按名称查找表 schema。
    /// </summary>
    /// <param name="name">表名。</param>
    /// <returns>找到时返回 schema；否则返回 null。</returns>
    public TableSchema? TryGet(string name)
        => string.IsNullOrWhiteSpace(name) ? null : TryGet(new TableObjectName(name));

    /// <summary>按完整且精确的身份查找表 schema。</summary>
    /// <param name="objectName">schema 与表名组成的身份。</param>
    /// <returns>找到时返回 schema；否则返回 null。</returns>
    public TableSchema? TryGet(TableObjectName objectName)
    {
        if (_session is not null)
            return ResolveSession(objectName, schemaIsQuoted: true, nameIsQuoted: true);
        return Volatile.Read(ref _snapshot).Exact.TryGetValue(objectName, out var schema) ? schema : null;
    }

    /// <summary>未加引号名称忽略大小写；加引号名称精确匹配。历史重名必须显式迁移。</summary>
    /// <param name="name">待解析的表名。</param>
    /// <param name="quoted">名称是否使用双引号。</param>
    /// <returns>匹配的表 schema；不存在时返回 <c>null</c>。</returns>
    public TableSchema? Resolve(string name, bool quoted)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : Resolve(new TableObjectName(name), schemaIsQuoted: false, nameIsQuoted: quoted);

    /// <summary>按 schema 与表名各自的引号状态解析关系表。</summary>
    public TableSchema? Resolve(
        string schemaName,
        bool schemaIsQuoted,
        string name,
        bool nameIsQuoted)
        => Resolve(new TableObjectName(schemaName, name), schemaIsQuoted, nameIsQuoted);

    /// <summary>按完整身份及逐段引号状态解析关系表。</summary>
    public TableSchema? Resolve(TableObjectName objectName, bool schemaIsQuoted, bool nameIsQuoted)
    {
        if (_session is not null)
            return ResolveSession(objectName, schemaIsQuoted, nameIsQuoted);
        var snapshot = Volatile.Read(ref _snapshot);
        if (schemaIsQuoted && nameIsQuoted)
            return snapshot.Exact.TryGetValue(objectName, out var exact) ? exact : null;
        IEqualityComparer<TableObjectName> comparer = TableObjectName.CreateComparer(schemaIsQuoted, nameIsQuoted);
        TableSchema? match = null;
        foreach (TableSchema candidate in snapshot.Exact.Values)
        {
            if (!comparer.Equals(objectName, candidate.ObjectName))
                continue;
            if (match is not null)
                throw new InvalidOperationException(
                    $"table {objectName} 存在大小写歧义，请逐段使用双引号精确访问并显式迁移。");
            match = candidate;
        }
        if (match is null)
            return null;
        return match;
    }

    /// <summary>
    /// 返回当前 schema 快照，按表名升序排列。
    /// </summary>
    public IReadOnlyList<TableSchema> Snapshot()
        => _session is null
            ? Volatile.Read(ref _snapshot).Exact.Values
                .OrderBy(static schema => schema.SchemaName, StringComparer.Ordinal)
                .ThenBy(static schema => schema.Name, StringComparer.Ordinal)
                .ToArray()
            : SnapshotSession();

    /// <summary>预先分配整套目录快照，返回落盘成功后无需再分配的原子发布操作。</summary>
    internal Action PrepareReplacement(IReadOnlyList<TableSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        var replacement = new Dictionary<TableObjectName, TableSchema>(schemas.Count, TableObjectName.ExactComparer);
        foreach (var schema in schemas)
        {
            GuardMutation(schema, "REPLACE SNAPSHOT");
            replacement.Add(schema.ObjectName, schema);
        }
        CatalogSnapshot snapshot = CreateSnapshot(replacement);
        return () =>
        {
            lock (_sync)
            {
                _mutable = replacement;
                Volatile.Write(ref _snapshot, snapshot);
            }
        };
    }

    private void PublishSnapshot()
        => Volatile.Write(ref _snapshot, CreateSnapshot(_mutable));

    internal TableSchema CanonicalizeSchema(TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        string canonicalSchema = string.Equals(schema.SchemaName, TableObjectName.DefaultSchemaName,
            StringComparison.OrdinalIgnoreCase)
            ? TableObjectName.DefaultSchemaName
            : Snapshot().FirstOrDefault(candidate => string.Equals(
                candidate.SchemaName, schema.SchemaName, StringComparison.OrdinalIgnoreCase))?.SchemaName
                ?? schema.SchemaName;
        return string.Equals(canonicalSchema, schema.SchemaName, StringComparison.Ordinal)
            ? schema
            : schema.WithObjectName(new TableObjectName(canonicalSchema, schema.Name));
    }

    private static CatalogSnapshot CreateSnapshot(Dictionary<TableObjectName, TableSchema> schemas)
    {
        var unquoted = new Dictionary<TableObjectName, TableSchema?>(schemas.Count, TableObjectName.UnquotedComparer);
        foreach (var schema in schemas.Values)
        {
            if (!unquoted.TryAdd(schema.ObjectName, schema))
                unquoted[schema.ObjectName] = null;
        }
        return new CatalogSnapshot(
            schemas.ToFrozenDictionary(TableObjectName.ExactComparer),
            unquoted.ToFrozenDictionary(TableObjectName.UnquotedComparer));
    }

    private void GuardMutation(TableSchema schema, string operation)
    {
        if (schema.ObjectName.IsDefaultSchema)
            MutationGuard?.Invoke(schema.Name, operation);
    }

    private sealed record CatalogSnapshot(
        FrozenDictionary<TableObjectName, TableSchema> Exact,
        FrozenDictionary<TableObjectName, TableSchema?> Unquoted);
}
