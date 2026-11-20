using System.Data;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Core.Tests.Ado;

public sealed class SndbSqlSavepointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-ado-savepoint-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SndbCommand_SavepointCommandsUseBoundTransaction()
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        Execute(connection, "CREATE TABLE items (id INT, value STRING, PRIMARY KEY (id))");

        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        Execute(connection, "INSERT INTO items (id, value) VALUES (1, 'before')", transaction);
        Execute(connection, "SAVEPOINT item_one", transaction);
        Execute(connection, "UPDATE items SET value = 'temporary' WHERE id = 1", transaction);
        Execute(connection, "ROLLBACK TO SAVEPOINT item_one", transaction);
        Execute(connection, "RELEASE SAVEPOINT item_one", transaction);
        transaction.Commit();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM items WHERE id = 1";
        Assert.Equal("before", Assert.Single(ReadRows(command)));
    }

    private static void Execute(SndbConnection connection, string sql, SndbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<object?> ReadRows(SndbCommand command)
    {
        using var reader = command.ExecuteReader();
        var values = new List<object?>();
        while (reader.Read())
            values.Add(reader.GetValue(0));
        return values;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
