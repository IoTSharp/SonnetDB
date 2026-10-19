using System.Data;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Core.Tests.Ado;

/// <summary>验证 #227 嵌入式 ADO.NET 事务内关系 DDL 的真实提交、回滚与恢复边界。</summary>
public sealed class SndbDdlTransactionAdoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-ddl-tx-" + Guid.NewGuid().ToString("N"));

    public SndbDdlTransactionAdoTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 测试清理失败不能掩盖断言结果。 */ }
    }

    [Fact]
    public void ExistingRows_AlterAndIndex_CommitPublishesAfterReopen()
    {
        using (var setup = Open())
        {
            Execute(setup, "CREATE TABLE items (id INT, name STRING, PRIMARY KEY (id))");
            Execute(setup, "INSERT INTO items (id, name) VALUES (1, 'one'), (2, 'two')");
        }

        using (var connection = Open())
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction,
                "ALTER TABLE items ADD COLUMN extra STRING NOT NULL DEFAULT 'stable'");
            Execute(connection, transaction, "CREATE INDEX ix_items_extra ON items (extra)");

            Assert.Equal(2, QueryInt(connection, transaction, "SELECT COUNT(*) FROM items WHERE extra = 'stable'"));
            transaction.Commit();
        }

        using (var reopened = Open())
        {
            Assert.Equal(2, QueryInt(reopened, null, "SELECT COUNT(*) FROM items WHERE extra = 'stable'"));
            Assert.Contains(
                "ix_items_extra",
                QueryFirstColumn(reopened, null, "SHOW INDEXES ON items"));
        }
    }

    [Fact]
    public void ExistingRows_AlterAndIndex_RollbackRestoresSchemaAndData()
    {
        using (var setup = Open())
        {
            Execute(setup, "CREATE TABLE items (id INT, name STRING, PRIMARY KEY (id))");
            Execute(setup, "INSERT INTO items (id, name) VALUES (1, 'one'), (2, 'two')");
        }

        using (var connection = Open())
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "ALTER TABLE items ADD COLUMN extra STRING NULL");
            Execute(connection, transaction, "CREATE INDEX ix_items_extra ON items (extra)");
            transaction.Rollback();
        }

        using (var reopened = Open())
        {
            Assert.Equal(2, QueryInt(reopened, null, "SELECT COUNT(*) FROM items"));
            Assert.ThrowsAny<Exception>(() => QueryInt(reopened, null, "SELECT COUNT(*) FROM items WHERE extra IS NULL"));
            Assert.DoesNotContain("ix_items_extra", QueryFirstColumn(reopened, null, "SHOW INDEXES ON items"));
        }
    }

    [Fact]
    public void SecondDdlFailure_DoesNotLeakFirstAlter()
    {
        using (var setup = Open())
            Execute(setup, "CREATE TABLE items (id INT, PRIMARY KEY (id))");

        using (var connection = Open())
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "ALTER TABLE items ADD COLUMN extra STRING NULL");
            using var failing = connection.CreateCommand();
            failing.Transaction = transaction;
            failing.CommandText = "CREATE INDEX ix_items_missing ON items (missing)";
            Assert.ThrowsAny<Exception>(() => failing.ExecuteNonQuery());
            transaction.Rollback();
        }

        using var reopened = Open();
        Assert.ThrowsAny<Exception>(() => QueryInt(reopened, null, "SELECT COUNT(*) FROM items WHERE extra IS NULL"));
    }

    [Fact]
    public void CreateAndDropTable_RollbackKeepsOriginalObjects()
    {
        using (var setup = Open())
            Execute(setup, "CREATE TABLE items (id INT, PRIMARY KEY (id))");

        using (var connection = Open())
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "CREATE TABLE added (id INT, PRIMARY KEY (id))");
            Execute(connection, transaction, "DROP TABLE items");
            transaction.Rollback();
        }

        using var reopened = Open();
        Assert.Equal(0, QueryInt(reopened, null, "SELECT COUNT(*) FROM items"));
        Assert.ThrowsAny<Exception>(() => QueryInt(reopened, null, "SELECT COUNT(*) FROM added"));
    }

    private SndbConnection Open()
    {
        var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        return connection;
    }

    private static void Execute(SndbConnection connection, string sql)
        => Execute(connection, transaction: null, sql);

    private static void Execute(SndbConnection connection, SndbTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private static int QueryInt(SndbConnection connection, SndbTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> QueryFirstColumn(SndbConnection connection, SndbTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetValue(0)?.ToString() ?? string.Empty);
        return values;
    }
}
