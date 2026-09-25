using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;

if (args.Length != 2 || args[1] is not ("update" or "delete" or "drop" or "verify-update" or "verify-delete" or "verify-drop"))
    throw new ArgumentException("Usage: VectorCrashWorker <database-root> <update|delete|drop|verify-update|verify-delete|verify-drop>");

using var database = Tsdb.Open(new TsdbOptions { RootDirectory = args[0] });
if (args[1].StartsWith("verify-", StringComparison.Ordinal))
{
    if (args[1] == "verify-drop")
    {
        if (database.Measurements.Contains("docs"))
            throw new InvalidDataException("DROP measurement was not recovered.");
    }
    else
    {
        var selected = (SelectExecutionResult)SqlExecutor.Execute(database,
            "SELECT embedding FROM docs WHERE source = 'a'")!;
        if (args[1] == "verify-delete")
        {
            if (selected.Rows.Count != 0)
                throw new InvalidDataException("Deleted vector returned after recovery.");
        }
        else
        {
            var vector = (float[])selected.Rows.Single()[0]!;
            if (!vector.SequenceEqual([0f, 1f, 0f]))
                throw new InvalidDataException("Updated vector was not recovered.");
        }
    }
    Console.WriteLine("VERIFIED");
    return;
}

SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
SqlExecutor.Execute(database, "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0])");
SqlExecutor.Execute(database, "UPDATE docs SET embedding = [0,1,0] WHERE source = 'a'");
if (args[1] == "delete")
    SqlExecutor.Execute(database, "DELETE FROM docs WHERE source = 'a' AND time = 1000");
else if (args[1] == "drop")
    SqlExecutor.Execute(database, "DROP MEASUREMENT docs");

Console.WriteLine("READY");
Console.Out.Flush();
Process.GetCurrentProcess().Kill();
throw new InvalidOperationException("Crash worker termination unexpectedly returned.");
