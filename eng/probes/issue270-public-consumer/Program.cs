using System.Data.Common;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

if (args.Length != 4 || args[0] is not ("SonnetDB" or "Sqlite")
    || args[1] is not ("original" or "matrix"))
{
    Console.Error.WriteLine("Usage: ContainsRepro SonnetDB|Sqlite original|matrix percent|underscore|backslash|all output-directory");
    return 2;
}

string output = Path.GetFullPath(args[3]);
if (Directory.Exists(output))
    throw new InvalidOperationException("Each run requires a new owned output directory.");
Directory.CreateDirectory(output);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var receipt = new ProbeReceipt { Provider = args[0], Mode = args[1], StartedUtc = DateTimeOffset.UtcNow };
var capture = new CommandCapture();
try
{
    await using var context = new ReproContext(args[0], output, args[1] == "matrix", capture);
    await File.WriteAllTextAsync(Path.Combine(output, "create.sql"), context.Database.GenerateCreateScript(), deadline.Token);
    await context.Database.EnsureCreatedAsync(deadline.Token);
    string?[] names = args[1] == "original"
        ? ["100% Real", "100X Real", "A_B", "AXB", "path\\name"]
        : ["100% Real", "100X Real", "A_B", "AXB", "path\\name", "%leading", "trailing%",
            "_leading", "trailing_", "\\leading", "trailing\\", "\\%_", "泵_%\\😀设备", "", null, "quote'_%\\end"];
    Author[] authors = names.Select((name, index) => new Author
    {
        Id = Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
        Name = name,
        BirthDate = new DateTime(1980 + index, 1, 1),
    }).ToArray();
    context.Authors.AddRange(authors);
    await context.SaveChangesAsync(deadline.Token);
    context.ChangeTracker.Clear();

    string?[] patterns = args[1] == "original" ? [args[2] switch
    {
        "percent" => "%",
        "underscore" => "_",
        "backslash" => "\\",
        _ => throw new ArgumentException("Unknown original scenario."),
    }] : ["%", "_", "\\", "\\%_", "泵_%\\😀设备", "", null, "'_%\\"];
    string[] methods = args[1] == "original" ? [nameof(string.Contains)]
        : [nameof(string.Contains), nameof(string.StartsWith), nameof(string.EndsWith)];
    bool[] constantModes = args[1] == "original" ? [false] : [false, true];
    foreach (string method in methods)
    foreach (string? pattern in patterns)
    foreach (bool constant in constantModes)
    {
        var result = new CaseResult { Method = method, Pattern = pattern, Constant = constant };
        capture.Commands.Clear();
        try
        {
            Author[] expectedAuthors = authors.Where(author => author.Name is not null && pattern is not null
                && (method switch
                {
                    nameof(string.Contains) => author.Name.Contains(pattern, StringComparison.Ordinal),
                    nameof(string.StartsWith) => author.Name.StartsWith(pattern, StringComparison.Ordinal),
                    nameof(string.EndsWith) => author.Name.EndsWith(pattern, StringComparison.Ordinal),
                    _ => throw new ArgumentException("Unknown method."),
                })).OrderBy(author => author.BirthDate).ToArray();
            result.Expected = expectedAuthors.Select(author => author.Name!).ToArray();
            IQueryable<Author> query = Query(context.Authors, method, pattern, constant);
            var ordered = args[1] == "original" ? query.OrderBy(author => author.Name) : query.OrderBy(author => author.BirthDate);
            result.GeneratedListSql = ordered.ToQueryString();
            result.Actual = (await ordered.Select(author => author.Name).ToArrayAsync(deadline.Token))!;
            result.Count = await query.CountAsync(deadline.Token);
            result.Page = (await ordered.Skip(1).Take(2).Select(author => author.Name).ToArrayAsync(deadline.Token))!;
            if (!result.Expected.SequenceEqual(result.Actual) || result.Count != result.Expected.Length
                || !result.Expected.Skip(1).Take(2).SequenceEqual(result.Page))
                throw new InvalidOperationException("Actual list, Count or page differs from literal ordinal matching with SQL null exclusion.");
            if (!constant && !string.IsNullOrEmpty(pattern))
            {
                bool parameterized = capture.Commands.First().Parameters.Any(parameter => args[0] == "SonnetDB"
                    ? parameter.Value == pattern : parameter.DbType == "String");
                if (!parameterized)
                    throw new InvalidOperationException("The runtime pattern query was not parameterized as required.");
            }
            if (!capture.Commands.Any(command => command.Sql.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Count was not observed as an executed SQL command.");
            result.Passed = true;
        }
        catch (Exception exception)
        {
            result.Error = exception.ToString();
        }
        result.Executed = capture.Commands.ToArray();
        receipt.Cases.Add(result);
        Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {args[0]} {method} constant={constant} pattern={pattern ?? "<NULL>"} count={result.Count}");
    }
}
catch (Exception exception)
{
    receipt.FatalError = exception.ToString();
    Console.Error.WriteLine(exception);
}
receipt.Assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(assembly =>
    assembly.GetName().Name?.StartsWith("SonnetDB", StringComparison.Ordinal) == true
    || assembly.GetName().Name?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true)
    .OrderBy(assembly => assembly.GetName().Name).Select(assembly => new AssemblyEvidence
    {
        Name = assembly.GetName().Name!,
        InformationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        Location = assembly.Location,
        Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
    }).ToArray();
receipt.FinishedUtc = DateTimeOffset.UtcNow;
receipt.Passed = receipt.FatalError is null && receipt.Cases.Count > 0 && receipt.Cases.All(result => result.Passed);
await File.WriteAllTextAsync(Path.Combine(output, "receipt.json"),
    JsonSerializer.Serialize(receipt, ProbeJsonContext.Default.ProbeReceipt));
Console.WriteLine($"RESULT {receipt.Passed} cases={receipt.Cases.Count} failures={receipt.Cases.Count(result => !result.Passed)}");
return receipt.Passed ? 0 : 1;

static IQueryable<Author> Query(IQueryable<Author> source, string method, string? pattern, bool constant)
{
    if (!constant)
    {
        return method switch
        {
            nameof(string.Contains) => source.Where(author => author.Name!.Contains(pattern!)),
            nameof(string.StartsWith) => source.Where(author => author.Name!.StartsWith(pattern!)),
            nameof(string.EndsWith) => source.Where(author => author.Name!.EndsWith(pattern!)),
            _ => throw new ArgumentException("Unknown method."),
        };
    }
    var author = Expression.Parameter(typeof(Author), "author");
    var predicate = Expression.Call(Expression.Property(author, nameof(Author.Name)),
        typeof(string).GetMethod(method, [typeof(string)])!, Expression.Constant(pattern, typeof(string)));
    return source.Where(Expression.Lambda<Func<Author, bool>>(predicate, author));
}

internal sealed class ReproContext(string provider, string directory, bool nullableName, CommandCapture capture) : DbContext
{
    public DbSet<Author> Authors => Set<Author>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (provider == "Sqlite")
            options.UseSqlite("Data Source=" + Path.Combine(directory, "reference.db") + ";Pooling=False");
        else
            options.UseSonnetDB("Data Source=" + Path.Combine(directory, "sonnetdb"));
        options.AddInterceptors(capture);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Author>(entity =>
        {
            entity.ToTable("A06Authors");
            entity.HasKey(author => author.Id);
            entity.Property(author => author.Id).ValueGeneratedNever();
            entity.Property(author => author.Name).IsRequired(!nullableName).HasMaxLength(64);
        });
    }
}

internal sealed class Author
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public DateTime BirthDate { get; set; }
}

internal sealed class CommandCapture : DbCommandInterceptor
{
    internal List<CommandEvidence> Commands { get; } = [];

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Capture(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Capture(command);
        return ValueTask.FromResult(result);
    }

    private void Capture(DbCommand command) => Commands.Add(new CommandEvidence
    {
        Sql = command.CommandText,
        Parameters = command.Parameters.Cast<DbParameter>().Select(parameter => new ParameterEvidence
        {
            Name = parameter.ParameterName,
            DbType = parameter.DbType.ToString(),
            Value = parameter.Value is null or DBNull ? null : Convert.ToString(parameter.Value, CultureInfo.InvariantCulture),
        }).ToArray(),
    });
}

internal sealed class ProbeReceipt
{
    public string Provider { get; set; } = "";
    public string Mode { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset FinishedUtc { get; set; }
    public bool Passed { get; set; }
    public string? FatalError { get; set; }
    public List<CaseResult> Cases { get; set; } = [];
    public AssemblyEvidence[] Assemblies { get; set; } = [];
}

internal sealed class CaseResult
{
    public string Method { get; set; } = "";
    public string? Pattern { get; set; }
    public bool Constant { get; set; }
    public bool Passed { get; set; }
    public string? Error { get; set; }
    public string GeneratedListSql { get; set; } = "";
    public string[] Expected { get; set; } = [];
    public string[] Actual { get; set; } = [];
    public string[] Page { get; set; } = [];
    public int Count { get; set; }
    public CommandEvidence[] Executed { get; set; } = [];
}

internal sealed class CommandEvidence
{
    public string Sql { get; set; } = "";
    public ParameterEvidence[] Parameters { get; set; } = [];
}

internal sealed class ParameterEvidence
{
    public string Name { get; set; } = "";
    public string DbType { get; set; } = "";
    public string? Value { get; set; }
}

internal sealed class AssemblyEvidence
{
    public string Name { get; set; } = "";
    public string? InformationalVersion { get; set; }
    public string Location { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

[JsonSerializable(typeof(ProbeReceipt))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ProbeJsonContext : JsonSerializerContext;
