using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Core.Tests.Ado;

public sealed class SndbDmlReturningAdoTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-ado-dml-" + Guid.NewGuid().ToString("N"));

    public SndbDmlReturningAdoTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ExecuteReader_UpdateAndDeleteReturning_UsesResultSetsAndAffectedRows()
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();

        ExecuteNonQuery(connection, "CREATE TABLE items (id INT, value INT, PRIMARY KEY (id))");
        ExecuteNonQuery(connection, "INSERT INTO items (id, value) VALUES (1, 10), (2, 20)");

        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE items SET value = value + 1 WHERE id >= 1 RETURNING id, value";
            using var reader = update.ExecuteReader();
            Assert.Equal(2, reader.RecordsAffected);
            Assert.Equal("id", reader.GetName(0));
            Assert.Equal("value", reader.GetName(1));
            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(11L, reader.GetInt64(1));
            Assert.True(reader.Read());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(21L, reader.GetInt64(1));
            Assert.False(reader.Read());
        }

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM items WHERE id = 1 RETURNING id, value";
            using var reader = delete.ExecuteReader();
            Assert.Equal(1, reader.RecordsAffected);
            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(11L, reader.GetInt64(1));
            Assert.False(reader.Read());
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void ExecuteNonQuery(SndbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }
}
