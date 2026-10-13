using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

public sealed partial class SonnetDbJsonAggregateModelTests
{
    private const string EscapedOwnedName = "child'.with.dot 😃";

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ToJson_NestedScalarUpdate_EscapesOwnershipAndLeafNames(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using (var context = fixture.CreateEscapedOwnedContext(capture))
        {
            await context.Database.EnsureCreatedAsync(token);
            context.Add(new AggregateRow { Id = 1, Stamp = "initial", Data = InitialAggregate() });
            Assert.Equal(1, await context.SaveChangesAsync(token));
        }
        await using (var context = fixture.CreateEscapedOwnedContext(capture))
        {
            var row = await context.Set<AggregateRow>().SingleAsync(token);
            row.Data.Child.Label = "exact nested property";
            Assert.Equal(1, await context.SaveChangesAsync(token));
            var path = "$['" + EscapedOwnedName.Replace("'", "''", StringComparison.Ordinal)
                + "']['" + ChildLabelJson.Replace("'", "''", StringComparison.Ordinal) + "']";
            Assert.Contains("json_set_value(\"Data\", '" + path.Replace("'", "''", StringComparison.Ordinal) + "', @", capture.CompletedUpdates.Last().Command.Sql);
        }
        await fixture.ReopenAsync(token);
        await using (var context = fixture.CreateEscapedOwnedContext(capture))
        {
            Assert.Equal("exact nested property", (await context.Set<AggregateRow>().SingleAsync(token)).Data.Child.Label);
            using var document = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!);
            Assert.Equal("exact nested property", document.RootElement.GetProperty(EscapedOwnedName).GetProperty(ChildLabelJson).GetString());
            Assert.False(document.RootElement.TryGetProperty("Child", out _));
        }
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ToJson_NestedScalarUpdate_PreservesSiblingsRollsBackAndReopens(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedScalarRowsAsync(fixture, capture, token);
        string before;
        await using (var setup = fixture.CreateContext(true, capture))
        {
            var original = (await ReadRawAsync(setup, 1, token))!;
            using var document = JsonDocument.Parse(original);
            var child = document.RootElement.GetProperty("Child").GetRawText();
            before = original.Replace(child, child[..^1] + ",\"Unknown\":{\"exact\":9007199254740993,\"lexical\":1.2300e+04}}", StringComparison.Ordinal);
            Assert.Equal(1, await setup.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AggregateRows\" SET \"Data\"={before} WHERE \"Id\"=1", token));
        }

        await using (var context = fixture.CreateContext(true, capture))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(token);
            var row = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
            row.Data.Child.Label = "rollback nested";
            Assert.Equal(1, await context.SaveChangesAsync(token));
            Assert.Equal("rollback nested", await context.Set<AggregateRow>().Where(row => row.Id == 1).Select(row => row.Data.Child.Label).SingleAsync(token));
            await using (var outside = fixture.CreateContext(true, new JsonCommandCapture()))
                Assert.Equal(before, await ReadRawAsync(outside, 1, token));
            await transaction.RollbackAsync(token);
        }

        const string replacement = "nested O'Reilly \\\" 中文😃";
        await using (var context = fixture.CreateContext(true, capture))
        {
            Assert.Equal(before, await ReadRawAsync(context, 1, token));
            var row = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
            row.Data.Child.Label = replacement;
            Assert.Equal(1, await context.SaveChangesAsync(token));
            var update = capture.CompletedUpdates.Last();
            Assert.Equal(1, update.Affected);
            var path = "$['Child']['" + ChildLabelJson.Replace("'", "''", StringComparison.Ordinal) + "']";
            Assert.Contains("json_set_value(\"Data\", '" + path.Replace("'", "''", StringComparison.Ordinal) + "', @", update.Command.Sql);
            Assert.Contains(update.Command.Parameters, parameter => Equals(parameter.Value, replacement));
        }

        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateContext(true, capture))
        {
            Assert.Equal(replacement, await reopened.Set<AggregateRow>().Where(row => row.Id == 1).Select(row => row.Data.Child.Label).SingleAsync(token));
            using var previous = JsonDocument.Parse(before);
            using var current = JsonDocument.Parse((await ReadRawAsync(reopened, 1, token))!);
            AssertScalarDocument(previous.RootElement.GetProperty("Child").GetRawText(), current.RootElement.GetProperty("Child").GetRawText(), ChildLabelJson, replacement);
            Assert.All(previous.RootElement.EnumerateObject().Where(property => property.Name != "Child"),
                property => Assert.Equal(property.Value.GetRawText(), current.RootElement.GetProperty(property.Name).GetRawText()));
        }
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ToJson_NestedScalarUpdate_MissingParentRejectsWithoutWrite(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedScalarRowsAsync(fixture, capture, token);
        await using var context = fixture.CreateContext(true, capture);
        var row = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
        const string before = "{\"Child\":null,\"Unknown\":9007199254740993}";
        Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AggregateRows\" SET \"Data\"={before} WHERE \"Id\"=1", token));
        row.Data.Child.Label = "no parent";
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(token));
        Assert.Contains("json_set_value(", capture.Commands.Last().Sql);
        Assert.Empty(capture.CompletedUpdates);
        Assert.Equal(before, await ReadRawAsync(context, 1, token));
        fixture.AssertTransport();
    }

    private sealed class EscapedOwnedContext(DbContextOptions<EscapedOwnedContext> options, Action release)
        : AggregateContext(options, release)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<AggregateRow>().ToTable("AggregateRows");
            builder.Entity<AggregateRow>().Property(row => row.Id).ValueGeneratedNever();
            builder.Entity<AggregateRow>().Property(row => row.Stamp).IsConcurrencyToken();
            builder.Entity<AggregateRow>().OwnsOne(row => row.Data, data =>
            {
                data.ToJson("Data");
                data.OwnsOne(item => item.Child, child =>
                {
                    child.HasJsonPropertyName(EscapedOwnedName);
                    child.Property(item => item.Label).HasJsonPropertyName(ChildLabelJson);
                });
            });
        }
    }
}
