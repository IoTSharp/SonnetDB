using System.Buffers.Binary;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>#264：显式 Decimal 写入约束、裸类型兼容与 catalog 版本边界。</summary>
public sealed class SqlDecimalEnforcementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s264-" + Guid.NewGuid().ToString("N"));

    /// <summary>裸类型与显式默认值在 AST、克隆及重开后保持不同来源。</summary>
    [Fact]
    public void Declaration_BareAndExplicitDefault_PreservesSourceAcrossEvolutionAndReopen()
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE ledger(id INT, bare DECIMAL, declared DECIMAL(38,28), PRIMARY KEY(id))");
            Execute(db, "CREATE INDEX ix_bare ON ledger(bare)");
            Execute(db, "ALTER TABLE ledger ADD amount NUMERIC(5,2)");
            Execute(db, "ALTER TABLE ledger RENAME COLUMN amount TO total");
            Execute(db, "ALTER TABLE ledger ALTER declared DROP NOT NULL");
            var schema = db.Tables.Catalog.TryGet("ledger")!;
            Assert.False(schema.TryGetColumn("bare")!.HasExplicitDecimalDeclaration);
            Assert.True(schema.TryGetColumn("declared")!.HasExplicitDecimalDeclaration);
            Assert.True(schema.TryGetColumn("total")!.HasExplicitDecimalDeclaration);
            Assert.False(schema.WithName("copy").WithPrimaryKey(["id"]).TryGetColumn("bare")!.HasExplicitDecimalDeclaration);
            var (name, type, _, _, _, _) = schema.TryGetColumn("bare")!;
            Assert.Equal("bare", name);
            Assert.Equal(TableColumnType.Decimal, type);
            Execute(db, "ALTER TABLE ledger ALTER declared TYPE DECIMAL");
            Assert.False(db.Tables.Catalog.TryGet("ledger")!.TryGetColumn("declared")!.HasExplicitDecimalDeclaration);
            Execute(db, "ALTER TABLE ledger ALTER declared TYPE DECIMAL(38,28)");
        }
        using var reopened = Open();
        Assert.False(reopened.Tables.Catalog.TryGet("ledger")!.TryGetColumn("bare")!.HasExplicitDecimalDeclaration);
        Assert.True(reopened.Tables.Catalog.TryGet("ledger")!.TryGetColumn("declared")!.HasExplicitDecimalDeclaration);
        Assert.True(reopened.Tables.Catalog.TryGet("ledger")!.TryGetColumn("total")!.HasExplicitDecimalDeclaration);
    }

    /// <summary>后行超小数位或精度在分配 identity 前拒绝整条插入。</summary>
    [Theory]
    [InlineData("1.234")]
    [InlineData("1000")]
    [InlineData("-1000")]
    [InlineData("123456.789")]
    public void Insert_InvalidLaterValue_LeavesRowsAndIdentityUnchanged(string invalid)
    {
        using var db = Open();
        CreateAuto(db, "ledger");
        Assert.Throws<InvalidOperationException>(() => Execute(db,
            $"INSERT INTO ledger(amount) VALUES('1.23'),('{invalid}')"));
        Assert.Empty(Rows(db, "SELECT id FROM ledger"));
        Execute(db, "INSERT INTO ledger(amount) VALUES('1.2300')");
        var row = Assert.Single(Rows(db, "SELECT id,amount FROM ledger"));
        Assert.Equal(1L, row[0]);
        Assert.Equal(1.23m, row[1]);
    }

    /// <summary>核心直接写入入口不能绕过约束或消耗失败批次的生成列。</summary>
    [Theory]
    [InlineData("insert")]
    [InlineData("upsert")]
    [InlineData("many")]
    [InlineData("batch")]
    public void Storage_InvalidValue_IsRejectedBeforeGeneratedState(string operation)
    {
        using var db = Open();
        CreateAuto(db, "ledger");
        var store = db.Tables.Open("ledger");
        Assert.Throws<InvalidOperationException>(() =>
        {
            switch (operation)
            {
                case "insert": store.Insert([null, "1.234"]); break;
                case "upsert": store.Upsert([null, "1.234"]); break;
                case "many": store.InsertMany([new object?[] { null, 1.25m }, new object?[] { null, "1.234" }]); break;
                case "batch": store.ApplyBatch([new TableRowMutation(null, [null, 1.25m]), new TableRowMutation(null, [null, "1.234"])]); break;
            }
        });
        Assert.Empty(Rows(db, "SELECT id FROM ledger"));
        store.Insert([null, "1.25"]);
        Assert.Equal(1L, Assert.Single(Rows(db, "SELECT id FROM ledger"))[0]);
        Assert.IsType<decimal>(Assert.Single(Rows(db, "SELECT amount FROM ledger"))[0]);
    }

    /// <summary>跨表晚到失败不能提前预留另一表的 identity。</summary>
    [Fact]
    public void ApplyTransaction_InvalidSecondTable_LeavesBothTablesAndIdentitiesUnchanged()
    {
        using var db = Open();
        CreateAuto(db, "alpha");
        CreateAuto(db, "beta");
        var mutations = new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
        {
            ["alpha"] = [new TableRowMutation(null, [null, 1.25m])],
            ["beta"] = [new TableRowMutation(null, [null, 1.234m])],
        };
        Assert.Throws<InvalidOperationException>(() => db.Tables.ApplyTransaction(mutations));
        Assert.Empty(Rows(db, "SELECT id FROM alpha"));
        Assert.Empty(Rows(db, "SELECT id FROM beta"));
        db.Tables.Open("alpha").Insert([null, 1.25m]);
        db.Tables.Open("beta").Insert([null, 1.25m]);
        Assert.Equal(1L, Assert.Single(Rows(db, "SELECT id FROM alpha"))[0]);
        Assert.Equal(1L, Assert.Single(Rows(db, "SELECT id FROM beta"))[0]);
    }

    /// <summary>更新在整条语句成功前不发布部分 mutation，包括活动轻事务。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Update_InvalidLaterRow_LeavesStatementAndTransactionOverlayUnchanged(bool transaction)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE ledger(id INT, amount DECIMAL(5,2), PRIMARY KEY(id))");
        Execute(db, "INSERT INTO ledger(id,amount) VALUES(1,'1.25'),(2,'2.50')");
        var tx = transaction ? new SqlTransactionContext() : null;
        using IDisposable? scope = tx is null ? null : SqlTransactionContext.EnterScope(tx);
        Assert.Throws<InvalidOperationException>(() => Execute(db,
            "UPDATE ledger SET amount=CASE WHEN id=1 THEN '9.25' ELSE '1.234' END WHERE id>0"));
        Assert.Equal([1.25m, 2.50m], Rows(db, "SELECT amount FROM ledger ORDER BY id").Select(static r => r[0]).ToArray());
        if (tx is not null)
            Assert.Empty(tx.SnapshotTableMutations());
        Assert.Equal([1.25m, 2.50m], Rows(db, "SELECT amount FROM ledger ORDER BY id").Select(static r => r[0]).ToArray());
    }

    /// <summary>裸 Decimal 保持真实 CLR 范围与原二进制浮点兼容，显式类型拒绝隐式舍入。</summary>
    [Fact]
    public void BareDecimal_FullClrRangeAndDoubleCompatibility_ArePreserved()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE ledger(id INT, bare DECIMAL, declared DECIMAL(38,28), amount DECIMAL(5,2), PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "default", "INSERT INTO ledger(id,bare) VALUES(1,@value)", new SqlParameters().AddNamed("value", decimal.MaxValue));
        Assert.Equal(decimal.MaxValue, Assert.Single(Rows(db, "SELECT bare FROM ledger"))[0]);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "default",
            "INSERT INTO ledger(id,declared) VALUES(2,@value)", new SqlParameters().AddNamed("value", decimal.MaxValue)));
        db.Tables.Open("ledger").Insert([3L, 0.1d, null, null]);
        Assert.Equal(0.1m, Assert.Single(Rows(db, "SELECT bare FROM ledger WHERE id=3"))[0]);
        Assert.Throws<InvalidOperationException>(() => db.Tables.Open("ledger").Insert([4L, null, null, 0.1d]));
        db.Tables.Open("ledger").Insert([4L, null, null, 0.5d]);
        Assert.Equal(0.5m, Assert.Single(Rows(db, "SELECT amount FROM ledger WHERE id=4"))[0]);
    }

    /// <summary>旧版非默认声明启用后续写入检查，来源不明的38/28保持兼容。</summary>
    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void LegacyCodec_NonDefaultDeclarationIsExplicit_DefaultRemainsUnconstrained(int version)
    {
        Directory.CreateDirectory(_root);
        var schema = TableSchema.CreateWithDefaults("ledger",
            [("id", TableColumnType.Int64, false), ("amount", TableColumnType.Decimal, true), ("bare", TableColumnType.Decimal, true)],
            ["id"], null, null, null, 1, null, null, null,
            new Dictionary<string, (byte Precision, byte Scale)> { ["amount"] = (5, 2), ["bare"] = (38, 28) },
            new HashSet<string>(StringComparer.Ordinal));
        string file = Path.Combine(_root, "legacy.tblschema");
        TableSchemaCodec.Save(file, [schema]);
        byte[] bytes = File.ReadAllBytes(file);
        Assert.Equal(11, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), version);
        File.WriteAllBytes(file, bytes);
        var loaded = Assert.Single(TableSchemaCodec.Load(file));
        Assert.True(loaded.TryGetColumn("amount")!.HasExplicitDecimalDeclaration);
        Assert.False(loaded.TryGetColumn("bare")!.HasExplicitDecimalDeclaration);
    }

    /// <summary>历史不合规行可以精确读取，之后只能通过合法写入修正。</summary>
    [Fact]
    public void LegacyInvalidRows_AreNotRewrittenOnRead_AndLaterWritesAreEnforced()
    {
        string schemaPath;
        using (var db = Open())
        {
            var schema = TableSchema.CreateWithDefaults("ledger",
                [("id", TableColumnType.Int64, false), ("amount", TableColumnType.Decimal, true)],
                ["id"], null, null, null, 1, null, null, null,
                new Dictionary<string, (byte Precision, byte Scale)> { ["amount"] = (5, 2) },
                new HashSet<string>(StringComparer.Ordinal));
            db.Tables.Create(schema);
            db.Tables.Open("ledger").Insert([1L, 1.234m]);
            schemaPath = db.Tables.SchemaPath;
        }
        byte[] bytes = File.ReadAllBytes(schemaPath);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), 10);
        File.WriteAllBytes(schemaPath, bytes);
        using var reopened = Open();
        Assert.True(reopened.Tables.Catalog.TryGet("ledger")!.TryGetColumn("amount")!.HasExplicitDecimalDeclaration);
        Assert.Equal(1.234m, Assert.Single(Rows(reopened, "SELECT amount FROM ledger"))[0]);
        Assert.Throws<InvalidOperationException>(() => Execute(reopened, "INSERT INTO ledger(id,amount) VALUES(2,'1.235')"));
        Execute(reopened, "UPDATE ledger SET amount='1.23' WHERE id=1");
        Assert.Equal(1.23m, Assert.Single(Rows(reopened, "SELECT amount FROM ledger"))[0]);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private static object? Execute(Tsdb db, string sql) => SqlExecutor.Execute(db, sql);
    private static IReadOnlyList<IReadOnlyList<object?>> Rows(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(Execute(db, sql)).Rows;
    private static void CreateAuto(Tsdb db, string name)
        => Execute(db, $"CREATE TABLE {name}(id INT AUTO_INCREMENT, amount DECIMAL(5,2), PRIMARY KEY(id))");

    /// <summary>回收独占测试目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
