using System.Data;
using System.Globalization;
using System.Reflection;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length != 3) return 2;
string phase = args[0];
string transport = args[2];
try
{
    foreach (string name in new[] { "SonnetDB.Data", "SonnetDB.Core" })
    {
        var assembly = Assembly.Load(name);
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        MetadataCases.Require(version.Contains("1af23d8f299febfdf66b040ab3419980396acf46", StringComparison.Ordinal), "public-source");
        Console.WriteLine($"ASSEMBLY|{name}|{version}|{assembly.Location}");
    }
    if (phase.StartsWith("core-", StringComparison.Ordinal))
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = args[1] });
        if (phase == "core-initial")
        {
            foreach (string sql in MetadataCases.Create) SqlExecutor.Execute(db, sql);
            VerifyCore(db, false);
            foreach (string sql in MetadataCases.Populate) SqlExecutor.Execute(db, sql);
        }
        VerifyCore(db, true);
    }
    else
    {
        using var connection = new SndbConnection(args[1]);
        connection.Open();
        if (phase == "initial")
        {
            foreach (string sql in MetadataCases.Create) Execute(connection, sql);
            VerifyAdo(connection, false);
            foreach (string sql in MetadataCases.Populate) Execute(connection, sql);
        }
        VerifyAdo(connection, true);
    }
    Console.WriteLine($"COMPLETE|native|{transport}|{phase}|pid={Environment.ProcessId}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

static void Execute(SndbConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static void VerifyAdo(SndbConnection connection, bool populated)
{
    foreach (ViewCase item in MetadataCases.Cases(populated))
    {
        using var catalog = connection.GetSchema("Columns", [null,null,item.Name,null]);
        MetadataCases.Require(catalog.Rows.Count == 0, item.Name + "/view-columns-not-advertised");
        Console.WriteLine($"CATALOG|Columns|{item.Name}|rows={catalog.Rows.Count}|declarations=reader");
        foreach (string suffix in new[] { " LIMIT 0", " WHERE 1 = 0", "" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM " + item.Name + suffix;
            using var reader = command.ExecuteReader();
            MetadataCases.AssertSchema(reader, item, "before-read" + suffix);
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values);
                MetadataCases.AssertSchema(reader, item, "row" + rows.Count);
            }
            MetadataCases.AssertSchema(reader, item, "eof" + suffix);
            MetadataCases.AssertRows(rows, suffix.Length == 0 ? item.Rows : [], item.Name + suffix);
        }
    }
    using var baseColumns = connection.GetSchema("Columns", [null,null,"ViewItems",null]);
    MetadataCases.Require(baseColumns.Rows.Count == 4, "base-columns");
    MetadataCases.Require(Equals(baseColumns.Rows[2]["NUMERIC_PRECISION"],(short)20) && Equals(baseColumns.Rows[2]["NUMERIC_SCALE"],(short)6), "base-decimal-declaration");
}

static void VerifyCore(Tsdb database, bool populated)
{
    foreach (ViewCase item in MetadataCases.Cases(populated))
    {
        foreach (string suffix in new[] { " LIMIT 0", " WHERE 1 = 0", "" })
        {
            var result = SqlExecutor.Execute(database, "SELECT * FROM " + item.Name + suffix) as SelectExecutionResult
                ?? throw new InvalidOperationException("SELECT result missing.");
            MetadataCases.Require(result.Columns.SequenceEqual(item.Columns.Select(c => c.Name)), item.Name + "/core-names");
            var columns = result.ColumnInfo ?? throw new InvalidOperationException("Core declarations missing.");
            for (int i = 0; i < columns.Count; i++)
            {
                var expected = item.Columns[i];
                var column = columns[i];
                MetadataCases.Require((column.DataType?.ToString() ?? "Object") == expected.SqlType, item.Name + "/core-type/" + expected.Name);
                MetadataCases.Require(column.IsNullable == expected.Nullable, item.Name + "/core-nullable/" + expected.Name);
                if (expected.Type != typeof(long))
                    MetadataCases.Require(column.NumericPrecision == expected.Precision && column.NumericScale == expected.Scale, item.Name + "/core-p-s/" + expected.Name);
                MetadataCases.Require(column.ColumnSize is null, item.Name + "/core-size");
            }
            MetadataCases.AssertRows(result.Rows.Select(row => row.ToArray()), suffix.Length == 0 ? item.Rows : [], "core/" + item.Name + suffix);
        }
    }
}
