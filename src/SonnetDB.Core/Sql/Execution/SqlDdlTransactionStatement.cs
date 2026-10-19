using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>限定 #227 可进入持久关系 DDL workspace 的语句集合。</summary>
internal static class SqlDdlTransactionStatement
{
    internal static bool IsSupported(SqlStatement statement)
        => statement is
            CreateTableStatement { IsTemporary: false, ModbusBinding: null }
            or DropTableStatement
            or CreateTableIndexStatement { DocumentOptions: null }
            or DropTableIndexStatement
            or AlterTableAddColumnStatement
            or AlterTableAlterPrimaryKeyStatement
            or AlterTableAutoIncrementStatement
            or AlterTableAlterColumnStatement
            or AlterTableAddForeignKeyStatement
            or AlterTableAddCheckConstraintStatement
            or AlterTableDropColumnStatement
            or AlterTableDropConstraintStatement
            or AlterTableRenameColumnStatement
            or AlterTableRenameTableStatement;
}
