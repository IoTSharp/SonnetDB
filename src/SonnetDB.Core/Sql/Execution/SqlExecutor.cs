using System.Globalization;
using SonnetDB.Catalog;
using SonnetDB.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Routines;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;
using SonnetDB.Tables;
using SonnetDB.Views;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 把 <see cref="SqlStatement"/> AST 应用到 <see cref="Tsdb"/> 实例的执行器。
/// 当前 Milestone 支持 <see cref="CreateMeasurementStatement"/>、<see cref="InsertStatement"/>、
/// <see cref="SelectStatement"/> 与 <see cref="DeleteStatement"/>。
/// </summary>
public static class SqlExecutor
{
    private static readonly IReadOnlyList<string> _nameColumns =
        new List<string>(1) { "name" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _describeMeasurementColumns =
        new List<string>(3) { "column_name", "column_type", "data_type" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _showViewColumns =
        new List<string>(2) { "name", "created_utc" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _describeViewColumns =
        new List<string>(4) { "name", "definition", "dependencies", "created_utc" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _showMaterializedViewColumns =
        new List<string>(7)
        {
            "name", "status", "definition_version", "active_generation", "row_count", "refreshed_utc", "error"
        }.AsReadOnly();
    private static readonly IReadOnlyList<string> _describeMaterializedViewColumns =
        new List<string>(11)
        {
            "name", "definition", "dependencies", "definition_version", "status", "active_generation",
            "row_count", "created_utc", "last_refresh_utc", "last_successful_refresh_utc", "error"
        }.AsReadOnly();
    private static readonly IReadOnlyList<string> _showProcedureColumns =
        new List<string>(5) { "name", "parameters", "language", "requires_write", "created_utc" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _describeProcedureColumns =
        new List<string>(8)
        {
            "name", "parameters", "language", "body", "object_dependencies",
            "procedure_dependencies", "requires_write", "created_utc"
        }.AsReadOnly();
    private static readonly IReadOnlyList<string> _showTriggerColumns =
        new List<string>(5) { "name", "table_name", "event", "when", "created_utc" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _describeTriggerColumns =
        new List<string>(8)
        {
            "name", "table_name", "event", "when", "language", "body", "dependencies", "created_utc"
        }.AsReadOnly();
    private static readonly IReadOnlyList<string> _userColumns =
        new List<string>(4) { "name", "is_superuser", "created_utc", "token_count" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _grantColumns =
        new List<string>(3) { "user_name", "database", "permission" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _tokenColumns =
        new List<string>(4) { "token_id", "user_name", "created_utc", "last_used_utc" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _issuedTokenColumns =
        new List<string>(2) { "token_id", "token" }.AsReadOnly();

    /// <summary>
    /// 解析并执行单条 SQL 语句。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="sql">单条 SQL 文本。</param>
    /// <returns>语句执行结果对象（具体类型取决于语句种类）。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null。</exception>
    /// <exception cref="NotSupportedException">语句类型尚未实现。</exception>
    public static object? Execute(Tsdb tsdb, string sql)
        => Execute(tsdb, databaseName: null, sql: sql, controlPlane: null);

    /// <summary>
    /// 解析并执行单条 SQL 语句，可选传入控制面以支持 CREATE USER / GRANT 等 DDL。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="sql">单条 SQL 文本。</param>
    /// <param name="controlPlane">控制面实现；为 <c>null</c> 时控制面 DDL 抛 <see cref="NotSupportedException"/>。</param>
    /// <returns>语句执行结果对象。</returns>
    public static object? Execute(Tsdb tsdb, string sql, IControlPlane? controlPlane)
        => Execute(tsdb, databaseName: null, sql: sql, controlPlane: controlPlane);

    /// <summary>
    /// 解析并执行单条 SQL 语句，可选传入当前数据库名以便 <c>EXPLAIN</c> 结果展示。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="databaseName">当前数据库名；嵌入式场景未知时可为 <c>null</c>。</param>
    /// <param name="sql">单条 SQL 文本。</param>
    /// <param name="controlPlane">控制面实现；为 <c>null</c> 时控制面 DDL 抛 <see cref="NotSupportedException"/>。</param>
    /// <returns>语句执行结果对象。</returns>
    public static object? Execute(Tsdb tsdb, string? databaseName, string sql, IControlPlane? controlPlane = null)
        => Execute(tsdb, databaseName, sql, parameters: null, controlPlane);

    /// <summary>
    /// 解析并执行单条参数化 SQL 语句（#213）。占位符 <c>?</c> / <c>@name</c> / <c>:name</c> 由
    /// <paramref name="parameters"/> 值绑定后执行；解析结果可命中解析缓存并对不同参数值复用。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="databaseName">当前数据库名；未知可为 <c>null</c>。</param>
    /// <param name="sql">单条 SQL 文本，可含参数占位符。</param>
    /// <param name="parameters">参数值集合；为 <c>null</c> 时不做参数绑定。</param>
    /// <param name="controlPlane">控制面实现；为 <c>null</c> 时控制面 DDL 抛 <see cref="NotSupportedException"/>。</param>
    /// <returns>语句执行结果对象。</returns>
    public static object? Execute(
        Tsdb tsdb,
        string? databaseName,
        string sql,
        SqlParameters? parameters,
        IControlPlane? controlPlane = null)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(sql);

        var statement = SqlParser.Parse(sql);
        statement = SqlParameterBinder.Bind(statement, parameters);
        return ExecuteStatement(tsdb, databaseName, statement, controlPlane);
    }

    /// <summary>
    /// 使用显式治理选项执行单条参数化 SQL 语句。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="databaseName">当前数据库名；未知可为 <c>null</c>。</param>
    /// <param name="sql">单条 SQL 文本。</param>
    /// <param name="parameters">参数值集合；为 <c>null</c> 时不做参数绑定。</param>
    /// <param name="controlPlane">控制面实现；为 <c>null</c> 时控制面 DDL 抛 <see cref="NotSupportedException"/>。</param>
    /// <param name="options">取消、调用方、权限和例程上限。</param>
    /// <returns>语句执行结果对象。</returns>
    public static object? Execute(
        Tsdb tsdb,
        string? databaseName,
        string sql,
        SqlParameters? parameters,
        IControlPlane? controlPlane,
        SqlExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(options);

        var statement = SqlParser.Parse(sql);
        statement = SqlParameterBinder.Bind(statement, parameters);
        return ExecuteStatement(tsdb, databaseName, statement, controlPlane, transaction: null, options);
    }

    /// <summary>
    /// 解析并执行一段 SQL 脚本，支持 <c>BEGIN</c> / <c>COMMIT</c> / <c>ROLLBACK</c> 轻事务。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="sql">SQL 脚本文本。</param>
    /// <returns>每条语句的执行结果。</returns>
    public static IReadOnlyList<object?> ExecuteScript(Tsdb tsdb, string sql)
        => ExecuteScript(tsdb, databaseName: null, sql: sql, controlPlane: null);

    /// <summary>
    /// 使用显式治理选项执行一段 SQL 脚本，支持 <c>BEGIN</c> / <c>COMMIT</c> /
    /// <c>ROLLBACK</c> 轻事务。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="sql">SQL 脚本文本。</param>
    /// <param name="options">取消、调用方、权限和例程上限。</param>
    /// <returns>每条语句的执行结果。</returns>
    public static IReadOnlyList<object?> ExecuteScript(
        Tsdb tsdb,
        string sql,
        SqlExecutionOptions options)
        => ExecuteScript(
            tsdb,
            databaseName: null,
            sql: sql,
            controlPlane: null,
            options: options);

    /// <summary>
    /// 解析并执行一段 SQL 脚本，支持可选控制面与轻事务。
    /// </summary>
    public static IReadOnlyList<object?> ExecuteScript(Tsdb tsdb, string? databaseName, string sql, IControlPlane? controlPlane = null)
        => ExecuteScript(tsdb, databaseName, sql, controlPlane, SqlExecutionOptions.Default);

    /// <summary>
    /// 使用显式治理选项执行一段 SQL 脚本，可选传入控制面。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="databaseName">当前数据库名；未知可为 <c>null</c>。</param>
    /// <param name="sql">SQL 脚本文本。</param>
    /// <param name="controlPlane">控制面实现。</param>
    /// <param name="options">取消、调用方、权限和例程上限。</param>
    /// <returns>每条语句的执行结果。</returns>
    public static IReadOnlyList<object?> ExecuteScript(
        Tsdb tsdb,
        string? databaseName,
        string sql,
        IControlPlane? controlPlane,
        SqlExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(options);

        var statements = SqlParser.ParseScript(sql);
        var results = new List<object?>(statements.Count);
        SqlTransactionContext? transaction = null;
        foreach (var statement in statements)
        {
            if (statement is BeginTransactionStatement && transaction is not null && !transaction.IsCompleted)
                throw new InvalidOperationException("当前已有活动轻事务，不能嵌套 BEGIN。");

            var result = ExecuteStatement(tsdb, databaseName, statement, controlPlane, transaction, options);
            if (result is SqlTransactionContext started)
            {
                transaction = started;
            }
            else if (statement is CommitTransactionStatement or RollbackTransactionStatement)
            {
                transaction = null;
            }

            results.Add(result);
        }

        if (transaction is not null && !transaction.IsCompleted)
            throw new InvalidOperationException("SQL 脚本结束时仍有未提交的轻事务。");

        return results.AsReadOnly();
    }

    /// <summary>
    /// 执行一条已解析的 SQL 语句。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="statement">已解析的语句 AST。</param>
    /// <returns>执行结果。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null。</exception>
    /// <exception cref="NotSupportedException">语句类型尚未实现。</exception>
    public static object? ExecuteStatement(Tsdb tsdb, SqlStatement statement)
        => ExecuteStatement(tsdb, databaseName: null, statement: statement, controlPlane: null);

    /// <summary>
    /// 执行一条已解析的 SQL 语句，可选传入控制面以支持控制面 DDL。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="statement">已解析的语句 AST。</param>
    /// <param name="controlPlane">控制面实现；为 <c>null</c> 时控制面 DDL 抛 <see cref="NotSupportedException"/>。</param>
    public static object? ExecuteStatement(Tsdb tsdb, SqlStatement statement, IControlPlane? controlPlane)
        => ExecuteStatement(tsdb, databaseName: null, statement: statement, controlPlane: controlPlane);

    /// <summary>
    /// 执行一条已解析的 SQL 语句，可选传入当前数据库名以便 <c>EXPLAIN</c> 结果展示。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="databaseName">当前数据库名；嵌入式场景未知时可为 <c>null</c>。</param>
    /// <param name="statement">已解析的语句 AST。</param>
    /// <param name="controlPlane">控制面实现；为 <c>null</c> 时控制面 DDL 抛 <see cref="NotSupportedException"/>。</param>
    public static object? ExecuteStatement(Tsdb tsdb, string? databaseName, SqlStatement statement, IControlPlane? controlPlane = null)
        => ExecuteStatement(tsdb, databaseName, statement, controlPlane, transaction: null);

    /// <summary>
    /// 执行一条已解析的 SQL 语句，可选传入轻事务上下文。
    /// </summary>
    public static object? ExecuteStatement(
        Tsdb tsdb,
        string? databaseName,
        SqlStatement statement,
        IControlPlane? controlPlane,
        SqlTransactionContext? transaction)
        => ExecuteStatement(
            tsdb,
            databaseName,
            statement,
            controlPlane,
            transaction,
            SqlExecutionOptions.Default);

    /// <summary>
    /// 使用显式治理选项执行一条已解析语句；现有重载保持默认嵌入式行为。
    /// </summary>
    /// <param name="tsdb">目标数据库。</param>
    /// <param name="databaseName">可选数据库名。</param>
    /// <param name="statement">已解析 AST。</param>
    /// <param name="controlPlane">可选控制面。</param>
    /// <param name="transaction">可选轻事务。</param>
    /// <param name="options">取消、调用方、权限和例程上限。</param>
    /// <returns>语句执行结果。</returns>
    public static object? ExecuteStatement(
        Tsdb tsdb,
        string? databaseName,
        SqlStatement statement,
        IControlPlane? controlPlane,
        SqlTransactionContext? transaction,
        SqlExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(options);
        RejectUnsupportedStatementInActiveTransaction(statement, transaction);
        EnsureModbusAdministrationAllowed(statement, options);

        using var routineExecutionScope = RoutineExecutionContext.EnterRoot(options);
        ThrowIfCancellationRequested();
        // read-your-writes：把活动轻事务设为 ambient，供 SELECT 读路径叠加本事务缓冲写（#218）。
        using var transactionScope = SqlTransactionContext.EnterScope(transaction);
        // UDF 解析必须覆盖 DML 的绑定与求值阶段，不能只在 SELECT 分发器内建立作用域。
        using var functionScope = SonnetDB.Query.Functions.UserFunctionRegistry.EnterScope(tsdb.Functions);

        return statement switch
        {
            BeginTransactionStatement => new SqlTransactionContext(),
            CommitTransactionStatement => transaction is null
                ? throw new InvalidOperationException("COMMIT 前没有活动轻事务。")
                : TableSqlExecutor.CommitTransaction(tsdb, transaction),
            RollbackTransactionStatement => RollbackTransaction(transaction),
            CreateModbusSourceStatement createModbusSource =>
                ModbusSqlExecutor.ExecuteCreateSource(tsdb, createModbusSource),
            CreateModbusEndpointStatement createModbusEndpoint =>
                ModbusSqlExecutor.ExecuteCreateEndpoint(tsdb, createModbusEndpoint),
            CreateMeasurementStatement create => ExecuteCreateMeasurement(tsdb, create),
            CreateTableStatement createTable => TableSqlExecutor.ExecuteCreateTable(tsdb, createTable),
            CreateDocumentCollectionStatement createDocumentCollection => DocumentSqlExecutor.ExecuteCreateCollection(tsdb, createDocumentCollection),
            CreateViewStatement createView => ExecuteCreateView(tsdb, createView),
            CreateMaterializedViewStatement createMaterializedView => ExecuteCreateMaterializedView(tsdb, createMaterializedView),
            CreateProcedureStatement createProcedure => SqlRoutineRuntime.CreateProcedure(tsdb, createProcedure),
            CreateTriggerStatement createTrigger => SqlRoutineRuntime.CreateTrigger(tsdb, createTrigger),
            CreateTableIndexStatement createIndex => ExecuteCreateIndex(tsdb, createIndex),
            CreateDocumentIndexStatement createDocumentIndex => DocumentSqlExecutor.ExecuteCreateIndex(tsdb, createDocumentIndex),
            CreateDocumentPathIndexStatement createDocumentIndex => DocumentSqlExecutor.ExecuteCreateIndex(
                tsdb,
                new CreateDocumentIndexStatement(
                    createDocumentIndex.IndexName,
                    createDocumentIndex.CollectionName,
                    [createDocumentIndex.Path],
                    IfNotExists: createDocumentIndex.IfNotExists)),
            CreateTableJsonPathIndexStatement createTableJsonIndex => TableSqlExecutor.ExecuteCreateJsonPathIndex(tsdb, createTableJsonIndex),
            CreateFullTextIndexStatement createFullTextIndex => DocumentSqlExecutor.ExecuteCreateFullTextIndex(tsdb, createFullTextIndex),
            CreateDocumentVectorIndexStatement createVectorIndex => DocumentSqlExecutor.ExecuteCreateVectorIndex(tsdb, createVectorIndex),
            ImportJsonStatement importJson => JsonFileSqlExecutor.ExecuteImport(
                tsdb,
                databaseName,
                importJson,
                controlPlane),
            InsertStatement insert => ExecuteInsert(tsdb, databaseName, insert, controlPlane, transaction),
            SelectStatement select => ExecuteSelect(tsdb, select),
            CallProcedureStatement call => SqlRoutineRuntime.ExecuteCall(tsdb, databaseName, call, controlPlane, transaction),
            RefreshMaterializedViewStatement refreshMaterializedView => ExecuteRefreshMaterializedView(tsdb, refreshMaterializedView),
            DeleteStatement delete => ExecuteDelete(tsdb, databaseName, delete, controlPlane, transaction),
            TruncateTableStatement truncate => ExecuteTruncate(tsdb, truncate),
            UpdateStatement update => ExecuteUpdate(tsdb, databaseName, update, controlPlane, transaction),
            DropMeasurementStatement dropMeasurement => ExecuteDropMeasurement(tsdb, dropMeasurement),
            DropTableStatement dropTable => TableSqlExecutor.ExecuteDropTable(tsdb, dropTable),
            DropDocumentCollectionStatement dropDocumentCollection => DocumentSqlExecutor.ExecuteDropCollection(tsdb, dropDocumentCollection),
            DropViewStatement dropView => ExecuteDropView(tsdb, dropView),
            DropMaterializedViewStatement dropMaterializedView => ExecuteDropMaterializedView(tsdb, dropMaterializedView),
            DropProcedureStatement dropProcedure => SqlRoutineRuntime.DropProcedure(tsdb, dropProcedure),
            DropTriggerStatement dropTrigger => SqlRoutineRuntime.DropTrigger(tsdb, dropTrigger),
            DropTableIndexStatement dropIndex => TableSqlExecutor.ExecuteDropIndex(tsdb, dropIndex),
            DropDocumentPathIndexStatement dropDocumentIndex => DocumentSqlExecutor.ExecuteDropIndex(tsdb, dropDocumentIndex),
            DropFullTextIndexStatement dropFullTextIndex => DocumentSqlExecutor.ExecuteDropFullTextIndex(tsdb, dropFullTextIndex),
            DropDocumentVectorIndexStatement dropVectorIndex => DocumentSqlExecutor.ExecuteDropVectorIndex(tsdb, dropVectorIndex),
            AlterTableAddColumnStatement alterAddColumn => TableSqlExecutor.ExecuteAlterTableAddColumn(tsdb, alterAddColumn),
            AlterTableAlterColumnStatement alterColumn => TableSqlExecutor.ExecuteAlterTableAlterColumn(tsdb, alterColumn),
            AlterTableAddForeignKeyStatement alterAddForeignKey => TableSqlExecutor.ExecuteAlterTableAddForeignKey(tsdb, alterAddForeignKey),
            AlterTableAddCheckConstraintStatement alterAddCheckConstraint => TableSqlExecutor.ExecuteAlterTableAddCheckConstraint(tsdb, alterAddCheckConstraint),
            AlterTableDropColumnStatement alterDropColumn => TableSqlExecutor.ExecuteAlterTableDropColumn(tsdb, alterDropColumn),
            AlterTableDropConstraintStatement alterDropConstraint => TableSqlExecutor.ExecuteAlterTableDropConstraint(tsdb, alterDropConstraint),
            AlterTableRenameColumnStatement alterRenameColumn => TableSqlExecutor.ExecuteAlterTableRenameColumn(tsdb, alterRenameColumn),
            AlterTableRenameTableStatement alterRenameTable => TableSqlExecutor.ExecuteAlterTableRenameTable(tsdb, alterRenameTable),
            AlterDocumentCollectionSetValidatorStatement setValidator => DocumentSqlExecutor.ExecuteSetValidator(tsdb, setValidator),
            AlterDocumentCollectionDropValidatorStatement dropValidator => DocumentSqlExecutor.ExecuteDropValidator(tsdb, dropValidator),
            ShowMeasurementsStatement => ShowMeasurements(tsdb),
            ShowTablesStatement => TableSqlExecutor.ShowTables(tsdb),
            ShowViewsStatement => ShowViews(tsdb),
            ShowMaterializedViewsStatement => ShowMaterializedViews(tsdb),
            ShowProceduresStatement => ShowProcedures(tsdb),
            ShowTriggersStatement showTriggers => ShowTriggers(tsdb, showTriggers.TableName),
            ShowDocumentCollectionsStatement => DocumentSqlExecutor.ShowCollections(tsdb),
            ShowTableIndexesStatement showIndexes => TableSqlExecutor.ShowIndexes(tsdb, showIndexes.TableName),
            ShowDocumentIndexesStatement showDocumentIndexes => DocumentSqlExecutor.ShowIndexes(tsdb, showDocumentIndexes.CollectionName),
            ShowFullTextIndexesStatement showFullTextIndexes => DocumentSqlExecutor.ShowFullTextIndexes(tsdb, showFullTextIndexes.CollectionName),
            ShowModbusSourcesStatement => ModbusSqlExecutor.ShowSources(tsdb.Modbus),
            ShowModbusEndpointsStatement => ModbusSqlExecutor.ShowEndpoints(tsdb.Modbus.Catalog),
            DescribeMeasurementStatement describe => DescribeMeasurement(tsdb, describe.Name),
            DescribeTableStatement describeTable => TableSqlExecutor.DescribeTable(tsdb, describeTable.Name),
            DescribeViewStatement describeView => DescribeView(tsdb, describeView.Name),
            DescribeMaterializedViewStatement describeMaterializedView => DescribeMaterializedView(tsdb, describeMaterializedView.Name),
            DescribeProcedureStatement describeProcedure => DescribeProcedure(tsdb, describeProcedure.Name),
            DescribeTriggerStatement describeTrigger => DescribeTrigger(tsdb, describeTrigger.Name),
            DescribeDocumentCollectionStatement describeDocumentCollection => DocumentSqlExecutor.DescribeCollection(tsdb, describeDocumentCollection.Name),
            DescribeModbusSourceStatement describeModbusSource =>
                ModbusSqlExecutor.DescribeSource(tsdb.Modbus, describeModbusSource.Name),
            DescribeModbusEndpointStatement describeModbusEndpoint =>
                ModbusSqlExecutor.DescribeEndpoint(tsdb.Modbus.Catalog, describeModbusEndpoint.Name),
            DescribeModbusTableStatement describeModbusTable =>
                ModbusSqlExecutor.DescribeTable(tsdb.Modbus.Catalog, describeModbusTable.Name),
            ExplainStatement explain => ExecuteExplain(tsdb, databaseName, explain),
            CreateUserStatement createUser => ExecuteControlPlane(controlPlane,
                cp => { cp.CreateUser(createUser.UserName, createUser.Password, createUser.IsSuperuser); return (object)1; }),
            AlterUserPasswordStatement alterUser => ExecuteControlPlane(controlPlane,
                cp => { cp.AlterUserPassword(alterUser.UserName, alterUser.NewPassword); return (object)1; }),
            DropUserStatement dropUser => ExecuteControlPlane(controlPlane,
                cp => { cp.DropUser(dropUser.UserName); return (object)1; }),
            GrantStatement grant => ExecuteControlPlane(controlPlane,
                cp => { cp.Grant(grant.UserName, grant.Database, grant.Permission); return (object)1; }),
            RevokeStatement revoke => ExecuteControlPlane(controlPlane,
                cp => { cp.Revoke(revoke.UserName, revoke.Database); return (object)1; }),
            CreateDatabaseStatement createDb => ExecuteControlPlane(controlPlane,
                cp => { cp.CreateDatabase(createDb.DatabaseName); return (object)1; }),
            DropDatabaseStatement dropDb => ExecuteControlPlane(controlPlane,
                cp => { cp.DropDatabase(dropDb.DatabaseName); return (object)1; }),
            ShowUsersStatement => ExecuteControlPlane(controlPlane, ShowUsers),
            ShowGrantsStatement showGrants => ExecuteControlPlane(controlPlane, cp => ShowGrants(cp, showGrants.UserName)),
            ShowDatabasesStatement => ExecuteControlPlane(controlPlane, ShowDatabases),
            ShowTokensStatement showTokens => ExecuteControlPlane(controlPlane, cp => ShowTokens(cp, showTokens.UserName)),
            IssueTokenStatement issueToken => ExecuteControlPlane(controlPlane, cp => IssueToken(cp, issueToken.UserName)),
            RevokeTokenStatement revokeToken => ExecuteControlPlane(controlPlane,
                cp => { cp.RevokeToken(revokeToken.TokenId); return (object)1; }),
            _ => throw new NotSupportedException(
                $"SQL 语句类型 '{statement.GetType().Name}' 尚未实现。"),
        };
    }

    /// <summary>
    /// 轮询当前 SQL 执行的取消令牌，保留例程取消的标准错误合同。
    /// </summary>
    internal static void ThrowIfCancellationRequested()
        => RoutineExecutionContext.Current?.CheckCancellation();

    /// <summary>
    /// 创建可能在后续版本连接外部设备或监听端口的 Modbus 定义时，强制要求当前数据库的 Admin 权限。
    /// </summary>
    private static void EnsureModbusAdministrationAllowed(
        SqlStatement statement,
        SqlExecutionOptions options)
    {
        if (options.CanAdminister)
            return;
        if (statement is not CreateModbusSourceStatement
            and not CreateModbusEndpointStatement
            and not CreateTableStatement { ModbusBinding: not null })
        {
            return;
        }

        throw new InvalidOperationException("Modbus source、endpoint 及表映射 DDL 需要当前数据库的 Admin 权限。");
    }

    // 轻事务只缓冲关系表 DML；schema、控制面和文件导入必须在进入执行分支前拒绝。
    private static void RejectUnsupportedStatementInActiveTransaction(
        SqlStatement statement,
        SqlTransactionContext? transaction)
    {
        if (transaction is null || transaction.IsCompleted)
            return;

        if (statement is ImportJsonStatement)
        {
            throw new NotSupportedException(
                "IMPORT JSON 不能在活动轻事务内执行；文件导入不会进入事务缓冲。请在事务外执行。");
        }

        if (!IsDdlStatement(statement))
            return;

        throw new NotSupportedException(
            "DDL 语句不能在活动轻事务内执行；请在 BEGIN 前或 COMMIT/ROLLBACK 后执行。");
    }

    private static bool IsDdlStatement(SqlStatement statement)
        => statement is
            CreateMeasurementStatement
            or CreateModbusSourceStatement
            or CreateModbusEndpointStatement
            or CreateTableStatement
            or CreateDocumentCollectionStatement
            or CreateViewStatement
            or CreateMaterializedViewStatement
            or CreateProcedureStatement
            or CreateTriggerStatement
            or CreateTableIndexStatement
            or CreateDocumentIndexStatement
            or CreateDocumentPathIndexStatement
            or CreateTableJsonPathIndexStatement
            or CreateFullTextIndexStatement
            or CreateDocumentVectorIndexStatement
            or AlterTableAddColumnStatement
            or AlterTableAlterColumnStatement
            or AlterTableAddForeignKeyStatement
            or AlterTableAddCheckConstraintStatement
            or AlterTableDropColumnStatement
            or AlterTableDropConstraintStatement
            or AlterTableRenameColumnStatement
            or AlterTableRenameTableStatement
            or AlterDocumentCollectionSetValidatorStatement
            or AlterDocumentCollectionDropValidatorStatement
            or DropMeasurementStatement
            or DropTableStatement
            or DropDocumentCollectionStatement
            or DropViewStatement
            or DropMaterializedViewStatement
            or DropProcedureStatement
            or DropTriggerStatement
            or DropTableIndexStatement
            or DropDocumentPathIndexStatement
            or DropFullTextIndexStatement
            or DropDocumentVectorIndexStatement
            or TruncateTableStatement
            or CreateUserStatement
            or AlterUserPasswordStatement
            or DropUserStatement
            or GrantStatement
            or RevokeStatement
            or CreateDatabaseStatement
            or DropDatabaseStatement
            or IssueTokenStatement
            or RevokeTokenStatement;

    private static RowsAffectedExecutionResult RollbackTransaction(SqlTransactionContext? transaction)
    {
        if (transaction is null)
            throw new InvalidOperationException("ROLLBACK 前没有活动轻事务。");
        transaction.MarkCompleted();
        return new RowsAffectedExecutionResult("*", 0, "rollback");
    }

    private static object ExecuteCreateIndex(Tsdb tsdb, CreateTableIndexStatement statement)
    {
        if (tsdb.Documents.Catalog.TryGet(statement.TableName) is not null
            || statement.Columns.Any(static c => c.StartsWith('$')))
        {
            return DocumentSqlExecutor.ExecuteCreateIndex(
                tsdb,
                new CreateDocumentIndexStatement(
                    statement.IndexName,
                    statement.TableName,
                    statement.Columns,
                    statement.IsUnique,
                    statement.DocumentOptions?.IsSparse ?? false,
                    statement.DocumentOptions?.TtlSeconds,
                    statement.DocumentOptions?.PartialFilter,
                    statement.IfNotExists));
        }

        return TableSqlExecutor.ExecuteCreateIndex(tsdb, statement);
    }

    private static ViewDefinition ExecuteCreateView(Tsdb tsdb, CreateViewStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        if (tsdb.Tables.Catalog.TryGet(statement.Name) is not null
            || tsdb.Measurements.Contains(statement.Name)
            || tsdb.Documents.Catalog.TryGet(statement.Name) is not null
            || tsdb.MaterializedViews.Catalog.TryGet(statement.Name) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 view '{statement.Name}'：同名基础对象已存在。");
        }

        if (tsdb.Views.Catalog.TryGet(statement.Name) is { } existing)
        {
            if (statement.IfNotExists)
                return existing;
            throw new InvalidOperationException($"view '{statement.Name}' 已存在。");
        }

        var definition = ViewDefinition.Create(
            statement.Name,
            statement.DefinitionSql,
            statement.Query);
        foreach (string dependency in definition.Dependencies)
        {
            if (string.Equals(dependency, statement.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"view '{statement.Name}' 不能直接或间接引用自身。");
            }

            if (!IsKnownViewSource(tsdb, dependency))
            {
                throw new InvalidOperationException(
                    $"view '{statement.Name}' 引用了不存在的数据源 '{dependency}'。");
            }
        }

        tsdb.Views.Create(definition);
        return definition;
    }

    private static MaterializedViewDefinition ExecuteCreateMaterializedView(
        Tsdb tsdb,
        CreateMaterializedViewStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        if (tsdb.Tables.Catalog.TryGet(statement.Name) is not null
            || tsdb.Measurements.Contains(statement.Name)
            || tsdb.Documents.Catalog.TryGet(statement.Name) is not null
            || tsdb.Views.Catalog.TryGet(statement.Name) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 materialized view '{statement.Name}'：同名基础对象或 view 已存在。");
        }

        if (tsdb.MaterializedViews.Catalog.TryGet(statement.Name) is { } existing)
        {
            if (statement.IfNotExists)
                return existing;
            throw new InvalidOperationException($"materialized view '{statement.Name}' 已存在。");
        }

        var definition = MaterializedViewDefinition.Create(
            statement.Name,
            statement.DefinitionSql,
            statement.Query);
        ValidateViewDependencies(tsdb, definition.Name, definition.Dependencies, "materialized view");
        tsdb.MaterializedViews.Create(definition);
        return definition;
    }

    private static RowsAffectedExecutionResult ExecuteRefreshMaterializedView(
        Tsdb tsdb,
        RefreshMaterializedViewStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        if (SqlTransactionContext.Current is not null)
            throw new InvalidOperationException("REFRESH MATERIALIZED VIEW 不能在活动轻事务内执行。");
        var definition = tsdb.MaterializedViews.Catalog.TryGet(statement.Name)
            ?? throw new InvalidOperationException($"materialized view '{statement.Name}' 不存在。");
        var result = tsdb.MaterializedViews.Refresh(
            statement.Name,
            () => ExecuteSelect(tsdb, definition.Query));
        return new RowsAffectedExecutionResult(statement.Name, result.Rows.Count, "refresh_materialized_view");
    }

    private static RowsAffectedExecutionResult ExecuteDropView(Tsdb tsdb, DropViewStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var viewDependents = tsdb.Views.FindDependents(statement.Name);
        var materializedDependents = tsdb.MaterializedViews.FindDependents(statement.Name);
        if (viewDependents.Count != 0 || materializedDependents.Count != 0)
        {
            string dependents = FormatDependentNames(viewDependents, materializedDependents);
            throw new InvalidOperationException(
                $"无法删除 view '{statement.Name}'：view/materialized view '{dependents}' 仍依赖它。");
        }

        bool removed = tsdb.Views.Drop(statement.Name);
        if (!removed && !statement.IfExists)
            throw new InvalidOperationException($"view '{statement.Name}' 不存在。");
        return new RowsAffectedExecutionResult(statement.Name, removed ? 1 : 0, "drop_view");
    }

    private static RowsAffectedExecutionResult ExecuteDropMaterializedView(
        Tsdb tsdb,
        DropMaterializedViewStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var viewDependents = tsdb.Views.FindDependents(statement.Name);
        var materializedDependents = tsdb.MaterializedViews.FindDependents(statement.Name);
        if (viewDependents.Count != 0 || materializedDependents.Count != 0)
        {
            string dependents = FormatDependentNames(viewDependents, materializedDependents);
            throw new InvalidOperationException(
                $"无法删除 materialized view '{statement.Name}'：view/materialized view '{dependents}' 仍依赖它。");
        }

        bool removed = tsdb.MaterializedViews.Drop(statement.Name);
        if (!removed && !statement.IfExists)
            throw new InvalidOperationException($"materialized view '{statement.Name}' 不存在。");
        return new RowsAffectedExecutionResult(
            statement.Name,
            removed ? 1 : 0,
            "drop_materialized_view");
    }

    private static void ValidateViewDependencies(
        Tsdb tsdb,
        string name,
        IReadOnlyList<string> dependencies,
        string objectType)
    {
        foreach (string dependency in dependencies)
        {
            if (string.Equals(dependency, name, StringComparison.Ordinal))
                throw new InvalidOperationException($"{objectType} '{name}' 不能直接或间接引用自身。");
            if (!IsKnownViewSource(tsdb, dependency))
            {
                throw new InvalidOperationException(
                    $"{objectType} '{name}' 引用了不存在的数据源 '{dependency}'。");
            }
        }
    }

    internal static bool IsKnownViewSource(Tsdb tsdb, string name)
    {
        if (name.StartsWith("information_schema.", StringComparison.OrdinalIgnoreCase))
        {
            return name.Equals("information_schema.tables", StringComparison.OrdinalIgnoreCase)
                || name.Equals("information_schema.columns", StringComparison.OrdinalIgnoreCase)
                || name.Equals("information_schema.indexes", StringComparison.OrdinalIgnoreCase)
                || name.Equals("information_schema.foreign_keys", StringComparison.OrdinalIgnoreCase)
                || name.Equals("information_schema.views", StringComparison.OrdinalIgnoreCase)
                || name.Equals("information_schema.materialized_views", StringComparison.OrdinalIgnoreCase);
        }

        return tsdb.Tables.Catalog.TryGet(name) is not null
            || tsdb.Measurements.Contains(name)
            || tsdb.Documents.Catalog.TryGet(name) is not null
            || tsdb.Views.Catalog.TryGet(name) is not null
            || tsdb.MaterializedViews.Catalog.TryGet(name) is not null;
    }

    internal static void EnsureNameDoesNotBelongToView(
        Tsdb tsdb,
        string objectName,
        string objectType)
    {
        if (tsdb.Views.Catalog.TryGet(objectName) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 {objectType} '{objectName}'：同名 view 已存在。");
        }
        if (tsdb.MaterializedViews.Catalog.TryGet(objectName) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 {objectType} '{objectName}'：同名 materialized view 已存在。");
        }
    }

    internal static void EnsureNoViewDependents(
        Tsdb tsdb,
        string objectName,
        string operation)
    {
        SqlRoutineRuntime.EnsureNoDependents(tsdb, objectName, operation);
        var viewDependents = tsdb.Views.FindDependents(objectName);
        var materializedDependents = tsdb.MaterializedViews.FindDependents(objectName);
        if (viewDependents.Count == 0 && materializedDependents.Count == 0)
            return;

        string dependents = FormatDependentNames(viewDependents, materializedDependents);
        throw new InvalidOperationException(
            $"无法执行 {operation}：view/materialized view "
            + $"'{dependents}' 依赖对象 '{objectName}'。");
    }

    private static string FormatDependentNames(
        IReadOnlyList<ViewDefinition> viewDependents,
        IReadOnlyList<MaterializedViewDefinition> materializedDependents)
        => string.Join(
            "', '",
            viewDependents.Select(static view => view.Name)
                .Concat(materializedDependents.Select(static view => view.Name))
                .OrderBy(static name => name, StringComparer.Ordinal));

    private static SelectExecutionResult ExecuteExplain(Tsdb tsdb, string? databaseName, ExplainStatement statement)
    {
        var explain = SqlExplainPlanner.Explain(databaseName, tsdb, statement.Statement);
        return SqlExplainPlanner.ToSelectExecutionResult(explain);
    }

    private static SelectExecutionResult ShowMeasurements(Tsdb tsdb)
    {
        var snapshot = tsdb.Measurements.Snapshot();
        var rows = new List<IReadOnlyList<object?>>(snapshot.Count);
        foreach (var schema in snapshot)
            rows.Add(new object?[] { schema.Name });
        return new SelectExecutionResult(_nameColumns, rows);
    }

    private static SelectExecutionResult ShowViews(Tsdb tsdb)
    {
        var snapshot = tsdb.Views.Catalog.Snapshot();
        var rows = new List<IReadOnlyList<object?>>(snapshot.Count);
        foreach (var definition in snapshot)
        {
            rows.Add(new object?[]
            {
                definition.Name,
                new DateTime(definition.CreatedAtUtcTicks, DateTimeKind.Utc),
            });
        }
        return new SelectExecutionResult(_showViewColumns, rows);
    }

    private static SelectExecutionResult DescribeView(Tsdb tsdb, string name)
    {
        var definition = tsdb.Views.Catalog.TryGet(name)
            ?? throw new InvalidOperationException($"view '{name}' 不存在。");
        var rows = new List<IReadOnlyList<object?>>(1)
        {
            new object?[]
            {
                definition.Name,
                definition.DefinitionSql,
                string.Join(",", definition.Dependencies),
                new DateTime(definition.CreatedAtUtcTicks, DateTimeKind.Utc),
            },
        };
        return new SelectExecutionResult(_describeViewColumns, rows);
    }

    private static SelectExecutionResult ShowMaterializedViews(Tsdb tsdb)
    {
        var rows = tsdb.MaterializedViews.Catalog.Snapshot()
            .Select(static definition => (IReadOnlyList<object?>)new object?[]
            {
                definition.Name,
                FormatMaterializedViewStatus(definition.Status),
                definition.DefinitionVersion,
                definition.ActiveGeneration,
                definition.RowCount,
                OptionalUtcDateTime(definition.LastSuccessfulRefreshAtUtcTicks),
                definition.LastError,
            })
            .ToArray();
        return new SelectExecutionResult(_showMaterializedViewColumns, rows);
    }

    private static SelectExecutionResult ShowProcedures(Tsdb tsdb)
    {
        var rows = tsdb.Routines.ListProcedures()
            .Select(definition => (IReadOnlyList<object?>)new object?[]
            {
                definition.Name,
                FormatProcedureParameters(definition.Parameters),
                definition.Language,
                SqlRoutineRuntime.RequiresWrite(tsdb, definition),
                new DateTime(definition.CreatedAtUtcTicks, DateTimeKind.Utc),
            })
            .ToArray();
        return new SelectExecutionResult(_showProcedureColumns, rows);
    }

    private static SelectExecutionResult DescribeProcedure(Tsdb tsdb, string name)
    {
        var definition = tsdb.Routines.TryGetProcedure(name)
            ?? throw new SonnetDB.Exceptions.RoutineExecutionException(
                SonnetDB.Exceptions.RoutineErrorCodes.ProcedureNotFound,
                $"procedure '{name}' 不存在。");
        IReadOnlyList<IReadOnlyList<object?>> rows =
        [
            new object?[]
            {
                definition.Name,
                FormatProcedureParameters(definition.Parameters),
                definition.Language,
                definition.BodySql,
                string.Join(",", definition.ObjectDependencies),
                string.Join(",", definition.ProcedureDependencies),
                SqlRoutineRuntime.RequiresWrite(tsdb, definition),
                new DateTime(definition.CreatedAtUtcTicks, DateTimeKind.Utc),
            },
        ];
        return new SelectExecutionResult(_describeProcedureColumns, rows);
    }

    private static SelectExecutionResult ShowTriggers(Tsdb tsdb, string? tableName)
    {
        var rows = tsdb.Routines.ListTriggers(tableName)
            .Select(static definition => (IReadOnlyList<object?>)new object?[]
            {
                definition.Name,
                definition.TableName,
                definition.Event.ToString().ToLowerInvariant(),
                definition.WhenSql,
                new DateTime(definition.CreatedAtUtcTicks, DateTimeKind.Utc),
            })
            .ToArray();
        return new SelectExecutionResult(_showTriggerColumns, rows);
    }

    private static SelectExecutionResult DescribeTrigger(Tsdb tsdb, string name)
    {
        var definition = tsdb.Routines.TryGetTrigger(name)
            ?? throw new SonnetDB.Exceptions.RoutineExecutionException(
                SonnetDB.Exceptions.RoutineErrorCodes.TriggerNotFound,
                $"trigger '{name}' 不存在。");
        IReadOnlyList<IReadOnlyList<object?>> rows =
        [
            new object?[]
            {
                definition.Name,
                definition.TableName,
                definition.Event.ToString().ToLowerInvariant(),
                definition.WhenSql,
                definition.Language,
                definition.BodySql,
                string.Join(",", definition.ObjectDependencies),
                new DateTime(definition.CreatedAtUtcTicks, DateTimeKind.Utc),
            },
        ];
        return new SelectExecutionResult(_describeTriggerColumns, rows);
    }

    private static string FormatProcedureParameters(IReadOnlyList<SqlProcedureParameter> parameters)
        => string.Join(", ", parameters.Select(static parameter =>
            $"IN {parameter.Name} {parameter.DataType switch
            {
                SqlProcedureParameterType.Int64 => "INT",
                SqlProcedureParameterType.Float64 => "FLOAT",
                SqlProcedureParameterType.Boolean => "BOOL",
                SqlProcedureParameterType.String => "STRING",
                _ => throw new ArgumentOutOfRangeException(nameof(parameter.DataType)),
            }}"));

    private static SelectExecutionResult DescribeMaterializedView(Tsdb tsdb, string name)
    {
        var definition = tsdb.MaterializedViews.Catalog.TryGet(name)
            ?? throw new InvalidOperationException($"materialized view '{name}' 不存在。");
        IReadOnlyList<IReadOnlyList<object?>> rows =
        [
            new object?[]
            {
                definition.Name,
                definition.DefinitionSql,
                string.Join(",", definition.Dependencies),
                definition.DefinitionVersion,
                FormatMaterializedViewStatus(definition.Status),
                definition.ActiveGeneration,
                definition.RowCount,
                new DateTime(definition.CreatedAtUtcTicks, DateTimeKind.Utc),
                OptionalUtcDateTime(definition.LastRefreshAtUtcTicks),
                OptionalUtcDateTime(definition.LastSuccessfulRefreshAtUtcTicks),
                definition.LastError,
            },
        ];
        return new SelectExecutionResult(_describeMaterializedViewColumns, rows);
    }

    private static string FormatMaterializedViewStatus(MaterializedViewRefreshStatus status)
        => status.ToString().ToLowerInvariant();

    private static DateTime? OptionalUtcDateTime(long ticks)
        => ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);

    private static SelectExecutionResult DescribeMeasurement(Tsdb tsdb, string name)
    {
        var schema = tsdb.Measurements.TryGet(name)
            ?? throw new InvalidOperationException($"measurement '{name}' 不存在。");
        var rows = new List<IReadOnlyList<object?>>(schema.Columns.Count);
        foreach (var col in schema.Columns)
        {
            rows.Add(new object?[]
            {
                col.Name,
                col.Role == MeasurementColumnRole.Tag ? "tag" : "field",
                FormatColumnDataType(col),
            });
        }
        return new SelectExecutionResult(
            _describeMeasurementColumns,
            rows);
    }

    private static string FormatFieldType(FieldType type) => type switch
    {
        FieldType.Float64 => "float64",
        FieldType.Int64 => "int64",
        FieldType.Boolean => "boolean",
        FieldType.String => "string",
        FieldType.Vector => "vector",
        FieldType.GeoPoint => "geopoint",
        _ => type.ToString().ToLowerInvariant(),
    };

    private static string FormatColumnDataType(MeasurementColumn col)
    {
        if (col.DataType == FieldType.Vector && col.VectorDimension is int dim)
            return $"vector({dim})";
        return FormatFieldType(col.DataType);
    }

    private static object ShowUsers(IControlPlane cp)
    {
        var users = cp.ListUsers();
        var rows = new List<IReadOnlyList<object?>>(users.Count);
        foreach (var u in users)
        {
            rows.Add(new object?[] { u.Name, u.IsSuperuser, u.CreatedUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture), (long)u.TokenCount });
        }
        return new SelectExecutionResult(
            _userColumns,
            rows);
    }

    private static object ShowGrants(IControlPlane cp, string? userName)
    {
        var grants = cp.ListGrants(userName);
        var rows = new List<IReadOnlyList<object?>>(grants.Count);
        foreach (var g in grants)
        {
            rows.Add(new object?[] { g.UserName, g.Database, g.Permission.ToString() });
        }
        return new SelectExecutionResult(
            _grantColumns,
            rows);
    }

    private static object ShowDatabases(IControlPlane cp)
    {
        var dbs = cp.ListDatabases();
        var rows = new List<IReadOnlyList<object?>>(dbs.Count);
        foreach (var d in dbs)
        {
            rows.Add(new object?[] { d });
        }
        return new SelectExecutionResult(_nameColumns, rows);
    }

    private static object ShowTokens(IControlPlane cp, string? userName)
    {
        var tokens = cp.ListTokens(userName);
        var rows = new List<IReadOnlyList<object?>>(tokens.Count);
        foreach (var t in tokens)
        {
            rows.Add(new object?[]
            {
                t.TokenId,
                t.UserName,
                t.CreatedUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                t.LastUsedUtc?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            });
        }
        return new SelectExecutionResult(
            _tokenColumns,
            rows);
    }

    private static object IssueToken(IControlPlane cp, string userName)
    {
        var (tokenId, plain) = cp.IssueToken(userName);
        var rows = new List<IReadOnlyList<object?>>(1)
        {
            new object?[] { tokenId, plain },
        };
        return new SelectExecutionResult(_issuedTokenColumns, rows);
    }

    private static object ExecuteControlPlane(IControlPlane? controlPlane, Func<IControlPlane, object> action)
    {
        if (controlPlane is null)
            throw new NotSupportedException("控制面 DDL（CREATE USER / GRANT / CREATE DATABASE 等）仅在服务端模式可用。");
        return action(controlPlane);
    }

    /// <summary>
    /// 仅执行控制面 SQL（不依赖任何具体 <see cref="Tsdb"/> 实例）。
    /// 适用于服务端 <c>POST /v1/sql</c> 端点：admin 通过该端点跑 CREATE USER / GRANT /
    /// CREATE DATABASE / SHOW USERS 等管理类语句。
    /// </summary>
    /// <param name="statement">已解析的 SQL 语句 AST，必须为控制面语句。</param>
    /// <param name="controlPlane">控制面实现。</param>
    /// <returns>对 SHOW 语句返回 <see cref="SelectExecutionResult"/>，对其他语句返回受影响行数 1。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null。</exception>
    /// <exception cref="NotSupportedException">语句不是控制面语句。</exception>
    public static object ExecuteControlPlaneStatement(SqlStatement statement, IControlPlane controlPlane)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(controlPlane);

        return statement switch
        {
            CreateUserStatement createUser => Run(() => { controlPlane.CreateUser(createUser.UserName, createUser.Password, createUser.IsSuperuser); return (object)1; }),
            AlterUserPasswordStatement alterUser => Run(() => { controlPlane.AlterUserPassword(alterUser.UserName, alterUser.NewPassword); return (object)1; }),
            DropUserStatement dropUser => Run(() => { controlPlane.DropUser(dropUser.UserName); return (object)1; }),
            GrantStatement grant => Run(() => { controlPlane.Grant(grant.UserName, grant.Database, grant.Permission); return (object)1; }),
            RevokeStatement revoke => Run(() => { controlPlane.Revoke(revoke.UserName, revoke.Database); return (object)1; }),
            CreateDatabaseStatement createDb => Run(() => { controlPlane.CreateDatabase(createDb.DatabaseName); return (object)1; }),
            DropDatabaseStatement dropDb => Run(() => { controlPlane.DropDatabase(dropDb.DatabaseName); return (object)1; }),
            ShowUsersStatement => ShowUsers(controlPlane),
            ShowGrantsStatement showGrants => ShowGrants(controlPlane, showGrants.UserName),
            ShowDatabasesStatement => ShowDatabases(controlPlane),
            ShowTokensStatement showTokens => ShowTokens(controlPlane, showTokens.UserName),
            IssueTokenStatement issueToken => IssueToken(controlPlane, issueToken.UserName),
            RevokeTokenStatement revokeToken => Run(() => { controlPlane.RevokeToken(revokeToken.TokenId); return (object)1; }),
            _ => throw new NotSupportedException(
                $"语句 '{statement.GetType().Name}' 不是控制面语句，请改走 /v1/db/{{db}}/sql。"),
        };

        static object Run(Func<object> action) => action();
    }

    /// <summary>
    /// 执行 <c>CREATE MEASUREMENT</c> 语句：把 AST 列定义映射到 catalog schema 并注册。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="statement">已解析的 CREATE MEASUREMENT 语句。</param>
    /// <returns>注册到 catalog 的 <see cref="MeasurementSchema"/>。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null。</exception>
    /// <exception cref="InvalidOperationException">同名 measurement 已存在。</exception>
    public static MeasurementSchema ExecuteCreateMeasurement(
        Tsdb tsdb,
        CreateMeasurementStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        EnsureNameDoesNotBelongToView(tsdb, statement.Name, "measurement");

        // IF NOT EXISTS：同名 measurement 已存在则直接复用，不校验列定义是否一致。
        if (statement.IfNotExists)
        {
            var existing = tsdb.Measurements.TryGet(statement.Name);
            if (existing is not null)
            {
                return existing;
            }
        }

        RejectUnsupportedDefaults(statement);

        var columns = new List<MeasurementColumn>(statement.Columns.Count);
        foreach (var col in statement.Columns)
        {
            columns.Add(new MeasurementColumn(
                col.Name,
                MapRole(col.Kind),
                MapType(col.DataType),
                col.VectorDimension,
                MapVectorIndex(col.VectorIndex)));
        }

        var schema = MeasurementSchema.Create(statement.Name, columns);
        return tsdb.CreateMeasurement(schema);
    }

    /// <summary>
    /// 执行 <c>DROP MEASUREMENT</c> 语句：删除 schema、series catalog 与对应时序数据。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="statement">已解析的 DROP MEASUREMENT 语句。</param>
    /// <returns>受影响行数结果。</returns>
    public static RowsAffectedExecutionResult ExecuteDropMeasurement(
        Tsdb tsdb,
        DropMeasurementStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        EnsureNoViewDependents(tsdb, statement.Name, "DROP MEASUREMENT");

        bool removed = tsdb.DropMeasurement(statement.Name);
        if (!removed && !statement.IfExists)
            throw new InvalidOperationException($"measurement '{statement.Name}' 不存在。");

        return new RowsAffectedExecutionResult(statement.Name, removed ? 1 : 0, "drop_measurement");
    }

    private static void RejectUnsupportedDefaults(CreateMeasurementStatement statement)
    {
        foreach (var col in statement.Columns)
        {
            if (col.DefaultExpression is not null)
            {
                throw new NotSupportedException(
                    $"CREATE MEASUREMENT 列 '{col.Name}' 的 DEFAULT 子句暂不支持；" +
                    "SonnetDB 使用稀疏字段语义，请在 INSERT 时显式写入该 FIELD，或省略该字段让查询结果返回 NULL。");
            }
        }
    }

    private static MeasurementColumnRole MapRole(ColumnKind kind) => kind switch
    {
        ColumnKind.Tag => MeasurementColumnRole.Tag,
        ColumnKind.Field => MeasurementColumnRole.Field,
        _ => throw new NotSupportedException($"未知列角色 {kind}。"),
    };

    private static FieldType MapType(SqlDataType type) => type switch
    {
        SqlDataType.Float64 => FieldType.Float64,
        SqlDataType.Int64 => FieldType.Int64,
        SqlDataType.Boolean => FieldType.Boolean,
        SqlDataType.String => FieldType.String,
        SqlDataType.Vector => FieldType.Vector,
        SqlDataType.GeoPoint => FieldType.GeoPoint,
        SqlDataType.DateTime or SqlDataType.Blob or SqlDataType.Json => throw new NotSupportedException(
            $"CREATE MEASUREMENT 不支持数据类型 {type}；该类型仅用于关系表 CREATE TABLE。"),
        _ => throw new NotSupportedException($"未知数据类型 {type}。"),
    };

    private static VectorIndexDefinition? MapVectorIndex(VectorIndexSpec? vectorIndex)
        => vectorIndex switch
        {
            null => null,
            HnswVectorIndexSpec hnsw => VectorIndexDefinition.CreateHnsw(hnsw.M, hnsw.Ef, hnsw.Metric, hnsw.EfConstruction),
            IvfVectorIndexSpec ivf => VectorIndexDefinition.CreateIvfFlat(ivf.NList, ivf.NProbe, ivf.MaxIterations, ivf.Metric),
            IvfPqVectorIndexSpec ivfPq => VectorIndexDefinition.CreateIvfPq(ivfPq.NList, ivfPq.NProbe, ivfPq.MaxIterations, ivfPq.M, ivfPq.NBits, ivfPq.Metric),
            VamanaVectorIndexSpec vamana => VectorIndexDefinition.CreateVamana(vamana.MaxDegree, vamana.SearchListSize, vamana.Alpha, vamana.BeamWidth, vamana.Metric),
            _ => throw new NotSupportedException($"未知向量索引声明 {vectorIndex.GetType().Name}。"),
        };

    /// <summary>
    /// 执行 <c>INSERT INTO measurement (col, ...) VALUES (...) [, (...)]*</c> 语句。
    /// 校验规则：
    /// <list type="bullet">
    ///   <item>目标 measurement 可不存在；写入时会按数据自动创建或扩展 schema。</item>
    ///   <item>列列表中的每个名字可以是 schema 中已声明的列、新列，或保留伪列 <c>time</c>（时间戳，不区分大小写）。</item>
    ///   <item>同一 INSERT 列列表中不允许重复列名。</item>
    ///   <item>Tag 列必须传入字符串字面量；不允许 NULL；不允许保留字符。</item>
    ///   <item>Field 列值必须与列声明类型兼容；INT 字面量可隐式转换为 FLOAT，INT 列遇到 FLOAT 会提升为 FLOAT。</item>
    ///   <item>未知 SQL 字符串列会按 TAG 推断，未知非字符串列会按 FIELD 推断。</item>
    ///   <item>每行至少需要包含一个 Field 列值（与 <see cref="Point"/> 的约束一致）。</item>
    ///   <item><c>time</c> 列必须为非负整数字面量；缺省时使用当前 UTC 毫秒。</item>
    ///   <item>VALUES 字面量当前仅支持 NULL / Boolean / Integer / Float / String，不支持运算表达式。</item>
    /// </list>
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="statement">已解析的 INSERT 语句。</param>
    /// <returns>包含写入行数的 <see cref="InsertExecutionResult"/>。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null。</exception>
    /// <exception cref="InvalidOperationException">未提供任何 Field / 类型不兼容等校验失败时抛出。</exception>
    public static InsertExecutionResult ExecuteInsert(Tsdb tsdb, InsertStatement statement)
        => ExecuteInsert(
            tsdb,
            databaseName: null,
            statement,
            controlPlane: null,
            transaction: null);

    private static InsertExecutionResult ExecuteInsert(
        Tsdb tsdb,
        string? databaseName,
        InsertStatement statement,
        IControlPlane? controlPlane,
        SqlTransactionContext? transaction)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var documentSchema = tsdb.Documents.Catalog.TryGet(statement.Measurement);
        if (documentSchema is not null)
        {
            if (statement.ReturningColumns.Count != 0)
                throw new NotSupportedException("INSERT ... RETURNING 当前仅支持关系表。");
            if (transaction is not null)
                throw new NotSupportedException("轻事务当前不支持文档集合写入。");
            return DocumentSqlExecutor.ExecuteInsert(tsdb, statement, documentSchema);
        }

        var tableSchema = tsdb.Tables.Catalog.TryGet(statement.Measurement);
        if (tableSchema is not null)
            return ExecuteTableInsertWithTriggers(
                tsdb,
                databaseName,
                statement,
                tableSchema,
                controlPlane,
                transaction);

        if (statement.ReturningColumns.Count != 0)
            throw new NotSupportedException("INSERT ... RETURNING 当前仅支持关系表。");

        if (statement.IsDefaultValues
            || statement.Rows.Any(static row => row.Any(static value => value is DefaultValueExpression)))
        {
            throw new InvalidOperationException(
                "DEFAULT VALUES 和 VALUES(DEFAULT) 仅支持关系表；measurement 没有关系表列 DEFAULT。");
        }

        // measurement 写入直接落 WAL/MemTable，不进事务缓冲；轻事务 ROLLBACK 无法撤销它，
        // 因此在事务上下文内显式拒绝，避免"ROLLBACK 后数据仍在"的假回滚（与文档写入一致）。
        if (transaction is not null)
            throw new NotSupportedException("轻事务当前不支持 measurement（时序）写入，请在事务外执行 INSERT。");

        var schema = tsdb.Measurements.TryGet(statement.Measurement);

        // 解析列绑定：(timeColumnIndex, columnBindings[])
        int timeColumnIndex = -1;
        var bindings = new ColumnBinding[statement.Columns.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < statement.Columns.Count; i++)
        {
            var name = statement.Columns[i];
            if (string.Equals(name, "time", StringComparison.OrdinalIgnoreCase))
            {
                if (timeColumnIndex >= 0)
                    throw new InvalidOperationException("INSERT 列列表中 'time' 出现多次。");
                timeColumnIndex = i;
                bindings[i] = ColumnBinding.Time;
                continue;
            }

            if (!seen.Add(name))
                throw new InvalidOperationException($"INSERT 列列表中列 '{name}' 重复。");

            var col = schema?.TryGetColumn(name);
            var inferredRole = schema is not null && !schema.TagColumns.Any()
                ? MeasurementColumnRole.Field
                : InferUnknownColumnRole(statement.Rows, i, name);
            bindings[i] = col is null
                ? ColumnBinding.Inferred(name, inferredRole)
                : ColumnBinding.Schema(col);
        }

        if (schema is not null && !HasFieldBinding(bindings, timeColumnIndex))
        {
            for (int i = 0; i < bindings.Length; i++)
            {
                if (i == timeColumnIndex)
                    continue;

                if (bindings[i].Column is null && bindings[i].Role == MeasurementColumnRole.Tag)
                    bindings[i] = ColumnBinding.Inferred(bindings[i].Name, MeasurementColumnRole.Field);
            }
        }

        int written = 0;
        foreach (var row in statement.Rows)
        {
            // row 长度由 parser 保证与 columns 等长
            long timestamp = timeColumnIndex < 0
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                : ExtractTimestamp(row[timeColumnIndex]);

            Dictionary<string, string>? tags = null;
            Dictionary<string, FieldValue>? fields = null;

            for (int i = 0; i < bindings.Length; i++)
            {
                if (i == timeColumnIndex)
                    continue;

                var binding = bindings[i];

                if (binding.Role == MeasurementColumnRole.Tag)
                {
                    var literal = AsLiteral(row[i], binding.Name);
                    if (literal.Kind == SqlLiteralKind.Null)
                        throw new InvalidOperationException(
                            $"Tag 列 '{binding.Name}' 不允许为 NULL。");
                    if (literal.Kind != SqlLiteralKind.String)
                        throw new InvalidOperationException(
                            $"Tag 列 '{binding.Name}' 必须是字符串字面量，实际为 {literal.Kind}。");
                    tags ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    tags[binding.Name] = literal.StringValue!;
                }
                else
                {
                    if (binding.Column?.DataType == FieldType.Vector)
                    {
                        if (row[i] is not VectorLiteralExpression vecExpr)
                            throw new InvalidOperationException(
                                $"Field 列 '{binding.Name}' 期望 VECTOR 字面量 [..]，实际为 {row[i].GetType().Name}。");
                        var value = ConvertVectorField(vecExpr, binding.Column);
                        fields ??= new Dictionary<string, FieldValue>(StringComparer.Ordinal);
                        fields[binding.Name] = value;
                        continue;
                    }

                    if (binding.Column?.DataType == FieldType.GeoPoint)
                    {
                        if (row[i] is not GeoPointLiteralExpression geoExpr)
                            throw new InvalidOperationException(
                                $"Field 列 '{binding.Name}' 期望 POINT(lat, lon) 字面量，实际为 {row[i].GetType().Name}。");
                        fields ??= new Dictionary<string, FieldValue>(StringComparer.Ordinal);
                        fields[binding.Name] = FieldValue.FromGeoPoint(geoExpr.Lat, geoExpr.Lon);
                        continue;
                    }

                    var fv = binding.Column is null
                        ? ConvertInferredField(row[i], binding.Name)
                        : ConvertDeclaredField(row[i], binding.Column);
                    fields ??= new Dictionary<string, FieldValue>(StringComparer.Ordinal);
                    fields[binding.Name] = fv;
                }
            }

            if (fields is null || fields.Count == 0)
                throw new InvalidOperationException(
                    $"INSERT 行至少需要包含一个 FIELD 列值（measurement '{statement.Measurement}'）。");

            var point = Point.Create(statement.Measurement, timestamp, tags, fields);
            tsdb.Write(point);
            written++;
        }

        return new InsertExecutionResult(statement.Measurement, written);
    }

    internal static InsertExecutionResult ExecuteImportedTableInsert(
        Tsdb tsdb,
        string? databaseName,
        InsertStatement statement,
        TableSchema schema,
        IControlPlane? controlPlane)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);
        return ExecuteTableInsertWithTriggers(
            tsdb,
            databaseName,
            statement,
            schema,
            controlPlane,
            transaction: null);
    }

    /// <summary>
    /// 执行 SELECT 语句，返回投影列名与行数据。
    /// </summary>
    /// <param name="tsdb">目标 Tsdb 实例。</param>
    /// <param name="statement">已解析的 SELECT 语句。</param>
    /// <returns>包含列名与行数据的 <see cref="SelectExecutionResult"/>。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null。</exception>
    /// <exception cref="InvalidOperationException">measurement 不存在 / WHERE 包含不支持的表达式 / 投影违规等。</exception>
    public static SelectExecutionResult ExecuteSelect(Tsdb tsdb, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        using var queryLoad = QueryActivityTracker.Enter();
        // ExecuteSelect 也是公开入口，直接调用时仍需建立当前数据库的 UDF 作用域。
        using var functionScope = SonnetDB.Query.Functions.UserFunctionRegistry.EnterScope(tsdb.Functions);

        if (tsdb.Views.Catalog.Count != 0)
            statement = ViewExpander.Expand(tsdb.Views.Catalog, statement);

        if (statement.UnionStatements.Count != 0)
            return ExecuteUnion(tsdb, statement);

        if (!statement.Distinct)
            return ExecuteSelectDispatch(tsdb, statement);

        // DISTINCT 在单一收敛点去重，覆盖 measurement / 关系 / 文档等所有 SELECT 路径。
        // 标准 SQL 求值顺序为 SELECT → DISTINCT → LIMIT，故先剥离分页交由子执行器算出全量投影行，
        // 去重后再施加 LIMIT/OFFSET；否则"先分页再去重"会少返回不足 k 行的去重结果。
        // ORDER BY 仍由子执行器施加于去重前全集，稳定去重保序 —— 对 ORDER BY 键 ⊆ 投影列的
        // 常见场景与标准结果一致。
        var pagination = statement.Pagination;
        var dispatched = pagination is null ? statement : statement with { Pagination = null };
        var result = ApplyDistinct(ExecuteSelectDispatch(tsdb, dispatched));
        return pagination is null ? result : ApplyResultPagination(result, pagination);
    }

    private static SelectExecutionResult ApplyDistinct(SelectExecutionResult result)
    {
        var seen = new HashSet<IReadOnlyList<object?>>(DistinctRowComparer.Instance);
        var deduped = new List<IReadOnlyList<object?>>(result.Rows.Count);
        foreach (var row in result.Rows)
        {
            if (seen.Add(row))
                deduped.Add(row);
        }
        return deduped.Count == result.Rows.Count
            ? result
            : new SelectExecutionResult(result.Columns, deduped);
    }

    private static SelectExecutionResult ApplyResultPagination(SelectExecutionResult result, PaginationSpec pagination)
    {
        int offset = pagination.Offset;
        if (offset >= result.Rows.Count)
            return new SelectExecutionResult(result.Columns, []);
        var skipped = result.Rows.Skip(offset);
        var taken = pagination.Fetch is { } fetch ? skipped.Take(fetch) : skipped;
        return new SelectExecutionResult(result.Columns, taken.ToArray());
    }

    /// <summary>
    /// SELECT DISTINCT 行去重比较器：逐列结构相等。数值按"整型 vs 浮点"两个规范化命名空间比较
    /// （整型统一装箱为 <see cref="long"/>，浮点为 <see cref="double"/>），避免把大 long 折成 double
    /// 时的精度误合并；<see cref="byte"/>[] 按内容序列比较。
    /// </summary>
    private sealed class DistinctRowComparer : IEqualityComparer<IReadOnlyList<object?>>
    {
        public static readonly DistinctRowComparer Instance = new();

        public bool Equals(IReadOnlyList<object?>? x, IReadOnlyList<object?>? y)
        {
            if (x is null || y is null)
                return ReferenceEquals(x, y);
            if (x.Count != y.Count)
                return false;
            for (int i = 0; i < x.Count; i++)
            {
                var a = Normalize(x[i]);
                var b = Normalize(y[i]);
                if (a is byte[] ab && b is byte[] bb)
                {
                    if (!ab.AsSpan().SequenceEqual(bb))
                        return false;
                }
                else if (!Equals(a, b))
                {
                    return false;
                }
            }
            return true;
        }

        public int GetHashCode(IReadOnlyList<object?> row)
        {
            var hash = new HashCode();
            foreach (var value in row)
            {
                var n = Normalize(value);
                if (n is byte[] bytes)
                {
                    hash.AddBytes(bytes);
                }
                else
                {
                    hash.Add(n);
                }
            }
            return hash.ToHashCode();
        }

        private static object? Normalize(object? value) => value switch
        {
            null => null,
            byte or sbyte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            ulong u => u <= long.MaxValue ? (long)u : (double)u,
            float or double or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            _ => value,
        };
    }

    private static SelectExecutionResult ExecuteUnion(Tsdb tsdb, SelectStatement statement)
        => ExecuteUnion(statement, branch => ExecuteSelect(tsdb, branch));

    internal static SelectExecutionResult ExecuteUnion(
        SelectStatement statement,
        Func<SelectStatement, SelectExecutionResult> executeBranch)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(executeBranch);
        var left = statement with
        {
            Unions = null,
            OrderBy = null,
            OrderByItems = null,
            Pagination = null
        };
        var first = executeBranch(left);
        var rows = new List<IReadOnlyList<object?>>(first.Rows);

        foreach (var union in statement.UnionStatements)
        {
            var branch = executeBranch(union);
            if (branch.Columns.Count != first.Columns.Count)
            {
                throw new InvalidOperationException(
                    $"UNION 分支列数不一致：期望 {first.Columns.Count} 列，实际 {branch.Columns.Count} 列。");
            }

            rows.AddRange(branch.Rows);
        }

        var combined = ApplyDistinct(new SelectExecutionResult(first.Columns, rows));
        return ApplyResultOrderByAndPagination(combined, statement.OrderByList, statement.Pagination);
    }

    private static SelectExecutionResult ApplyResultOrderByAndPagination(
        SelectExecutionResult result,
        IReadOnlyList<OrderBySpec> orderBy,
        PaginationSpec? pagination)
    {
        if (orderBy.Count == 0)
            return pagination is null ? result : ApplyResultPagination(result, pagination);

        var sortItems = orderBy.Select(order =>
        {
            if (order.Expression is not IdentifierExpression identifier)
                throw new InvalidOperationException("UNION ORDER BY 当前仅支持结果列名。");

            int columnIndex = -1;
            for (int i = 0; i < result.Columns.Count; i++)
            {
                if (string.Equals(result.Columns[i], identifier.Name, StringComparison.OrdinalIgnoreCase))
                {
                    columnIndex = i;
                    break;
                }
            }

            if (columnIndex < 0)
                throw new InvalidOperationException($"UNION ORDER BY 引用了结果集中不存在的列 '{identifier.Name}'。");
            return (ColumnIndex: columnIndex, order.Direction);
        }).ToArray();

        var comparer = new UnionResultRowComparer(sortItems);
        var rows = TopN.OrderByThenPaginate(
            result.Rows,
            comparer,
            pagination?.Offset ?? 0,
            pagination?.Fetch);
        return new SelectExecutionResult(result.Columns, rows);
    }

    private sealed class UnionResultRowComparer(
        IReadOnlyList<(int ColumnIndex, SortDirection Direction)> sortItems)
        : IComparer<IReadOnlyList<object?>>
    {
        public int Compare(IReadOnlyList<object?>? x, IReadOnlyList<object?>? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            foreach (var item in sortItems)
            {
                int comparison;
                if (x[item.ColumnIndex] is null)
                    comparison = y[item.ColumnIndex] is null ? 0 : -1;
                else if (y[item.ColumnIndex] is null)
                    comparison = 1;
                else
                    comparison = SqlScalarComparer.Compare(x[item.ColumnIndex], y[item.ColumnIndex]) ?? 0;

                if (comparison != 0)
                    return item.Direction == SortDirection.Descending ? -comparison : comparison;
            }

            return 0;
        }
    }

    private static SelectExecutionResult ExecuteSelectDispatch(Tsdb tsdb, SelectStatement statement)
    {
        if (TryExecuteInformationSchemaSelect(tsdb, statement, out var informationSchemaResult))
            return informationSchemaResult;

        var tableSchema = statement.FromSubquery is null
            ? tsdb.Tables.Catalog.TryGet(statement.Measurement)
            : null;
        var materializedView = statement.FromSubquery is null
            ? tsdb.MaterializedViews.Catalog.TryGet(statement.Measurement)
            : null;
        if (materializedView is not null)
            return RelationalSelectExecutor.Execute(tsdb, statement);
        if (DocumentVectorSearchExecutor.IsVectorSearch(statement))
            return DocumentVectorSearchExecutor.Execute(tsdb, statement);
        if (HybridSearchExecutor.IsHybridSearch(statement))
            return HybridSearchExecutor.Execute(tsdb, statement);
        if (string.IsNullOrEmpty(statement.Measurement) && statement.FromSubquery is null)
            return RelationalSelectExecutor.Execute(tsdb, statement);
        if ((RelationalSelectExecutor.NeedsRelationalPath(statement) || statement.JoinClauses.Count != 0)
            && (statement.FromSubquery is not null || tableSchema is not null))
        {
            return RelationalSelectExecutor.Execute(tsdb, statement);
        }
        if (statement.JoinClauses.Count != 0)
        {
            if (statement.JoinClauses.Count != 1)
                throw new InvalidOperationException("measurement JOIN 当前仅支持一个关系维表。");
            return JoinSqlExecutor.Execute(tsdb, statement);
        }
        if (statement.TableValuedFunction is FunctionCallExpression { Name: var tvfName }
            && (string.Equals(tvfName, "json_each", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tvfName, "json_table", StringComparison.OrdinalIgnoreCase)))
        {
            return TableValuedFunctionExecutor.Execute(tsdb, statement);
        }
        var documentSchema = tsdb.Documents.Catalog.TryGet(statement.Measurement);
        if (documentSchema is not null)
            return DocumentSqlExecutor.ExecuteSelect(tsdb, statement, documentSchema);

        if (tableSchema is not null)
            return TableSqlExecutor.ExecuteSelect(tsdb, statement, tableSchema);

        return SelectExecutor.Execute(tsdb, statement);
    }

    private static bool TryExecuteInformationSchemaSelect(
        Tsdb tsdb,
        SelectStatement statement,
        out SelectExecutionResult result)
    {
        result = default!;
        if (!statement.Measurement.StartsWith("information_schema.", StringComparison.OrdinalIgnoreCase))
            return false;
        if (statement.JoinClauses.Count != 0 || statement.TableValuedFunction is not null || statement.GroupBy.Count != 0)
            throw new InvalidOperationException("INFORMATION_SCHEMA 查询不支持 JOIN、表值函数或 GROUP BY。");

        var (columns, rows) = statement.Measurement.ToLowerInvariant() switch
        {
            "information_schema.tables" => BuildInformationSchemaTables(tsdb),
            "information_schema.columns" => BuildInformationSchemaColumns(tsdb),
            "information_schema.indexes" => BuildInformationSchemaIndexes(tsdb),
            "information_schema.foreign_keys" => BuildInformationSchemaForeignKeys(tsdb),
            "information_schema.views" => BuildInformationSchemaViews(tsdb),
            "information_schema.materialized_views" => BuildInformationSchemaMaterializedViews(tsdb),
            _ => throw new InvalidOperationException($"未知 INFORMATION_SCHEMA 视图 '{statement.Measurement}'。"),
        };

        rows = ApplyInformationSchemaWhere(columns, rows, statement.Where);
        rows = ApplyInformationSchemaOrderBy(columns, rows, statement.OrderBy);
        (columns, rows) = ApplyInformationSchemaProjection(columns, rows, statement.Projections);
        rows = ApplyInformationSchemaPagination(rows, statement.Pagination);
        result = new SelectExecutionResult(columns, rows);
        return true;
    }

    private static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows) BuildInformationSchemaTables(Tsdb tsdb)
    {
        var columns = new[] { "table_schema", "table_name", "table_type" };
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var table in tsdb.Tables.Catalog.Snapshot())
            rows.Add(new object?[] { "main", table.Name, "BASE TABLE" });
        foreach (var measurement in tsdb.Measurements.Snapshot())
            rows.Add(new object?[] { "main", measurement.Name, "MEASUREMENT" });
        foreach (var collection in tsdb.Documents.Catalog.Snapshot())
            rows.Add(new object?[] { "main", collection.Name, "DOCUMENT COLLECTION" });
        foreach (var view in tsdb.Views.Catalog.Snapshot())
            rows.Add(new object?[] { "main", view.Name, "VIEW" });
        foreach (var view in tsdb.MaterializedViews.Catalog.Snapshot())
            rows.Add(new object?[] { "main", view.Name, "MATERIALIZED VIEW" });
        return (columns, rows.OrderBy(static r => (string)r[1]!, StringComparer.Ordinal).ToArray());
    }

    private static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows) BuildInformationSchemaViews(Tsdb tsdb)
    {
        var columns = new[] { "table_schema", "table_name", "view_definition", "created_utc" };
        var rows = tsdb.Views.Catalog.Snapshot()
            .Select(static definition => (IReadOnlyList<object?>)new object?[]
            {
                "main",
                definition.Name,
                definition.DefinitionSql,
                new DateTime(definition.CreatedAtUtcTicks, DateTimeKind.Utc),
            })
            .ToArray();
        return (columns, rows);
    }

    private static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows) BuildInformationSchemaMaterializedViews(Tsdb tsdb)
    {
        var columns = new[]
        {
            "table_schema", "table_name", "view_definition", "definition_version", "status",
            "active_generation", "row_count", "created_utc", "last_refresh_utc",
            "last_successful_refresh_utc", "error"
        };
        var rows = tsdb.MaterializedViews.Catalog.Snapshot()
            .Select(static definition => (IReadOnlyList<object?>)new object?[]
            {
                "main",
                definition.Name,
                definition.DefinitionSql,
                definition.DefinitionVersion,
                FormatMaterializedViewStatus(definition.Status),
                definition.ActiveGeneration,
                definition.RowCount,
                new DateTime(definition.CreatedAtUtcTicks, DateTimeKind.Utc),
                OptionalUtcDateTime(definition.LastRefreshAtUtcTicks),
                OptionalUtcDateTime(definition.LastSuccessfulRefreshAtUtcTicks),
                definition.LastError,
            })
            .ToArray();
        return (columns, rows);
    }

    private static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows) BuildInformationSchemaColumns(Tsdb tsdb)
    {
        var columns = new[]
        {
            "table_schema", "table_name", "column_name", "ordinal_position", "data_type",
            "is_nullable", "is_primary_key", "column_default", "is_auto_increment"
        };
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var table in tsdb.Tables.Catalog.Snapshot())
        {
            foreach (var column in table.Columns)
            {
                rows.Add(new object?[]
                {
                    "main",
                    table.Name,
                    column.Name,
                    (long)column.Ordinal + 1,
                    FormatInformationSchemaTableType(column.DataType),
                    column.IsNullable ? "YES" : "NO",
                    column.IsPrimaryKey,
                    column.DefaultExpressionSql,
                    column.IsAutoIncrement,
                });
            }
        }

        foreach (var measurement in tsdb.Measurements.Snapshot())
        {
            var ordinal = 1L;
            foreach (var column in measurement.Columns)
            {
                rows.Add(new object?[]
                {
                    "main",
                    measurement.Name,
                    column.Name,
                    ordinal++,
                    column.DataType.ToString().ToLowerInvariant(),
                    "YES",
                    false,
                    null,
                    false,
                });
            }
        }

        return (columns, rows);
    }

    private static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows) BuildInformationSchemaIndexes(Tsdb tsdb)
    {
        var columns = new[] { "table_schema", "table_name", "index_name", "column_name", "ordinal_position", "is_unique" };
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var table in tsdb.Tables.Catalog.Snapshot())
        {
            foreach (var index in table.Indexes)
            {
                for (var i = 0; i < index.Columns.Count; i++)
                {
                    rows.Add(new object?[]
                    {
                        "main",
                        table.Name,
                        index.Name,
                        index.Columns[i],
                        (long)i + 1,
                        index.IsUnique,
                    });
                }
            }
        }

        return (columns, rows);
    }

    private static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows) BuildInformationSchemaForeignKeys(Tsdb tsdb)
    {
        var columns = new[]
        {
            "table_schema",
            "constraint_name",
            "table_name",
            "column_name",
            "ordinal_position",
            "principal_table_name",
            "principal_column_name",
            "on_delete",
        };
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var table in tsdb.Tables.Catalog.Snapshot())
        {
            foreach (var foreignKey in table.ForeignKeys)
            {
                for (var i = 0; i < foreignKey.Columns.Count; i++)
                {
                    rows.Add(new object?[]
                    {
                        "main",
                        foreignKey.Name,
                        table.Name,
                        foreignKey.Columns[i],
                        (long)i + 1,
                        foreignKey.PrincipalTable,
                        foreignKey.PrincipalColumns[i],
                        FormatInformationSchemaForeignKeyAction(foreignKey.OnDelete),
                    });
                }
            }
        }

        return (columns, rows);
    }

    private static IReadOnlyList<IReadOnlyList<object?>> ApplyInformationSchemaWhere(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        SqlExpression? where)
    {
        if (where is null)
            return rows;

        return rows.Where(row => EvaluateInformationSchemaPredicate(columns, row, where)).ToArray();
    }

    private static bool EvaluateInformationSchemaPredicate(
        IReadOnlyList<string> columns,
        IReadOnlyList<object?> row,
        SqlExpression expression)
    {
        if (expression is BinaryExpression { Operator: SqlBinaryOperator.And } and)
            return EvaluateInformationSchemaPredicate(columns, row, and.Left)
                   && EvaluateInformationSchemaPredicate(columns, row, and.Right);
        if (expression is not BinaryExpression { Operator: SqlBinaryOperator.Equal } equals)
            throw new InvalidOperationException("INFORMATION_SCHEMA WHERE 当前仅支持 AND 连接的等值过滤。");

        var (identifier, literal) = equals.Left is IdentifierExpression left && equals.Right is LiteralExpression right
            ? (left, right)
            : equals.Right is IdentifierExpression rightId && equals.Left is LiteralExpression leftLiteral
                ? (rightId, leftLiteral)
                : throw new InvalidOperationException("INFORMATION_SCHEMA WHERE 当前仅支持列名 = 字面量。");

        var ordinal = FindInformationSchemaColumn(columns, identifier.Name);
        var expected = EvaluateInformationSchemaLiteral(literal);
        return Equals(row[ordinal], expected);
    }

    private static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows) ApplyInformationSchemaProjection(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        IReadOnlyList<SelectItem> projections)
    {
        if (projections.Count == 1 && projections[0].Expression is StarExpression)
            return (columns, rows);

        var expressions = new List<SqlExpression>(projections.Count);
        var outputColumns = new List<string>(projections.Count);
        foreach (var projection in projections)
        {
            SqlProjectionExpressionEvaluator.Validate(
                projection.Expression,
                identifier => columns.Any(column => string.Equals(
                    column, identifier.Name, StringComparison.OrdinalIgnoreCase)),
                "INFORMATION_SCHEMA");
            expressions.Add(projection.Expression);
            outputColumns.Add(projection.Alias
                ?? (projection.Expression as IdentifierExpression)?.Name
                ?? "expression");
        }

        var projectedRows = rows
            .Select(row => (IReadOnlyList<object?>)expressions.Select(expression =>
                SqlProjectionExpressionEvaluator.Evaluate(
                    expression,
                    identifier => row[FindInformationSchemaColumn(columns, identifier.Name)],
                    "INFORMATION_SCHEMA")).ToArray())
            .ToArray();
        return (outputColumns, projectedRows);
    }

    private static IReadOnlyList<IReadOnlyList<object?>> ApplyInformationSchemaOrderBy(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        OrderBySpec? orderBy)
    {
        if (orderBy is null)
            return rows;
        if (orderBy.Expression is not IdentifierExpression id)
            throw new InvalidOperationException("INFORMATION_SCHEMA ORDER BY 当前仅支持列名。");

        var ordinal = FindInformationSchemaColumn(columns, id.Name);
        return orderBy.Direction == SortDirection.Descending
            ? rows.OrderByDescending(row => row[ordinal]).ToArray()
            : rows.OrderBy(row => row[ordinal]).ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<object?>> ApplyInformationSchemaPagination(
        IReadOnlyList<IReadOnlyList<object?>> rows,
        PaginationSpec? pagination)
    {
        if (pagination is null)
            return rows;

        var skipped = rows.Skip(pagination.Offset);
        return pagination.Fetch is { } fetch
            ? skipped.Take(fetch).ToArray()
            : skipped.ToArray();
    }

    private static int FindInformationSchemaColumn(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new InvalidOperationException($"INFORMATION_SCHEMA 中不存在列 '{name}'。");
    }

    private static string FormatInformationSchemaTableType(TableColumnType type)
        => type switch
        {
            TableColumnType.Int64 => "int64",
            TableColumnType.Float64 => "float64",
            TableColumnType.Boolean => "boolean",
            TableColumnType.String => "string",
            TableColumnType.DateTime => "datetime",
            TableColumnType.Blob => "blob",
            TableColumnType.Json => "json",
            _ => type.ToString().ToLowerInvariant(),
        };

    private static string FormatInformationSchemaForeignKeyAction(ForeignKeyAction action)
        => action switch
        {
            ForeignKeyAction.Cascade => "CASCADE",
            ForeignKeyAction.SetNull => "SET NULL",
            _ => "NO ACTION",
        };

    private static object? EvaluateInformationSchemaLiteral(LiteralExpression literal)
        => literal.Kind switch
        {
            SqlLiteralKind.Null => null,
            SqlLiteralKind.Boolean => literal.BooleanValue,
            SqlLiteralKind.Integer => literal.IntegerValue,
            SqlLiteralKind.Float => literal.FloatValue,
            SqlLiteralKind.String => literal.StringValue,
            _ => throw new InvalidOperationException($"不支持的字面量类型 {literal.Kind}。"),
        };

    /// <summary>
    /// 执行 DELETE 语句：把 WHERE 中 tag 等值过滤 + 时间窗 落到 PR #20 的 Tombstone 体系。
    /// 对命中 tag 过滤的所有 series × schema 中所有 Field 列追加墓碑。
    /// </summary>
    /// <param name="tsdb">目标 Tsdb 实例。</param>
    /// <param name="statement">已解析的 DELETE 语句。</param>
    /// <returns>包含 measurement 名、命中 series 数、追加墓碑数的 <see cref="DeleteExecutionResult"/>。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null。</exception>
    /// <exception cref="InvalidOperationException">measurement 不存在 / WHERE 包含不支持的表达式。</exception>
    public static DeleteExecutionResult ExecuteDelete(Tsdb tsdb, DeleteStatement statement)
        => ExecuteDelete(
            tsdb,
            databaseName: null,
            statement,
            controlPlane: null,
            transaction: null);

    private static RowsAffectedExecutionResult ExecuteTruncate(
        Tsdb tsdb,
        TruncateTableStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        int rows = tsdb.Tables.Truncate(statement.TableName);
        return new RowsAffectedExecutionResult(statement.TableName, rows, "truncate_generation");
    }

    private static DeleteExecutionResult ExecuteDelete(
        Tsdb tsdb,
        string? databaseName,
        DeleteStatement statement,
        IControlPlane? controlPlane,
        SqlTransactionContext? transaction)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        var documentSchema = tsdb.Documents.Catalog.TryGet(statement.Measurement);
        if (documentSchema is not null)
        {
            if (transaction is not null)
                throw new NotSupportedException("轻事务当前不支持文档集合删除。");
            return DocumentSqlExecutor.ExecuteDelete(tsdb, statement, documentSchema);
        }

        var tableSchema = tsdb.Tables.Catalog.TryGet(statement.Measurement);
        if (tableSchema is not null)
        {
            var affected = ExecuteTableDeleteWithTriggers(
                tsdb,
                databaseName,
                statement,
                tableSchema,
                controlPlane,
                transaction).RowsAffected;
            return new DeleteExecutionResult(
                statement.Measurement,
                SeriesAffected: affected,
                TombstonesAdded: affected);
        }

        // measurement 删除直接落 tombstone/WAL，不进事务缓冲；轻事务 ROLLBACK 无法撤销，
        // 因此在事务上下文内显式拒绝（与 measurement INSERT / 文档删除一致）。
        if (transaction is not null)
            throw new NotSupportedException("轻事务当前不支持 measurement（时序）删除，请在事务外执行 DELETE。");

        return DeleteExecutor.Execute(tsdb, statement);
    }

    private static RowsAffectedExecutionResult ExecuteUpdate(
        Tsdb tsdb,
        string? databaseName,
        UpdateStatement update,
        IControlPlane? controlPlane,
        SqlTransactionContext? transaction)
    {
        var documentSchema = tsdb.Documents.Catalog.TryGet(update.TableName);
        if (documentSchema is not null)
        {
            if (transaction is not null)
                throw new NotSupportedException("轻事务当前不支持文档集合更新。");
            return DocumentSqlExecutor.ExecuteUpdate(tsdb, update, documentSchema);
        }

        if (tsdb.Tables.Catalog.TryGet(update.TableName) is null
            && tsdb.Measurements.TryGet(update.TableName) is not null
            && update.Assignments.Any(static assignment => assignment.Value is DefaultValueExpression))
        {
            throw new InvalidOperationException(
                "UPDATE SET column = DEFAULT 仅支持关系表；measurement 不支持 UPDATE 或关系表列 DEFAULT。");
        }

        return ExecuteTableUpdateWithTriggers(
            tsdb,
            databaseName,
            update,
            controlPlane,
            transaction);
    }

    private static InsertExecutionResult ExecuteTableInsertWithTriggers(
        Tsdb tsdb,
        string? databaseName,
        InsertStatement statement,
        TableSchema schema,
        IControlPlane? controlPlane,
        SqlTransactionContext? transaction)
    {
        bool hasTriggers = tsdb.Routines.FindTriggers(schema.Name, SqlTriggerEvent.Insert).Count != 0;
        if (transaction is null && !hasTriggers)
            return TableSqlExecutor.ExecuteInsert(tsdb, statement, schema);

        bool ownsTransaction = transaction is null;
        var effectiveTransaction = transaction ?? new SqlTransactionContext();
        var savepoint = effectiveTransaction.CreateSavepoint();
        try
        {
            var result = TableSqlExecutor.QueueInsert(
                tsdb,
                effectiveTransaction,
                statement,
                schema,
                out var changes);
            IReadOnlyList<long> triggerAuditSequences = SqlRoutineRuntime.FireTriggers(
                tsdb,
                databaseName,
                SqlTriggerEvent.Insert,
                changes,
                controlPlane,
                effectiveTransaction);
            effectiveTransaction.AddTriggerAuditSequences(triggerAuditSequences);
            if (ownsTransaction)
                TableSqlExecutor.CommitTransaction(tsdb, effectiveTransaction);
            return result;
        }
        catch (Exception exception)
        {
            tsdb.Routines.Diagnostics.MarkTriggerTransactionFailure(
                effectiveTransaction.SnapshotTriggerAuditSequencesSince(savepoint),
                exception is SonnetDB.Exceptions.RoutineExecutionException routine
                    ? routine.Code
                    : SonnetDB.Exceptions.RoutineErrorCodes.ExecutionFailed);
            if (!ownsTransaction)
                effectiveTransaction.RollbackTo(savepoint);
            if (hasTriggers && exception is not SonnetDB.Exceptions.RoutineExecutionException)
            {
                throw new SonnetDB.Exceptions.RoutineExecutionException(
                    SonnetDB.Exceptions.RoutineErrorCodes.ExecutionFailed,
                    $"AFTER INSERT 触发器事务提交失败：{exception.Message}",
                    exception);
            }
            throw;
        }
    }

    private static RowsAffectedExecutionResult ExecuteTableUpdateWithTriggers(
        Tsdb tsdb,
        string? databaseName,
        UpdateStatement statement,
        IControlPlane? controlPlane,
        SqlTransactionContext? transaction)
    {
        bool hasTriggers = tsdb.Routines.FindTriggers(statement.TableName, SqlTriggerEvent.Update).Count != 0;
        if (transaction is null && !hasTriggers)
            return TableSqlExecutor.ExecuteUpdate(tsdb, statement);

        bool ownsTransaction = transaction is null;
        var effectiveTransaction = transaction ?? new SqlTransactionContext();
        var savepoint = effectiveTransaction.CreateSavepoint();
        try
        {
            var result = TableSqlExecutor.QueueUpdate(
                effectiveTransaction,
                tsdb,
                statement,
                out var changes);
            IReadOnlyList<long> triggerAuditSequences = SqlRoutineRuntime.FireTriggers(
                tsdb,
                databaseName,
                SqlTriggerEvent.Update,
                changes,
                controlPlane,
                effectiveTransaction);
            effectiveTransaction.AddTriggerAuditSequences(triggerAuditSequences);
            if (ownsTransaction)
                TableSqlExecutor.CommitTransaction(tsdb, effectiveTransaction);
            return result;
        }
        catch (Exception exception)
        {
            tsdb.Routines.Diagnostics.MarkTriggerTransactionFailure(
                effectiveTransaction.SnapshotTriggerAuditSequencesSince(savepoint),
                exception is SonnetDB.Exceptions.RoutineExecutionException routine
                    ? routine.Code
                    : SonnetDB.Exceptions.RoutineErrorCodes.ExecutionFailed);
            if (!ownsTransaction)
                effectiveTransaction.RollbackTo(savepoint);
            if (hasTriggers && exception is not SonnetDB.Exceptions.RoutineExecutionException)
            {
                throw new SonnetDB.Exceptions.RoutineExecutionException(
                    SonnetDB.Exceptions.RoutineErrorCodes.ExecutionFailed,
                    $"AFTER UPDATE 触发器事务提交失败：{exception.Message}",
                    exception);
            }
            throw;
        }
    }

    private static RowsAffectedExecutionResult ExecuteTableDeleteWithTriggers(
        Tsdb tsdb,
        string? databaseName,
        DeleteStatement statement,
        TableSchema schema,
        IControlPlane? controlPlane,
        SqlTransactionContext? transaction)
    {
        bool hasTriggers = tsdb.Routines.FindTriggers(schema.Name, SqlTriggerEvent.Delete).Count != 0;
        if (transaction is null && !hasTriggers)
            return TableSqlExecutor.ExecuteDelete(tsdb, statement, schema);

        bool ownsTransaction = transaction is null;
        var effectiveTransaction = transaction ?? new SqlTransactionContext();
        var savepoint = effectiveTransaction.CreateSavepoint();
        try
        {
            var result = TableSqlExecutor.QueueDelete(
                effectiveTransaction,
                tsdb,
                statement,
                schema,
                out var changes);
            IReadOnlyList<long> triggerAuditSequences = SqlRoutineRuntime.FireTriggers(
                tsdb,
                databaseName,
                SqlTriggerEvent.Delete,
                changes,
                controlPlane,
                effectiveTransaction);
            effectiveTransaction.AddTriggerAuditSequences(triggerAuditSequences);
            if (ownsTransaction)
                TableSqlExecutor.CommitTransaction(tsdb, effectiveTransaction);
            return result;
        }
        catch (Exception exception)
        {
            tsdb.Routines.Diagnostics.MarkTriggerTransactionFailure(
                effectiveTransaction.SnapshotTriggerAuditSequencesSince(savepoint),
                exception is SonnetDB.Exceptions.RoutineExecutionException routine
                    ? routine.Code
                    : SonnetDB.Exceptions.RoutineErrorCodes.ExecutionFailed);
            if (!ownsTransaction)
                effectiveTransaction.RollbackTo(savepoint);
            if (hasTriggers && exception is not SonnetDB.Exceptions.RoutineExecutionException)
            {
                throw new SonnetDB.Exceptions.RoutineExecutionException(
                    SonnetDB.Exceptions.RoutineErrorCodes.ExecutionFailed,
                    $"AFTER DELETE 触发器事务提交失败：{exception.Message}",
                    exception);
            }
            throw;
        }
    }

    private static LiteralExpression AsLiteral(SqlExpression expr, string columnName)
    {
        return expr switch
        {
            LiteralExpression lit => lit,
            UnaryExpression { Operator: SqlUnaryOperator.Negate, Operand: LiteralExpression lit } => NegateLiteral(lit, columnName),
            _ => throw new InvalidOperationException(
                $"列 '{columnName}' 的 VALUES 必须是字面量，不支持表达式 ({expr.GetType().Name})。"),
        };
    }

    private static LiteralExpression NegateLiteral(LiteralExpression literal, string columnName)
    {
        return literal.Kind switch
        {
            SqlLiteralKind.Integer => LiteralExpression.Integer(-literal.IntegerValue),
            SqlLiteralKind.Float => LiteralExpression.Float(-literal.FloatValue),
            _ => throw new InvalidOperationException(
                $"列 '{columnName}' 的 VALUES 只支持对数值字面量使用一元负号，实际为 {literal.Kind}。"),
        };
    }

    private static long ExtractTimestamp(SqlExpression expr)
    {
        var lit = AsLiteral(expr, "time");
        if (lit.Kind != SqlLiteralKind.Integer)
            throw new InvalidOperationException(
                $"'time' 列必须是非负整数字面量（Unix 毫秒），实际为 {lit.Kind}。");
        if (lit.IntegerValue < 0)
            throw new InvalidOperationException(
                $"'time' 列时间戳不能为负数，实际为 {lit.IntegerValue}。");
        return lit.IntegerValue;
    }

    private static FieldValue ConvertDeclaredField(SqlExpression expression, MeasurementColumn column)
    {
        if (expression is VectorLiteralExpression vecExpr)
        {
            if (column.DataType != FieldType.Vector)
                throw new InvalidOperationException(
                    $"Field 列 '{column.Name}' 不是 VECTOR 列，不允许传入向量字面量。");
            return ConvertVectorField(vecExpr, column);
        }

        if (expression is GeoPointLiteralExpression geoExpr)
        {
            if (column.DataType != FieldType.GeoPoint)
                throw new InvalidOperationException(
                    $"Field 列 '{column.Name}' 不是 GEOPOINT 列，不允许传入 POINT(lat, lon) 字面量。");
            return FieldValue.FromGeoPoint(geoExpr.Lat, geoExpr.Lon);
        }

        var literal = AsLiteral(expression, column.Name);
        if (literal.Kind == SqlLiteralKind.Null)
            throw new InvalidOperationException(
                $"Field 列 '{column.Name}' 不允许为 NULL。");
        return ConvertField(literal, column);
    }

    private static FieldValue ConvertInferredField(SqlExpression expression, string columnName)
    {
        if (expression is VectorLiteralExpression vecExpr)
            return ConvertVectorLiteral(vecExpr);
        if (expression is GeoPointLiteralExpression geoExpr)
            return FieldValue.FromGeoPoint(geoExpr.Lat, geoExpr.Lon);

        var literal = AsLiteral(expression, columnName);
        if (literal.Kind == SqlLiteralKind.Null)
            throw new InvalidOperationException(
                $"Field 列 '{columnName}' 不允许为 NULL。");

        return literal.Kind switch
        {
            SqlLiteralKind.Float => FieldValue.FromDouble(literal.FloatValue),
            SqlLiteralKind.Integer => FieldValue.FromLong(literal.IntegerValue),
            SqlLiteralKind.Boolean => FieldValue.FromBool(literal.BooleanValue),
            SqlLiteralKind.String => FieldValue.FromString(literal.StringValue!),
            _ => throw new InvalidOperationException($"不支持的 FIELD 字面量类型 {literal.Kind}。"),
        };
    }

    private static FieldValue ConvertField(LiteralExpression literal, MeasurementColumn column)
    {
        switch (column.DataType)
        {
            case FieldType.Float64:
                return literal.Kind switch
                {
                    SqlLiteralKind.Float => FieldValue.FromDouble(literal.FloatValue),
                    SqlLiteralKind.Integer => FieldValue.FromDouble(literal.IntegerValue),
                    _ => throw TypeMismatch(column, literal.Kind),
                };
            case FieldType.Int64:
                return literal.Kind switch
                {
                    SqlLiteralKind.Integer => FieldValue.FromLong(literal.IntegerValue),
                    SqlLiteralKind.Float => FieldValue.FromDouble(literal.FloatValue),
                    _ => throw TypeMismatch(column, literal.Kind),
                };
            case FieldType.Boolean:
                if (literal.Kind != SqlLiteralKind.Boolean)
                    throw TypeMismatch(column, literal.Kind);
                return FieldValue.FromBool(literal.BooleanValue);
            case FieldType.String:
                if (literal.Kind != SqlLiteralKind.String)
                    throw TypeMismatch(column, literal.Kind);
                return FieldValue.FromString(literal.StringValue!);
            case FieldType.Vector:
                throw new InvalidOperationException(
                    $"Field 列 '{column.Name}' 是 VECTOR 列，必须传入 [..] 向量字面量，不允许标量字面量。");
            case FieldType.GeoPoint:
                throw new InvalidOperationException(
                    $"Field 列 '{column.Name}' 是 GEOPOINT 列，必须传入 POINT(lat, lon) 字面量，不允许标量字面量。");
            default:
                throw new NotSupportedException($"不支持的列类型 {column.DataType}。");
        }
    }

    private static MeasurementColumnRole InferUnknownColumnRole(
        IReadOnlyList<IReadOnlyList<SqlExpression>> rows,
        int columnIndex,
        string columnName)
    {
        var sawValue = false;
        foreach (var row in rows)
        {
            var expr = row[columnIndex];
            if (expr is VectorLiteralExpression or GeoPointLiteralExpression)
                return MeasurementColumnRole.Field;

            var literal = AsLiteral(expr, columnName);
            if (literal.Kind == SqlLiteralKind.Null)
                continue;

            sawValue = true;
            if (literal.Kind != SqlLiteralKind.String)
                return MeasurementColumnRole.Field;
        }

        if (!sawValue)
            throw new InvalidOperationException(
                $"无法从全 NULL 列 '{columnName}' 推断 TAG / FIELD。");
        return MeasurementColumnRole.Tag;
    }

    private static bool HasFieldBinding(IReadOnlyList<ColumnBinding> bindings, int timeColumnIndex)
    {
        for (int i = 0; i < bindings.Count; i++)
        {
            if (i != timeColumnIndex && bindings[i].Role == MeasurementColumnRole.Field)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 把 <see cref="VectorLiteralExpression"/> 校验维度并转换为 <see cref="FieldValue"/>（PR #58 b）。
    /// </summary>
    private static FieldValue ConvertVectorField(VectorLiteralExpression literal, MeasurementColumn column)
    {
        int expectedDim = column.VectorDimension
            ?? throw new InvalidOperationException(
                $"VECTOR 列 '{column.Name}' 缺少维度声明（schema 损坏）。");
        if (literal.Components.Count != expectedDim)
            throw new InvalidOperationException(
                $"VECTOR 列 '{column.Name}' 维度不匹配：声明 {expectedDim}，字面量 {literal.Components.Count}。");

        var arr = new float[expectedDim];
        for (int i = 0; i < expectedDim; i++)
            arr[i] = (float)literal.Components[i];
        return FieldValue.FromVector(arr);
    }

    private static FieldValue ConvertVectorLiteral(VectorLiteralExpression literal)
    {
        var arr = new float[literal.Components.Count];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = (float)literal.Components[i];
        return FieldValue.FromVector(arr);
    }

    private static InvalidOperationException TypeMismatch(MeasurementColumn column, SqlLiteralKind actual)
        => new($"Field 列 '{column.Name}' 期望 {column.DataType}，实际字面量类别为 {actual}。");

    /// <summary>INSERT 列绑定：要么是时间戳伪列，要么是 schema 中的某一列。</summary>
    private readonly struct ColumnBinding
    {
        public MeasurementColumn? Column { get; }
        public string Name { get; }
        public MeasurementColumnRole Role { get; }
        public bool IsTime { get; }

        private ColumnBinding(MeasurementColumn? column, string name, MeasurementColumnRole role, bool isTime = false)
        {
            Column = column;
            Name = name;
            Role = role;
            IsTime = isTime;
        }

        public static ColumnBinding Time { get; } = new(null, "time", MeasurementColumnRole.Field, isTime: true);
        public static ColumnBinding Schema(MeasurementColumn column) => new(column, column.Name, column.Role);
        public static ColumnBinding Inferred(string name, MeasurementColumnRole role) => new(null, name, role);
    }
}
