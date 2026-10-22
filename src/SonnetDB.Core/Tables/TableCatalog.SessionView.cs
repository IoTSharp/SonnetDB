using SonnetDB.Sql.Execution;

namespace SonnetDB.Tables;

public sealed partial class TableCatalog
{
    private readonly TableCatalog? _persistent;
    private readonly SqlSessionContext? _session;

    /// <summary>创建独立的持久表目录。</summary>
    public TableCatalog()
    {
    }

    internal TableCatalog(TableCatalog persistent, SqlSessionContext session)
    {
        _persistent = persistent;
        _session = session;
    }

    private void RejectSessionMutation()
    {
        if (_session is not null)
            throw new InvalidOperationException("连接会话的联合目录是只读视图，不能直接发布或持久化。");
    }

    private TableSchema? ResolveSession(TableObjectName objectName, bool schemaIsQuoted, bool nameIsQuoted)
    {
        TableSchema? persistent = _persistent!.Resolve(objectName, schemaIsQuoted, nameIsQuoted);
        TableSchema? temporary = _session!.VisibleTables?.Catalog.Resolve(objectName, schemaIsQuoted, nameIsQuoted);
        if (persistent is not null && temporary is not null)
            throw new InvalidOperationException($"SQL 数据源 {objectName} 的持久表与连接临时表存在名称歧义。");
        return temporary ?? persistent;
    }

    private IReadOnlyList<TableSchema> SnapshotSession()
    {
        IReadOnlyList<TableSchema> persistent = _persistent!.Snapshot();
        IReadOnlyList<TableSchema> temporary = _session!.VisibleTables?.Catalog.Snapshot() ?? [];
        var names = new HashSet<TableObjectName>(persistent.Select(static schema => schema.ObjectName), TableObjectName.UnquotedComparer);
        foreach (TableSchema schema in temporary)
            if (!names.Add(schema.ObjectName))
                throw new InvalidOperationException($"SQL 数据源 {schema.ObjectName} 的持久表与连接临时表存在名称歧义。");
        return persistent.Concat(temporary)
            .OrderBy(static schema => schema.SchemaName, StringComparer.Ordinal)
            .ThenBy(static schema => schema.Name, StringComparer.Ordinal)
            .ToArray();
    }
}
