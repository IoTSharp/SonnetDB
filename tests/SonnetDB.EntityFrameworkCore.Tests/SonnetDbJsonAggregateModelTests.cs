using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using SonnetDB.EntityFrameworkCore.Extensions;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

/// <summary>
/// 验证 ToJson 的部分支持：模型与迁移生成可用，聚合查询和写入明确拒绝。
/// </summary>
public sealed class SonnetDbJsonAggregateModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToJson_WithoutExplicitColumnType_CreatesDesignAndRuntimeJsonContainer(bool owned)
    {
        using var context = CreateContext(owned);

        AssertJsonContainer(context.GetService<IDesignTimeModel>().Model);
        AssertJsonContainer(context.Model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToJson_InitialMigration_GeneratesNativeJsonColumn(bool owned)
    {
        using var context = CreateContext(owned);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel());
        var table = Assert.Single(operations.OfType<CreateTableOperation>());
        var container = Assert.Single(table.Columns, column => column.Name == "Data");

        Assert.Equal("JSON", container.ColumnType);
        Assert.DoesNotContain(table.Columns, column => column.Name == "DisplayName");
        var migrationSql = string.Join("\n", context.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, model).Select(command => command.CommandText));
        Assert.Contains("\"Data\" JSON", migrationSql);
        Assert.Contains("\"Data\" JSON", context.Database.GenerateCreateScript());
    }

    [Fact]
    public void TypeMapping_OrdinaryStringAndExplicitRawJson_PreservesExistingScalarBehavior()
    {
        using var context = new ScalarContext(CreateOptions<ScalarContext>());
        var mappings = context.GetService<IRelationalTypeMappingSource>();

        Assert.IsType<StringTypeMapping>(mappings.FindMapping(typeof(string)));
        Assert.IsType<StringTypeMapping>(mappings.FindMapping("TEXT"));
        Assert.IsType<StringTypeMapping>(mappings.FindMapping("JSON"));
        Assert.IsAssignableFrom<JsonTypeMapping>(mappings.FindMapping(typeof(JsonTypePlaceholder)));
        var querySql = context.Set<ScalarRecord>().Select(record => new { record.Name, record.RawJson }).ToQueryString();
        Assert.Contains("\"Name\"", querySql);
        Assert.Contains("\"RawJson\"", querySql);
        Assert.Contains("\"RawJson\" STRING", context.Database.GenerateCreateScript());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToJson_AggregateAndPathQueries_RejectsBeforeDatabaseExecution(bool owned)
    {
        var blocker = new ConnectionBlocker();
        using var context = CreateContext(owned, blocker);

        AssertUnsupported(() => context.Set<Passkey>().ToQueryString());
        AssertUnsupported(() => context.Set<Passkey>().Select(passkey => passkey.Data.DisplayName).ToQueryString());
        AssertUnsupported(() => context.Set<Passkey>()
            .Where(passkey => passkey.Data.DisplayName == "changed").Select(passkey => passkey.Id).ToQueryString());
        Assert.Equal(0, blocker.OpenAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveChanges_JsonAggregateInsert_RejectsBeforeOpeningDatabase(bool owned)
    {
        var blocker = new ConnectionBlocker();
        using var context = CreateContext(owned, blocker);
        context.Add(new Passkey { Id = 1, Data = new PasskeyData { DisplayName = "initial", Counter = 1 } });

        AssertUnsupported(() => context.SaveChanges());
        Assert.Equal(0, blocker.OpenAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveChanges_JsonPathUpdate_RejectsBeforeOpeningDatabase(bool owned)
    {
        var blocker = new ConnectionBlocker();
        using var context = CreateContext(owned, blocker);
        var passkey = new Passkey { Id = 1, Data = new PasskeyData { DisplayName = "initial", Counter = 1 } };
        context.Attach(passkey);
        passkey.Data.DisplayName = "changed";

        AssertUnsupported(() => context.SaveChanges());
        Assert.Equal(0, blocker.OpenAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveChanges_WholeJsonAggregateReplacement_RejectsBeforeOpeningDatabase(bool owned)
    {
        var blocker = new ConnectionBlocker();
        using var context = CreateContext(owned, blocker);
        var passkey = new Passkey { Id = 1, Data = new PasskeyData { DisplayName = "initial", Counter = 1 } };
        context.Attach(passkey);
        passkey.Data.DisplayName = "changed";
        passkey.Data.Counter = 2;

        AssertUnsupported(() => context.SaveChanges());
        Assert.Equal(0, blocker.OpenAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecuteUpdate_JsonPath_RejectsBeforeOpeningDatabase(bool owned)
    {
        var blocker = new ConnectionBlocker();
        using var context = CreateContext(owned, blocker);

        var exception = Assert.ThrowsAny<Exception>(() => context.Set<Passkey>().ExecuteUpdate(setters =>
            setters.SetProperty(passkey => passkey.Data.DisplayName, "changed")));
        if (exception is NotSupportedException)
        {
            Assert.Contains("limited to model and migration generation", exception.Message);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(exception);
            if (owned)
            {
                Assert.Contains("could not be translated", exception.Message);
            }
            else
            {
                Assert.Equal(RelationalStrings.JsonPartialExecuteUpdateNotSupportedByProvider, exception.Message);
            }
        }

        Assert.Equal(0, blocker.OpenAttempts);
    }

    private static void AssertJsonContainer(IModel model)
    {
        var table = Assert.Single(model.GetRelationalModel().Tables);
        var container = Assert.Single(table.Columns, column => column.Name == "Data");
        Assert.Equal("JSON", container.StoreType);
        Assert.IsAssignableFrom<JsonTypeMapping>(container.StoreTypeMapping);
        Assert.DoesNotContain(table.Columns, column => column.Name == "DisplayName");
    }

    private static void AssertUnsupported(Action action)
    {
        var exception = Assert.Throws<NotSupportedException>(action);
        Assert.Contains("limited to model and migration generation", exception.Message);
    }

    private static DbContext CreateContext(bool owned, ConnectionBlocker? blocker = null)
        => owned
            ? new OwnedContext(CreateOptions<OwnedContext>(blocker))
            : new ComplexContext(CreateOptions<ComplexContext>(blocker));

    private static DbContextOptions<T> CreateOptions<T>(ConnectionBlocker? blocker = null) where T : DbContext
    {
        var builder = new DbContextOptionsBuilder<T>()
            .UseSonnetDB("Host=127.0.0.1;Port=1;Database=issue266-model-only;Protocol=rest;Timeout=1");
        if (blocker is not null)
        {
            builder.AddInterceptors(blocker);
        }

        return builder.Options;
    }

    private sealed class ComplexContext(DbContextOptions<ComplexContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Passkey>().ToTable("Passkeys");
            builder.Entity<Passkey>().Property(passkey => passkey.Id).ValueGeneratedNever();
            builder.Entity<Passkey>().ComplexProperty(passkey => passkey.Data, data => data.ToJson());
        }
    }

    private sealed class OwnedContext(DbContextOptions<OwnedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Passkey>().ToTable("Passkeys");
            builder.Entity<Passkey>().Property(passkey => passkey.Id).ValueGeneratedNever();
            builder.Entity<Passkey>().OwnsOne(passkey => passkey.Data, data => data.ToJson("Data"));
        }
    }

    private sealed class ScalarContext(DbContextOptions<ScalarContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<ScalarRecord>().Property(record => record.RawJson).HasColumnType("JSON");
        }
    }

    private sealed class Passkey
    {
        public long Id { get; set; }

        public PasskeyData Data { get; set; } = new();
    }

    private sealed class PasskeyData
    {
        public string DisplayName { get; set; } = string.Empty;

        public int Counter { get; set; }
    }

    private sealed class ScalarRecord
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string RawJson { get; set; } = "{}";
    }

    private sealed class ConnectionBlocker : DbConnectionInterceptor
    {
        public int OpenAttempts { get; private set; }

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            OpenAttempts++;
            throw new InvalidOperationException("The JSON model-only test must not open a database connection.");
        }
    }
}
