using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlSchemaNamespaceTests
{
    [Fact]
    public void Execute_TwoSchemasSameTableName_RouteCrudAndReopen()
    {
        string root = Path.Combine(Path.GetTempPath(), "sndb-sql-schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (Tsdb db = Tsdb.Open(new TsdbOptions { RootDirectory = root }))
            {
                SqlExecutor.Execute(db, "CREATE TABLE items (id INT, value STRING, PRIMARY KEY(id))");
                SqlExecutor.Execute(db, "CREATE TABLE sales.items (id INT, value STRING, PRIMARY KEY(id))");
                SqlExecutor.Execute(db, "INSERT INTO items(id, value) VALUES (1, 'main')");
                SqlExecutor.Execute(db, "INSERT INTO sales.items(id, value) VALUES (1, 'sales')");

                Assert.Equal("main", Assert.Single(Assert.IsType<SelectExecutionResult>(
                    SqlExecutor.Execute(db, "SELECT value FROM items")).Rows)[0]);
                Assert.Equal("sales", Assert.Single(Assert.IsType<SelectExecutionResult>(
                    SqlExecutor.Execute(db, "SELECT value FROM sales.items")).Rows)[0]);
                SqlExecutor.Execute(db, "UPDATE sales.items SET value = 'updated' WHERE id = 1");
                SqlExecutor.Execute(db, "DELETE FROM items WHERE id = 1");
            }

            using Tsdb reopened = Tsdb.Open(new TsdbOptions { RootDirectory = root });
            Assert.Empty(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened, "SELECT * FROM items")).Rows);
            Assert.Equal("updated", Assert.Single(Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(reopened, "SELECT value FROM sales.items")).Rows)[0]);
            Assert.Equal("sales", reopened.Tables.Catalog.Resolve("sales", false, "items", false)!.SchemaName);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Parse_CreateCrudAndDrop_PreserveQualifiedIdentity()
    {
        var create = Assert.IsType<CreateTableStatement>(SqlParser.Parse(
            "CREATE TABLE sales.orders (id INT, PRIMARY KEY(id))"));
        var insert = Assert.IsType<InsertStatement>(SqlParser.Parse(
            "INSERT INTO sales.orders(id) VALUES(1)"));
        var select = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT * FROM sales.orders"));
        var update = Assert.IsType<UpdateStatement>(SqlParser.Parse(
            "UPDATE sales.orders SET id=2 WHERE id=1"));
        var delete = Assert.IsType<DeleteStatement>(SqlParser.Parse(
            "DELETE FROM sales.orders WHERE id=2"));
        var truncate = Assert.IsType<TruncateTableStatement>(SqlParser.Parse(
            "TRUNCATE TABLE sales.orders"));
        var drop = Assert.IsType<DropTableStatement>(SqlParser.Parse(
            "DROP TABLE sales.orders"));

        AssertName(create.QualifiedName);
        AssertName(insert.TargetQualifiedName);
        AssertName(select.SourceQualifiedName);
        AssertName(update.QualifiedName);
        AssertName(delete.TargetQualifiedName);
        AssertName(truncate.QualifiedName);
        AssertName(drop.QualifiedName);
    }

    [Fact]
    public void Parse_IndexForeignKeyTriggerAndCtas_PreserveQualifiedIdentity()
    {
        var index = Assert.IsType<CreateTableIndexStatement>(SqlParser.Parse(
            "CREATE INDEX ix_orders_id ON sales.orders(id)"));
        var dropIndex = Assert.IsType<DropTableIndexStatement>(SqlParser.Parse(
            "DROP INDEX ix_orders_id ON sales.orders"));
        var child = Assert.IsType<CreateTableStatement>(SqlParser.Parse(
            "CREATE TABLE audit.lines (id INT,parent_id INT,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES sales.orders(id))"));
        var trigger = Assert.IsType<CreateTriggerStatement>(SqlParser.Parse(
            "CREATE TRIGGER audit_order AFTER INSERT ON sales.orders FOR EACH ROW LANGUAGE SQL AS BEGIN SELECT 1; END"));
        var ctas = Assert.IsType<CreateTableAsStatement>(SqlParser.Parse(
            "CREATE TABLE archive.orders AS SELECT * FROM sales.orders"));

        AssertName(index.TableQualifiedName);
        AssertName(dropIndex.TableQualifiedName);
        AssertName(Assert.Single(child.ForeignKeyClauses).PrincipalQualifiedName);
        AssertName(trigger.TableQualifiedName);
        Assert.Equal("archive", ctas.QualifiedName!.Value.SchemaName);
        AssertName(ctas.Query.SourceQualifiedName);
    }

    [Fact]
    public void Parse_QuotedDotsAndEscapedQuotes_RemainTwoIndependentParts()
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT * FROM \"Sales.Region\".\"Order\"\"Items\""));

        SqlQualifiedObjectName name = statement.SourceQualifiedName!.Value;
        Assert.Equal("Sales.Region", name.SchemaName);
        Assert.Equal("Order\"Items", name.Name);
        Assert.True(name.SchemaIsQuoted);
        Assert.True(name.NameIsQuoted);
        Assert.Equal("\"Sales.Region\".\"Order\"\"Items\"", name.ToString());
    }

    [Theory]
    [InlineData("SELECT * FROM db.sales.orders")]
    [InlineData("CREATE TABLE db.sales.orders(id INT)")]
    [InlineData("INSERT INTO db.sales.orders(id) VALUES(1)")]
    public void Parse_ThreePartRelationName_Rejects(string sql)
    {
        SqlParseException error = Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));

        Assert.Contains("最多只支持", error.Message, StringComparison.Ordinal);
    }

    private static void AssertName(SqlQualifiedObjectName? actual)
    {
        Assert.NotNull(actual);
        Assert.True(actual.Value.HasExplicitSchema);
        Assert.Equal("sales", actual.Value.SchemaName);
        Assert.Equal("orders", actual.Value.Name);
    }
}
