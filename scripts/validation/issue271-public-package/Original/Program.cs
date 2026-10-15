using Microsoft.EntityFrameworkCore;

if (args.Length != 2 || args[0] is not ("Sqlite" or "SonnetDB") ||
    args[1] is not ("binary-key" or "passkey-any"))
{
    Console.Error.WriteLine("Usage: dotnet run -- Sqlite|SonnetDB binary-key|passkey-any");
    return 2;
}

var owner = Guid.NewGuid().ToString("N");
var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "binary-query-repro"));
var directory = Path.Combine(root, owner);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
ConsoleCancelEventHandler cancel = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    deadline.Cancel();
};
Console.CancelKeyPress += cancel;
var result = 1;
try
{
    Directory.CreateDirectory(directory);
    await File.WriteAllTextAsync(Path.Combine(directory, ".owner"), owner, deadline.Token);
    await using var context = new ReproContext(args[0], args[1], directory);
    Console.WriteLine(context.Database.GenerateCreateScript());
    await context.Database.EnsureCreatedAsync(deadline.Token);
    byte[] key = [0, 1, 127, 128, 255];

    if (args[1] == "binary-key")
    {
        context.Passkeys.Add(new Passkey
        {
            CredentialId = key, UserId = Guid.NewGuid(), Name = "non-JSON credential"
        });
        await context.SaveChangesAsync(deadline.Token);
        context.ChangeTracker.Clear();
        var query = context.Passkeys.Where(passkey => passkey.CredentialId == key);
        Console.WriteLine(query.ToQueryString());
        var saved = await query.SingleAsync(deadline.Token);
        Require(saved.CredentialId.SequenceEqual(key), "Credential contents must roundtrip.");
    }
    else
    {
        var user = new User { Id = Guid.NewGuid(), NormalizedUserName = "PASSKEY" };
        user.Passkeys.Add(new Passkey
        {
            CredentialId = key, UserId = user.Id, Name = "target credential"
        });
        var other = new User { Id = Guid.NewGuid(), NormalizedUserName = "OTHER" };
        other.Passkeys.Add(new Passkey
        {
            CredentialId = [9, 8, 7], UserId = other.Id, Name = "different credential"
        });
        context.Users.AddRange(user, other);
        await context.SaveChangesAsync(deadline.Token);
        context.ChangeTracker.Clear();
        byte[] lookup = [0, 1, 127, 128, 255];
        var query = context.Users
            .Where(row => row.Passkeys.Any(passkey => passkey.CredentialId.SequenceEqual(lookup)))
            .OrderBy(row => row.Id);
        Console.WriteLine(query.Take(1).ToQueryString());
        Require((await query.FirstOrDefaultAsync(deadline.Token))?.Id == user.Id,
            "Lookup must select the user whose credential contents match.");
        lookup = [4, 3, 2];
        Require(await query.FirstOrDefaultAsync(deadline.Token) is null,
            "An absent credential must not match either user.");
    }
    Console.WriteLine($"PASS {args[0]} {args[1]}");
    result = 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
}
finally
{
    Console.CancelKeyPress -= cancel;
    if (Directory.Exists(directory))
    {
        try
        {
            var expected = Path.GetFullPath(Path.Combine(root, owner));
            if (!string.Equals(expected, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) ||
                File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint) ||
                await File.ReadAllTextAsync(Path.Combine(directory, ".owner")) != owner)
            {
                throw new InvalidOperationException("Refusing cleanup without matching ownership.");
            }
            Directory.Delete(directory, recursive: true);
            Console.WriteLine("CLEANED " + directory);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("RESOURCE CLEANUP FAILED: " + exception);
            result = 1;
        }
    }
}
return result;

static void Require(bool condition, string message)
{
    if (!condition) { throw new InvalidOperationException(message); }
}

internal sealed class ReproContext(string provider, string scenario, string directory) : DbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Passkey> Passkeys => Set<Passkey>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (provider == "Sqlite")
        {
            options.UseSqlite("Data Source=" + Path.Combine(directory, "reference.db") +
                ";Pooling=False;Default Timeout=2");
        }
        else { options.UseSonnetDB("Data Source=" + Path.Combine(directory, "sonnetdb")); }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Passkey>(entity =>
        {
            entity.ToTable("A06UserPasskeys");
            entity.HasKey(row => row.CredentialId);
            entity.Property(row => row.CredentialId).HasMaxLength(1024).ValueGeneratedNever();
        });
        if (scenario == "passkey-any")
        {
            builder.Entity<User>(entity =>
            {
                entity.ToTable("A06Users");
                entity.HasKey(row => row.Id);
                entity.HasMany(row => row.Passkeys).WithOne().HasForeignKey(row => row.UserId);
            });
        }
        else { builder.Ignore<User>(); }
    }
}

internal sealed class User
{
    public Guid Id { get; set; }
    public string NormalizedUserName { get; set; } = string.Empty;
    public List<Passkey> Passkeys { get; set; } = [];
}

internal sealed class Passkey
{
    public byte[] CredentialId { get; set; } = [];
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
}
