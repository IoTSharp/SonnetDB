using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using static UuidChecks;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
if (args.Length != 3) return 2;
string phase = args[0];
string transport = args[2];
string state = Path.Combine(Environment.CurrentDirectory, "native-state-" + transport + ".txt");
try
{
    foreach (string name in new[] { "SonnetDB.Data", "SonnetDB.Core" })
    {
        var assembly = Assembly.Load(name);
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Require(version.Contains("1af23d8f299febfdf66b040ab3419980396acf46", StringComparison.Ordinal), "public-assembly/" + name);
        Console.WriteLine($"ASSEMBLY|{name}|{version}|{assembly.Location}");
    }
    if (phase.StartsWith("core-", StringComparison.Ordinal)) VerifyCore();
    else VerifyAdo();
    Console.WriteLine($"COMPLETE|native|{transport}|{phase}|pid={Environment.ProcessId}");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

void VerifyAdo()
{
    using var connection = new SndbConnection(args[1]);
    connection.Open();
    if (phase == "reopen")
    {
        VerifySaved(Read("SELECT id,value,other FROM Rows ORDER BY id"), state);
        var previous = File.ReadAllLines(state).SelectMany(line => line.Split('|').Skip(1)).ToArray();
        var next = Distinct(Read("SELECT uuid(),uuid() FROM Rows ORDER BY id").SelectMany(row => row), "reopen-new-calls");
        Require(!next.Intersect(previous, StringComparer.Ordinal).Any(), "reopen-new-values");
        return;
    }
    Execute("CREATE TABLE Rows(id INT,value STRING,other STRING,PRIMARY KEY(id))");
    Execute("INSERT INTO Rows(id,value) VALUES(1,NULL),(2,'keep'),(3,NULL),(4,NULL)");
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (string sql in new[]
    {
        "SELECT uuid(),UUID()",
        "SELECT uuid(),UUID() FROM Rows ORDER BY id",
        "SELECT uuid(),UUID() FROM Rows a JOIN Rows b ON a.id=b.id ORDER BY a.id",
        "SELECT uuid(),UUID() FROM Rows ORDER BY id"
    })
    {
        var values = Distinct(Read(sql, uuidSchema: true).SelectMany(row => row), "call/" + sql);
        Require(values.All(seen.Add), "separate-calls-not-folded");
    }
    Require(Read("SELECT uuid(),uuid() FROM Rows LIMIT 0", uuidSchema: true).Count == 0, "limit-zero-schema");
    Require(Read("SELECT uuid() FROM Rows WHERE id<0", uuidSchema: true).Count == 0, "empty-schema");
    var branches = Read("SELECT id,CASE WHEN value IS NULL THEN uuid() ELSE value END,coalesce(value,uuid()) FROM Rows ORDER BY id");
    Require(branches[1][1] as string == "keep" && branches[1][2] as string == "keep", "null-branches-preserve-existing");
    var generated = Distinct(branches.Where(row => (long)row[0]! != 2).SelectMany(row => row.Skip(1)), "null-branches");
    Require(generated.All(seen.Add), "null-branches-separate-calls");
    foreach (string expression in new[] { "uuid(NULL)", "uuid(1)", "uuid('x')", "uuid(1,2)" })
        Expect(() => Read("SELECT " + expression + " FROM Rows WHERE id<0"), "0 个参数", "invalid-arity/" + expression);
    Expect(() => Execute("CREATE TABLE InvalidDefault(id INT,value STRING DEFAULT uuid(),PRIMARY KEY(id))"), "DEFAULT", "uuid-default-rejected");
    Require(Read("UPDATE Rows SET value=uuid() WHERE id<0 RETURNING id,value").Count == 0, "update-no-match");
    string[] discarded;
    using (var tx = connection.BeginTransaction(IsolationLevel.ReadCommitted))
    {
        var returned = Read("UPDATE Rows SET value=uuid() WHERE value IS NULL RETURNING id,value", tx);
        Require(returned.Count == 3, "rollback-returning-count");
        discarded = Distinct(returned.Select(row => row[1]), "rollback-values");
        MatchReturning(returned, Read("SELECT id,value FROM Rows ORDER BY id", tx));
        tx.Rollback();
    }
    Require(Read("SELECT value FROM Rows WHERE value IS NULL").Count == 3, "rollback-restores-null");
    using (var tx = connection.BeginTransaction(IsolationLevel.ReadCommitted))
    {
        var returned = Read("UPDATE Rows SET value=uuid() WHERE value IS NULL RETURNING id,value", tx);
        var committed = Distinct(returned.Select(row => row[1]), "commit-values");
        Require(returned.Count == 3 && !committed.Intersect(discarded, StringComparer.Ordinal).Any(), "commit-new-values");
        MatchReturning(returned, Read("SELECT id,value FROM Rows ORDER BY id", tx));
        tx.Commit();
        MatchReturning(returned, Read("SELECT id,value FROM Rows ORDER BY id"));
    }
    Require(Read("SELECT value FROM Rows WHERE id=2").Single()[0] as string == "keep", "update-null-predicate");
    var other = Read("UPDATE Rows SET other=uuid() WHERE id>0 RETURNING id,other");
    Require(other.Count == 4, "batch-update-count");
    Distinct(other.Select(row => row[1]), "batch-update-values");
    MatchReturning(other, Read("SELECT id,other FROM Rows ORDER BY id"));
    var stored = Read("SELECT id,value,other FROM Rows ORDER BY id");
    File.WriteAllLines(state, stored.Select(Row), new System.Text.UTF8Encoding(false));
    Console.WriteLine("SAVED|" + state);

    void Execute(string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    List<object?[]> Read(string sql, DbTransaction? tx = null, bool uuidSchema = false)
    {
        using DbCommand command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = tx;
        using var reader = command.ExecuteReader();
        void Schema()
        {
            if (uuidSchema) Require(reader.GetColumnSchema().All(c => c.DataType == typeof(string) && c.DataTypeName == "String" && c.AllowDBNull == false), "uuid-declaration");
        }
        Schema(); var rows = new List<object?[]>();
        while (reader.Read()) { rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()); Schema(); }
        Schema(); return rows;
    }
}

void VerifyCore()
{
    using var db = Tsdb.Open(new TsdbOptions { RootDirectory = args[1] });
    SelectExecutionResult Query(string sql) => SqlExecutor.Execute(db, sql) as SelectExecutionResult ?? throw new InvalidOperationException("Expected SELECT result.");
    if (phase == "core-reopen")
    {
        VerifySaved(Query("SELECT id,value FROM Rows ORDER BY id").Rows.Select(row => row.ToArray()).ToList(), state);
        Distinct(Query("SELECT uuid(),uuid() FROM Rows").Rows.SelectMany(row => row), "core-reopen-calls");
        return;
    }
    SqlExecutor.Execute(db, "CREATE TABLE Rows(id INT,value STRING,PRIMARY KEY(id))");
    SqlExecutor.Execute(db, "INSERT INTO Rows(id) VALUES(1),(2),(3),(4)");
    foreach (string suffix in new[] { "", " FROM Rows", " FROM Rows a JOIN Rows b ON a.id=b.id" })
    {
        var result = Query("SELECT uuid(),uuid()" + suffix);
        Distinct(result.Rows.SelectMany(row => row), "core-calls/" + suffix);
        Require(result.ColumnInfo!.All(c => c.DataType == TableColumnType.String && c.IsNullable == false), "core-declaration");
    }
    int calls = 0;
    db.Functions.RegisterScalar("uuid", _ => ++calls, 0, 0);
    Require(Query("SELECT uuid() FROM Rows WHERE id<0").Rows.Count == 0 && calls == 0, "core-empty-zero-evaluations");
    Require(Query("SELECT uuid() FROM Rows LIMIT 0").Rows.Count == 0 && calls == 0, "core-limit-zero-evaluations");
    var counted = Query("SELECT uuid(),uuid() FROM Rows ORDER BY id");
    Require(calls == 8 && counted.Rows.SelectMany(row => row).Cast<int>().SequenceEqual(Enumerable.Range(1, 8)), "core-once-per-row-and-occurrence");
    Query("SELECT coalesce('keep',uuid()),CASE WHEN false THEN uuid() ELSE 'keep' END FROM Rows");
    Require(calls == 8, "core-short-circuit-no-evaluation");
    SqlExecutor.Execute(db, "UPDATE Rows SET value=uuid() WHERE id<0");
    Require(calls == 8, "core-no-match-no-evaluation");
    Require(db.Functions.Unregister("uuid"), "core-remove-counter");
    var update = SqlExecutor.Execute(db, "UPDATE Rows SET value=uuid() WHERE id>0 RETURNING id,value") as RowsAffectedExecutionResult ?? throw new InvalidOperationException("Expected UPDATE result.");
    var returned = (SelectExecutionResult)update.Returning!;
    Distinct(returned.Rows.Select(row => row[1]), "core-returning-values");
    MatchReturning(returned.Rows.Select(row => row.ToArray()).ToList(), Query("SELECT id,value FROM Rows").Rows.Select(row => row.ToArray()).ToList());
    File.WriteAllLines(state, Query("SELECT id,value FROM Rows ORDER BY id").Rows.Select(row => Row(row.ToArray())), new System.Text.UTF8Encoding(false));
}

static void MatchReturning(List<object?[]> returned, List<object?[]> stored)
{
    Require(returned.All(row => Equals(row[1], stored.Single(candidate => Equals(candidate[0], row[0]))[1])), "returning-matches-stored");
}
static string Row(object?[] row) => string.Join('|', row.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "NULL"));
static void VerifySaved(List<object?[]> rows, string path) => Require(rows.Select(Row).SequenceEqual(File.ReadAllLines(path)), "independent-reopen-exact-values");
