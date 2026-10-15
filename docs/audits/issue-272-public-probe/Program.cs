using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
Console.InputEncoding = new System.Text.UTF8Encoding(false);

if (args.Length != 3 || args[0] is not ("SonnetDB" or "Sqlite"))
{
    Console.Error.WriteLine("Usage: Consumer SonnetDB|Sqlite initial|removed|replacement|rollback|reopen database-root");
    return 2;
}

string provider = args[0];
string phase = args[1];
string root = Path.GetFullPath(args[2]);
Directory.CreateDirectory(root);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
CancellationToken token = deadline.Token;
try
{
    foreach (string name in new[] { "SonnetDB.EntityFrameworkCore", "SonnetDB.Data", "SonnetDB.Core", "Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Sqlite" })
    {
        Assembly assembly = Assembly.Load(name);
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Console.WriteLine($"ASSEMBLY|{name}|{version}|{assembly.Location}");
        if (name.StartsWith("SonnetDB", StringComparison.Ordinal))
            Check(version.Contains("1af23d8f299febfdf66b040ab3419980396acf46", StringComparison.Ordinal), "public-source-commit-" + name);
    }
    await using var db = new MigrationContext(provider, root);
    IMigrator migrator = db.GetService<IMigrator>();
    switch (phase)
    {
        case "initial":
            Check(!(await db.Database.GetAppliedMigrationsAsync(token)).Any(), "fresh-history");
            string create = migrator.GenerateScript(Migration.InitialDatabase, Initial.Id);
            Console.WriteLine("INITIAL-SCRIPT\n" + create);
            Check(create.Contains("CONSTRAINT " + Quote(Names.Original) + " FOREIGN KEY", StringComparison.Ordinal), "initial-script-name");
            await migrator.MigrateAsync(Initial.Id, token);
            await Schema(db, Names.Original, Names.Authors);
            db.Authors.AddRange(new Author { Id = 1 }, new Author { Id = 2 });
            db.ReplacementAuthors.AddRange(new ReplacementAuthor { Id = 1 }, new ReplacementAuthor { Id = 3 });
            await db.SaveChangesAsync(token);
            db.Books.Add(new Book { Id = 1, AuthorId = 1 });
            await db.SaveChangesAsync(token);
            await RejectOrphan(db, 90, 3, Names.Original);
            await History(db, Initial.Id);
            await Books(db, (1, 1));
            break;
        case "removed":
            await Schema(db, Names.Original, Names.Authors);
            await History(db, Initial.Id);
            await Books(db, (1, 1));
            await RejectOrphan(db, 91, 3, Names.Original);
            if (provider == "SonnetDB")
            {
                var missing = db.GetService<IMigrationsSqlGenerator>().Generate(
                    [new DropForeignKeyOperation { Table = Names.Books, Name = "Missing_约束" }]);
                Check(missing.Count == 1, "missing-drop-single-command");
                Console.WriteLine("MISSING-DROP-SQL\n" + missing[0].CommandText);
                Check(await db.Database.ExecuteSqlRawAsync(missing[0].CommandText, token) == 0, "missing-drop-returns-zero");
                await Schema(db, Names.Original, Names.Authors);
                await RejectOrphan(db, 92, 3, Names.Original);
                await History(db, Initial.Id);
            }
            await migrator.MigrateAsync(Removed.Id, token);
            await Schema(db, null, null);
            db.Books.Add(new Book { Id = 2, AuthorId = 3 });
            await db.SaveChangesAsync(token);
            await Books(db, (1, 1), (2, 3));
            await History(db, Initial.Id, Removed.Id);
            break;
        case "replacement":
            await Schema(db, null, null);
            await Books(db, (1, 1), (2, 3));
            await History(db, Initial.Id, Removed.Id);
            await migrator.MigrateAsync(Replaced.Id, token);
            await Schema(db, Names.Replacement, Names.ReplacementAuthors);
            await RejectOrphan(db, 93, 2, Names.Replacement);
            db.Books.Add(new Book { Id = 3, AuthorId = 3 });
            await db.SaveChangesAsync(token);
            await Books(db, (1, 1), (2, 3), (3, 3));
            db.ChangeTracker.Clear();
            db.ReplacementAuthors.Remove(await db.ReplacementAuthors.SingleAsync(row => row.Id == 3, token));
            await db.SaveChangesAsync(token);
            await Books(db, (1, 1));
            await History(db, Initial.Id, Removed.Id, Replaced.Id);
            break;
        case "rollback":
            await Schema(db, Names.Replacement, Names.ReplacementAuthors);
            await Books(db, (1, 1));
            await History(db, Initial.Id, Removed.Id, Replaced.Id);
            await RejectOrphan(db, 94, 2, Names.Replacement);
            await migrator.MigrateAsync(Removed.Id, token);
            await Schema(db, null, null);
            db.Books.Add(new Book { Id = 4, AuthorId = 99 });
            await db.SaveChangesAsync(token);
            await Books(db, (1, 1), (4, 99));
            db.ChangeTracker.Clear();
            db.Books.Remove(await db.Books.SingleAsync(row => row.Id == 4, token));
            await db.SaveChangesAsync(token);
            await migrator.MigrateAsync(Initial.Id, token);
            await Schema(db, Names.Original, Names.Authors);
            await RejectOrphan(db, 95, 3, Names.Original);
            await History(db, Initial.Id);
            await Books(db, (1, 1));
            break;
        case "reopen":
            await Schema(db, Names.Original, Names.Authors);
            await History(db, Initial.Id);
            await Books(db, (1, 1));
            await RejectOrphan(db, 96, 3, Names.Original);
            await migrator.MigrateAsync(Migration.InitialDatabase, token);
            await History(db);
            await AssertTablesRemoved(db);
            db.ChangeTracker.Clear();
            await migrator.MigrateAsync(Initial.Id, token);
            await Schema(db, Names.Original, Names.Authors);
            await RejectOrphan(db, 97, 1, Names.Original);
            db.Authors.Add(new Author { Id = 10 });
            await db.SaveChangesAsync(token);
            db.Books.Add(new Book { Id = 10, AuthorId = 10 });
            await db.SaveChangesAsync(token);
            await migrator.MigrateAsync(Initial.Id, token);
            await Books(db, (10, 10));
            await History(db, Initial.Id);
            break;
        default:
            throw new ArgumentException("Unknown phase: " + phase);
    }
    Console.WriteLine($"COMPLETE|{provider}|{phase}|pid={Environment.ProcessId}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

void Check(bool condition, string check)
{
    if (!condition) throw new InvalidOperationException("Assertion failed: " + check);
    Console.WriteLine($"PASS|{provider}|{phase}|{check}");
}

async Task Books(MigrationContext db, params (long Id, long AuthorId)[] expected)
{
    var rows = await db.Books.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token);
    Check(rows.Select(row => (row.Id, row.AuthorId)).SequenceEqual(expected), "rows=" + string.Join(';', expected));
}

async Task History(MigrationContext db, params string[] expected)
{
    string[] actual = (await db.Database.GetAppliedMigrationsAsync(token)).ToArray();
    Check(actual.SequenceEqual(expected), "history=" + string.Join(',', expected));
}

async Task RejectOrphan(MigrationContext db, long id, long author, string expectedName)
{
    db.Books.Add(new Book { Id = id, AuthorId = author });
    DbUpdateException? failure = null;
    try { await db.SaveChangesAsync(token); }
    catch (DbUpdateException error) { failure = error; }
    finally { db.ChangeTracker.Clear(); }
    Check(failure is not null, "reject-orphan-" + id);
    if (provider == "SonnetDB") Check(failure!.ToString().Contains(expectedName, StringComparison.Ordinal), "error-names-constraint");
    else Check(failure!.ToString().Contains("FOREIGN KEY constraint failed", StringComparison.Ordinal), "sqlite-fk-violation");
    Check(!await db.Books.AnyAsync(row => row.Id == id, token), "rejected-row-absent");
}

async Task Schema(MigrationContext db, string? expectedName, string? principal)
{
    await db.Database.OpenConnectionAsync(token);
    try
    {
        DbConnection connection = db.Database.GetDbConnection();
        if (provider == "SonnetDB")
        {
            var rows = connection.GetSchema("ForeignKeys", [null, null, Names.Books, null]).Rows;
            Check(rows.Count == (expectedName is null ? 0 : 1), "catalog-fk-count");
            if (expectedName is not null)
            {
                var row = rows[0];
                Check(Equals(row["CONSTRAINT_NAME"], expectedName), "catalog-exact-name=" + expectedName);
                Check(Equals(row["TABLE_NAME"], Names.Books) && Equals(row["COLUMN_NAME"], Names.AuthorId), "catalog-child-columns");
                Check(Equals(row["PRINCIPAL_TABLE_NAME"], principal) && Equals(row["PRINCIPAL_COLUMN_NAME"], "Id"), "catalog-principal=" + principal);
                Check(Equals(row["ON_DELETE"], "Cascade"), "catalog-cascade");
            }
        }
        else
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_key_list(" + Quote(Names.Books) + ")";
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                int count = 0;
                while (await reader.ReadAsync(token))
                {
                    count++;
                    Check(expectedName is not null && reader.GetString(2) == principal
                        && reader.GetString(3) == Names.AuthorId && reader.GetString(4) == "Id"
                        && reader.GetString(6) == "CASCADE", "sqlite-fk-columns-and-action");
                }
                Check(count == (expectedName is null ? 0 : 1), "sqlite-fk-count");
            }
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@name";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = Names.Books;
            command.Parameters.Add(parameter);
            string sql = (string)(await command.ExecuteScalarAsync(token))!;
            Console.WriteLine("SQLITE-CATALOG-SQL\n" + sql);
            Check(expectedName is null ? !sql.Contains("FOREIGN KEY", StringComparison.Ordinal)
                : sql.Contains("CONSTRAINT " + Quote(expectedName) + " FOREIGN KEY", StringComparison.Ordinal), "sqlite-exact-persisted-name");
        }
    }
    finally { await db.Database.CloseConnectionAsync(); }
}

async Task AssertTablesRemoved(MigrationContext db)
{
    await db.Database.OpenConnectionAsync(token);
    try
    {
        DbConnection connection = db.Database.GetDbConnection();
        foreach (string name in new[] { Names.Books, Names.Authors, Names.ReplacementAuthors })
        {
            if (provider == "SonnetDB")
                Check(connection.GetSchema("Tables", [null, null, name, null]).Rows.Count == 0, "drop-all-table=" + name);
            else
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@name";
                parameter.Value = name;
                command.Parameters.Add(parameter);
                Check(Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 0, "drop-all-table=" + name);
            }
        }
    }
    finally { await db.Database.CloseConnectionAsync(); }
}

static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

internal static class Names
{
    internal const string Books = "A06 书\"籍";
    internal const string Authors = "A06 作\"者";
    internal const string ReplacementAuthors = "A06 替\"代作者";
    internal const string AuthorId = "Author ID";
    internal const string Original = "FK_书\"籍_作者";
    internal const string Replacement = "FK_替\"代_作者";
}

internal sealed class MigrationContext(string provider, string root) : DbContext
{
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<ReplacementAuthor> ReplacementAuthors => Set<ReplacementAuthor>();
    public DbSet<Book> Books => Set<Book>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (provider == "SonnetDB") options.UseSonnetDB("Data Source=" + Path.Combine(root, "sonnetdb"));
        else options.UseSqlite("Data Source=" + Path.Combine(root, "reference.db") + ";Pooling=False;Default Timeout=2");
    }
    protected override void OnModelCreating(ModelBuilder builder) => Models.Configure(builder, 2);
}

internal static class Models
{
    internal static void Configure(ModelBuilder builder, int stage)
    {
        builder.HasAnnotation("ProductVersion", "10.0.12");
        builder.Entity<Author>(entity =>
        {
            entity.ToTable(Names.Authors);
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnType("INT").ValueGeneratedNever();
        });
        builder.Entity<ReplacementAuthor>(entity =>
        {
            entity.ToTable(Names.ReplacementAuthors);
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnType("INT").ValueGeneratedNever();
        });
        builder.Entity<Book>(entity =>
        {
            entity.ToTable(Names.Books);
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnType("INT").ValueGeneratedNever();
            entity.Property(row => row.AuthorId).HasColumnName(Names.AuthorId).HasColumnType("INT");
            entity.HasIndex(row => row.AuthorId).HasDatabaseName("IX_A06_AuthorId");
            if (stage == 0) entity.HasOne<Author>().WithMany().HasForeignKey(row => row.AuthorId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName(Names.Original);
            if (stage == 2) entity.HasOne<ReplacementAuthor>().WithMany().HasForeignKey(row => row.AuthorId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName(Names.Replacement);
        });
    }
}

internal sealed class Author { public long Id { get; set; } }
internal sealed class ReplacementAuthor { public long Id { get; set; } }
internal sealed class Book { public long Id { get; set; } public long AuthorId { get; set; } }

/// <summary>创建带命名外键的初始模型。</summary>
[DbContext(typeof(MigrationContext))]
[Migration(Id)]
public sealed class Initial : Migration
{
    internal const string Id = "20261016010000_Initial";
    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => Models.Configure(modelBuilder, 0);
    /// <inheritdoc />
    protected override void Up(MigrationBuilder builder)
    {
        builder.CreateTable(Names.Authors, table => new { Id = table.Column<long>(type: "INT", nullable: false) }, constraints: table => table.PrimaryKey("PK_A06Authors", row => row.Id));
        builder.CreateTable(Names.ReplacementAuthors, table => new { Id = table.Column<long>(type: "INT", nullable: false) }, constraints: table => table.PrimaryKey("PK_A06ReplacementAuthors", row => row.Id));
        builder.CreateTable(Names.Books, table => new
        {
            Id = table.Column<long>(type: "INT", nullable: false),
            AuthorId = table.Column<long>(name: Names.AuthorId, type: "INT", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_A06Books", row => row.Id);
            table.ForeignKey(Names.Original, row => row.AuthorId, Names.Authors, "Id", onDelete: ReferentialAction.Cascade);
        });
        builder.CreateIndex("IX_A06_AuthorId", Names.Books, Names.AuthorId);
    }
    /// <inheritdoc />
    protected override void Down(MigrationBuilder builder)
    {
        builder.DropTable(Names.Books);
        builder.DropTable(Names.ReplacementAuthors);
        builder.DropTable(Names.Authors);
    }
}

/// <summary>升级移除外键，回退恢复原外键。</summary>
[DbContext(typeof(MigrationContext))]
[Migration(Id)]
public sealed class Removed : Migration
{
    internal const string Id = "20261016020000_Removed";
    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => Models.Configure(modelBuilder, 1);
    /// <inheritdoc />
    protected override void Up(MigrationBuilder builder) => builder.DropForeignKey(Names.Original, Names.Books);
    /// <inheritdoc />
    protected override void Down(MigrationBuilder builder) => builder.AddForeignKey(Names.Original, Names.Books, Names.AuthorId, Names.Authors, principalColumn: "Id", onDelete: ReferentialAction.Cascade);
}

/// <summary>升级创建替换外键，回退移除替换关系。</summary>
[DbContext(typeof(MigrationContext))]
[Migration(Id)]
public sealed class Replaced : Migration
{
    internal const string Id = "20261016030000_Replaced";
    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => Models.Configure(modelBuilder, 2);
    /// <inheritdoc />
    protected override void Up(MigrationBuilder builder) => builder.AddForeignKey(Names.Replacement, Names.Books, Names.AuthorId, Names.ReplacementAuthors, principalColumn: "Id", onDelete: ReferentialAction.Cascade);
    /// <inheritdoc />
    protected override void Down(MigrationBuilder builder) => builder.DropForeignKey(Names.Replacement, Names.Books);
}

/// <summary>最终目标模型快照，供 EF 校验真实迁移模型。</summary>
[DbContext(typeof(MigrationContext))]
public sealed class FinalSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(ModelBuilder modelBuilder) => Models.Configure(modelBuilder, 2);
}
