using System.Buffers.Binary;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>#241：Decimal DDL 声明、无损预检与持久化原子性。</summary>
public sealed class SqlDecimalDeclarationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "s241-" + Guid.NewGuid().ToString("N"));

    /// <summary>各个 ALTER 写法保留精度及零小数位。</summary>
    [Theory]
    [InlineData("ADD COLUMN fee DECIMAL(12,0)", true)]
    [InlineData("ADD fee NUMERIC(12,0)", true)]
    [InlineData("ALTER COLUMN amount TYPE DECIMAL(12,0)", false)]
    [InlineData("ALTER amount SET DATA TYPE NUMERIC(12,0)", false)]
    [InlineData("ALTER COLUMN amount DECIMAL(12,0) NULL", false)]
    [InlineData("ALTER amount NUMERIC(12,0)", false)]
    public void ParseAlter_ExplicitDeclaration_PreservesPrecisionAndScale(string clause, bool add)
    {
        var statement = SqlParser.Parse("ALTER TABLE ledger " + clause);
        if (add)
        {
            var parsed = Assert.IsType<AlterTableAddColumnStatement>(statement);
            Assert.Equal((byte)12, parsed.DecimalPrecision);
            Assert.Equal((byte)0, parsed.DecimalScale);
            var (_, _, type, _, _, _, _) = parsed;
            Assert.Equal(SqlDataType.Decimal, type);
        }
        else
        {
            var parsed = Assert.IsType<AlterTableAlterColumnStatement>(statement);
            Assert.Equal((byte)12, parsed.DecimalPrecision);
            Assert.Equal((byte)0, parsed.DecimalScale);
            var (_, _, type, _, _, _) = parsed;
            Assert.Equal(SqlDataType.Decimal, type);
        }
    }

    /// <summary>已有 CREATE 元数据、添加列与各个修改写法均在重开后保持。</summary>
    [Fact]
    public void AlterDecimal_ValidValues_PreservesDeclarationsAcrossReopen()
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE ledger (id INT, amount DECIMAL(18,4), untouched NUMERIC(9,0), PRIMARY KEY(id))");
            Execute(db, "INSERT INTO ledger(id,amount,untouched) VALUES(1,'1.2300',7),(2,'-999.9900',8),(3,NULL,9)");
            Execute(db, "CREATE INDEX ix_amount ON ledger(amount)");
            Execute(db, "ALTER TABLE ledger ADD COLUMN fee DECIMAL(12,2) NOT NULL DEFAULT CAST('1.25' AS DECIMAL)");
            Execute(db, "ALTER TABLE ledger ALTER COLUMN amount TYPE DECIMAL(15,2)");
            Execute(db, "ALTER TABLE ledger ALTER COLUMN amount SET DATA TYPE NUMERIC(14,2)");
            Execute(db, "ALTER TABLE ledger ALTER COLUMN amount DECIMAL(13,2) NULL");
            Execute(db, "ALTER TABLE ledger ALTER COLUMN fee SET DEFAULT '1.25'");
            Execute(db, "ALTER TABLE ledger ALTER COLUMN fee DROP NOT NULL");
            AssertDeclaration(db, "amount", 13, 2);
            AssertDeclaration(db, "fee", 12, 2);
            AssertDeclaration(db, "untouched", 9, 0);
            Assert.Equal([1.23m, -999.99m, null], Values(db, "SELECT amount FROM ledger ORDER BY id"));
            Assert.Equal([1.25m, 1.25m, 1.25m], Values(db, "SELECT fee FROM ledger ORDER BY id"));
        }
        using var reopened = Open();
        AssertDeclaration(reopened, "amount", 13, 2);
        AssertDeclaration(reopened, "fee", 12, 2);
        AssertDeclaration(reopened, "untouched", 9, 0);
        Assert.Equal([1.23m, -999.99m, null], Values(reopened, "SELECT amount FROM ledger ORDER BY id"));
        Assert.Single(Assert.IsType<SelectExecutionResult>(Execute(reopened, "SELECT id FROM ledger WHERE amount = 1.23")).Rows);
    }

    /// <summary>后续行的缩窄失败保留所有行、索引和原始声明，重开后亦相同。</summary>
    [Theory]
    [InlineData("1.234")]
    [InlineData("1000")]
    [InlineData("-1000")]
    public void AlterDecimal_LaterRowFails_LeavesRowsIndexesAndSchemaUntouched(string rejected)
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE ledger (id INT, amount DECIMAL(18,4), untouched DECIMAL(9,0), PRIMARY KEY(id))");
            Execute(db, $"INSERT INTO ledger(id,amount,untouched) VALUES(1,'1.23',7),(2,'{rejected}',8)");
            Execute(db, "CREATE UNIQUE INDEX ix_amount ON ledger(amount)");
            var before = Values(db, "SELECT amount FROM ledger ORDER BY id");
            Assert.Throws<InvalidOperationException>(() => Execute(db, "ALTER TABLE ledger ALTER COLUMN amount TYPE DECIMAL(5,2)"));
            Assert.Equal(before, Values(db, "SELECT amount FROM ledger ORDER BY id"));
            AssertDeclaration(db, "amount", 18, 4);
            AssertDeclaration(db, "untouched", 9, 0);
            Assert.Single(Assert.IsType<SelectExecutionResult>(Execute(db, "SELECT id FROM ledger WHERE amount = 1.23")).Rows);
        }
        using var reopened = Open();
        AssertDeclaration(reopened, "amount", 18, 4);
        AssertDeclaration(reopened, "untouched", 9, 0);
        Assert.Equal(2, Values(reopened, "SELECT amount FROM ledger ORDER BY id").Length);
        Assert.Equal(decimal.Parse(rejected, System.Globalization.CultureInfo.InvariantCulture), Values(reopened, "SELECT amount FROM ledger WHERE id = 2")[0]);
    }

    /// <summary>有损字符串及嵌套 CAST 默认值在转换发生舍入之前拒绝。</summary>
    [Theory]
    [InlineData("'1.234'", "DECIMAL(5,2)")]
    [InlineData("'1000'", "DECIMAL(5,2)")]
    [InlineData("'0.00000000000000000000000000001'", "DECIMAL")]
    [InlineData("'1.00000000000000000000000000001'", "DECIMAL")]
    [InlineData("CAST('1.00000000000000000000000000001' AS DECIMAL)", "DECIMAL")]
    public void AddDecimal_LossyDefault_LeavesSchemaAndRowsUntouched(string expression, string type)
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE ledger(id INT, amount DECIMAL(18,4), PRIMARY KEY(id))");
            Execute(db, "INSERT INTO ledger(id,amount) VALUES(1,'1.23')");
            Assert.Throws<InvalidOperationException>(() => Execute(db, $"ALTER TABLE ledger ADD fee {type} DEFAULT {expression}"));
            Assert.Null(db.Tables.Catalog.TryGet("ledger")!.TryGetColumn("fee"));
            Assert.Equal([1.23m], Values(db, "SELECT amount FROM ledger"));
        }
        using var reopened = Open();
        Assert.Null(reopened.Tables.Catalog.TryGet("ledger")!.TryGetColumn("fee"));
        Assert.Equal([1.23m], Values(reopened, "SELECT amount FROM ledger"));
    }

    /// <summary>修改类型时预检保留的默认值，修改默认值时亦拒绝声明内的精度损失。</summary>
    [Fact]
    public void AlterDecimal_DefaultFails_LeavesOriginalDefaultAndDeclaration()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE ledger(id INT, amount DECIMAL(18,4) DEFAULT '1.234', PRIMARY KEY(id))");
        Assert.Throws<InvalidOperationException>(() => Execute(db, "ALTER TABLE ledger ALTER amount TYPE DECIMAL(5,2)"));
        AssertDeclaration(db, "amount", 18, 4);
        Execute(db, "ALTER TABLE ledger ALTER amount TYPE DECIMAL(5,2) SET DEFAULT '1.23'");
        var before = db.Tables.Catalog.TryGet("ledger")!.TryGetColumn("amount")!.DefaultExpressionSql;
        Assert.Throws<InvalidOperationException>(() => Execute(db, "ALTER TABLE ledger ALTER amount SET DEFAULT '1.234'"));
        Assert.Throws<InvalidOperationException>(() => Execute(db, "ALTER TABLE ledger ALTER amount TYPE DECIMAL(5,2) SET DEFAULT '1.00000000000000000000000000001'"));
        Assert.Equal(before, db.Tables.Catalog.TryGet("ledger")!.TryGetColumn("amount")!.DefaultExpressionSql);
        AssertDeclaration(db, "amount", 5, 2);
    }

    /// <summary>裸 Decimal 类型变更保留 CLR 完整值域，显式声明的缩窄则拒绝损失。</summary>
    [Fact]
    public void AlterDecimal_BareType_PreservesFullClrRange()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE ledger(id INT, amount DECIMAL, PRIMARY KEY(id))");
        Execute(db, $"INSERT INTO ledger(id,amount) VALUES(1,DECIMAL '{decimal.MaxValue}')");
        Execute(db, $"INSERT INTO ledger(id,amount) VALUES(2,DECIMAL '{decimal.MinValue}')");
        Execute(db, "ALTER TABLE ledger ALTER amount TYPE DECIMAL");
        Assert.Equal([decimal.MaxValue, decimal.MinValue], Values(db, "SELECT amount FROM ledger ORDER BY id"));
        Assert.Throws<InvalidOperationException>(() => Execute(db, "ALTER TABLE ledger ALTER amount TYPE DECIMAL(38,28)"));
        Assert.Equal([decimal.MaxValue, decimal.MinValue], Values(db, "SELECT amount FROM ledger ORDER BY id"));
        Execute(db, $"ALTER TABLE ledger ADD maximum DECIMAL DEFAULT '{decimal.MaxValue}'");
        Assert.Equal([decimal.MaxValue, decimal.MaxValue], Values(db, "SELECT maximum FROM ledger ORDER BY id"));
    }

    /// <summary>DDL 的 38 位声明仍独立受 Decimal 实际值域约束。</summary>
    [Fact]
    public void AlterDecimal_ExtremeScale_UsesExactIntegerValidation()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE ledger(id INT, amount DECIMAL, PRIMARY KEY(id))");
        Execute(db, "INSERT INTO ledger(id,amount) VALUES(1,DECIMAL '0.0000000000000000000000000001')");
        Execute(db, "ALTER TABLE ledger ALTER amount TYPE DECIMAL(38,38)");
        AssertDeclaration(db, "amount", 38, 38);
        Assert.Throws<InvalidOperationException>(() => Execute(db, "ALTER TABLE ledger ALTER amount TYPE DECIMAL(1,0)"));
        AssertDeclaration(db, "amount", 38, 38);
    }

    /// <summary>从字符串列迁移也在所有行转换成功前保持旧载荷。</summary>
    [Fact]
    public void AlterString_ToDecimalRejectsRounding_LeavesAllStringPayloads()
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE ledger(id INT, amount STRING, PRIMARY KEY(id))");
            Execute(db, "INSERT INTO ledger(id,amount) VALUES(1,'1.23'),(2,'1.00000000000000000000000000001')");
            var before = Values(db, "SELECT amount FROM ledger ORDER BY id");
            Assert.Throws<InvalidOperationException>(() => Execute(db, "ALTER TABLE ledger ALTER amount TYPE DECIMAL"));
            Assert.Equal(before, Values(db, "SELECT amount FROM ledger ORDER BY id"));
        }
        using var reopened = Open();
        Assert.Equal(TableColumnType.String, reopened.Tables.Catalog.TryGet("ledger")!.TryGetColumn("amount")!.DataType);
        Assert.Equal("1.00000000000000000000000000001", Values(reopened, "SELECT amount FROM ledger WHERE id=2")[0]);
    }

    /// <summary>二进制浮点输入仅在真实数值可无损表示时允许 DDL 转换。</summary>
    [Theory]
    [InlineData("0.5", true)]
    [InlineData("0.1", false)]
    public void AlterFloat_ToDecimal_ChecksBinaryValueExactly(string number, bool succeeds)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE ledger(id INT, amount FLOAT, PRIMARY KEY(id))");
        Execute(db, $"INSERT INTO ledger(id,amount) VALUES(1,{number})");
        if (succeeds)
        {
            Execute(db, "ALTER TABLE ledger ALTER amount TYPE DECIMAL(8,2)");
            Assert.Equal([0.5m], Values(db, "SELECT amount FROM ledger"));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => Execute(db, "ALTER TABLE ledger ALTER amount TYPE DECIMAL(8,2)"));
            Assert.Equal(TableColumnType.Float64, db.Tables.Catalog.TryGet("ledger")!.TryGetColumn("amount")!.DataType);
        }
    }

    /// <summary>每个 schema 克隆路径保留或正确重命名 Decimal 声明，codec 升级为 v11。</summary>
    [Theory]
    [InlineData("add-index")]
    [InlineData("drop-index")]
    [InlineData("add-fk")]
    [InlineData("drop-fk")]
    [InlineData("add-check")]
    [InlineData("drop-check")]
    [InlineData("add-column")]
    [InlineData("alter-column")]
    [InlineData("drop-column")]
    [InlineData("drop-decimal")]
    [InlineData("to-float")]
    [InlineData("to-decimal")]
    [InlineData("rename-column")]
    [InlineData("rename-table")]
    [InlineData("primary-key")]
    public void SchemaClone_PreservesAllDecimalDeclarationsAcrossCodec(string operation)
    {
        Directory.CreateDirectory(_root);
        var schema = TableSchema.CreateWithDefaults("ledger",
            [("id", TableColumnType.Int64, false), ("amount", TableColumnType.Decimal, true),
             ("untouched", TableColumnType.Decimal, true), ("description", TableColumnType.String, true)],
            ["id"], [new TableIndexDefinition("ix_amount", ["amount"], false)],
            [new TableForeignKeyDefinition("fk_id", ["id"], "parents", ["id"])],
            rowVersionColumns: null, createdAtUtcTicks: 0,
            checkConstraints: [new TableCheckConstraintDefinition("ck_id", "id >= 0")],
            columnDefaults: new Dictionary<string, string?> { ["amount"] = "1.25" },
            autoIncrementColumns: null,
            decimalDefinitions: new Dictionary<string, (byte Precision, byte Scale)> { ["amount"] = (18, 4), ["untouched"] = (9, 0) });
        var clone = operation switch
        {
            "add-index" => schema.WithIndex(new TableIndexDefinition("ix_other", ["untouched"], false)),
            "drop-index" => schema.WithoutIndex("ix_amount"),
            "add-fk" => schema.WithForeignKey(new TableForeignKeyDefinition("fk_other", ["id"], "others", ["id"])),
            "drop-fk" => schema.WithoutForeignKey("fk_id"),
            "add-check" => schema.WithCheckConstraint(new TableCheckConstraintDefinition("ck_other", "id < 100")),
            "drop-check" => schema.WithoutCheckConstraint("ck_id"),
            "add-column" => schema.WithAddedColumn("extra", TableColumnType.String, true),
            "alter-column" => schema.WithAlteredColumn("description", TableColumnType.String, false, null),
            "drop-column" => schema.WithoutColumn("description"),
            "drop-decimal" => schema.WithoutIndex("ix_amount").WithoutColumn("amount"),
            "to-float" => schema.WithAlteredColumn("amount", TableColumnType.Float64, true, null),
            "to-decimal" => schema.WithAlteredColumn("description", TableColumnType.Decimal, true, null),
            "rename-column" => schema.WithRenamedColumn("amount", "renamed"),
            "rename-table" => schema.WithName("renamed_table"),
            "primary-key" => schema.WithPrimaryKey(["id"]),
            _ => throw new InvalidOperationException("未知测试路径。"),
        };
        string amountName = operation == "rename-column" ? "renamed" : "amount";
        if (operation == "drop-decimal")
        {
            Assert.Null(clone.TryGetColumn(amountName));
        }
        else
        {
            Assert.Equal(operation == "to-float" ? (byte)0 : (byte)18, clone.TryGetColumn(amountName)!.DecimalPrecision);
            Assert.Equal(operation == "to-float" ? (byte)0 : (byte)4, clone.TryGetColumn(amountName)!.DecimalScale);
        }
        if (operation == "to-decimal")
        {
            Assert.Equal((byte)38, clone.TryGetColumn("description")!.DecimalPrecision);
            Assert.Equal((byte)28, clone.TryGetColumn("description")!.DecimalScale);
        }
        Assert.Equal((byte)9, clone.TryGetColumn("untouched")!.DecimalPrecision);
        Assert.Equal((byte)0, clone.TryGetColumn("untouched")!.DecimalScale);
        string path = Path.Combine(_root, "schema.tblschema");
        TableSchemaCodec.Save(path, [clone]);
        Assert.Equal(11, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(path).AsSpan(8, 4)));
        var loaded = Assert.Single(TableSchemaCodec.Load(path));
        Assert.Equal(clone.Columns, loaded.Columns);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private static object? Execute(Tsdb db, string sql) => SqlExecutor.Execute(db, sql);
    private static object?[] Values(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(Execute(db, sql)).Rows.Select(static row => row[0]).ToArray();
    private static void AssertDeclaration(Tsdb db, string name, byte precision, byte scale)
    {
        var column = db.Tables.Catalog.TryGet("ledger")!.TryGetColumn(name)!;
        Assert.Equal(precision, column.DecimalPrecision);
        Assert.Equal(scale, column.DecimalScale);
    }

    /// <summary>回收本测试独占的数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
