using SonnetDB.Routines;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using SonnetDB.Exceptions;
using SonnetDB.Backup;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>#338 提交阶段约束触发器的真实事务合同。</summary>
public sealed class SqlConstraintTriggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-m39-338-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ConstraintTrigger_IsDeferredUntilCommit_AndPersistsDefinition()
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE source_rows (id INT, PRIMARY KEY (id))");
            Execute(db, "CREATE TABLE audit_rows (id INT, PRIMARY KEY (id))");
            Execute(db, """
                CREATE CONSTRAINT TRIGGER audit_source AFTER INSERT ON source_rows
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW LANGUAGE SQL AS BEGIN
                    INSERT INTO audit_rows (id) VALUES (NEW.id);
                END
                """);
            var transaction = Assert.IsType<SqlTransactionContext>(Execute(db, "BEGIN"));
            Execute(db, "INSERT INTO source_rows (id) VALUES (1)", transaction);
            Assert.Empty(Select(db, "SELECT * FROM audit_rows").Rows);
            Execute(db, "COMMIT", transaction);
            Assert.Single(Select(db, "SELECT * FROM audit_rows").Rows);
            Assert.True(db.Routines.TryGetTrigger("audit_source")!.IsConstraint);
            Assert.True(db.Routines.TryGetTrigger("audit_source")!.InitiallyDeferred);
        }

        using var reopened = Open();
        Assert.True(reopened.Routines.TryGetTrigger("audit_source")!.IsConstraint);
        Assert.True(reopened.Routines.TryGetTrigger("audit_source")!.InitiallyDeferred);
    }

    [Fact]
    public void ConstraintTrigger_CommitFailure_RollsBackSourceAndCompletesTransaction()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE audit_rows (id INT, PRIMARY KEY (id))");
        Execute(db, "INSERT INTO audit_rows (id) VALUES (1)");
        Execute(db, """
            CREATE CONSTRAINT TRIGGER audit_source AFTER INSERT ON source_rows
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW LANGUAGE SQL AS BEGIN
                INSERT INTO audit_rows (id) VALUES (NEW.id);
            END
            """);
        var transaction = Assert.IsType<SqlTransactionContext>(Execute(db, "BEGIN"));
        Execute(db, "INSERT INTO source_rows (id) VALUES (1)", transaction);
        Assert.Throws<InvalidOperationException>(() => Execute(db, "COMMIT", transaction));
        Assert.True(transaction.IsCompleted);
        Assert.Empty(Select(db, "SELECT * FROM source_rows").Rows);
        Assert.Single(Select(db, "SELECT * FROM audit_rows").Rows);
    }

    [Fact]
    public void ConstraintTrigger_SavepointRollback_RemovesOnlyNewDeferredInvocations()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE audit_rows (id INT, PRIMARY KEY (id))");
        Execute(db, """
            CREATE CONSTRAINT TRIGGER audit_source AFTER INSERT ON source_rows
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW LANGUAGE SQL AS BEGIN
                INSERT INTO audit_rows (id) VALUES (NEW.id);
            END
            """);
        Execute(db, "CREATE PROCEDURE fail_after_insert() LANGUAGE SQL AS BEGIN INSERT INTO source_rows (id) VALUES (2); SELECT * FROM source_rows; END");
        var transaction = Assert.IsType<SqlTransactionContext>(Execute(db, "BEGIN"));
        Execute(db, "INSERT INTO source_rows (id) VALUES (1)", transaction);
        Assert.Throws<RoutineExecutionException>(() => Execute(db, "CALL fail_after_insert()", transaction,
            new SqlExecutionOptions { MaxRoutineResultRows = 1 }));
        Execute(db, "COMMIT", transaction);
        Assert.Equal([1L], Select(db, "SELECT id FROM source_rows ORDER BY id").Rows.Select(static row => row[0]).ToArray());
        Assert.Equal([1L], Select(db, "SELECT id FROM audit_rows ORDER BY id").Rows.Select(static row => row[0]).ToArray());
    }

    [Theory]
    [InlineData(70, true)]
    [InlineData(69, false)]
    public void Commit_TransferAcrossTwoStatements_ValidatesFinalBalances(int destinationBalance, bool valid)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE accounts (id INT, balance INT, PRIMARY KEY(id))");
        Execute(db, "INSERT INTO accounts(id,balance) VALUES(1,40),(2,60)");
        Execute(db, "CREATE TABLE checks (id INT, total INT, PRIMARY KEY(id), CHECK(total = 100))");
        Execute(db, """
            CREATE CONSTRAINT TRIGGER balanced AFTER UPDATE ON accounts DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW LANGUAGE SQL AS BEGIN
                INSERT INTO checks(id,total) SELECT NEW.id,total FROM (SELECT SUM(balance) AS total FROM accounts) AS totals;
            END
            """);
        string transfer = $"BEGIN; UPDATE accounts SET balance=30 WHERE id=1; UPDATE accounts SET balance={destinationBalance} WHERE id=2; COMMIT;";
        if (valid)
        {
            SqlExecutor.ExecuteScript(db, transfer);
            Assert.Equal(new object?[] {100L,100L}, Select(db,"SELECT total FROM checks ORDER BY id").Rows.Select(row=>row[0]));
            Assert.All(db.Routines.Diagnostics.SnapshotAudit(), row=>Assert.Equal("committed",row.Outcome));
        }
        else
        {
            Assert.Throws<TableConstraintException>(() => SqlExecutor.ExecuteScript(db, transfer));
            Assert.Empty(Select(db, "SELECT * FROM checks").Rows);
            Assert.Equal(new object?[] { 40L, 60L }, Select(db, "SELECT balance FROM accounts ORDER BY id").Rows.Select(row => row[0]));
            Assert.DoesNotContain(db.Routines.Diagnostics.SnapshotAudit(), row => row.Outcome is "pending" or "committed");
        }
    }

    [Fact]
    public void ImmediateTrigger_TransferSnapshot_RejectsIntermediateBalance()
    {
        using var db=Open();
        Execute(db,"CREATE TABLE accounts(id INT,balance INT,PRIMARY KEY(id))");
        Execute(db,"INSERT INTO accounts(id,balance) VALUES(1,40),(2,60)");
        Execute(db,"CREATE TABLE checks(id INT,total INT,PRIMARY KEY(id),CHECK(total=100))");
        Execute(db,"CREATE TRIGGER balanced AFTER UPDATE ON accounts FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO checks(id,total) SELECT NEW.id,total FROM (SELECT SUM(balance) AS total FROM accounts) AS totals; END");
        Assert.Throws<TableConstraintException>(()=>SqlExecutor.ExecuteScript(db,"BEGIN; UPDATE accounts SET balance=30 WHERE id=1; UPDATE accounts SET balance=70 WHERE id=2; COMMIT;"));
        Assert.Empty(Select(db,"SELECT * FROM checks").Rows);
        Assert.Equal(40L,Select(db,"SELECT balance FROM accounts WHERE id=1").Rows[0][0]);
    }

    [Theory]
    [InlineData("CASCADE", "SELECT COUNT(*) FROM children")]
    [InlineData("SET NULL", "SELECT COUNT(*) FROM children WHERE parent_id IS NOT NULL")]
    public void Commit_CascadeActions_AreVisibleToDeferredChecks(string action,string query)
    {
        using var db=Open();
        Execute(db,"CREATE TABLE parents(id INT,PRIMARY KEY(id))");
        Execute(db,$"CREATE TABLE children(id INT,parent_id INT,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES parents(id) ON DELETE {action})");
        Execute(db,"CREATE TABLE checks(id INT,total INT,PRIMARY KEY(id),CHECK(total=0))");
        Execute(db,"INSERT INTO parents(id) VALUES(1)");
        Execute(db,"INSERT INTO children(id,parent_id) VALUES(1,1)");
        Execute(db,$"CREATE CONSTRAINT TRIGGER removed AFTER DELETE ON parents DEFERRABLE INITIALLY DEFERRED FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO checks(id,total) SELECT OLD.id,total FROM ({query.Replace("COUNT(*)", "COUNT(*) AS total", StringComparison.Ordinal)}) AS totals; END");
        Execute(db,"DELETE FROM parents WHERE id=1");
        Assert.Equal(0L,Assert.Single(Select(db,"SELECT total FROM checks").Rows)[0]);
    }

    [Theory]
    [InlineData("CASCADE")]
    [InlineData("SET NULL")]
    public void Commit_DeferredParentReinsert_PreservesAlreadyExpandedCascade(string action)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE parents(id INT,PRIMARY KEY(id))");
        Execute(db, $"CREATE TABLE children(id INT,parent_id INT,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES parents(id) ON DELETE {action})");
        Execute(db, "INSERT INTO parents(id) VALUES(1)");
        Execute(db, "INSERT INTO children(id,parent_id) VALUES(1,1)");
        Execute(db, "CREATE CONSTRAINT TRIGGER restore_parent AFTER DELETE ON parents DEFERRABLE INITIALLY DEFERRED FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO parents(id) VALUES(OLD.id); END");
        Execute(db, "DELETE FROM parents WHERE id=1");
        Assert.Single(Select(db, "SELECT * FROM parents").Rows);
        var children = Select(db, "SELECT parent_id FROM children").Rows;
        if (action == "CASCADE") Assert.Empty(children);
        else Assert.Null(Assert.Single(children)[0]);
    }

    [Theory]
    [InlineData("CASCADE")]
    [InlineData("SET NULL")]
    public void Commit_DeferredFailureAfterCascadeExpansion_RestoresSourceChildrenAndActions(string action)
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE parents(id INT,PRIMARY KEY(id))");
            Execute(db, $"CREATE TABLE children(id INT,parent_id INT,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES parents(id) ON DELETE {action})");
            Execute(db, "CREATE TABLE actions(id INT,PRIMARY KEY(id))");
            Execute(db, "INSERT INTO parents(id) VALUES(1)");
            Execute(db, "INSERT INTO children(id,parent_id) VALUES(1,1)");
            Execute(db, "CREATE CONSTRAINT TRIGGER fail_after_action AFTER DELETE ON parents DEFERRABLE INITIALLY DEFERRED FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO actions(id) VALUES(1); INSERT INTO actions(id) VALUES(2); END");
            var error = Assert.Throws<RoutineExecutionException>(() => Execute(db, "DELETE FROM parents WHERE id=1",
                options: new SqlExecutionOptions { MaxRoutineStatements = 1 }));
            Assert.Equal(RoutineErrorCodes.StatementLimit, error.Code);
            Assert.Single(Select(db, "SELECT * FROM parents").Rows);
            Assert.Equal(1L, Assert.Single(Select(db, "SELECT parent_id FROM children").Rows)[0]);
            Assert.Empty(Select(db, "SELECT * FROM actions").Rows);
        }
        using var reopened = Open();
        Assert.Single(Select(reopened, "SELECT * FROM parents").Rows);
        Assert.Equal(1L, Assert.Single(Select(reopened, "SELECT parent_id FROM children").Rows)[0]);
        Assert.Empty(Select(reopened, "SELECT * FROM actions").Rows);
    }

    [Fact]
    public void Commit_CompletionAcknowledgementFailure_ReportsUnknownAndRecovers()
    {
        using(var db=Open())
        {
            SetupAudit(db);
            db.Tables.ApplyTransactionAfterCompleteTestHook=()=>throw new IOException("lost acknowledgement");
            var transaction=new SqlTransactionContext();
            Execute(db,"INSERT INTO source_rows(id) VALUES(1)",transaction);
            Assert.Throws<TableTransactionRecoveryException>(()=>Execute(db,"COMMIT",transaction));
            Assert.True(transaction.IsCompleted);
            Assert.All(db.Routines.Diagnostics.SnapshotAudit(),record=>Assert.Equal("unknown",record.Outcome));
            db.Tables.ApplyTransactionAfterCompleteTestHook=null;
        }
        using var reopened=Open();
        Assert.Single(Select(reopened,"SELECT * FROM source_rows").Rows);
        Assert.Single(Select(reopened,"SELECT * FROM audit_rows").Rows);
    }

    [Fact]
    public void Commit_BeforeCompletionFailure_RestoresBothTables()
    {
        using(var db=Open())
        {
            SetupAudit(db);
            db.Tables.ApplyTransactionBeforeCompleteTestHook=()=>throw new IOException("before completion");
            var transaction=new SqlTransactionContext();
            Execute(db,"INSERT INTO source_rows(id) VALUES(1)",transaction);
            Assert.Throws<IOException>(()=>Execute(db,"COMMIT",transaction));
            Assert.True(transaction.IsCompleted);
            Assert.Empty(Select(db,"SELECT * FROM source_rows").Rows);
            Assert.Empty(Select(db,"SELECT * FROM audit_rows").Rows);
            db.Tables.ApplyTransactionBeforeCompleteTestHook=null;
        }
        using var reopened=Open();
        Assert.Empty(Select(reopened,"SELECT * FROM source_rows").Rows);
        Assert.Empty(Select(reopened,"SELECT * FROM audit_rows").Rows);
    }

    [Theory]
    [InlineData("ROLLBACK")]
    [InlineData("")]
    public void Transaction_RollbackOrAbandonedScript_DiscardsDeferredEvents(string ending)
    {
        using var db=Open();
        SetupAudit(db);
        string script=$"BEGIN; INSERT INTO source_rows(id) VALUES(1); {ending}";
        if(ending.Length==0)Assert.Throws<InvalidOperationException>(()=>SqlExecutor.ExecuteScript(db,script));
        else SqlExecutor.ExecuteScript(db,script);
        Assert.Empty(Select(db,"SELECT * FROM source_rows").Rows);
        Assert.Empty(Select(db,"SELECT * FROM audit_rows").Rows);
        Assert.Empty(db.Routines.Diagnostics.SnapshotAudit());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(10000)]
    public void Commit_BoundedBatch_VisitsEachEventOnce(int count)
    {
        using var db = Open();
        SetupAudit(db);
        var options = new SqlExecutionOptions { MaxRoutineStatements=10001, MaxDeferredTriggerInvocations=10000 };
        var transaction = new SqlTransactionContext();
        if(count > 0)
            Execute(db,"INSERT INTO source_rows(id) VALUES " + string.Join(',',Enumerable.Range(1,count).Select(id=>$"({id})")),transaction,options);
        else
            Execute(db,"INSERT INTO source_rows(id) SELECT id FROM source_rows WHERE id<0",transaction,options);
        Execute(db,"COMMIT",transaction,options);
        Assert.Equal((long)count, Select(db,"SELECT COUNT(*) FROM audit_rows").Rows[0][0]);
        Assert.Equal(count,db.Routines.Diagnostics.GetMetrics().TriggerExecutions);
    }

    [Fact]
    public void Commit_ImplicitTransaction_ReadsItsBufferedSource()
    {
        using var db = Open();
        SetupAudit(db, "INSERT INTO audit_rows(id) SELECT id FROM source_rows WHERE id=NEW.id;");
        Execute(db,"INSERT INTO source_rows(id) VALUES(1)");
        Assert.Single(Select(db,"SELECT * FROM audit_rows").Rows);
    }

    [Fact]
    public void Commit_FifoOrderAndWhen_RetainEachEventImage()
    {
        using var db=Open();
        Execute(db,"CREATE TABLE source_rows(id INT,value INT,PRIMARY KEY(id))");
        Execute(db,"CREATE TABLE audit_rows(id INT,previous INT,changed INT,PRIMARY KEY(id))");
        Execute(db,"INSERT INTO source_rows(id,value) VALUES(1,1)");
        Execute(db,"CREATE CONSTRAINT TRIGGER first_action AFTER UPDATE ON source_rows DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN(NEW.value=2) LANGUAGE SQL AS BEGIN INSERT INTO audit_rows(id,previous,changed) VALUES(NEW.value*10+1,OLD.value,NEW.value); END");
        Execute(db,"CREATE CONSTRAINT TRIGGER second_action AFTER UPDATE ON source_rows DEFERRABLE INITIALLY DEFERRED FOR EACH ROW FOLLOWS first_action LANGUAGE SQL AS BEGIN INSERT INTO audit_rows(id,previous,changed) VALUES(NEW.value*10+2,OLD.value,NEW.value); END");
        SqlExecutor.ExecuteScript(db,"BEGIN; UPDATE source_rows SET value=2 WHERE id=1; UPDATE source_rows SET value=3 WHERE id=1; COMMIT;");
        var rows=Select(db,"SELECT id,previous,changed FROM audit_rows ORDER BY id").Rows;
        Assert.Equal(new object?[]{21L,1L,2L},rows[0]);
        Assert.Equal(new object?[]{22L,1L,2L},rows[1]);
        Assert.Equal(new object?[]{32L,2L,3L},rows[2]);
        Assert.Equal(new[]{"first_action","second_action","first_action","second_action"},db.Routines.Diagnostics.SnapshotAudit().Select(row=>row.Name));
    }

    [Fact]
    public void Backup_RestoreDeferredDefinition_StillExecutesOnlyAtCommit()
    {
        string backupPath=_root+"-backup";
        string restoredPath=_root+"-restored";
        try
        {
            using var db=Open();
            SetupAudit(db);
            Execute(db,"INSERT INTO source_rows(id) VALUES(1)");
            var service=new BackupService();
            service.Create(db,new BackupCreateOptions {DestinationDirectory=backupPath});
            service.Restore(new BackupRestoreOptions {BackupDirectory=backupPath,TargetDirectory=restoredPath});
            using var restored=Tsdb.Open(new TsdbOptions {RootDirectory=restoredPath,BackgroundFlush=new BackgroundFlushOptions {Enabled=false}});
            Assert.True(restored.Routines.TryGetTrigger("audit_source")!.InitiallyDeferred);
            var transaction=new SqlTransactionContext();
            Execute(restored,"INSERT INTO source_rows(id) VALUES(2)",transaction);
            Assert.Single(Select(restored,"SELECT * FROM audit_rows").Rows);
            Execute(restored,"COMMIT",transaction);
            Assert.Equal(2,Select(restored,"SELECT * FROM audit_rows").Rows.Count);
        }
        finally
        {
            if(Directory.Exists(backupPath))Directory.Delete(backupPath,true);
            if(Directory.Exists(restoredPath))Directory.Delete(restoredPath,true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Commit_CancelledOriginOrCommit_CompletesWithoutWrites(bool cancelOrigin)
    {
        using var db = Open();
        SetupAudit(db);
        using var cancellation = new CancellationTokenSource();
        var transaction = new SqlTransactionContext();
        Execute(db,"INSERT INTO source_rows(id) VALUES(1)",transaction,
            new SqlExecutionOptions { CancellationToken = cancelOrigin ? cancellation.Token : default });
        cancellation.Cancel();
        var error=Assert.Throws<RoutineExecutionException>(()=>Execute(db,"COMMIT",transaction,
            new SqlExecutionOptions { CancellationToken=cancelOrigin? default : cancellation.Token }));
        Assert.Equal(RoutineErrorCodes.Cancelled,error.Code);
        Assert.True(transaction.IsCompleted);
        Assert.Empty(Select(db,"SELECT * FROM source_rows").Rows);
        Assert.Equal(0,transaction.DeferredTriggerCount);
    }

    [Fact]
    public void Commit_OriginalStatementBudgetAndCaller_ArePreserved()
    {
        using var db=Open();
        SetupAudit(db);
        var transaction=new SqlTransactionContext();
        Execute(db,"INSERT INTO source_rows(id) VALUES(1),(2)",transaction,
            new SqlExecutionOptions { MaxRoutineStatements=1,Caller="origin" });
        Assert.Equal(RoutineErrorCodes.StatementLimit,Assert.Throws<RoutineExecutionException>(()=>
            Execute(db,"COMMIT",transaction,new SqlExecutionOptions {MaxRoutineStatements=100,Caller="committer"})).Code);
        Assert.All(db.Routines.Diagnostics.SnapshotAudit(),row=>Assert.Equal("origin",row.Caller));
        Assert.DoesNotContain(db.Routines.Diagnostics.SnapshotAudit(),row=>row.Outcome=="pending");
        Assert.Empty(Select(db,"SELECT * FROM source_rows").Rows);
    }

    [Fact]
    public void Commit_MultipleOriginStatements_ShareCommitBudget()
    {
        using var db=Open();
        SetupAudit(db);
        var transaction=new SqlTransactionContext();
        Execute(db,"INSERT INTO source_rows(id) VALUES(1)",transaction);
        Execute(db,"INSERT INTO source_rows(id) VALUES(2)",transaction);
        Assert.Equal(RoutineErrorCodes.StatementLimit,Assert.Throws<RoutineExecutionException>(()=>
            Execute(db,"COMMIT",transaction,new SqlExecutionOptions {MaxRoutineStatements=1})).Code);
        Assert.Empty(Select(db,"SELECT * FROM source_rows").Rows);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Commit_ReturningRows_RespectOriginAndCommitBudget(bool limitOrigin)
    {
        using var db = Open();
        SetupAudit(db, "INSERT INTO audit_rows(id) VALUES(NEW.id) RETURNING id;");
        var transaction = new SqlTransactionContext();
        Execute(db, "INSERT INTO source_rows(id) VALUES(1),(2)", transaction,
            new SqlExecutionOptions { MaxRoutineResultRows = limitOrigin ? 1 : 100 });
        var error = Assert.Throws<RoutineExecutionException>(() => Execute(db, "COMMIT", transaction,
            new SqlExecutionOptions { MaxRoutineResultRows = limitOrigin ? 100 : 1 }));
        Assert.Equal(RoutineErrorCodes.ResultRowLimit, error.Code);
        Assert.True(transaction.IsCompleted);
        Assert.Empty(Select(db, "SELECT * FROM source_rows").Rows);
        Assert.Empty(Select(db, "SELECT * FROM audit_rows").Rows);
    }

    [Fact]
    public void Commit_ProcedureAndDeferredChain_PreserveAncestryAndCommitAllActions()
    {
        using var db = Open();
        SetupAudit(db);
        Execute(db, "CREATE TABLE sink(id INT,PRIMARY KEY(id))");
        Execute(db, "CREATE CONSTRAINT TRIGGER audit_sink AFTER INSERT ON audit_rows DEFERRABLE INITIALLY DEFERRED FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO sink(id) VALUES(NEW.id); END");
        Execute(db, "CREATE PROCEDURE produce() LANGUAGE SQL AS BEGIN INSERT INTO source_rows(id) VALUES(1); END");
        var transaction = new SqlTransactionContext();
        Execute(db, "CALL produce()", transaction, new SqlExecutionOptions { Caller = "producer", MaxRoutineDepth = 3 });
        Assert.Empty(Select(db, "SELECT * FROM audit_rows").Rows);
        Assert.Empty(Select(db, "SELECT * FROM sink").Rows);
        Execute(db, "COMMIT", transaction, new SqlExecutionOptions { Caller = "committer" });
        Assert.Single(Select(db, "SELECT * FROM source_rows").Rows);
        Assert.Single(Select(db, "SELECT * FROM audit_rows").Rows);
        Assert.Single(Select(db, "SELECT * FROM sink").Rows);
        var audit = db.Routines.Diagnostics.SnapshotAudit();
        Assert.Equal(3, audit.Count);
        Assert.All(audit, row => { Assert.Equal("producer", row.Caller); Assert.Equal("committed", row.Outcome); });
        Assert.Contains(audit, row => row.CallChain == "procedure:produce > trigger:audit_source > trigger:audit_sink");
    }

    [Theory]
    [InlineData(1,67108864)]
    [InlineData(100,600)]
    public void Queue_OverBudget_RestoresEarlierEventsAndBudget(int count,long bytes)
    {
        using var db=Open();
        SetupAudit(db);
        var transaction=new SqlTransactionContext();
        var options=new SqlExecutionOptions {MaxDeferredTriggerInvocations=count,MaxDeferredTriggerBytes=bytes};
        Execute(db,"INSERT INTO source_rows(id) VALUES(1)",transaction,options);
        Assert.Equal(RoutineErrorCodes.DeferredLimit,Assert.Throws<RoutineExecutionException>(()=>
            Execute(db,"INSERT INTO source_rows(id) VALUES(2),(3)",transaction,options)).Code);
        Assert.Equal(1,transaction.DeferredTriggerCount);
        Execute(db,"COMMIT",transaction);
        Assert.Equal(1L,Assert.Single(Select(db,"SELECT id FROM audit_rows").Rows)[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commit_TighterQueueBudget_RejectsBeforeExecutingEvents(bool limitBytes)
    {
        using var db = Open();
        SetupAudit(db);
        var transaction = new SqlTransactionContext();
        Execute(db, "INSERT INTO source_rows(id) VALUES(1),(2)", transaction);
        var error = Assert.Throws<RoutineExecutionException>(() => Execute(db, "COMMIT", transaction,
            new SqlExecutionOptions
            {
                MaxDeferredTriggerInvocations = limitBytes ? 100 : 1,
                MaxDeferredTriggerBytes = limitBytes ? 600 : 67108864,
            }));
        Assert.Equal(RoutineErrorCodes.DeferredLimit, error.Code);
        Assert.True(transaction.IsCompleted);
        Assert.Empty(db.Routines.Diagnostics.SnapshotAudit());
        Assert.Empty(Select(db, "SELECT * FROM source_rows").Rows);
        Assert.Empty(Select(db, "SELECT * FROM audit_rows").Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Queue_LaterRequestRaisesBudget_PreservesStrictestOriginLimit(bool limitBytes)
    {
        using var db = Open();
        SetupAudit(db);
        var transaction = new SqlTransactionContext();
        Execute(db, "INSERT INTO source_rows(id) VALUES(1)", transaction, new SqlExecutionOptions
        {
            MaxDeferredTriggerInvocations = limitBytes ? 100 : 1,
            MaxDeferredTriggerBytes = limitBytes ? 600 : 67108864,
        });
        var error = Assert.Throws<RoutineExecutionException>(() =>
            Execute(db, "INSERT INTO source_rows(id) VALUES(2)", transaction));
        Assert.Equal(RoutineErrorCodes.DeferredLimit, error.Code);
        Execute(db, "COMMIT", transaction);
        Assert.Equal(1L, Assert.Single(Select(db, "SELECT id FROM source_rows").Rows)[0]);
        Assert.Equal(1L, Assert.Single(Select(db, "SELECT id FROM audit_rows").Rows)[0]);
    }

    [Fact]
    public void Commit_DeferredRecursion_RetainsAncestry()
    {
        using var db=Open();
        SetupAudit(db,"INSERT INTO source_rows(id) VALUES(NEW.id+1);");
        var transaction=new SqlTransactionContext();
        Execute(db,"INSERT INTO source_rows(id) VALUES(1)",transaction);
        Assert.Equal(RoutineErrorCodes.TriggerRecursion,Assert.Throws<RoutineExecutionException>(()=>Execute(db,"COMMIT",transaction)).Code);
        Assert.Empty(Select(db,"SELECT * FROM source_rows").Rows);
    }

    [Fact]
    public void Commit_DeferredChain_RetainsDepthAcrossDrain()
    {
        using var db=Open();
        SetupAudit(db);
        Execute(db,"CREATE TABLE sink(id INT,PRIMARY KEY(id))");
        Execute(db,"CREATE CONSTRAINT TRIGGER audit_sink AFTER INSERT ON audit_rows DEFERRABLE INITIALLY DEFERRED FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO sink(id) VALUES(NEW.id); END");
        var transaction=new SqlTransactionContext();
        Execute(db,"INSERT INTO source_rows(id) VALUES(1)",transaction,new SqlExecutionOptions {MaxRoutineDepth=1});
        Assert.Equal(RoutineErrorCodes.DepthLimit,Assert.Throws<RoutineExecutionException>(()=>Execute(db,"COMMIT",transaction)).Code);
        Assert.Empty(Select(db,"SELECT * FROM source_rows").Rows);
    }

    [Fact]
    public void Commit_LifecycleChange_UsesQueuedDefinitionSnapshot()
    {
        using var db=Open();
        SetupAudit(db);
        var transaction=new SqlTransactionContext();
        Execute(db,"INSERT INTO source_rows(id) VALUES(1)",transaction);
        Execute(db,"ALTER TRIGGER audit_source DISABLE");
        Execute(db,"ALTER TRIGGER audit_source RENAME TO renamed");
        Execute(db,"COMMIT",transaction);
        Assert.Single(Select(db,"SELECT * FROM audit_rows").Rows);
        Execute(db,"INSERT INTO source_rows(id) VALUES(2)");
        Assert.Single(Select(db,"SELECT * FROM audit_rows").Rows);
    }

    [Fact]
    public void Commit_DroppedDependencyAndRecreatedTable_RejectsStaleSchema()
    {
        using var db=Open();
        SetupAudit(db);
        var transaction=new SqlTransactionContext();
        Execute(db,"INSERT INTO source_rows(id) VALUES(1)",transaction);
        Execute(db,"DROP TRIGGER audit_source");
        Execute(db,"DROP TABLE audit_rows");
        Execute(db,"CREATE TABLE audit_rows(id INT,PRIMARY KEY(id))");
        Assert.Equal(RoutineErrorCodes.Dependency,Assert.Throws<RoutineExecutionException>(()=>Execute(db,"COMMIT",transaction)).Code);
        Assert.Empty(Select(db,"SELECT * FROM source_rows").Rows);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Commit_WaitingForManagerLock_CancelsOrTimesOut(bool cancel)
    {
        using var db=Open();
        SetupAudit(db);
        var transaction=new SqlTransactionContext();
        Execute(db,"INSERT INTO source_rows(id) VALUES(1)",transaction);
        using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task holder=Task.Run(()=>db.Tables.ExecuteLocked(()=>
        {
            entered.SetResult();
            if(!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return 0;
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation=new CancellationTokenSource();
            if(cancel)cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            var error=await Task.Run(()=>Assert.Throws<RoutineExecutionException>(()=>Execute(db,"COMMIT",transaction,
                new SqlExecutionOptions {CancellationToken=cancellation.Token,TransactionCommitTimeoutMilliseconds=cancel?2000:100})))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(cancel?RoutineErrorCodes.Cancelled:RoutineErrorCodes.CommitTimeout,error.Code);
            Assert.True(transaction.IsCompleted);
        }
        finally {release.Set();await holder.WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.Empty(Select(db,"SELECT * FROM source_rows").Rows);
    }

    [Fact]
    public async Task Commit_ConcurrentDisjointWrites_RejectsFinalStateWriteSkew()
    {
        using var db=Open();
        Execute(db,"CREATE TABLE reservations(id INT,PRIMARY KEY(id))");
        Execute(db,"CREATE TABLE capacity_checks(id INT,total INT,PRIMARY KEY(id),CHECK(total<=1))");
        Execute(db,"CREATE CONSTRAINT TRIGGER capacity AFTER INSERT ON reservations DEFERRABLE INITIALLY DEFERRED FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO capacity_checks(id,total) SELECT NEW.id,total FROM (SELECT COUNT(*) AS total FROM reservations) AS totals; END");
        var first=new SqlTransactionContext();var second=new SqlTransactionContext();
        Execute(db,"INSERT INTO reservations(id) VALUES(1)",first);
        Execute(db,"INSERT INTO reservations(id) VALUES(2)",second);
        using var barrier=new Barrier(2);
        Task<Exception?> Commit(SqlTransactionContext transaction)=>Task.Run<Exception?>(()=>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
            return Record.Exception(()=>Execute(db,"COMMIT",transaction));
        });
        var results=await Task.WhenAll(Commit(first),Commit(second)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(results,error=>error is null);
        Assert.Single(results,error=>error is TableConstraintException);
        Assert.Single(Select(db,"SELECT * FROM reservations").Rows);
        Assert.Single(Select(db,"SELECT * FROM capacity_checks").Rows);
    }

    private static void SetupAudit(Tsdb db,string body="INSERT INTO audit_rows(id) VALUES(NEW.id);")
    {
        Execute(db,"CREATE TABLE source_rows(id INT,PRIMARY KEY(id))");
        Execute(db,"CREATE TABLE audit_rows(id INT,PRIMARY KEY(id))");
        Execute(db,$"CREATE CONSTRAINT TRIGGER audit_source AFTER INSERT ON source_rows DEFERRABLE INITIALLY DEFERRED FOR EACH ROW LANGUAGE SQL AS BEGIN {body} END");
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root,
        BackgroundFlush=new BackgroundFlushOptions {Enabled=false} });

    private static object? Execute(Tsdb db, string sql, SqlTransactionContext? transaction = null, SqlExecutionOptions? options=null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, transaction,
            options ?? SqlExecutionOptions.Default);

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
}
