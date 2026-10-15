using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

if (args.Length != 3 || args[0] is not ("Sqlite" or "SonnetDB") || args[1] is not ("exercise" or "reopen"))
{
    Console.Error.WriteLine("Usage: Matrix Sqlite|SonnetDB exercise|reopen owned-data-directory");
    return 2;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
try
{
    string directory = Path.GetFullPath(args[2]);
    if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, ".owner")))
        throw new InvalidOperationException("The runner must create the owned database directory first.");
    foreach (string name in new[] { "SonnetDB.Core", "SonnetDB.Data", "SonnetDB.EntityFrameworkCore" })
    {
        Assembly assembly = Assembly.Load(name);
        Console.WriteLine($"ASSEMBLY {name} {assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} SHA256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))}");
    }
    await using var context = new ProbeContext(args[0], directory);
    if (args[1] == "exercise") await Matrix.ExerciseAsync(context, args[0], deadline.Token);
    else await Matrix.ReopenAsync(context, deadline.Token);
    Console.WriteLine($"PASS {args[0]} {args[1]} ASSERTIONS={Matrix.Assertions}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

internal static class Matrix
{
    internal static int Assertions { get; private set; }
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Carol = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly byte[] Target = [0, 1, 127, 128, 255];
    private static readonly byte[] Alternative = [128, 255, 0, 255];
    private static readonly byte[] BobKey = [9, 8, 7];
    private static readonly byte[] DeclaredLengthKey = Bytes(1024, 17);
    private static readonly byte[] RolledBackKey = [55, 66, 77];
    private static readonly byte[] LargePayload = Bytes(65_536, 5);

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("ASSERTION FAILED: " + message);
        Console.WriteLine($"ASSERT {++Assertions:D3} {message}");
    }

    internal static async Task ExerciseAsync(ProbeContext context, string provider, CancellationToken token)
    {
        Require(await context.Database.EnsureCreatedAsync(token), "created fresh database");
        context.Users.AddRange(
            new User { Id = Alice, NormalizedUserName = "ALICE" },
            new User { Id = Bob, NormalizedUserName = "BOB" },
            new User { Id = Carol, NormalizedUserName = "NO_CREDENTIAL" });
        await context.SaveChangesAsync(token);
        context.Passkeys.AddRange(
            Key(Target, Alice, "target", [0, 255], Target),
            Key(Alternative, Alice, "alternative", [128, 0, 254], Alternative),
            Key([], Alice, "empty", [], []),
            Key(BobKey, Bob, "bob", null, null),
            Key([0], Bob, "zero", [0], [0]),
            Key(DeclaredLengthKey, Bob, "declared-1024", [1], null));
        Require(await context.SaveChangesAsync(token) == 6, "seeded multiple users and multiple credentials per user");
        context.ChangeTracker.Clear();

        var cases = new (byte[] Key, Guid? Owner, string Name)[]
        {
            (Target.ToArray(), Alice, "same-content-different-instance"),
            (Alternative.ToArray(), Alice, "second-credential-same-user"),
            (BobKey.ToArray(), Bob, "different-user"),
            ([], Alice, "empty-array"),
            ([0], Bob, "zero-byte"),
            (DeclaredLengthKey.ToArray(), Bob, "declared-1024-bytes"),
            ([4, 3, 2], null, "absent"),
            ([0, 1, 127, 128], null, "shorter-prefix"),
            ([0, 1, 127, 128, 254], null, "different-high-byte"),
            ([0, 1, 127, 128, 255, 0], null, "longer-prefix"),
        };
        foreach (var item in cases) await CheckLookupAsync(context, item.Key, item.Owner, item.Name, token);

        byte[] indexed = Target.ToArray();
        context.Capture.Reset();
        Require(await context.Passkeys.AsNoTracking().Where(row => row.Lookup == indexed)
            .Select(row => row.UserId).SingleAsync(token) == Alice, "secondary BLOB equality selects the owner");
        context.Capture.RequireBinary(indexed, exists: false);
        context.Capture.Reset();
        Require(await context.Users.AsNoTracking()
            .Where(user => user.Passkeys.Any(row => row.Lookup!.SequenceEqual(indexed)))
            .Select(user => user.Id).SingleAsync(token) == Alice, "secondary BLOB correlated Any selects the owner");
        context.Capture.RequireBinary(indexed, exists: true);
        if (provider == "SonnetDB")
        {
            await CheckPlanAsync(context, "CredentialId", "primary_key", token);
            await CheckPlanAsync(context, "Lookup", "secondary_index", token);
        }

        await CheckNullsAsync(context, token);
        await CheckBinaryForeignKeyAsync(context, token);
        await CheckLengthBoundaryAsync(context, provider, token);

        byte[] lookup = Target.ToArray();
        var target = await context.Passkeys.SingleAsync(row => row.CredentialId == lookup, token);
        target.Name = "updated";
        target.Payload = LargePayload.ToArray();
        Require(await context.SaveChangesAsync(token) == 1, "updated row located by BLOB primary key");
        context.ChangeTracker.Clear();
        context.Capture.Reset();
        byte[] payload = LargePayload.ToArray();
        Require(await context.Passkeys.AsNoTracking().Where(row => row.Payload == payload)
            .Select(row => row.UserId).SingleAsync(token) == Alice, "65536-byte payload contents roundtrip and compare");
        context.Capture.RequireBinary(payload, exists: false);

        await using (var transaction = await context.Database.BeginTransactionAsync(token))
        {
            target = await context.Passkeys.SingleAsync(row => row.CredentialId == lookup, token);
            target.Name = "rolled-back";
            byte[] bob = BobKey.ToArray();
            context.Passkeys.Remove(await context.Passkeys.SingleAsync(row => row.CredentialId == bob, token));
            context.Passkeys.Add(Key(RolledBackKey, Alice, "rolled-back-insert", null, null));
            Require(await context.SaveChangesAsync(token) == 3, "transaction buffered update/delete/insert");
            context.ChangeTracker.Clear();
            Require(await context.Passkeys.AsNoTracking().Where(row => row.CredentialId == lookup)
                .Select(row => row.Name).SingleAsync(token) == "rolled-back", "transaction sees own BLOB-key update");
            await transaction.RollbackAsync(token);
        }
        context.ChangeTracker.Clear();
        Require(await context.Passkeys.AsNoTracking().Where(row => row.CredentialId == lookup)
            .Select(row => row.Name).SingleAsync(token) == "updated", "rollback restores pre-transaction update");
        await CheckLookupAsync(context, BobKey.ToArray(), Bob, "rollback-restores-deleted-key", token);
        await CheckLookupAsync(context, RolledBackKey.ToArray(), null, "rollback-removes-inserted-key", token);

        byte[] empty = [];
        context.Passkeys.Remove(await context.Passkeys.SingleAsync(row => row.CredentialId == empty, token));
        Require(await context.SaveChangesAsync(token) == 1, "deleted empty BLOB key");
        context.ChangeTracker.Clear();
        await CheckLookupAsync(context, [], null, "committed-empty-key-delete", token);
        Require(await context.Passkeys.CountAsync(token) == 5, "five credentials remain after committed delete");
        await context.Database.CloseConnectionAsync();
    }

    internal static async Task ReopenAsync(ProbeContext context, CancellationToken token)
    {
        Require(!await context.Database.EnsureCreatedAsync(token), "independent process reopened existing database");
        await CheckLookupAsync(context, Target.ToArray(), Alice, "reopen-target", token);
        await CheckLookupAsync(context, Alternative.ToArray(), Alice, "reopen-second-credential", token);
        await CheckLookupAsync(context, BobKey.ToArray(), Bob, "reopen-rolled-back-delete", token);
        await CheckLookupAsync(context, [], null, "reopen-committed-delete", token);
        await CheckLookupAsync(context, RolledBackKey.ToArray(), null, "reopen-rolled-back-insert", token);
        byte[] lookup = Target.ToArray();
        var target = await context.Passkeys.AsNoTracking().SingleAsync(row => row.CredentialId == lookup, token);
        Require(target.Name == "updated" && target.Payload!.SequenceEqual(LargePayload), "reopen retains committed update and complete 65536-byte payload");
        Require(await context.Passkeys.CountAsync(token) == 5 && await context.Users.CountAsync(token) == 3,
            "reopen retains exact credential and user counts");
        byte[] maximum = Bytes(65_531, 81);
        Require((await context.BoundaryKeys.AsNoTracking().SingleAsync(row => row.CredentialId == maximum, token))
            .CredentialId.SequenceEqual(maximum), "reopen maximum standalone BLOB key contents");
        Require(await context.BinaryChildren.CountAsync(token) == 1 && await context.BinaryOwners.CountAsync(token) == 1,
            "reopen BLOB parent/foreign-key rows");
    }

    private static async Task CheckLookupAsync(ProbeContext context, byte[] lookup, Guid? owner, string label, CancellationToken token)
    {
        context.Capture.Reset();
        var rows = await context.Passkeys.AsNoTracking().Where(row => row.CredentialId == lookup).ToListAsync(token);
        Require(rows.SingleOrDefault()?.UserId == owner, label + ": ordinary equality owner");
        if (owner is not null) Require(rows[0].CredentialId.SequenceEqual(lookup), label + ": complete credential bytes");
        context.Capture.RequireBinary(lookup, exists: false);
        context.Capture.Reset();
        var user = await context.Users.AsNoTracking()
            .Where(row => row.Passkeys.Any(passkey => passkey.CredentialId.SequenceEqual(lookup)))
            .OrderBy(row => row.Id).FirstOrDefaultAsync(token);
        Require(user?.Id == owner, label + ": correlated Any/SequenceEqual owner");
        context.Capture.RequireBinary(lookup, exists: true);
    }

    private static async Task CheckNullsAsync(ProbeContext context, CancellationToken token)
    {
        Require(await context.Passkeys.AsNoTracking().Where(row => row.Payload == null)
            .Select(row => row.UserId).SingleAsync(token) == Bob, "nullable BLOB column IS NULL");
        byte[]? nullLookup = null;
        Require(await context.Passkeys.AsNoTracking().Where(row => row.Payload == nullLookup)
            .Select(row => row.UserId).SingleAsync(token) == Bob, "EF null BLOB parameter follows null equality translation");
        await context.Database.OpenConnectionAsync(token);
        foreach (string column in new[] { "Payload", "CredentialId" })
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT \"UserId\" FROM \"A06UserPasskeys\" WHERE \"{column}\" = @value";
            AddParameter(command, "value", DbType.Binary, DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(token);
            Require(!await reader.ReadAsync(token), column + ": ADO DbType.Binary/DBNull equality returns no rows");
        }
        int before = await context.BoundaryKeys.CountAsync(token);
        Exception? failure = null;
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "INSERT INTO \"BoundaryKeys\" (\"CredentialId\",\"Label\") VALUES (@key,@label)";
            AddParameter(command, "key", DbType.Binary, DBNull.Value);
            AddParameter(command, "label", DbType.String, "invalid-null-key");
            await command.ExecuteNonQueryAsync(token);
        }
        catch (Exception exception) { failure = exception; }
        Require(failure is not null, "NULL BLOB primary key insert explicitly rejected");
        Require(await context.BoundaryKeys.CountAsync(token) == before, "NULL key rejection leaves rows unchanged");
        Console.WriteLine("EXPECTED_NULL_KEY_REJECTION " + failure!.GetType().Name + ": " + failure.Message);
    }

    private static async Task CheckBinaryForeignKeyAsync(ProbeContext context, CancellationToken token)
    {
        byte[] ownerKey = [0, 128, 255, 0];
        context.BinaryOwners.Add(new BinaryOwner { Id = ownerKey.ToArray() });
        await context.SaveChangesAsync(token);
        context.BinaryChildren.Add(new BinaryChild { Id = 1, OwnerKey = ownerKey.ToArray() });
        await context.SaveChangesAsync(token);
        context.ChangeTracker.Clear();
        Require(await context.BinaryChildren.AsNoTracking().Where(row => row.OwnerKey == ownerKey)
            .Select(row => row.Id).SingleAsync(token) == 1, "BLOB foreign key accepts equal contents from a distinct instance");
        context.BinaryChildren.Add(new BinaryChild { Id = 2, OwnerKey = [254, 254] });
        Exception? failure = null;
        try { await context.SaveChangesAsync(token); }
        catch (DbUpdateException exception) { failure = exception; }
        context.ChangeTracker.Clear();
        Require(failure is not null && await context.BinaryChildren.CountAsync(token) == 1,
            "BLOB foreign key rejects nonexistent parent without adding an orphan");
    }

    private static async Task CheckLengthBoundaryAsync(ProbeContext context, string provider, CancellationToken token)
    {
        foreach (int size in new[] { 65_530, 65_531 })
        {
            byte[] key = Bytes(size, 81);
            context.BoundaryKeys.Add(new BoundaryKey { CredentialId = key.ToArray(), Label = "length-" + size });
            Require(await context.SaveChangesAsync(token) == 1, "standalone BLOB key accepted at " + size + " bytes");
            context.ChangeTracker.Clear();
            context.Capture.Reset();
            var row = await context.BoundaryKeys.AsNoTracking().SingleAsync(row => row.CredentialId == key, token);
            Require(row.CredentialId.SequenceEqual(key), "standalone BLOB key full contents match at " + size + " bytes");
            context.Capture.RequireBinary(key, exists: false);
        }
        byte[] overflow = Bytes(65_532, 81);
        context.BoundaryKeys.Add(new BoundaryKey { CredentialId = overflow, Label = "one-byte-over" });
        Exception? failure = null;
        try { await context.SaveChangesAsync(token); }
        catch (DbUpdateException exception) { failure = exception; }
        context.ChangeTracker.Clear();
        if (provider == "SonnetDB")
        {
            Require(failure is not null, "65532-byte standalone key rejects beyond 64KiB encoded-key limit");
            Require(await context.BoundaryKeys.CountAsync(token) == 2, "oversize key rejection preserves the two valid boundary rows");
            Console.WriteLine("EXPECTED_KEY_BOUNDARY_REJECTION " + failure!.Message + " INNER=" + failure.InnerException?.Message);
        }
        else
        {
            Require(failure is null, "SQLite reference accepts the SonnetDB-specific one-byte-over boundary");
            context.BoundaryKeys.Remove(await context.BoundaryKeys.SingleAsync(row => row.CredentialId == overflow, token));
            await context.SaveChangesAsync(token);
        }
    }

    private static async Task CheckPlanAsync(ProbeContext context, string column, string expected, CancellationToken token)
    {
        await context.Database.OpenConnectionAsync(token);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"EXPLAIN ANALYZE SELECT \"UserId\" FROM \"A06UserPasskeys\" WHERE \"{column}\" = @key";
        AddParameter(command, "key", DbType.Binary, Target.ToArray());
        var plan = new Dictionary<string, string?>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) plan[reader.GetString(0)] = reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1));
        Require(plan["access_path"] == expected && plan["actual_access_path"] == expected,
            column + ": public-package server-side access path is " + expected);
        Require(plan["actual_rows"] == "1" && plan["actual_examined_rows"] == "1",
            column + ": one examined/matching row, no client full-table filter");
    }

    private static Passkey Key(byte[] key, Guid user, string name, byte[]? payload, byte[]? indexed)
        => new() { CredentialId = key.ToArray(), UserId = user, Name = name, Payload = payload, Lookup = indexed };
    private static byte[] Bytes(int size, int seed) => Enumerable.Range(0, size).Select(index => (byte)(index * 31 + seed)).ToArray();
    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

internal sealed class ProbeContext(string provider, string directory) : DbContext
{
    internal BinaryCapture Capture { get; } = new();
    internal DbSet<User> Users => Set<User>();
    internal DbSet<Passkey> Passkeys => Set<Passkey>();
    internal DbSet<BoundaryKey> BoundaryKeys => Set<BoundaryKey>();
    internal DbSet<BinaryOwner> BinaryOwners => Set<BinaryOwner>();
    internal DbSet<BinaryChild> BinaryChildren => Set<BinaryChild>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (provider == "Sqlite") options.UseSqlite("Data Source=" + Path.Combine(directory, "reference.db") + ";Pooling=False;Default Timeout=5");
        else options.UseSonnetDB(Environment.GetEnvironmentVariable("ISSUE271_CONNECTION")
            ?? "Data Source=" + Path.Combine(directory, "sonnetdb"));
        options.AddInterceptors(Capture);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<User>(entity =>
        {
            entity.ToTable("A06Users");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.HasMany(row => row.Passkeys).WithOne().HasForeignKey(row => row.UserId);
        });
        builder.Entity<Passkey>(entity =>
        {
            entity.ToTable("A06UserPasskeys");
            entity.HasKey(row => row.CredentialId);
            entity.Property(row => row.CredentialId).HasMaxLength(1024).ValueGeneratedNever();
            entity.HasIndex(row => row.Lookup).HasDatabaseName("ix_lookup");
        });
        builder.Entity<BoundaryKey>(entity =>
        {
            entity.ToTable("BoundaryKeys");
            entity.HasKey(row => row.CredentialId);
            entity.Property(row => row.CredentialId).ValueGeneratedNever();
        });
        builder.Entity<BinaryOwner>(entity =>
        {
            entity.ToTable("BinaryOwners");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
        });
        builder.Entity<BinaryChild>(entity =>
        {
            entity.ToTable("BinaryChildren");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.HasOne<BinaryOwner>().WithMany().HasForeignKey(row => row.OwnerKey).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

internal sealed class BinaryCapture : DbCommandInterceptor
{
    private string _sql = "";
    private (DbType Type, byte[] Value)[] _parameters = [];
    private int _readers;

    internal void Reset() { _sql = ""; _parameters = []; _readers = 0; }
    internal void RequireBinary(byte[] expected, bool exists)
    {
        Matrix.Require(_readers == 1 && _sql.Contains("WHERE", StringComparison.OrdinalIgnoreCase)
            && (!exists || _sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase)),
            exists ? "single server-side correlated EXISTS command" : "single server-side parameterized WHERE command");
        Matrix.Require(_parameters.Any(item => item.Type == DbType.Binary && item.Value.AsSpan().SequenceEqual(expected)),
            "actual ADO parameter is Binary with exact byte[] contents");
        Console.WriteLine("SQL " + _sql.Replace('\r', ' ').Replace('\n', ' '));
        Console.WriteLine($"BINARY_PARAMETER length={expected.Length} sha256={Convert.ToHexString(SHA256.HashData(expected))}");
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Capture(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Capture(command);
        return ValueTask.FromResult(result);
    }

    private void Capture(DbCommand command)
    {
        _readers++;
        _sql = command.CommandText;
        _parameters = command.Parameters.Cast<DbParameter>().Where(parameter => parameter.Value is byte[])
            .Select(parameter => (parameter.DbType, ((byte[])parameter.Value!).ToArray())).ToArray();
    }
}

internal sealed class User
{
    public Guid Id { get; set; }
    public string NormalizedUserName { get; set; } = "";
    public List<Passkey> Passkeys { get; set; } = [];
}

internal sealed class Passkey
{
    public byte[] CredentialId { get; set; } = [];
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public byte[]? Payload { get; set; }
    public byte[]? Lookup { get; set; }
}

internal sealed class BoundaryKey
{
    public byte[] CredentialId { get; set; } = [];
    public string Label { get; set; } = "";
}

internal sealed class BinaryOwner
{
    public byte[] Id { get; set; } = [];
}

internal sealed class BinaryChild
{
    public int Id { get; set; }
    public byte[] OwnerKey { get; set; } = [];
}
