using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证第二批功能在同一目录、关系约束与失败恢复中的组合行为。</summary>
public sealed class SqlBatchTwoIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-batch02-" + Guid.NewGuid().ToString("N"));

    /// <summary>主键与关联外键同步重命名时保留双方长度和注释，随后重开及有界删除仍受约束。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenamePrimaryKey_WithBoundedStringsAndComments_PreservesMetadataAndRecovery(bool injectFailure)
    {
        using (var database = Open())
        {
            Execute(database, "CREATE TABLE Parents(Id STRING(4),Label STRING(8) DEFAULT 'parent',PRIMARY KEY(Id),CHECK(LENGTH(Id)>0))");
            Execute(database, "CREATE TABLE Children(Id INT,ParentId STRING(4),Label STRING(8),PRIMARY KEY(Id),FOREIGN KEY(ParentId) REFERENCES Parents(Id))");
            Execute(database, "CREATE UNIQUE INDEX ix_parent_label ON Parents(Label)");
            Execute(database, "COMMENT ON TABLE Parents IS '父表'");
            Execute(database, "COMMENT ON COLUMN Parents.Id IS '父键'");
            Execute(database, "COMMENT ON COLUMN Parents.Label IS '父名称'");
            Execute(database, "COMMENT ON TABLE Children IS '子表'");
            Execute(database, "COMMENT ON COLUMN Children.ParentId IS '引用键'");
            Execute(database, "COMMENT ON COLUMN Children.Label IS '子名称'");
            Execute(database, "INSERT INTO Parents(Id,Label) VALUES('a','first'),('b','second')");
            Execute(database, "INSERT INTO Children(Id,ParentId,Label) VALUES(1,'a','child')");
            var heldParent = database.Tables.Open("Parents");
            var heldChild = database.Tables.Open("Children");
            if (injectFailure)
            {
                byte[] previous = File.ReadAllBytes(database.Tables.SchemaPath);
                database.Tables.AfterCatalogPersistedBeforePublishTestHook = () => throw new IOException("publication failure");
                Assert.Throws<IOException>(() => Execute(database, "ALTER TABLE Parents RENAME COLUMN Id TO Identifier"));
                database.Tables.AfterCatalogPersistedBeforePublishTestHook = null;
                Assert.Equal(previous, File.ReadAllBytes(database.Tables.SchemaPath));
                AssertMetadata(database, "Id");
                Assert.Equal("父键", heldParent.Schema.TryGetColumn("Id")!.Comment);
                Assert.Equal("引用键", heldChild.Schema.TryGetColumn("ParentId")!.Comment);
            }
            Execute(database, "ALTER TABLE Parents RENAME COLUMN Id TO Identifier");
            AssertMetadata(database, "Identifier");
            Assert.Equal("父键", heldParent.Schema.TryGetColumn("Identifier")!.Comment);
            Assert.Equal(["Identifier"], Assert.Single(heldChild.Schema.ForeignKeys).PrincipalColumns);
        }

        using var reopened = Open();
        AssertMetadata(reopened, "Identifier");
        Assert.Equal(TableConstraintException.StringLengthViolation,
            Assert.Throws<TableConstraintException>(() => Execute(reopened,
                "INSERT INTO Parents(Identifier,Label) VALUES('abcde','too-long')")).ErrorCode);
        Assert.Equal(TableConstraintException.StringLengthViolation,
            Assert.Throws<TableConstraintException>(() => Execute(reopened,
                "INSERT INTO Children(Id,ParentId,Label) VALUES(2,'abcde','child')")).ErrorCode);
        Assert.Equal(TableConstraintException.ForeignKeyViolation,
            Assert.Throws<TableConstraintException>(() => Execute(reopened,
                "INSERT INTO Children(Id,ParentId,Label) VALUES(2,'bad','child')")).ErrorCode);
        Assert.Equal(TableConstraintException.ForeignKeyViolation,
            Assert.Throws<TableConstraintException>(() => Execute(reopened,
                "DELETE FROM Parents WHERE Identifier='a' LIMIT 1")).ErrorCode);
        var removed = Assert.IsType<DeleteExecutionResult>(Execute(reopened,
            "DELETE FROM Parents WHERE Identifier='b' LIMIT 1 RETURNING Identifier"));
        Assert.Equal("b", Assert.Single(removed.Returning!.Rows)[0]);
        Assert.Equal(1, removed.SeriesAffected);
        AssertMetadata(reopened, "Identifier");
    }

    /// <summary>序列默认值经过主键重命名后逐行执行，事务回滚耗号且有界删除保留注释和长度。</summary>
    [Fact]
    public void SequenceDefault_AfterPrimaryKeyRenameAndRollback_PreservesAllocationAndMetadata()
    {
        using (var database = Open())
        {
            Execute(database, "CREATE SEQUENCE ItemIds START WITH 41");
            Execute(database, "CREATE TABLE Items(Id INT DEFAULT nextval('ItemIds'),Label STRING(4),PRIMARY KEY(Id))");
            Execute(database, "COMMENT ON COLUMN Items.Id IS '序列键'");
            Execute(database, "COMMENT ON COLUMN Items.Label IS '名称'");
            Execute(database, "INSERT INTO Items(Label) VALUES('甲'),('乙')");
            Execute(database, "ALTER TABLE Items RENAME COLUMN Id TO Identifier");
            SqlExecutor.ExecuteScript(database, "BEGIN; INSERT INTO Items(Label) VALUES('丙'); ROLLBACK;");
            Execute(database, "INSERT INTO Items(Label) VALUES('丁')");
            Assert.Equal(new object?[] { 41L, 42L, 44L }, Assert.IsType<SelectExecutionResult>(
                Execute(database, "SELECT Identifier FROM Items ORDER BY Identifier")).Rows.Select(row => row[0]).ToArray());
            var deleted = Assert.IsType<DeleteExecutionResult>(Execute(database,
                "DELETE FROM Items WHERE Identifier<44 LIMIT 1 RETURNING Identifier"));
            Assert.Equal(1, deleted.SeriesAffected);
            Assert.Equal("序列键", database.Tables.Catalog.TryGet("Items")!.TryGetColumn("Identifier")!.Comment);
        }

        using var reopened = Open();
        Execute(reopened, "INSERT INTO Items(Label) VALUES('戊')");
        Assert.Equal(45L, Assert.Single(Assert.IsType<SelectExecutionResult>(
            Execute(reopened, "SELECT Identifier FROM Items WHERE Label='戊'")).Rows)[0]);
        Assert.Equal("序列键", reopened.Tables.Catalog.TryGet("Items")!.TryGetColumn("Identifier")!.Comment);
        Assert.Equal("名称", reopened.Tables.Catalog.TryGet("Items")!.TryGetColumn("Label")!.Comment);
        Assert.Equal(4, reopened.Tables.Catalog.TryGet("Items")!.TryGetColumn("Label")!.StringLength);
    }

    private static void AssertMetadata(Tsdb database, string primaryKey)
    {
        var parent = database.Tables.Catalog.TryGet("Parents")!;
        var child = database.Tables.Catalog.TryGet("Children")!;
        Assert.Equal("父表", parent.Comment);
        Assert.Equal("子表", child.Comment);
        Assert.Equal(4, parent.TryGetColumn(primaryKey)!.StringLength);
        Assert.Equal("父键", parent.TryGetColumn(primaryKey)!.Comment);
        Assert.Equal(8, parent.TryGetColumn("Label")!.StringLength);
        Assert.Equal("父名称", parent.TryGetColumn("Label")!.Comment);
        Assert.Equal(4, child.TryGetColumn("ParentId")!.StringLength);
        Assert.Equal("引用键", child.TryGetColumn("ParentId")!.Comment);
        Assert.Equal(8, child.TryGetColumn("Label")!.StringLength);
        Assert.Equal("子名称", child.TryGetColumn("Label")!.Comment);
        Assert.Equal([primaryKey], parent.PrimaryKey);
        Assert.Equal([primaryKey], Assert.Single(child.ForeignKeys).PrincipalColumns);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static object? Execute(Tsdb database, string sql) => SqlExecutor.Execute(database, sql);

    /// <summary>清理本夹具唯一的临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
