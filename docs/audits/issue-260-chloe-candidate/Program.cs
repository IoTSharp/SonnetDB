using System.Data;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Chloe;
using Chloe.Annotations;
using Chloe.SonnetDB;
using SonnetDB.Data;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length != 2 || args[0] is not ("guard" or "exercise" or "reopen")) return 2;
int assertions = 0;
try
{
    string root = Path.GetFullPath(args[1]);
    if (!File.Exists(Path.Combine(root, ".owner"))) throw new InvalidOperationException("Owned runtime directory required.");
    string connection = Environment.GetEnvironmentVariable("ISSUE260_CONNECTION") ?? "Data Source=" + Path.Combine(root, "db");
    foreach (string name in new[] { "SonnetDB.Core", "SonnetDB.Data", "Chloe", "Chloe.SonnetDB" })
    {
        var assembly = Assembly.Load(name);
        Console.WriteLine($"ASSEMBLY {name} {assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} SHA256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))}");
    }
    using var context = new SonnetDBContext(() => new SndbConnection(connection));
    context.Session.CommandTimeout = 15;
    if (args[0] == "guard")
    {
        Reject(() => context.Query<ParsedRow>().Select(row => ulong.Parse(row.Text!)).ToList(), "260");
        Console.WriteLine($"PASS original guard ASSERTIONS={assertions}");
        return 0;
    }
    if (args[0] == "exercise")
    {
        context.Session.ExecuteNonQuery("CREATE TABLE parsed_consumer(Id INT,Text STRING NULL,Value DECIMAL(20,0),PRIMARY KEY(Id))");
        context.InsertRange(new List<ParsedRow>
        {
            new() { Id = 1, Text = "18446744073709551615", Value = 7 },
            new() { Id = 2, Text = "9223372036854775808", Value = 7 },
            new() { Id = 3, Text = " +0000 ", Value = 7 },
            new() { Id = 4, Text = null, Value = null },
        });
    }

    ulong?[] expected = [ulong.MaxValue, 9223372036854775808UL, 0UL, null];
    var query = context.Query<ParsedRow>().OrderBy(row => row.Id).Select(row => (ulong?)ulong.Parse(row.Text!));
    string sql = query.ToString()!;
    Console.WriteLine("SQL " + sql);
    Require(sql.Contains("parse_uint64", StringComparison.OrdinalIgnoreCase), "Real ulong.Parse expression emits native parser");
    Require(query.ToList().SequenceEqual(expected), "Synchronous projected UInt64/NULL values are exact");
    Require((await query.ToListAsync()).SequenceEqual(expected), "Asynchronous projected UInt64/NULL values are exact");
    Require(context.Query<ParsedRow>().Where(row => ulong.Parse(row.Text!) == ulong.MaxValue).Count() == 1, "Native parsed predicate finds UInt64 maximum");
    var raw = await context.SqlQueryAsync<ulong?>("SELECT parse_uint64(Text) FROM parsed_consumer ORDER BY Id");
    Require(raw.SequenceEqual(expected), "Raw SqlQuery materializes exact UInt64 from Decimal");
    Reject(() => context.Query<ParsedRow>().Select(row => ulong.Parse(row.Text!, CultureInfo.InvariantCulture)).ToList(), "Parse");
    Reject(() => context.Query<ParsedRow>().Select(row => ulong.Parse(row.Text!, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToList(), "Parse");

    if (args[0] == "exercise")
    {
        Require(await context.UpdateAsync<ParsedRow>(row => row.Id > 0,
            row => new ParsedRow { Value = (ulong?)ulong.Parse(row.Text!) }) == 4, "Native parser executes in actual ORM UPDATE");
        Require(context.Query<ParsedRow>().OrderBy(row => row.Id).Select(row => row.Value).ToList().SequenceEqual(expected), "ORM update persisted exact nullable UInt64");

        foreach (string text in new[] { "2.9", "2.0", "-1", "-0", "1e2", "18446744073709551616", "1\0", new string('0', 1025) })
        {
            context.Update<ParsedRow>(row => row.Id == 3, row => new ParsedRow { Text = text });
            Reject(() => context.Query<ParsedRow>().Where(row => row.Id == 3).Select(row => ulong.Parse(row.Text!)).ToList(), "parse_uint64");
        }
        context.Update<ParsedRow>(row => row.Id == 3, row => new ParsedRow { Text = "0" });
        context.Update<ParsedRow>(row => row.Id == 1, row => new ParsedRow { Value = 7 });
        context.Insert(new ParsedRow { Id = 5, Text = "2.9", Value = 55 });
        Reject(() => context.Update<ParsedRow>(row => row.Id > 0,
            row => new ParsedRow { Value = (ulong?)ulong.Parse(row.Text!) }), "parse_uint64");
        Require(context.Query<ParsedRow>().Where(row => row.Id < 5).OrderBy(row => row.Id).Select(row => row.Value).ToList().SequenceEqual(new ulong?[] { 7, 9223372036854775808UL, 0, null }),
            "Invalid later ORM UPDATE leaves prior rows unchanged");
        Require(context.Query<ParsedRow>().Where(row => row.Id == 5).Select(row => row.Value).First() == 55UL, "Invalid target retains old value");
        context.Delete<ParsedRow>(row => row.Id == 5);
        context.Update<ParsedRow>(row => row.Id > 0, row => new ParsedRow { Value = (ulong?)ulong.Parse(row.Text!) });

        context.Session.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            context.Update<ParsedRow>(row => row.Id == 1, row => new ParsedRow { Value = ulong.Parse("0") });
            context.Insert(new ParsedRow { Id = 5, Text = "1", Value = 1 });
        }
        finally { context.Session.RollbackTransaction(); }
        Require(context.Query<ParsedRow>().Count() == 4, "Rollback removes inserted row");
    }
    Require(context.Query<ParsedRow>().OrderBy(row => row.Id).Select(row => row.Value).ToList().SequenceEqual(expected), "Committed values and rollback/reopen remain exact");
    Console.WriteLine($"PASS {args[0]} ASSERTIONS={assertions}");
    return 0;
}
catch (Exception failure)
{
    Console.Error.WriteLine(failure);
    return 1;
}

void Require(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("ASSERTION FAILED: " + label);
    Console.WriteLine($"ASSERT {++assertions:D3} {label}");
}

void Reject(Action action, string expectedMessage)
{
    Exception? failure = null;
    try { action(); } catch (Exception exception) { failure = exception; }
    Require(failure is not null && failure.ToString().Contains(expectedMessage, StringComparison.OrdinalIgnoreCase), "Expected rejection: " + expectedMessage);
    Console.WriteLine("REJECTION " + failure!.GetType().FullName);
}

/// <summary>真实 Chloe 消费的字符串来源和已有 Decimal 承载的 UInt64 属性。</summary>
[Table("parsed_consumer")]
public sealed class ParsedRow
{
    /// <summary>固定主键。</summary>
    [Column(IsPrimaryKey = true), NonAutoIncrement]
    public long Id { get; set; }

    /// <summary>待解析的原始文本。</summary>
    public string? Text { get; set; }

    /// <summary>由现有 Decimal 存储承载的完整 UInt64 值。</summary>
    public ulong? Value { get; set; }
}
