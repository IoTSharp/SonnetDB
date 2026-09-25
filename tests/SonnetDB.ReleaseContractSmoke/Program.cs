using SonnetDB.Data;

if (args.Length != 2 || args[0] is not ("legacy" or "current" or "old-server"))
    throw new ArgumentException("Usage: <legacy|current|old-server> <connection-string>");

var scenario = args[0];
var table = "release_contract_" + Guid.NewGuid().ToString("N")[..12];
using var connection = new SndbConnection(args[1]);
connection.Open();
using var command = connection.CreateCommand();
command.CommandText = $"CREATE TABLE {table} (id INT AUTO_INCREMENT, name STRING DEFAULT 'default', rv INT ROWVERSION, PRIMARY KEY (id))";
command.ExecuteNonQuery();

command.CommandText = $"INSERT INTO {table} (name) VALUES ('first'), ('second') RETURNING id, name, rv";
using (var rows = command.ExecuteReader())
{
    if (rows.FieldCount != 3 || rows.GetName(0) != "id"
        || (scenario != "legacy" && rows.GetFieldType(0) != typeof(long)))
        throw new InvalidOperationException("INSERT RETURNING metadata differs from the expected contract.");
    for (long id = 1; id <= 2; id++)
    {
        if (!rows.Read() || rows.GetInt64(0) != id || rows.GetString(1) != (id == 1 ? "first" : "second")
            || rows.GetInt64(2) != 1)
            throw new InvalidOperationException("INSERT RETURNING row order or generated values differ.");
    }
    if (rows.Read() || rows.RecordsAffected != 2)
        throw new InvalidOperationException("INSERT RETURNING affected-row count differs.");
}

if (scenario == "legacy")
{
    Console.WriteLine("PASS legacy 3.1.0 package INSERT RETURNING against current Server");
    return;
}

using var transaction = connection.BeginTransaction();
command.Transaction = transaction;
command.CommandText = $"INSERT INTO {table} (id, name) VALUES (1, 'updated') "
    + "ON CONFLICT (id) DO UPDATE SET name = excluded.name RETURNING id, name, rv";
if (scenario == "old-server")
{
    try
    {
        using var ignored = command.ExecuteReader();
        throw new InvalidOperationException("Old Server unexpectedly accepted transactional DO UPDATE RETURNING.");
    }
    catch (NotSupportedException)
    {
        transaction.Rollback();
        Console.WriteLine("PASS current package rejects transactional UPSERT on old Server");
        return;
    }
}

using (var updated = command.ExecuteReader())
{
    if (!updated.Read() || updated.GetInt64(0) != 1 || updated.GetString(1) != "updated"
        || updated.GetInt64(2) != 2 || updated.Read() || updated.RecordsAffected != 1)
        throw new InvalidOperationException("Transactional UPSERT RETURNING differs from the expected contract.");
}
transaction.Commit();
command.Transaction = null;
command.CommandText = $"SELECT name, rv FROM {table} WHERE id = 1";
using (var persisted = command.ExecuteReader())
{
    if (!persisted.Read() || persisted.GetString(0) != "updated" || persisted.GetInt64(1) != 2)
        throw new InvalidOperationException("Transactional UPSERT was not committed.");
}
Console.WriteLine("PASS current package INSERT RETURNING and transactional UPSERT");
