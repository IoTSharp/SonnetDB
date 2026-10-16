using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed partial class SqlMaintenanceContractTests
{
    /// <summary>维护消费者的 COUNT(*) 查询在过滤后计数，空目录仍返回一行整数零。</summary>
    [Fact]
    public void ConstraintDirectory_CountStar_FiltersAndPreservesIntegerMetadata()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE Device(Id INT,CONSTRAINT PkDevice PRIMARY KEY(Id))");
        foreach (var (name, expected) in new[] { ("PkDevice", 1L), ("missing", 0L) })
        {
            var result = Query(db, $"SELECT COUNT(*) AS total FROM information_schema.table_constraints WHERE constraint_name='{name}'");
            Assert.Equal(expected, result.Rows.Single()[0]);
            Assert.Equal("total", Assert.Single(result.Columns));
            Assert.Equal(TableColumnType.Int64, result.ColumnInfo![0].DataType);
        }
        var empty = Query(db, "SELECT COUNT(*) FROM information_schema.table_constraints LIMIT 0");
        Assert.Empty(empty.Rows);
        Assert.Equal(TableColumnType.Int64, empty.ColumnInfo![0].DataType);
        Assert.ThrowsAny<Exception>(() => Query(db, "SELECT COUNT(*),table_name FROM information_schema.table_constraints"));
    }

    /// <summary>无主键表的选项首次启用须持久提升到 v16；全部删除也不能降低屏障。</summary>
    [Theory]
    [InlineData("DESC", "")]
    [InlineData("ASC", "INCLUDE(Payload)")]
    public void IndexOptions_KeylessDirectory_PreservesVersionFloor(string direction, string include)
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE Device(Rank INT,Payload STRING)");
            Assert.Equal(13, TableSchemaCodec.ReadFormatVersion(db.Tables.SchemaPath));
            Execute(db, $"CREATE INDEX Ix ON Device(Rank {direction}) {include}");
            Assert.Equal(16, TableSchemaCodec.ReadFormatVersion(db.Tables.SchemaPath));
            Execute(db, "INSERT INTO Device(Rank,Payload) VALUES(1,'kept')");
        }
        using var reopened = Open();
        Assert.Equal("kept", Query(reopened, "SELECT Payload FROM Device WHERE Rank=1").Rows.Single()[0]);
        Execute(reopened, "DROP INDEX Ix ON Device");
        Execute(reopened, "DROP TABLE Device");
        Assert.Equal(16, TableSchemaCodec.ReadFormatVersion(reopened.Tables.SchemaPath));
    }

    /// <summary>v16 屏障落盘失败时离线和在线索引都不得先写新编码。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("ONLINE")]
    public void IndexOptions_BarrierFailure_LeavesWalAndCatalogUnchanged(string online)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE Device(Rank INT,Payload STRING)");
        Execute(db, "INSERT INTO Device(Rank,Payload) VALUES(1,'kept')");
        TableStore store = db.Tables.Open("Device");
        long before = store.ActiveWalLengthForEvidence;
        string temporary = db.Tables.SchemaPath + ".tmp";
        Directory.CreateDirectory(temporary);
        try
        {
            Exception failure = Assert.ThrowsAny<Exception>(() => Execute(db, $"CREATE INDEX Ix ON Device(Rank DESC) INCLUDE(Payload) {online}"));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal(before, store.ActiveWalLengthForEvidence);
            Assert.Empty(store.Schema.Indexes);
            Assert.Equal(13, TableSchemaCodec.ReadFormatVersion(db.Tables.SchemaPath));
        }
        finally { Directory.Delete(temporary); }
        Execute(db, "CREATE INDEX Ix ON Device(Rank DESC) INCLUDE(Payload)");
        Assert.Equal("kept", Query(db, "SELECT Payload FROM Device WHERE Rank=1").Rows.Single()[0]);
    }

    /// <summary>重复设置当前下一值保持幂等；无自增列和显式事务内维护均明确拒绝。</summary>
    [Fact]
    public void AutoIncrement_IdempotentNonIdentityAndTransaction_HasExplicitBoundary()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE Device(Id INT AUTO_INCREMENT,PRIMARY KEY(Id))");
        Execute(db, "CREATE TABLE Plain(Id INT,PRIMARY KEY(Id))");
        Execute(db, "ALTER TABLE Device AUTO_INCREMENT = 50");
        Execute(db, "ALTER TABLE Device AUTO_INCREMENT = 50");
        Assert.Throws<InvalidOperationException>(() => Execute(db, "ALTER TABLE Plain AUTO_INCREMENT = 100"));
        Assert.ThrowsAny<Exception>(() => SonnetDB.Sql.Execution.SqlExecutor.ExecuteScript(db,
            "BEGIN; ALTER TABLE Device AUTO_INCREMENT = 100; COMMIT;"));
        Execute(db, "INSERT INTO Device(Id) VALUES(NULL)");
        Assert.Equal(50L, Query(db, "SELECT Id FROM Device").Rows.Single()[0]);
        Assert.Empty(Query(db, "SELECT Id FROM Plain").Rows);
    }
}
