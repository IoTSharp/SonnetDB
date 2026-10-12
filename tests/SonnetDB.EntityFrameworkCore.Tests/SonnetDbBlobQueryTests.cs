using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.EntityFrameworkCore.Extensions;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

/// <summary>通过实际 ADO 参数和真实 Server 验证 #271 的 Passkey 二进制查询合同。</summary>
public sealed class SonnetDbBlobQueryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-ef-blob-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] _credential = [0, 1, 127, 128, 255];

    /// <summary>嵌入式 EF 等值和相关 SequenceEqual 在更新、回滚、删除及重开后保留正确内容语义。</summary>
    [Fact]
    public async Task BinaryPredicates_Embedded_PreserveTypeAndLifecycle()
    {
        string connectionString = $"Data Source={_root}";
        await PrepareAndExerciseAsync(connectionString);
        await VerifyPersistedAsync(connectionString);
    }

    /// <summary>真实 REST 与精确 h2c Server 执行二进制谓词，并在宿主停止和重新打开后读取持久化内容。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryPredicates_Remote_PreserveTypeAndLifecycle(bool frameHttp2)
    {
        const string token = "ef-blob-local-admin";
        const string database = "blob_passkeys";
        var requests = new ConcurrentQueue<(string Path, string Protocol)>();
        var options = new ServerOptions
        {
            DataRoot = _root,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [token] = ServerRoles.Admin },
        };
        await using (var app = BuildServer(options, frameHttp2, requests))
        {
            try
            {
                await app.StartAsync();
                await PrepareAndExerciseAsync(ConnectionString(app, database, token, frameHttp2));
            }
            finally
            {
                await app.StopAsync();
            }
        }
        await using (var app = BuildServer(options, frameHttp2, requests))
        {
            try
            {
                await app.StartAsync();
                await VerifyPersistedAsync(ConnectionString(app, database, token, frameHttp2));
            }
            finally
            {
                await app.StopAsync();
            }
        }
        var observed = requests.ToArray();
        Assert.Contains(observed, request => request.Path == $"/v1/db/{database}/sql");
        if (frameHttp2)
        {
            Assert.All(observed, request => Assert.Equal("HTTP/2", request.Protocol));
            Assert.Contains(observed, request => request.Path.Contains("frame", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.All(observed, request => Assert.Equal("HTTP/1.1", request.Protocol));
        }
    }

    private static WebApplication BuildServer(
        ServerOptions options, bool frameHttp2, ConcurrentQueue<(string Path, string Protocol)> requests)
    {
        var app = EfTestServerHost.Build(options,
            extraArgs: frameHttp2 ? ["--Kestrel:Endpoints:Http:Protocols=Http2"] : null);
        app.Use(async (context, next) =>
        {
            requests.Enqueue((context.Request.Path.Value ?? string.Empty, context.Request.Protocol));
            await next(context);
        });
        return app;
    }

    private static string ConnectionString(WebApplication app, string database, string token, bool frameHttp2)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        return $"Data Source=sonnetdb+http://{new Uri(Assert.Single(addresses.Addresses)).Authority}/{database};" +
            $"Token={token};Timeout=30;Protocol={(frameHttp2 ? "frame-http2" : "rest")}";
    }

    private static PasskeyContext CreateContext(string connectionString, BinaryParameterCapture? capture = null)
    {
        var builder = new DbContextOptionsBuilder<PasskeyContext>().UseSonnetDB(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (capture is not null)
            builder.AddInterceptors(capture);
        return new PasskeyContext(builder.Options);
    }

    private static async Task PrepareAndExerciseAsync(string connectionString)
    {
        var capture = new BinaryParameterCapture();
        await using var context = CreateContext(connectionString, capture);
        Assert.True(await context.Database.EnsureCreatedAsync());
        context.Users.AddRange(new User { Id = "alice" }, new User { Id = "bob" });
        await context.SaveChangesAsync();
        context.Passkeys.AddRange(
            new Passkey { CredentialId = _credential.ToArray(), UserId = "alice", Name = "original", Payload = [0, 255], Lookup = _credential.ToArray() },
            new Passkey { CredentialId = [9, 8, 7], UserId = "bob", Name = "other", Payload = null },
            new Passkey { CredentialId = [], UserId = "alice", Name = "empty", Payload = [] },
            new Passkey { CredentialId = [0], UserId = "bob", Name = "zero", Payload = [0] });
        Assert.Equal(4, await context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        await AssertAccessAsync(context, "SELECT \"UserId\" FROM \"Passkeys\" WHERE \"CredentialId\" = @key", "primary_key");
        await AssertAccessAsync(context, "SELECT \"UserId\" FROM \"Passkeys\" WHERE \"Lookup\" = @key", "secondary_index");
        await AssertAccessAsync(context, "SELECT EXISTS (SELECT 1 FROM \"Passkeys\" WHERE \"Lookup\" = @key)", "secondary_index", standaloneExists: true);
        byte[] indexedLookup = _credential.ToArray();
        Assert.Equal("alice", await context.Users.AsNoTracking()
            .Where(user => user.Passkeys.Any(passkey => passkey.Lookup!.SequenceEqual(indexedLookup)))
            .Select(user => user.Id).SingleAsync());
        AssertBinaryQuery(capture, indexedLookup, exists: true, column: "Lookup");

        byte[][] lookups = [_credential.ToArray(), [9, 8, 7], [], [0], [4, 3, 2], [0, 1, 127, 128], [0, 1, 127, 128, 254]];
        string?[] expectedUsers = ["alice", "bob", "alice", "bob", null, null, null];
        for (int index = 0; index < lookups.Length; index++)
        {
            byte[] lookup = lookups[index];
            capture.Reset();
            var passkeys = await context.Passkeys.AsNoTracking()
                .Where(passkey => passkey.CredentialId == lookup).ToListAsync();
            Assert.Equal(expectedUsers[index], passkeys.SingleOrDefault()?.UserId);
            AssertBinaryQuery(capture, lookup, exists: false);
            capture.Reset();
            var owners = await context.Users.AsNoTracking()
                .Where(user => user.Passkeys.Any(passkey => passkey.CredentialId.SequenceEqual(lookup)))
                .OrderBy(user => user.Id).Select(user => user.Id).ToListAsync();
            Assert.Equal(expectedUsers[index] is { } owner ? [owner] : Array.Empty<string>(), owners);
            AssertBinaryQuery(capture, lookup, exists: true);
        }

        // 明确检查 DbType.Binary + DBNull 的 SQL 三值逻辑，避免 EF 将空参数优化为 IS NULL 掩盖绑定缺口。
        await context.Database.OpenConnectionAsync();
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT \"UserId\" FROM \"Passkeys\" WHERE \"Payload\" = @payload";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "payload";
            parameter.DbType = DbType.Binary;
            parameter.Value = DBNull.Value;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.False(await reader.ReadAsync());
        }
        Assert.Equal("bob", await context.Passkeys.AsNoTracking().Where(passkey => passkey.Payload == null).Select(passkey => passkey.UserId).SingleAsync());

        var tracked = await context.Passkeys.SingleAsync(passkey => passkey.CredentialId == _credential);
        tracked.Name = "updated";
        byte[] largePayload = Enumerable.Range(0, 65_536).Select(index => (byte)index).ToArray();
        tracked.Payload = largePayload;
        Assert.Equal(1, await context.SaveChangesAsync());
        capture.Reset();
        Assert.Equal("alice", await context.Passkeys.AsNoTracking().Where(passkey => passkey.Payload == largePayload)
            .Select(passkey => passkey.UserId).SingleAsync());
        AssertBinaryQuery(capture, largePayload, exists: false, column: "Payload");

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            tracked.Name = "rolled-back";
            Assert.Equal(1, await context.SaveChangesAsync());
            await transaction.RollbackAsync();
        }
        context.ChangeTracker.Clear();
        Assert.Equal("updated", await context.Passkeys.AsNoTracking().Where(passkey => passkey.CredentialId == _credential)
            .Select(passkey => passkey.Name).SingleAsync());
        byte[] empty = [];
        context.Passkeys.Remove(await context.Passkeys.SingleAsync(passkey => passkey.CredentialId == empty));
        Assert.Equal(1, await context.SaveChangesAsync());
        Assert.Empty(await context.Users.AsNoTracking().Where(user => user.Passkeys.Any(passkey => passkey.CredentialId.SequenceEqual(empty))).ToListAsync());
        await context.Database.CloseConnectionAsync();
    }

    private static async Task VerifyPersistedAsync(string connectionString)
    {
        var capture = new BinaryParameterCapture();
        await using var context = CreateContext(connectionString, capture);
        byte[] lookup = _credential.ToArray();
        var passkey = await context.Passkeys.AsNoTracking().SingleAsync(passkey => passkey.CredentialId == lookup);
        Assert.Equal("updated", passkey.Name);
        Assert.Equal(65_536, passkey.Payload!.Length);
        Assert.Equal(255, passkey.Payload[255]);
        AssertBinaryQuery(capture, lookup, exists: false);
        Assert.Equal("alice", await context.Users.AsNoTracking()
            .Where(user => user.Passkeys.Any(passkey => passkey.CredentialId.SequenceEqual(lookup)))
            .Select(user => user.Id).SingleAsync());
        AssertBinaryQuery(capture, lookup, exists: true);
        Assert.Equal(3, await context.Passkeys.CountAsync());
    }

    private static void AssertBinaryQuery(BinaryParameterCapture capture, byte[] value, bool exists, string column = "CredentialId")
    {
        Assert.Contains("WHERE", capture.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(column, capture.CommandText, StringComparison.Ordinal);
        if (exists)
            Assert.Contains("EXISTS", capture.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(capture.Parameters, parameter => parameter.Type == DbType.Binary && parameter.Value.AsSpan().SequenceEqual(value));
    }

    private static async Task AssertAccessAsync(PasskeyContext context, string sql, string expectedPath, bool standaloneExists = false)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN ANALYZE " + sql;
        command.CommandTimeout = 30;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.DbType = DbType.Binary;
        parameter.Value = _credential.ToArray();
        command.Parameters.Add(parameter);
        var plan = new Dictionary<string, string?>();
        await using var reader = await command.ExecuteReaderAsync();
        for (int row = 0; row < 64; row++)
        {
            if (!await reader.ReadAsync())
                break;
            plan[reader.GetString(0)] = reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
        }
        Assert.Equal(expectedPath, plan["access_path"]);
        Assert.Equal("1", plan["actual_rows"]);
        if (!standaloneExists)
        {
            Assert.Equal(expectedPath, plan["actual_access_path"]);
            Assert.Equal("1", plan["actual_examined_rows"]);
        }
    }

    private sealed class BinaryParameterCapture : DbCommandInterceptor
    {
        public string CommandText { get; private set; } = string.Empty;
        public (DbType Type, byte[] Value)[] Parameters { get; private set; } = [];
        public void Reset()
        {
            CommandText = string.Empty;
            Parameters = [];
        }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }
        private void Capture(DbCommand command)
        {
            Assert.IsType<SndbCommand>(command);
            CommandText = command.CommandText;
            Parameters = command.Parameters.Cast<DbParameter>().Where(parameter => parameter.Value is byte[])
                .Select(parameter => (parameter.DbType, ((byte[])parameter.Value!).ToArray())).ToArray();
        }
    }

    private sealed class PasskeyContext(DbContextOptions<PasskeyContext> options) : DbContext(options)
    {
        public DbSet<User> Users => Set<User>();
        public DbSet<Passkey> Passkeys => Set<Passkey>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("Users");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.Id).HasColumnType("STRING").ValueGeneratedNever();
                entity.HasMany(user => user.Passkeys).WithOne().HasForeignKey(passkey => passkey.UserId);
            });
            modelBuilder.Entity<Passkey>(entity =>
            {
                entity.ToTable("Passkeys");
                entity.HasKey(passkey => passkey.CredentialId);
                entity.Property(passkey => passkey.CredentialId).HasColumnType("BLOB").ValueGeneratedNever();
                entity.Property(passkey => passkey.UserId).HasColumnType("STRING").IsRequired();
                entity.Property(passkey => passkey.Name).HasColumnType("STRING").IsRequired();
                entity.Property(passkey => passkey.Payload).HasColumnType("BLOB");
                entity.Property(passkey => passkey.Lookup).HasColumnType("BLOB");
                entity.HasIndex(passkey => passkey.Lookup).HasDatabaseName("ix_passkeys_lookup");
            });
        }
    }

    private sealed class User
    {
        public string Id { get; set; } = string.Empty;
        public List<Passkey> Passkeys { get; set; } = [];
    }

    private sealed class Passkey
    {
        public byte[] CredentialId { get; set; } = [];
        public string UserId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public byte[]? Payload { get; set; }
        public byte[]? Lookup { get; set; }
    }

    /// <summary>回收本测试独占的数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
