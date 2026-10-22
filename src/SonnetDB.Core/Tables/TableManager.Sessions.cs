using SonnetDB.Sql.Execution;

namespace SonnetDB.Tables;

public sealed partial class TableManager
{
    private TableManager? TemporaryManager(string name) => TemporaryManager(new TableObjectName(name));

    private TableManager? TemporaryManager(TableObjectName objectName)
    {
        TableManager? temporary = SqlSessionContext.TemporaryManagerFor(this);
        if (temporary?.Catalog.TryGet(objectName) is null)
            return null;
        if (_catalog.TryGet(objectName) is not null)
            throw new InvalidOperationException($"table {objectName} 的临时对象与持久对象存在名称歧义。");
        return temporary;
    }

    private bool TryCreateSessionTable(TableSchema schema)
    {
        if (!SqlSessionContext.OwnsPersistentManager(this))
            return false;
        SqlSessionContext session = SqlSessionContext.Current!;
        if (!session.IsMutatingTemporaryObjects)
        {
            if (session.VisibleTables?.Catalog.Resolve(schema.ObjectName, schemaIsQuoted: false, nameIsQuoted: false) is not null)
                throw new InvalidOperationException($"SQL 数据源 {schema.ObjectName} 已被当前连接的临时表占用。");
            if (schema.ForeignKeys.Any(key => session.VisibleTables?.Catalog.TryGet(key.PrincipalObjectName) is not null))
                throw new NotSupportedException("持久表不能将连接临时对象作为外键依赖。");
            return false;
        }
        if (_catalog.Resolve(schema.ObjectName, schemaIsQuoted: false, nameIsQuoted: false) is not null)
            throw new InvalidOperationException($"SQL 数据源 {schema.ObjectName} 已被持久关系表占用。");
        SqlTemporaryTableIdentity.Register(schema, session.Id);
        session.VisibleTables!.Create(schema);
        return true;
    }

    private TableManager? TemporaryMutationManager(
        IReadOnlyDictionary<TableObjectName, IReadOnlyList<TableRowMutation>> mutations)
    {
        TableManager? temporary = SqlSessionContext.TemporaryManagerFor(this);
        if (temporary is null)
            return null;
        int count = mutations.Keys.Count(name => temporary.Catalog.TryGet(name) is not null);
        if (count == 0)
            return null;
        if (count != mutations.Count)
            throw new NotSupportedException("临时对象与持久对象须由 SQL 会话事务协调，不能直接提交混合管理器批次。");
        foreach (TableObjectName name in mutations.Keys)
            if (_catalog.TryGet(name) is not null)
                throw new InvalidOperationException($"table '{name}' 的临时与持久对象存在歧义。");
        return temporary;
    }

    private TableStore OpenReadSourceLocked(TableObjectName name)
    {
        if (TemporaryManager(name) is { } temporary)
            return temporary.Open(name);
        TableSchema schema = _catalog.TryGet(name)
            ?? throw new InvalidOperationException($"table '{name}' 不存在。");
        return OpenStoreLocked(schema);
    }
}
