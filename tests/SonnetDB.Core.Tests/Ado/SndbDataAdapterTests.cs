using System.Data;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Core.Tests.Ado;

/// <summary>SonnetDB ADO.NET DataAdapter 的填充、架构和显式 DML 命令合同。</summary>
public sealed class SndbDataAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-data-adapter-" + Guid.NewGuid().ToString("N"));

    public SndbDataAdapterTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ProviderFactory_CreatesDataAdapter()
    {
        Assert.True(SndbProviderFactory.Instance.CanCreateDataAdapter);
        using var adapter = SndbProviderFactory.Instance.CreateDataAdapter();
        Assert.IsType<SndbDataAdapter>(adapter);
    }

    [Fact]
    public void Fill_DataSet_LoadsMultipleResultSets()
    {
        using var connection = OpenConnection();
        Execute(connection, "CREATE TABLE items (id INT, name STRING, PRIMARY KEY (id))");
        Execute(connection, "INSERT INTO items (id, name) VALUES (1, 'first'), (2, 'second')");

        using var adapter = new SndbDataAdapter(
            "SELECT id, name FROM items ORDER BY id; SELECT name FROM items ORDER BY id DESC",
            connection);
        using var data = new DataSet();

        // DbDataAdapter 的返回值按其 BCL 合同报告首个源表的新增行数，
        // 后续结果集仍会按默认表名映射到 DataSet。
        Assert.Equal(2, adapter.Fill(data, "items"));
        Assert.Equal(2, data.Tables.Count);
        Assert.Equal("items", data.Tables[0].TableName);
        Assert.Equal("items1", data.Tables[1].TableName);
        Assert.Equal(1L, data.Tables[0].Rows[0]["id"]);
        Assert.Equal("second", data.Tables[1].Rows[0]["name"]);
    }

    [Fact]
    public void FillSchema_DataTable_UsesReaderSchema()
    {
        using var connection = OpenConnection();
        Execute(connection, "CREATE TABLE items (id INT, name STRING, PRIMARY KEY (id))");

        using var adapter = new SndbDataAdapter("SELECT id, name FROM items", connection);
        using var table = new DataTable("items");

        var schema = adapter.FillSchema(table, SchemaType.Source);

        Assert.Same(table, schema);
        Assert.Equal(typeof(long), table.Columns["id"]!.DataType);
        Assert.Equal(typeof(string), table.Columns["name"]!.DataType);
        Assert.Equal(-1, table.Columns["name"]!.MaxLength);
    }

    [Fact]
    public void Update_UsesExplicitCommandsAndSourceColumnMappings()
    {
        using var connection = OpenConnection();
        Execute(connection, "CREATE TABLE items (id INT, name STRING, PRIMARY KEY (id))");
        Execute(connection, "INSERT INTO items (id, name) VALUES (1, 'first'), (3, 'third')");

        using var adapter = new SndbDataAdapter("SELECT id, name FROM items", connection);
        using var table = new DataTable("items");
        Assert.Equal(2, adapter.Fill(table));

        var update = connection.CreateCommand();
        update.CommandText = "UPDATE items SET name = @name WHERE id = @id";
        var name = new SndbParameter("name", null) { DbType = DbType.String, SourceColumn = "name" };
        update.Parameters.Add(name);
        var id = new SndbParameter("id", null)
        {
            DbType = DbType.Int64,
            SourceColumn = "id",
            SourceVersion = DataRowVersion.Original,
        };
        update.Parameters.Add(id);
        adapter.UpdateCommand = update;

        var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO items (id, name) VALUES (@id, @name)";
        insert.Parameters.Add(new SndbParameter("id", null) { SourceColumn = "id" });
        insert.Parameters.Add(new SndbParameter("name", null) { SourceColumn = "name" });
        adapter.InsertCommand = insert;

        var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM items WHERE id = @id";
        delete.Parameters.Add(new SndbParameter("id", null)
        {
            SourceColumn = "id",
            SourceVersion = DataRowVersion.Original,
        });
        adapter.DeleteCommand = delete;

        table.Rows[0]["name"] = "updated";
        table.Rows.Add(2L, "second");
        table.Rows.Cast<DataRow>().Single(row => (long)row["id"] == 3L).Delete();
        Assert.Equal(3, adapter.Update(table));
        Assert.Equal("updated", ReadScalar(connection, "SELECT name FROM items WHERE id = 1"));
        Assert.Equal("second", ReadScalar(connection, "SELECT name FROM items WHERE id = 2"));
        Assert.Null(ReadScalar(connection, "SELECT name FROM items WHERE id = 3"));

        update.Dispose();
        insert.Dispose();
        delete.Dispose();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Test cleanup should not hide the assertion result.
        }
    }

    private SndbConnection OpenConnection()
    {
        var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        return connection;
    }

    private static void Execute(SndbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private static object? ReadScalar(SndbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
