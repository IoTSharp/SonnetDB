using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证维护接口的真实约束名、索引选项及自增下一值合同。</summary>
public sealed partial class SqlMaintenanceContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sndb-maintenance-{Guid.NewGuid():N}");

    /// <summary>显式约束名必须经过表和列重命名、重开后保持原拼写。</summary>
    [Fact]
    public void Constraints_RenameAndReopen_PreserveRealNames()
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE Parent(Id INT,PRIMARY KEY(Id))");
            Execute(db, "CREATE TABLE Device(Id INT,ParentId INT,Value INT,CONSTRAINT PkDevice PRIMARY KEY(Id),CONSTRAINT FkDevice FOREIGN KEY(ParentId) REFERENCES Parent(Id),CONSTRAINT CkDevice CHECK(Value>=0))");
            Execute(db, "ALTER TABLE Device RENAME COLUMN Id TO DeviceId");
            Execute(db, "ALTER TABLE Device RENAME TO RenamedDevice");
        }

        using var reopened = Open();
        var rows = Query(reopened, "SELECT constraint_name,constraint_type,table_name FROM information_schema.table_constraints WHERE table_name='RenamedDevice'").Rows;
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, row => Equals(row[0], "PkDevice") && Equals(row[1], "PRIMARY KEY"));
        Assert.Contains(rows, row => Equals(row[0], "FkDevice") && Equals(row[1], "FOREIGN KEY"));
        Assert.Contains(rows, row => Equals(row[0], "CkDevice") && Equals(row[1], "CHECK"));
        Assert.All(rows, row => Assert.Equal("RenamedDevice", row[2]));
    }

    /// <summary>约束名遵循引号精确匹配和未加引号忽略大小写规则。</summary>
    [Fact]
    public void DropConstraint_QuotedName_RequiresExactSpelling()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE Device(Id INT,CONSTRAINT \"PkDevice\" PRIMARY KEY(Id))");
        Execute(db, "INSERT INTO Device(Id) VALUES(1)");
        Execute(db, "ALTER TABLE Device DROP CONSTRAINT \"pkdevice\"");
        Assert.Single(Query(db, "SELECT constraint_name FROM information_schema.table_constraints WHERE table_name='Device'").Rows);
        Execute(db, "ALTER TABLE Device DROP CONSTRAINT pkdevice");
        Assert.Empty(Query(db, "SELECT constraint_name FROM information_schema.table_constraints WHERE table_name='Device'").Rows);
        Execute(db, "INSERT INTO Device(Id) VALUES(1)");
        Assert.Equal(2, Query(db, "SELECT Id FROM Device").Rows.Count);
    }

    /// <summary>同一表的约束名称不能仅因类型或大小写不同而重复。</summary>
    [Theory]
    [InlineData("CONSTRAINT Same PRIMARY KEY(Id)")]
    [InlineData("CONSTRAINT Same FOREIGN KEY(ParentId) REFERENCES Parent(Id),PRIMARY KEY(Id)")]
    public void CreateConstraint_CaseCollision_RejectsWithoutPublishing(string constraints)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE Parent(Id INT,PRIMARY KEY(Id))");
        Execute(db, $"CREATE TABLE Device(Id INT,ParentId INT,{constraints})");
        Assert.ThrowsAny<Exception>(() => Execute(db, "ALTER TABLE Device ADD CONSTRAINT same CHECK(Id>0)"));
        Assert.Empty(db.Tables.Catalog.TryGet("Device")!.CheckConstraints);
    }

    /// <summary>删除主键必须迁移已有行的稳定身份，并保留二级索引和恢复能力。</summary>
    [Fact]
    public void DropPrimaryConstraint_ExistingRows_PreservesIndexAndKeylessIdentity()
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE Device(Id INT,Name STRING,CONSTRAINT PkDevice PRIMARY KEY(Id))");
            Execute(db, "CREATE INDEX IxName ON Device(Name)");
            Execute(db, "INSERT INTO Device(Id,Name) VALUES(1,'same')");
            Execute(db, "ALTER TABLE Device DROP CONSTRAINT PkDevice");
            Execute(db, "INSERT INTO Device(Id,Name) VALUES(1,'same')");
            Assert.Equal(2, Query(db, "SELECT Id FROM Device WHERE Name='same'").Rows.Count);
        }

        using var reopened = Open();
        Assert.Empty(reopened.Tables.Catalog.TryGet("Device")!.PrimaryKey);
        Assert.Single(reopened.Tables.Catalog.TryGet("Device")!.Indexes);
        Assert.Equal(2, Query(reopened, "SELECT Id FROM Device WHERE Name='same'").Rows.Count);
        Execute(reopened, "UPDATE Device SET Name='changed' WHERE Id=1");
        Assert.Equal(2, Query(reopened, "SELECT Id FROM Device WHERE Name='changed'").Rows.Count);
    }

    /// <summary>有入站外键的主键删除失败后必须保留约束和原始数据。</summary>
    [Fact]
    public void DropPrimaryConstraint_ReferencedParent_RejectsAtomically()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE Parent(Id INT,CONSTRAINT PkParent PRIMARY KEY(Id))");
        Execute(db, "CREATE TABLE Child(Id INT,ParentId INT,PRIMARY KEY(Id),CONSTRAINT FkParent FOREIGN KEY(ParentId) REFERENCES Parent(Id))");
        Execute(db, "INSERT INTO Parent(Id) VALUES(1)");
        Execute(db, "INSERT INTO Child(Id,ParentId) VALUES(1,1)");
        Assert.ThrowsAny<Exception>(() => Execute(db, "ALTER TABLE Parent DROP CONSTRAINT PkParent"));
        Assert.Single(Query(db, "SELECT constraint_name FROM information_schema.table_constraints WHERE constraint_name='PkParent'").Rows);
        Assert.Single(Query(db, "SELECT Id FROM Parent").Rows);
        Assert.Single(Query(db, "SELECT Id FROM Child").Rows);
    }

    /// <summary>空表设置下一值不能创建占位记录，重开后仍分配该值。</summary>
    [Fact]
    public void AutoIncrement_EmptyTableAndReopen_UsesConfiguredNextValue()
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE Device(Id INT AUTO_INCREMENT,Name STRING,PRIMARY KEY(Id))");
            Execute(db, "ALTER TABLE Device AUTO_INCREMENT = 50");
            Assert.Empty(Query(db, "SELECT Id FROM Device").Rows);
        }

        using var reopened = Open();
        Execute(reopened, "INSERT INTO Device(Name) VALUES('first')");
        Assert.Equal(50L, Query(reopened, "SELECT Id FROM Device").Rows.Single()[0]);
    }

    /// <summary>删除历史最大行不会降低高水位；冲突设置失败也不能改变下一值。</summary>
    [Fact]
    public void AutoIncrement_DeletedMaximumAndConflict_PreservesHighWater()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE Device(Id INT AUTO_INCREMENT,PRIMARY KEY(Id))");
        Execute(db, "INSERT INTO Device(Id) VALUES(100)");
        Execute(db, "DELETE FROM Device WHERE Id=100");
        Execute(db, "ALTER TABLE Device AUTO_INCREMENT = 200");
        Assert.ThrowsAny<Exception>(() => Execute(db, "ALTER TABLE Device AUTO_INCREMENT = 100"));
        Execute(db, "INSERT INTO Device(Id) VALUES(NULL)");
        Assert.Equal(200L, Query(db, "SELECT Id FROM Device").Rows.Single()[0]);
    }

    /// <summary>无效输入不得改变已有自增状态。</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    public void AutoIncrement_InvalidNextValue_LeavesStateUnchanged(string value)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE Device(Id INT AUTO_INCREMENT,PRIMARY KEY(Id))");
        Execute(db, "ALTER TABLE Device AUTO_INCREMENT = 50");
        Assert.ThrowsAny<Exception>(() => Execute(db, $"ALTER TABLE Device AUTO_INCREMENT = {value}"));
        Execute(db, "INSERT INTO Device(Id) VALUES(NULL)");
        Assert.Equal(50L, Query(db, "SELECT Id FROM Device").Rows.Single()[0]);
    }

    /// <summary>最大下一值只能分配一次，溢出失败不能插入记录。</summary>
    [Fact]
    public void AutoIncrement_MaximumNextValue_AllocatesOnceThenRejectsOverflow()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE Device(Id INT AUTO_INCREMENT,PRIMARY KEY(Id))");
        Execute(db, "ALTER TABLE Device AUTO_INCREMENT = 9223372036854775807");
        Execute(db, "INSERT INTO Device(Id) VALUES(NULL)");
        Assert.ThrowsAny<Exception>(() => Execute(db, "INSERT INTO Device(Id) VALUES(NULL)"));
        Assert.Equal(long.MaxValue, Query(db, "SELECT Id FROM Device").Rows.Single()[0]);
    }

    /// <summary>并发分配与冲突维护命令不能重复分配或回退高水位。</summary>
    [Fact]
    public void AutoIncrement_ConcurrentInsertsAndConflicts_RemainUnique()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE Device(Id INT AUTO_INCREMENT,PRIMARY KEY(Id))");
        Execute(db, "ALTER TABLE Device AUTO_INCREMENT = 100");
        Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ =>
        {
            Execute(db, "INSERT INTO Device(Id) VALUES(NULL)");
            Assert.ThrowsAny<Exception>(() => Execute(db, "ALTER TABLE Device AUTO_INCREMENT = 100"));
        });
        long[] ids = Query(db, "SELECT Id FROM Device ORDER BY Id").Rows.Select(row => (long)row[0]!).ToArray();
        Assert.Equal(Enumerable.Range(100, 32).Select(value => (long)value), ids);
    }

    /// <summary>包含列须由索引直接读取，且更新和重开后仍保持最新值。</summary>
    [Fact]
    public void IncludedIndex_UpdateAndReopen_CoversStoredPayload()
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE Device(Id INT,GroupName STRING,Rank INT,Payload STRING,PRIMARY KEY(Id))");
            Execute(db, "CREATE INDEX IxCovered ON Device(GroupName ASC,Rank DESC) INCLUDE(Payload)");
            Execute(db, "INSERT INTO Device(Id,GroupName,Rank,Payload) VALUES(1,'ready',-1,'old'),(2,'ready',0,'middle'),(3,'ready',1,'last')");
            Execute(db, "UPDATE Device SET Payload='first' WHERE Id=1");
        }

        using var reopened = Open();
        int decoded = 0;
        reopened.Tables.Open("Device").RowDecodedTestHook = _ => decoded++;
        var rows = Query(reopened, "SELECT d.Rank,d.Payload FROM Device d WHERE d.GroupName='ready' ORDER BY d.Rank DESC").Rows;
        Assert.Equal(new object?[] { 1L, 0L, -1L }, rows.Select(row => row[0]));
        Assert.Equal(new object?[] { "last", "middle", "first" }, rows.Select(row => row[1]));
        Assert.Equal(0, decoded);
        var metadata = Query(reopened, "SELECT column_name,sort_direction,is_included FROM information_schema.indexes WHERE index_name='IxCovered'").Rows;
        Assert.Equal(3, metadata.Count);
        Assert.Contains(metadata, row => Equals(row[0], "Rank") && Equals(row[1], "DESC") && Equals(row[2], false));
        Assert.Contains(metadata, row => Equals(row[0], "Payload") && Equals(row[2], true));
    }

    /// <summary>包含列不参与唯一性；只更新包含列应原子替换载荷。</summary>
    [Fact]
    public void UniqueIncludedIndex_PayloadChanges_PreservesKeyUniqueness()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE Device(Id INT,Code STRING,Payload STRING,PRIMARY KEY(Id))");
        Execute(db, "CREATE UNIQUE INDEX IxUnique ON Device(Code DESC) INCLUDE(Payload)");
        Execute(db, "INSERT INTO Device(Id,Code,Payload) VALUES(1,'a','old')");
        Execute(db, "UPDATE Device SET Payload='new' WHERE Id=1");
        Assert.Throws<TableConstraintException>(() => Execute(db, "INSERT INTO Device(Id,Code,Payload) VALUES(2,'a','different')"));
        Assert.Equal("new", Query(db, "SELECT Payload FROM Device WHERE Code='a'").Rows.Single()[0]);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private static void Execute(Tsdb db, string sql) => SqlExecutor.Execute(db, sql);
    private static SelectExecutionResult Query(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

    /// <summary>清理本测试专属数据库。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
