using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// #342：关系表多语句、多表业务事务的原子性回归。
///
/// 真进程强杀与恢复边界由 SonnetDB.CrashTests 的固定握手剧本覆盖；本组测试
/// 固定业务前态/后态，确保嵌入式 SQL 入口不会把事务缓冲或语句级保存点误当成
/// 已持久提交。
/// </summary>
public sealed class SqlTransactionAtomicityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-transaction-atomicity-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TransferAcrossAccountsAndLedger_CommitPublishesCompleteStateAfterReopen()
    {
        using (var database = Open())
        {
            CreateBusinessTables(database);
            Execute(database, "INSERT INTO accounts(id, balance) VALUES (1, 100)");

            ExecuteScript(database, """
                BEGIN;
                UPDATE accounts SET balance = balance - 35 WHERE id = 1;
                INSERT INTO ledger(id, account_id, amount) VALUES (1, 1, 35);
                COMMIT;
                """);

            AssertBusinessState(database, balance: 65, ledgerCount: 1, ledgerAmount: 35);
        }

        using var reopened = Open();
        AssertBusinessState(reopened, balance: 65, ledgerCount: 1, ledgerAmount: 35);
    }

    [Fact]
    public void TransferAcrossAccountsAndLedger_RollbackRestoresCompleteBeforeState()
    {
        using (var database = Open())
        {
            CreateBusinessTables(database);
            Execute(database, "INSERT INTO accounts(id, balance) VALUES (1, 100)");

            ExecuteScript(database, """
                BEGIN;
                UPDATE accounts SET balance = balance - 35 WHERE id = 1;
                INSERT INTO ledger(id, account_id, amount) VALUES (1, 1, 35);
                ROLLBACK;
                """);

            AssertBusinessState(database, balance: 100, ledgerCount: 0, ledgerAmount: null);
        }

        using var reopened = Open();
        AssertBusinessState(reopened, balance: 100, ledgerCount: 0, ledgerAmount: null);
    }

    [Fact]
    public void StatementFailure_RollsBackOnlyFailedStatementAndTransactionCanContinue()
    {
        using var database = Open();
        CreateBusinessTables(database);
        Execute(database, "INSERT INTO accounts(id, balance) VALUES (1, 100)");
        Execute(database, "INSERT INTO ledger(id, account_id, amount) VALUES (1, 1, 10)");

        var transaction = new SqlTransactionContext();
        ExecuteStatement(database, transaction, "UPDATE accounts SET balance = balance - 35 WHERE id = 1");

        // Value conversion fails while the statement is being queued, before any
        // mutation is added to the transaction. This is distinct from a COMMIT-time
        // duplicate-key failure, which correctly completes the whole transaction.
        Assert.Throws<FormatException>(() => ExecuteStatement(
            database, transaction, "INSERT INTO ledger(id, account_id, amount) VALUES (2, 1, 'not-an-int')"));

        // The failed statement must not discard the successful debit, and the caller
        // may continue the same transaction after the statement-level rollback.
        ExecuteStatement(database, transaction, "INSERT INTO ledger(id, account_id, amount) VALUES (2, 1, 35)");
        ExecuteStatement(database, transaction, "COMMIT");

        AssertBusinessState(database, balance: 65, ledgerCount: 2, ledgerAmount: null);
        Assert.Equal([10L, 35L], Rows(database,
            "SELECT amount FROM ledger ORDER BY id").Select(static row => (long)row[0]!).ToArray());
    }

    [Fact]
    public void CommitFailureBeforeDecision_RestoresBothTablesAndRepeatedReopenIsStable()
    {
        using (var database = Open())
        {
            CreateBusinessTables(database);
            Execute(database, "INSERT INTO accounts(id, balance) VALUES (1, 100)");

            var transaction = new SqlTransactionContext();
            ExecuteStatement(database, transaction, "UPDATE accounts SET balance = balance - 35 WHERE id = 1");
            ExecuteStatement(database, transaction,
                "INSERT INTO ledger(id, account_id, amount) VALUES (1, 1, 35)");

            database.Tables.ApplyTransactionAfterTableTestHook = static tableName =>
            {
                if (string.Equals(tableName, "ledger", StringComparison.Ordinal))
                    throw new IOException("#342 deterministic commit failure after first table apply.");
            };

            Assert.Throws<IOException>(() => ExecuteStatement(database, transaction, "COMMIT"));
            database.Tables.ApplyTransactionAfterTableTestHook = null;
            AssertBusinessState(database, balance: 100, ledgerCount: 0, ledgerAmount: null);
        }

        for (int reopen = 0; reopen < 2; reopen++)
        {
            using var database = Open();
            AssertBusinessState(database, balance: 100, ledgerCount: 0, ledgerAmount: null);
        }
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
    });

    private static void CreateBusinessTables(Tsdb database)
    {
        Execute(database, "CREATE TABLE accounts(id INT, balance INT, PRIMARY KEY (id))");
        Execute(database, "CREATE TABLE ledger(id INT, account_id INT, amount INT, PRIMARY KEY (id), FOREIGN KEY (account_id) REFERENCES accounts (id))");
    }

    private static void Execute(Tsdb database, string sql)
        => _ = SqlExecutor.Execute(database, sql);

    private static void ExecuteScript(Tsdb database, string sql)
        => _ = SqlExecutor.ExecuteScript(database, sql);

    private static void ExecuteStatement(Tsdb database, SqlTransactionContext transaction, string sql)
        => _ = SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse(sql), null, transaction);

    private static IReadOnlyList<IReadOnlyList<object?>> Rows(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql)).Rows;

    private static void AssertBusinessState(Tsdb database, long balance, int ledgerCount, long? ledgerAmount)
    {
        var account = Assert.Single(Rows(database, "SELECT id, balance FROM accounts"));
        Assert.Equal(1L, account[0]);
        Assert.Equal(balance, account[1]);

        var ledgerRows = Rows(database, "SELECT id, account_id, amount FROM ledger ORDER BY id");
        Assert.Equal(ledgerCount, ledgerRows.Count);
        if (ledgerAmount is long amount)
        {
            Assert.All(ledgerRows, row =>
            {
                Assert.Equal(1L, row[1]);
                Assert.Equal(amount, row[2]);
            });
        }
    }

    /// <summary>回收隔离事务目录。</summary>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
