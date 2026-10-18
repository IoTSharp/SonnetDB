using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static partial class TableSqlExecutor
{
    internal static TableSchema ExecuteCreateTemporaryTable(Tsdb database, CreateTableStatement statement)
    {
        if (statement.ModbusBinding is not null || statement.ForeignKeyClauses.Count != 0)
            throw new NotSupportedException("会话临时表首版不支持 Modbus 绑定或跨对象外键。");
        if (database.Tables.PersistentCatalog.Resolve(statement.Name, quoted: false) is not null)
            throw new InvalidOperationException($"SQL 数据源 '{statement.Name}' 已被持久表占用。");
        SqlSourceNameResolver.EnsureAvailable(database, statement.Name, SqlSourceKind.Table);
        return ExecuteCreateTableLocked(database, statement);
    }
}
