using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Reflection;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Consumer initial|reopen|core connection-string-or-core-root transport");
    return 2;
}
string phase = args[0];
string transport = args[2];
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
CancellationToken token = deadline.Token;
try
{
    foreach (string name in new[] { "SonnetDB.Data", "SonnetDB.Core" })
    {
        Assembly assembly = Assembly.Load(name);
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Check(version.Contains("1af23d8f299febfdf66b040ab3419980396acf46", StringComparison.Ordinal), "package-source-" + name);
        Console.WriteLine($"ASSEMBLY|{name}|{version}|{assembly.Location}");
    }
    if (phase == "core")
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = args[1] });
        SqlExecutor.Execute(db, "CREATE TABLE alpha(id INT AUTO_INCREMENT, amount DECIMAL(5,2), PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "CREATE TABLE beta(id INT AUTO_INCREMENT, amount DECIMAL(5,2), PRIMARY KEY(id))");
        try
        {
            db.Tables.ApplyTransaction(new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
            {
                ["alpha"] = [new TableRowMutation(null, [null, 1.25m])],
                ["beta"] = [new TableRowMutation(null, [null, 1.234m])]
            });
            throw new InvalidOperationException("Expected core transaction precision rejection.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("DECIMAL", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("EXPECTED-REJECTION|core-cross-table|" + error.Message);
        }
        foreach (string table in new[] { "alpha", "beta" })
        {
            var before = SqlExecutor.Execute(db, "SELECT id FROM " + table) as SelectExecutionResult
                ?? throw new InvalidOperationException("Expected SELECT result.");
            Check(before.Rows.Count == 0, "core-failed-transaction-empty-" + table);
            db.Tables.Open(table).Insert([null, 1.25m]);
            var after = SqlExecutor.Execute(db, "SELECT id,amount FROM " + table) as SelectExecutionResult
                ?? throw new InvalidOperationException("Expected SELECT result.");
            var row = after.Rows.Single();
            Check(Equals(row[0], 1L) && Equals(row[1], 1.25m), "core-identity-unconsumed-" + table);
        }
        byte[] schema = File.ReadAllBytes(db.Tables.SchemaPath);
        int format = BinaryPrimitives.ReadInt32LittleEndian(schema.AsSpan(8, 4));
        Check(format == 11, "public-catalog-v11");
        Console.WriteLine("CATALOG|" + db.Tables.SchemaPath + "|version=" + format);
    }
    else
    {
        await using var connection = new SndbConnection(args[1]);
        await connection.OpenAsync(token);
        if (phase == "initial") await Initial(connection);
        else if (phase == "reopen") await VerifyPersisted(connection);
        else throw new ArgumentException("Unknown phase: " + phase);
    }
    Console.WriteLine($"COMPLETE|native|{transport}|{phase}|pid={Environment.ProcessId}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("Assertion failed: " + label);
    Console.WriteLine($"PASS|native|{transport}|{phase}|{label}");
}

async Task Initial(SndbConnection connection)
{
    await Execute(connection, "CREATE TABLE amounts (id INT, amount DECIMAL(5,2), PRIMARY KEY(id))");
    await Reject(connection, "INSERT INTO amounts(id,amount) VALUES(1,'1.234'),(2,'123456.789')", "original-issue-sql");
    await Rows(connection, "amounts", []);
    Metadata(connection, "amounts", "amount", 5, 2);

    decimal?[] valid = [0m, 0.01m, -0.01m, 999.99m, -999.99m, 1.2300m, null];
    for (int i = 0; i < valid.Length; i++)
        await Execute(connection, $"INSERT INTO amounts(id,amount) VALUES({i + 1},@value)", value: valid[i]);
    await Execute(connection, "INSERT INTO amounts(id,amount) VALUES(8,@value)", value: "+1.2300");
    await Execute(connection, "INSERT INTO amounts(id,amount) VALUES(9,@value)", value: "-999.9900");
    var expected = valid.Select((value, index) => ((long)index + 1, value)).Concat([(8L, (decimal?)1.23m), (9L, (decimal?)-999.99m)]).ToArray();
    await Rows(connection, "amounts", expected);

    foreach (decimal value in new[] { 1.234m, -1.234m, 1000m, -1000m, 123456.789m, -123456.789m, 1.0000000000000000000000000001m })
    {
        await Reject(connection, "INSERT INTO amounts(id,amount) VALUES(100,@value)", "raw-decimal-" + value, value: value);
        await Reject(connection, "INSERT INTO amounts(id,amount) VALUES(101,@value)", "string-" + value, value: value.ToString(CultureInfo.InvariantCulture));
    }
    await Reject(connection, "INSERT INTO amounts(id,amount) VALUES(102,'2.50'),(103,'1.234')", "batch-later-scale");
    await Reject(connection, "INSERT INTO amounts(id,amount) VALUES(102,'2.50'),(103,'1000')", "batch-later-precision");
    await Rows(connection, "amounts", expected);

    await Execute(connection, "CREATE TABLE bare(id INT, amount DECIMAL, PRIMARY KEY(id))");
    decimal?[] wide = [decimal.MaxValue, decimal.MinValue, 0.0000000000000000000000000001m, -0.0000000000000000000000000001m, 123456789.1234567890123456789m, 0m, null];
    for (int i = 0; i < wide.Length; i++)
        await Execute(connection, $"INSERT INTO bare(id,amount) VALUES({i + 1},@value)", value: wide[i]);
    await Rows(connection, "bare", wide.Select((value, index) => ((long)index + 1, value)).ToArray());
    await Execute(connection, "CREATE TABLE explicit_default(id INT, amount DECIMAL(38,28), PRIMARY KEY(id))");
    await Reject(connection, "INSERT INTO explicit_default(id,amount) VALUES(1,@value)", "explicit-38-28-rejects-max", value: decimal.MaxValue);
    await Execute(connection, "INSERT INTO explicit_default(id,amount) VALUES(1,@value)", value: 0.1234567890123456789012345678m);
    Metadata(connection, "explicit_default", "amount", 38, 28);

    await Execute(connection, "CREATE TABLE whole(id INT, amount NUMERIC(3,0), PRIMARY KEY(id))");
    await Execute(connection, "INSERT INTO whole(id,amount) VALUES(1,'999.000'),(2,'-999'),(3,NULL)");
    await Reject(connection, "INSERT INTO whole(id,amount) VALUES(4,'0.1')", "zero-scale-fraction");
    await Reject(connection, "INSERT INTO whole(id,amount) VALUES(4,'1000')", "zero-scale-precision");
    Metadata(connection, "whole", "amount", 3, 0);

    await Execute(connection, "CREATE TABLE atomic_rows(id INT, amount DECIMAL(5,2), PRIMARY KEY(id))");
    await Execute(connection, "INSERT INTO atomic_rows(id,amount) VALUES(1,'1.25'),(2,'2.50')");
    await Reject(connection, "UPDATE atomic_rows SET amount=CASE WHEN id=1 THEN '9.25' ELSE '1.234' END WHERE id>0", "update-later-invalid");
    await Rows(connection, "atomic_rows", [(1, 1.25m), (2, 2.50m)]);
    await using (var transaction = (SndbTransaction)await connection.BeginTransactionAsync(token))
    {
        await Execute(connection, "UPDATE atomic_rows SET amount='1.50' WHERE id=1", transaction);
        await Reject(connection, "UPDATE atomic_rows SET amount=CASE WHEN id=1 THEN '9.25' ELSE '1.234' END WHERE id>0", "transaction-update-later-invalid", transaction);
        await Reject(connection, "INSERT INTO atomic_rows(id,amount) VALUES(3,'3.50'),(4,'1000')", "transaction-insert-later-invalid", transaction);
        await Rows(connection, "atomic_rows", [(1, 1.50m), (2, 2.50m)], transaction);
        await transaction.RollbackAsync(token);
    }
    await Rows(connection, "atomic_rows", [(1, 1.25m), (2, 2.50m)]);
    await using (var transaction = (SndbTransaction)await connection.BeginTransactionAsync(token))
    {
        await Execute(connection, "UPDATE atomic_rows SET amount='1.75' WHERE id=1", transaction);
        await Execute(connection, "INSERT INTO atomic_rows(id,amount) VALUES(3,'3.75')", transaction);
        await transaction.CommitAsync(token);
    }
    await VerifyPersisted(connection);
}

async Task VerifyPersisted(SndbConnection connection)
{
    Metadata(connection, "amounts", "amount", 5, 2);
    Metadata(connection, "whole", "amount", 3, 0);
    Metadata(connection, "explicit_default", "amount", 38, 28);
    await Rows(connection, "amounts", [(1, 0m), (2, 0.01m), (3, -0.01m), (4, 999.99m), (5, -999.99m), (6, 1.23m), (7, null), (8, 1.23m), (9, -999.99m)]);
    await Rows(connection, "atomic_rows", [(1, 1.75m), (2, 2.50m), (3, 3.75m)]);
    await Rows(connection, "bare", [(1, decimal.MaxValue), (2, decimal.MinValue), (3, 0.0000000000000000000000000001m), (4, -0.0000000000000000000000000001m), (5, 123456789.1234567890123456789m), (6, 0m), (7, null)]);
    await Rows(connection, "whole", [(1, 999m), (2, -999m), (3, null)]);
    await Rows(connection, "explicit_default", [(1, 0.1234567890123456789012345678m)]);
    await Reject(connection, "INSERT INTO amounts(id,amount) VALUES(500,@value)", "persisted-rejection", value: 1.234m);
    await Rows(connection, "atomic_rows", [(1, 1.75m), (2, 2.50m), (3, 3.75m)]);
}

void Metadata(SndbConnection connection, string table, string column, short precision, short scale)
{
    var rows = connection.GetSchema("Columns", [null, null, table, column]).Rows;
    Check(rows.Count == 1 && Equals(rows[0]["NUMERIC_PRECISION"], precision) && Equals(rows[0]["NUMERIC_SCALE"], scale), $"schema-{table}-{precision}-{scale}");
}

async Task<int> Execute(SndbConnection connection, string sql, SndbTransaction? transaction = null, object? value = null)
{
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    command.CommandTimeout = 15;
    if (sql.Contains("@value", StringComparison.Ordinal))
        command.Parameters.Add(new SndbParameter("value", value ?? DBNull.Value) { DbType = value is string ? DbType.String : DbType.Decimal });
    return await command.ExecuteNonQueryAsync(token);
}

async Task Reject(SndbConnection connection, string sql, string label, SndbTransaction? transaction = null, object? value = null)
{
    Exception? failure = null;
    try { await Execute(connection, sql, transaction, value); }
    catch (Exception error) when (error is not OperationCanceledException) { failure = error; }
    Check(failure is not null && failure.ToString().Contains("DECIMAL", StringComparison.OrdinalIgnoreCase), "reject-" + label);
    Console.WriteLine("EXPECTED-REJECTION|" + label + "|" + failure!.Message);
}

async Task Rows(SndbConnection connection, string table, (long Id, decimal? Amount)[] expected, SndbTransaction? transaction = null)
{
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "SELECT id,amount FROM " + table + " ORDER BY id";
    command.CommandTimeout = 15;
    await using var reader = await command.ExecuteReaderAsync(token);
    Check(reader.GetFieldType(1) == typeof(decimal), "reader-decimal-" + table);
    var actual = new List<(long, decimal?)>();
    while (await reader.ReadAsync(token))
    {
        object value = reader.GetValue(1);
        Check(value is decimal || value == DBNull.Value, "native-value-type-" + table);
        actual.Add((reader.GetInt64(0), value is decimal number ? number : null));
    }
    Check(actual.SequenceEqual(expected), "rows-" + table + "=" + string.Join(';', actual));
}
