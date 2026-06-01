using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;

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
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(sql);

        var statement = SqlParser.Parse(sql);
        return ExecuteStatement(tsdb, databaseName, statement, controlPlane);
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
    /// 解析并执行一段 SQL 脚本，支持可选控制面与轻事务。
    /// </summary>
    public static IReadOnlyList<object?> ExecuteScript(Tsdb tsdb, string? databaseName, string sql, IControlPlane? controlPlane = null)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(sql);

        var statements = SqlParser.ParseScript(sql);
        var results = new List<object?>(statements.Count);
        SqlTransactionContext? transaction = null;
        foreach (var statement in statements)
        {
            if (statement is BeginTransactionStatement && transaction is not null && !transaction.IsCompleted)
                throw new InvalidOperationException("当前已有活动轻事务，不能嵌套 BEGIN。");

            var result = ExecuteStatement(tsdb, databaseName, statement, controlPlane, transaction);
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
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        return statement switch
        {
            BeginTransactionStatement => new SqlTransactionContext(),
            CommitTransactionStatement => transaction is null
                ? throw new InvalidOperationException("COMMIT 前没有活动轻事务。")
                : TableSqlExecutor.CommitTransaction(tsdb, transaction),
            RollbackTransactionStatement => RollbackTransaction(transaction),
            CreateMeasurementStatement create => ExecuteCreateMeasurement(tsdb, create),
            CreateTableStatement createTable => TableSqlExecutor.ExecuteCreateTable(tsdb, createTable),
            CreateTableIndexStatement createIndex => TableSqlExecutor.ExecuteCreateIndex(tsdb, createIndex),
            InsertStatement insert => ExecuteInsert(tsdb, insert, transaction),
            SelectStatement select => ExecuteSelect(tsdb, select),
            DeleteStatement delete => ExecuteDelete(tsdb, delete, transaction),
            UpdateStatement update => transaction is null
                ? TableSqlExecutor.ExecuteUpdate(tsdb, update)
                : TableSqlExecutor.QueueUpdate(transaction, tsdb, update),
            DropTableStatement dropTable => TableSqlExecutor.ExecuteDropTable(tsdb, dropTable),
            DropTableIndexStatement dropIndex => TableSqlExecutor.ExecuteDropIndex(tsdb, dropIndex),
            ShowMeasurementsStatement => ShowMeasurements(tsdb),
            ShowTablesStatement => TableSqlExecutor.ShowTables(tsdb),
            ShowTableIndexesStatement showIndexes => TableSqlExecutor.ShowIndexes(tsdb, showIndexes.TableName),
            DescribeMeasurementStatement describe => DescribeMeasurement(tsdb, describe.Name),
            DescribeTableStatement describeTable => TableSqlExecutor.DescribeTable(tsdb, describeTable.Name),
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

    private static RowsAffectedExecutionResult RollbackTransaction(SqlTransactionContext? transaction)
    {
        if (transaction is null)
            throw new InvalidOperationException("ROLLBACK 前没有活动轻事务。");
        transaction.MarkCompleted();
        return new RowsAffectedExecutionResult("*", 0, "rollback");
    }

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
            HnswVectorIndexSpec hnsw => VectorIndexDefinition.CreateHnsw(hnsw.M, hnsw.Ef),
            IvfVectorIndexSpec ivf => VectorIndexDefinition.CreateIvfFlat(ivf.NList, ivf.NProbe, ivf.MaxIterations),
            IvfPqVectorIndexSpec ivfPq => VectorIndexDefinition.CreateIvfPq(ivfPq.NList, ivfPq.NProbe, ivfPq.MaxIterations, ivfPq.M, ivfPq.NBits),
            VamanaVectorIndexSpec vamana => VectorIndexDefinition.CreateVamana(vamana.MaxDegree, vamana.SearchListSize, vamana.Alpha, vamana.BeamWidth),
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
        => ExecuteInsert(tsdb, statement, transaction: null);

    private static InsertExecutionResult ExecuteInsert(Tsdb tsdb, InsertStatement statement, SqlTransactionContext? transaction)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var tableSchema = tsdb.Tables.Catalog.TryGet(statement.Measurement);
        if (tableSchema is not null)
            return transaction is null
                ? TableSqlExecutor.ExecuteInsert(tsdb, statement, tableSchema)
                : TableSqlExecutor.QueueInsert(transaction, statement, tableSchema);

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
            bindings[i] = col is null
                ? ColumnBinding.Inferred(name, InferUnknownColumnRole(statement.Rows, i, name))
                : ColumnBinding.Schema(col);
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
        using var _ = SonnetDB.Query.Functions.UserFunctionRegistry.EnterScope(tsdb.Functions);
        var tableSchema = tsdb.Tables.Catalog.TryGet(statement.Measurement);
        if (statement.Join is not null)
            return JoinSqlExecutor.Execute(tsdb, statement);

        if (tableSchema is not null)
            return TableSqlExecutor.ExecuteSelect(tsdb, statement, tableSchema);

        return SelectExecutor.Execute(tsdb, statement);
    }

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
        => ExecuteDelete(tsdb, statement, transaction: null);

    private static DeleteExecutionResult ExecuteDelete(Tsdb tsdb, DeleteStatement statement, SqlTransactionContext? transaction)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        var tableSchema = tsdb.Tables.Catalog.TryGet(statement.Measurement);
        if (tableSchema is not null)
        {
            var affected = transaction is null
                ? TableSqlExecutor.ExecuteDelete(tsdb, statement, tableSchema).RowsAffected
                : TableSqlExecutor.QueueDelete(transaction, tsdb, statement, tableSchema).RowsAffected;
            return new DeleteExecutionResult(
                statement.Measurement,
                SeriesAffected: affected,
                TombstonesAdded: affected);
        }

        return DeleteExecutor.Execute(tsdb, statement);
    }

    private static LiteralExpression AsLiteral(SqlExpression expr, string columnName)
    {
        return expr switch
        {
            LiteralExpression lit => lit,
            _ => throw new InvalidOperationException(
                $"列 '{columnName}' 的 VALUES 必须是字面量，不支持表达式 ({expr.GetType().Name})。"),
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
