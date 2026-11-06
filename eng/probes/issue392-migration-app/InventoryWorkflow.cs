using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SonnetDB.EntityFrameworkCore.Extensions;

namespace InventoryMigrationApp;

/// <summary>可由独立包消费者及进程恢复套件执行的业务旅程。</summary>
public static class InventoryWorkflow
{
    /// <summary>建立应用上下文，所有迁移均使用实际 Provider。</summary>
    /// <param name="connectionString">嵌入式或测试服务的连接串。</param>
    /// <returns>由调用方释放的库存上下文。</returns>
    public static InventoryContext Open(string connectionString)
        => new(new DbContextOptionsBuilder<InventoryContext>().UseSonnetDB(connectionString)
            .LogTo(Console.WriteLine, [RelationalEventId.CommandExecuting, RelationalEventId.CommandExecuted]).Options);

    /// <summary>执行固定应用动作。</summary>
    /// <param name="action">初始化、升级、回退或验收动作。</param>
    /// <param name="connectionString">应用连接串。</param>
    /// <param name="cancellationToken">本次业务旅程的取消令牌。</param>
    /// <returns>异步动作的完成任务。</returns>
    public static async Task RunAsync(string action, string connectionString, CancellationToken cancellationToken)
    {
        await using var context = Open(connectionString);
        var migrator = context.GetService<IMigrator>();
        switch (action)
        {
            case "initialize":
                await migrator.MigrateAsync(InventoryInitial.Id, cancellationToken);
                await VerifyAsync(context, upgraded: false, cancellationToken);
                break;
            case "upgrade":
                await migrator.MigrateAsync(InventoryShipping.Id, cancellationToken);
                await VerifyAsync(context, upgraded: true, cancellationToken);
                break;
            case "downgrade":
                await migrator.MigrateAsync(InventoryInitial.Id, cancellationToken);
                await VerifyAsync(context, upgraded: false, cancellationToken);
                break;
            case "verify-old":
            case "verify-new":
                await VerifyAsync(context, upgraded: action == "verify-new", cancellationToken);
                break;
            case "roundtrip":
                await migrator.MigrateAsync(InventoryInitial.Id, cancellationToken);
                await VerifyAsync(context, upgraded: false, cancellationToken);
                // Actual history write rejection occurs after all the migration's DDL/DML.
                await context.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"__EFMigrationsHistory\" ADD CONSTRAINT \"CK_BlockShipping\" CHECK (\"MigrationId\" <> '"
                    + InventoryShipping.Id + "')", cancellationToken);
                Exception? failure = null;
                try { await migrator.MigrateAsync(InventoryShipping.Id, cancellationToken); }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { failure = exception; }
                Require(failure?.Message.Contains("CK_BlockShipping", StringComparison.Ordinal) == true,
                    "Expected actual history constraint rejection.");
                Console.WriteLine("EXPECTED_FAILURE " + failure!.GetType().Name + " | " + failure.Message);
                await VerifyAsync(context, upgraded: false, cancellationToken);
                await context.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"__EFMigrationsHistory\" DROP CONSTRAINT \"CK_BlockShipping\"", cancellationToken);
                await migrator.MigrateAsync(InventoryShipping.Id, cancellationToken);
                await migrator.MigrateAsync(InventoryShipping.Id, cancellationToken);
                await VerifyAsync(context, upgraded: true, cancellationToken);
                Console.WriteLine("BUSINESS query-start");
                Product product = await context.Products.SingleAsync(row => row.Id == 10L, cancellationToken);
                Console.WriteLine("BUSINESS save-start");
                product.Stock = 4;
                Require(await context.SaveChangesAsync(cancellationToken) == 1, "Business SaveChanges must update one product.");
                Console.WriteLine("BUSINESS saved");
                context.ChangeTracker.Clear();
                Require(await context.Products.Where(row => row.Id == 10L).Select(row => row.Stock).SingleAsync(cancellationToken) == 4L,
                    "Business stock readback.");
                product = await context.Products.SingleAsync(row => row.Id == 10L, cancellationToken);
                product.Stock = 5;
                await context.SaveChangesAsync(cancellationToken);
                context.ChangeTracker.Clear();
                Console.WriteLine("BUSINESS downgrade-start");
                await migrator.MigrateAsync(InventoryInitial.Id, cancellationToken);
                await migrator.MigrateAsync(InventoryInitial.Id, cancellationToken);
                await VerifyAsync(context, upgraded: false, cancellationToken);
                Console.WriteLine("BUSINESS reupgrade-start");
                await migrator.MigrateAsync(InventoryShipping.Id, cancellationToken);
                await VerifyAsync(context, upgraded: true, cancellationToken);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    /// <summary>核对完整旧/新状态和实际约束，并确认失败后连接仍可使用。</summary>
    /// <param name="context">当前应用上下文。</param>
    /// <param name="upgraded">是否期望完整发货模型。</param>
    /// <param name="cancellationToken">验收取消令牌。</param>
    /// <returns>完整状态核对的完成任务。</returns>
    public static async Task VerifyAsync(InventoryContext context, bool upgraded, CancellationToken cancellationToken)
    {
        var history = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        Require(history.SequenceEqual(upgraded ? [InventoryInitial.Id, InventoryShipping.Id] : [InventoryInitial.Id]), "Migration history.");
        Require(await ScalarAsync(context, "SELECT COUNT(*) FROM \"Categories\"", cancellationToken) is 2L, "Original categories.");
        Require(await ScalarAsync(context, "SELECT \"ParentId\" FROM \"Categories\" WHERE \"Id\"=1", cancellationToken) is 1L, "Self reference.");
        Require(await ScalarAsync(context, "SELECT \"CategoryId\" FROM \"Products\" WHERE \"Id\"=10", cancellationToken) is 2L, "Inbound FK data.");
        Require(Equals(await ScalarAsync(context, "SELECT \"Name\" FROM \"Products\" WHERE \"Id\"=10", cancellationToken), "温度传感器"), "Unicode data.");
        Require(Equals(await ScalarAsync(context, "SELECT \"Stock\" FROM \"Products\" WHERE \"Id\"=10", cancellationToken), upgraded ? 5L : 7L), "Atomic stock.");
        Require((await NamesAsync(context, "DESCRIBE TABLE \"Categories\"", cancellationToken)).Contains("Note") == upgraded, "Atomic column.");
        Require((await NamesAsync(context, "SHOW TABLES", cancellationToken)).Contains("Shipments") == upgraded, "Atomic new table.");
        Require((await NamesAsync(context, "SHOW INDEXES ON \"Products\"", cancellationToken)).Contains("IX_Products_Name") == upgraded, "Atomic index.");
        await RejectAsync(context, "INSERT INTO \"Categories\" (\"Id\",\"ParentId\",\"Name\") VALUES (99,999,'orphan')", "FK_Categories_Parent", cancellationToken);
        await RejectAsync(context, "INSERT INTO \"Products\" (\"Id\",\"CategoryId\",\"Name\",\"Stock\") VALUES (99,999,'orphan',1)", "FK_Products_Category", cancellationToken);
        if (upgraded)
        {
            Require(await ScalarAsync(context, "SELECT COUNT(*) FROM \"Shipments\"", cancellationToken) is 1L, "Seed count.");
            Require(await ScalarAsync(context, "SELECT \"Quantity\" FROM \"Shipments\" WHERE \"Id\"=100", cancellationToken) is 2L, "Seed value.");
            Require(Equals(await ScalarAsync(context, "SELECT \"Note\" FROM \"Categories\" WHERE \"Id\"=1", cancellationToken), "已迁移✓"), "Migrated data.");
            await RejectAsync(context, "UPDATE \"Products\" SET \"Stock\"=-1 WHERE \"Id\"=10", "CK_Products_Stock", cancellationToken);
            await RejectAsync(context, "INSERT INTO \"Shipments\" (\"Id\",\"ProductId\",\"Quantity\") VALUES (99,999,1)", "FK_Shipments_Product", cancellationToken);
        }
        else
        {
            // Prove CHECK removal, then restore the exact baseline on the same connection.
            await context.Database.ExecuteSqlRawAsync("UPDATE \"Products\" SET \"Stock\"=-1 WHERE \"Id\"=10", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("UPDATE \"Products\" SET \"Stock\"=7 WHERE \"Id\"=10", cancellationToken);
        }
        Require(await ScalarAsync(context, "SELECT COUNT(*) FROM \"Products\"", cancellationToken) is 1L, "Connection usable; rejected writes left no rows.");
        Console.WriteLine("VERIFIED " + (upgraded ? "new" : "old") + " | data/schema/index/FK/self-FK/CHECK/history/connection");
    }

    private static async Task RejectAsync(InventoryContext context, string sql, string constraint, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try { await context.Database.ExecuteSqlRawAsync(sql, cancellationToken); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { failure = exception; }
        Require(failure?.Message.Contains(constraint, StringComparison.Ordinal) == true, "Expected constraint " + constraint);
    }

    private static async Task<object?> ScalarAsync(InventoryContext context, string sql, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using DbCommand command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(cancellationToken);
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }

    private static async Task<List<string>> NamesAsync(InventoryContext context, string sql, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using DbCommand command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var names = new List<string>();
            for (var count = 0; count < 32 && await reader.ReadAsync(cancellationToken); count++) names.Add(reader.GetString(0));
            Require(!await reader.ReadAsync(cancellationToken), "Metadata result bound.");
            return names;
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
