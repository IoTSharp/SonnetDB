using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.EntityFrameworkCore.Extensions;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

public sealed class SonnetDbDmlBatchTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public async Task SaveChanges_Writes43ValuesWithExpectedRequestCount(
        bool legacyServer, bool observeCommands, bool autoTransactionDisabled, bool asynchronous)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = deadline.Token;
        await using var fixture = new BatchFixture();
        await fixture.StartAsync(legacyServer, token);
        var observer = new CommandObserver();
        await using var context = observeCommands ? fixture.CreateContext(observer) : fixture.CreateContext();
        if (autoTransactionDisabled)
            context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
        await fixture.CreateTableAsync(context, token);
        fixture.Requests.Clear();
        observer.Executions = 0;

        var timestamp = new DateTime(2026, 11, 24, 1, 2, 3, DateTimeKind.Utc).AddTicks(1234567);
        context.Rows.AddRange(Enumerable.Range(1, 43).Select(id => new AttributeRow
        {
            Id = id,
            Value = "attribute '" + id,
            ObservedAt = timestamp.AddTicks(id),
        }));
        Assert.Equal(43, asynchronous ? await context.SaveChangesAsync(token) : context.SaveChanges());

        var requests = fixture.Requests.ToArray();
        var useBatch = !legacyServer && !observeCommands && !autoTransactionDisabled;
        Assert.Equal(useBatch ? 1 : 0, requests.Count(IsBatchRequest));
        // 单语句远程写入会在每条精确 DATETIME2 请求前探测 SQL v7 能力。
        Assert.Equal(useBatch ? 0 : 86, requests.Count(IsSingleCommandRequest));
        Assert.Equal(observeCommands ? 43 : 0, observer.Executions);

        context.ChangeTracker.Clear();
        var stored = await context.Rows.OrderBy(row => row.Id).ToArrayAsync(token);
        Assert.Equal(43, stored.Length);
        Assert.All(stored, row =>
        {
            Assert.Equal("attribute '" + row.Id, row.Value);
            Assert.Equal(timestamp.AddTicks(row.Id), row.ObservedAt);
        });
    }

    [Fact]
    public async Task SaveChanges_StopsAfterValidPrefixWhenAParameterConverterFails()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = deadline.Token;
        await using var fixture = new BatchFixture();
        await fixture.StartAsync(legacyServer: false, token);
        await using var context = fixture.CreateContext();
        await fixture.CreateTableAsync(context, token);
        await using var transaction = await context.Database.BeginTransactionAsync(token);
        fixture.Requests.Clear();

        var timestamp = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
        context.Rows.AddRange(
            new AttributeRow { Id = 1, Value = "prefix", ObservedAt = timestamp },
            new AttributeRow { Id = 2, Value = "invalid-conversion", ObservedAt = timestamp },
            new AttributeRow { Id = 3, Value = "suffix", ObservedAt = timestamp });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(token));
        Assert.IsType<FormatException>(exception.InnerException);
        Assert.Equal(2, Assert.IsType<AttributeRow>(Assert.Single(exception.Entries).Entity).Id);
        Assert.Single(fixture.Requests, IsBatchRequest);
        var rows = await context.Rows.AsNoTracking().ToArrayAsync(token);
        Assert.Equal("prefix", Assert.Single(rows).Value);
        await transaction.RollbackAsync(token);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    public async Task SaveChanges_StopsAtConcurrencyMismatchUntilEfAllowsContinuation(
        bool explicitTransaction, bool suppressConcurrency, bool asynchronous)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = deadline.Token;
        await using var fixture = new BatchFixture();
        await fixture.StartAsync(legacyServer: false, token);
        var observer = new ConcurrencyObserver(suppressConcurrency, fixture.Requests);
        await using var context = fixture.CreateContext(observer);
        await fixture.CreateTableAsync(context, token);
        var initial = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
        context.Rows.AddRange(
            new AttributeRow { Id = 1, Value = "one", ObservedAt = initial },
            new AttributeRow { Id = 3, Value = "three", ObservedAt = initial });
        await context.SaveChangesAsync(token);
        context.ChangeTracker.Clear();

        await using var transaction = explicitTransaction ? await context.Database.BeginTransactionAsync(token) : null;
        fixture.Requests.Clear();
        foreach (var id in new[] { 1, 2, 3 })
        {
            var row = new AttributeRow { Id = id, Value = "changed", ObservedAt = initial };
            context.Attach(row);
            context.Entry(row).Property(item => item.Value).IsModified = true;
        }

        if (suppressConcurrency)
        {
            Assert.Equal(3, asynchronous ? await context.SaveChangesAsync(token) : context.SaveChanges());
            Assert.NotNull(transaction);
            await transaction.CommitAsync(token);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync(token));
            Assert.Equal(2, Assert.IsType<AttributeRow>(Assert.Single(exception.Entries).Entity).Id);
        }

        Assert.Equal(2, observer.MissingId);
        Assert.Equal(asynchronous, observer.AsyncCallback);
        Assert.Equal(1, observer.RequestsObserved);
        Assert.Equal(suppressConcurrency ? 2 : 1, fixture.Requests.Count(IsBatchRequest));
        Assert.DoesNotContain(fixture.Requests, IsSingleCommandRequest);

        if (transaction is not null && !suppressConcurrency)
        {
            var inTransaction = await context.Rows.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token);
            Assert.Collection(inTransaction, row => Assert.Equal("changed", row.Value),
                row => Assert.Equal("three", row.Value));
            await transaction.RollbackAsync(token);
        }

        await using var verification = fixture.CreateContext();
        var stored = await verification.Rows.OrderBy(row => row.Id).ToArrayAsync(token);
        Assert.Collection(stored, row => Assert.Equal(suppressConcurrency ? "changed" : "one", row.Value),
            row => Assert.Equal(suppressConcurrency ? "changed" : "three", row.Value));
    }

    private static bool IsBatchRequest(string request) => BatchFixture.IsBatchRequest(request);

    private static bool IsSingleCommandRequest(string request) => request.EndsWith("/sql", StringComparison.Ordinal);

    private sealed class BatchFixture : IAsyncDisposable
    {
        private const string LocalToken = "ef-transaction-dml-batch-test";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-ef-dml-batch-" + Guid.NewGuid().ToString("N"));
        private WebApplication? _app;
        private string _connectionString = "";

        public ConcurrentQueue<string> Requests { get; } = new();

        public async Task StartAsync(bool legacyServer, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_root);
            _app = EfTestServerHost.Build(new ServerOptions
            {
                DataRoot = _root,
                Tokens = new Dictionary<string, string> { [LocalToken] = ServerRoles.Admin },
            });
            _app.Use(async (httpContext, next) =>
            {
                var path = httpContext.Request.Path.Value ?? "";
                Requests.Enqueue(path);
                if (legacyServer && path.EndsWith("/sql/transactions", StringComparison.Ordinal))
                {
                    httpContext.Response.OnStarting(() =>
                    {
                        httpContext.Response.Headers.Remove(SqlTransactionDmlBatchProtocol.CapabilityHeader);
                        return Task.CompletedTask;
                    });
                }
                await next(httpContext);
            });
            await _app.StartAsync(cancellationToken);
            var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            var authority = new Uri(Assert.Single(addresses.Addresses)).Authority;
            _connectionString = $"Data Source=sonnetdb+http://{authority}/ef_dml_batch;Token={LocalToken};Timeout=15";
        }

        public BatchContext CreateContext(params IInterceptor[] interceptors)
            => new(new DbContextOptionsBuilder<BatchContext>().UseSonnetDB(_connectionString)
                .AddInterceptors(interceptors).Options);

        public async Task CreateTableAsync(BatchContext context, CancellationToken cancellationToken)
        {
            await context.Database.GetService<IRelationalDatabaseCreator>()
                .CreateAsync(cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "Rows" (
                    "Id" INT NOT NULL,
                    "Value" STRING NOT NULL,
                    "ObservedAt" DATETIME2 NOT NULL,
                    PRIMARY KEY ("Id")
                )
                """, cancellationToken);
        }

        public static bool IsBatchRequest(string request) => request.Contains("/sql/transactions/", StringComparison.Ordinal)
            && request.EndsWith("/sql/batch", StringComparison.Ordinal);

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_app is not null)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        await _app.StopAsync(deadline.Token);
                    }
                    finally
                    {
                        await _app.DisposeAsync();
                    }
                }
            }
            finally
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class BatchContext(DbContextOptions<BatchContext> options) : DbContext(options)
    {
        public DbSet<AttributeRow> Rows => Set<AttributeRow>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AttributeRow>().Property(row => row.Id).ValueGeneratedNever();
            modelBuilder.Entity<AttributeRow>().Property(row => row.ObservedAt).HasColumnType("DATETIME2");
            modelBuilder.Entity<AttributeRow>().Property(row => row.Value)
                .HasConversion(value => ConvertValue(value), value => value);
        }

        private static string ConvertValue(string value) => value == "invalid-conversion"
            ? throw new FormatException("Test value is invalid for parameter conversion.")
            : value;
    }

    private sealed class AttributeRow
    {
        public int Id { get; set; }
        public string Value { get; set; } = "";
        public DateTime ObservedAt { get; set; }
    }

    private sealed class CommandObserver : DbCommandInterceptor
    {
        public int Executions { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Executions++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ConcurrencyObserver(bool suppress, ConcurrentQueue<string> requests) : SaveChangesInterceptor
    {
        public int? MissingId { get; private set; }
        public int? RequestsObserved { get; private set; }
        public bool? AsyncCallback { get; private set; }

        public override InterceptionResult ThrowingConcurrencyException(
            ConcurrencyExceptionEventData eventData, InterceptionResult result)
        {
            AsyncCallback = false;
            return Observe(eventData, result);
        }

        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
            ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            AsyncCallback = true;
            return ValueTask.FromResult(Observe(eventData, result));
        }

        private InterceptionResult Observe(ConcurrencyExceptionEventData eventData, InterceptionResult result)
        {
            MissingId = Assert.IsType<AttributeRow>(Assert.Single(eventData.Entries).Entity).Id;
            RequestsObserved = requests.Count(IsBatchRequest);
            return suppress ? InterceptionResult.Suppress() : result;
        }
    }
}
