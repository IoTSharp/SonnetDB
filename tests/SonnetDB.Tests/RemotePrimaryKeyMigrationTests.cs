using System.Data;
using SonnetDB.Data;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class RemoteAdoHttp2TransportTests
{
    /// <summary>原始主键定义和类型 DDL 在三种连接的同步、异步路径保持行身份、入站外键及重开合同。</summary>
    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task PrimaryKeyMigration_DefinitionAndType_RefreshMetadataAndPersistAcrossHostRestart(
        string protocol, bool asynchronous)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string embeddedRoot = Path.Combine(_dataRoot!, "primary-key-migration");
        await using (var connection = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            await OpenPrimaryKeyMigrationAsync(connection, asynchronous, deadline.Token);
            foreach (string sql in new[]
            {
                "CREATE TABLE migration_parents(id INT AUTO_INCREMENT,code STRING NOT NULL,label STRING DEFAULT 'base',version INT ROWVERSION,PRIMARY KEY(id),CONSTRAINT ck_migration_label CHECK(label<>'forbidden'))",
                "CREATE UNIQUE INDEX ux_migration_label ON migration_parents(label)",
                "CREATE INDEX ix_migration_code_label ON migration_parents(code,label)",
                "CREATE INDEX ix_migration_old_id ON migration_parents(id)",
                "CREATE TABLE migration_children(id INT,parent_id INT,PRIMARY KEY(id),CONSTRAINT fk_migration_parent FOREIGN KEY(parent_id) REFERENCES migration_parents(id))",
                "CREATE INDEX ix_migration_parent ON migration_children(parent_id)",
                "INSERT INTO migration_parents(code,label) VALUES('2','one'),('1','two')",
                "INSERT INTO migration_children(id,parent_id) VALUES(10,1),(11,2),(12,NULL)",
                "UPDATE migration_parents SET label='updated' WHERE id=1 AND version=1",
            })
            {
                await ExecutePrimaryKeyMigrationAsync(connection, sql, asynchronous, deadline.Token);
            }

            AssertPrimaryKeyMigrationMetadata(connection, "id", "STRING", "INT");
            await AssertPrimaryKeyMigrationStateAsync(connection, codeIsInteger: false,
                referenceIsInteger: true, definitionChanged: false, asynchronous, deadline.Token);

            // 新旧键值交换，且 INT 主键切换到 STRING 列，子表必须继续引用原来的父行。
            await ExecutePrimaryKeyMigrationAsync(connection,
                "ALTER TABLE migration_parents ALTER PRIMARY KEY (code)", asynchronous, deadline.Token);
            AssertPrimaryKeyMigrationMetadata(connection, "code", "STRING", "STRING");
            await AssertPrimaryKeyMigrationStateAsync(connection, codeIsInteger: false,
                referenceIsInteger: false, definitionChanged: true, asynchronous, deadline.Token);

            await ExecutePrimaryKeyMigrationAsync(connection,
                "ALTER TABLE migration_parents ALTER COLUMN code TYPE INT", asynchronous, deadline.Token);
            AssertPrimaryKeyMigrationMetadata(connection, "code", "INT", "INT");
            await AssertPrimaryKeyMigrationStateAsync(connection, codeIsInteger: true,
                referenceIsInteger: true, definitionChanged: true, asynchronous, deadline.Token);

            await using (var observer = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
            {
                await OpenPrimaryKeyMigrationAsync(observer, asynchronous, deadline.Token);
                AssertPrimaryKeyMigrationMetadata(observer, "code", "INT", "INT");
            }
            await AssertPrimaryKeyMigrationRejectedAsync(connection,
                "DELETE FROM migration_parents WHERE code=2", protocol, asynchronous, deadline.Token,
                TableConstraintException.ForeignKeyViolation);
            await AssertPrimaryKeyMigrationRejectedAsync(connection,
                "INSERT INTO migration_children(id,parent_id) VALUES(13,99)", protocol, asynchronous, deadline.Token,
                TableConstraintException.ForeignKeyViolation);
        }

        // 远程路径彻底 Stop/Dispose 原宿主后，从同一目录新建宿主；嵌入式已关闭全部连接。
        if (protocol != "embedded")
            await ReopenHostAsync(deadline.Token);
        await using (var reopened = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            await OpenPrimaryKeyMigrationAsync(reopened, asynchronous, deadline.Token);
            AssertPrimaryKeyMigrationMetadata(reopened, "code", "INT", "INT");
            await AssertPrimaryKeyMigrationStateAsync(reopened, codeIsInteger: true,
                referenceIsInteger: true, definitionChanged: true, asynchronous, deadline.Token);
            Assert.Equal(1, await ExecutePrimaryKeyMigrationAsync(reopened,
                "INSERT INTO migration_parents(code) VALUES(3)", asynchronous, deadline.Token));
            await AssertPrimaryKeyMigrationRowsAsync(reopened,
                "SELECT id,label,version FROM migration_parents WHERE code=3",
                [typeof(long), typeof(string), typeof(long)], [[3L, "base", 1L]], asynchronous, deadline.Token);
            await AssertPrimaryKeyMigrationRejectedAsync(reopened,
                "INSERT INTO migration_parents(id,code,label) VALUES(3,1,'duplicate-key')", protocol,
                asynchronous, deadline.Token, TableConstraintException.UniqueViolation);
            await AssertPrimaryKeyMigrationRejectedAsync(reopened,
                "INSERT INTO migration_parents(id,code,label) VALUES(3,4,'two')", protocol,
                asynchronous, deadline.Token, TableConstraintException.UniqueViolation);
            await AssertPrimaryKeyMigrationRejectedAsync(reopened,
                "INSERT INTO migration_parents(id,code,label) VALUES(3,4,'forbidden')", protocol,
                asynchronous, deadline.Token, TableConstraintException.CheckViolation);
            await AssertPrimaryKeyMigrationRejectedAsync(reopened,
                "DELETE FROM migration_parents WHERE code=2", protocol,
                asynchronous, deadline.Token, TableConstraintException.ForeignKeyViolation);
            await AssertPrimaryKeyMigrationRowsAsync(reopened, "SELECT COUNT(*) FROM migration_parents",
                [typeof(long)], [[3L]], asynchronous, deadline.Token);
        }
        AssertPrimaryKeyMigrationTransport(protocol);
    }

    /// <summary>重复、NULL、溢出、无法转换及精度损失拒绝后，旧列、索引、外键和全部数据在完整重开后保持一致。</summary>
    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task PrimaryKeyMigration_InvalidTargets_PreserveOriginalTablesAcrossHostRestart(
        string protocol, bool asynchronous)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string embeddedRoot = Path.Combine(_dataRoot!, "primary-key-migration-rejected");
        PrimaryKeyMigrationFailure[] cases =
        [
            new("duplicate", "INT", "1", 1L, "1", 1L, null),
            new("null", "INT", "NULL", null, "2", 2L, null),
            new("overflow", "STRING", "'9223372036854775808'", "9223372036854775808", "'2'", "2", "INT"),
            new("invalid", "STRING", "'not-an-integer'", "not-an-integer", "'2'", "2", "INT"),
            new("lossy", "STRING", "'79228162514264337593543950337'", "79228162514264337593543950337", "'2'", "2", "FLOAT"),
        ];
        var snapshots = new Dictionary<(string Collection, string Table), DataTable>();
        await using (var connection = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            await OpenPrimaryKeyMigrationAsync(connection, asynchronous, deadline.Token);
            foreach (var item in cases)
            {
                string parent = "rejected_" + item.Name;
                string child = parent + "_children";
                string key = item.TargetType is null ? "id" : "code";
                string referenceType = item.TargetType is null ? "INT" : item.CodeType;
                string reference = item.TargetType is null ? "1" : item.FirstLiteral;
                foreach (string sql in new[]
                {
                    $"CREATE TABLE {parent}(id INT,code {item.CodeType},label STRING DEFAULT 'base',PRIMARY KEY({key}),CHECK(id>0))",
                    $"CREATE INDEX ix_{item.Name}_code ON {parent}(code)",
                    $"CREATE UNIQUE INDEX ux_{item.Name}_label ON {parent}(label)",
                    $"CREATE TABLE {child}(id INT,parent_id {referenceType},PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES {parent}({key}))",
                    $"CREATE INDEX ix_{item.Name}_parent ON {child}(parent_id)",
                    $"INSERT INTO {parent}(id,code,label) VALUES(1,{item.FirstLiteral},'one'),(2,{item.SecondLiteral},'two')",
                    $"INSERT INTO {child}(id,parent_id) VALUES(10,{reference}),(11,NULL)",
                })
                {
                    await ExecutePrimaryKeyMigrationAsync(connection, sql, asynchronous, deadline.Token);
                }

                foreach (string table in new[] { parent, child })
                {
                    foreach (string collection in new[] { "Columns", "Indexes", "ForeignKeys" })
                        snapshots.Add((collection, table), connection.GetSchema(collection, [null, null, table, null]));
                }
                string migration = item.TargetType is null
                    ? $"ALTER TABLE {parent} ALTER PRIMARY KEY(code)"
                    : $"ALTER TABLE {parent} ALTER COLUMN code TYPE {item.TargetType}";
                await AssertPrimaryKeyMigrationRejectedAsync(connection, migration, protocol, asynchronous,
                    deadline.Token, item.Name == "duplicate" ? TableConstraintException.UniqueViolation : null);
                AssertPrimaryKeyMigrationSnapshots(connection, snapshots);
                await AssertRejectedPrimaryKeyMigrationStateAsync(connection, item, asynchronous, deadline.Token);
            }
        }

        if (protocol != "embedded")
            await ReopenHostAsync(deadline.Token);
        await using (var reopened = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            await OpenPrimaryKeyMigrationAsync(reopened, asynchronous, deadline.Token);
            AssertPrimaryKeyMigrationSnapshots(reopened, snapshots);
            foreach (var item in cases)
            {
                await AssertRejectedPrimaryKeyMigrationStateAsync(reopened, item, asynchronous, deadline.Token);
                string parent = "rejected_" + item.Name;
                string predicate = item.TargetType is null ? "id=1" : "code=" + item.FirstLiteral;
                await AssertPrimaryKeyMigrationRejectedAsync(reopened, $"DELETE FROM {parent} WHERE {predicate}",
                    protocol, asynchronous, deadline.Token, TableConstraintException.ForeignKeyViolation);
                Assert.Equal(1, await ExecutePrimaryKeyMigrationAsync(reopened,
                    $"UPDATE {parent} SET label='after-reopen' WHERE id=2", asynchronous, deadline.Token));
                await AssertPrimaryKeyMigrationRowsAsync(reopened,
                    $"SELECT id FROM {parent} WHERE label='after-reopen'", [typeof(long)], [[2L]], asynchronous, deadline.Token);
            }
        }
        AssertPrimaryKeyMigrationTransport(protocol);
    }

    /// <summary>旧事务缓冲的 FK 写入不能在主键迁移后静默改挂父行；拒绝旧提交后可重新开始正确写入。</summary>
    [Theory]
    [InlineData("embedded", IsolationLevel.ReadCommitted)]
    [InlineData("embedded", IsolationLevel.Serializable)]
    [InlineData("rest", IsolationLevel.ReadCommitted)]
    [InlineData("rest", IsolationLevel.Serializable)]
    [InlineData("frame-http2", IsolationLevel.ReadCommitted)]
    [InlineData("frame-http2", IsolationLevel.Serializable)]
    public async Task PrimaryKeyMigration_BufferedForeignKeyWrite_RejectsStaleTransactionAndAllowsFreshTransaction(
        string protocol, IsolationLevel isolationLevel)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string embeddedRoot = Path.Combine(_dataRoot!, "primary-key-migration-transaction");
        await using (var connection = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        await using (var observer = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            await connection.OpenAsync(deadline.Token);
            await observer.OpenAsync(deadline.Token);
            foreach (string sql in new[]
            {
                "CREATE TABLE transaction_parents(id INT,code INT NOT NULL,label STRING,PRIMARY KEY(id))",
                "CREATE TABLE transaction_children(id INT,parent_id INT,PRIMARY KEY(id),FOREIGN KEY(parent_id) REFERENCES transaction_parents(id))",
                "CREATE INDEX ix_transaction_parent ON transaction_children(parent_id)",
                "INSERT INTO transaction_parents(id,code,label) VALUES(1,2,'one'),(2,1,'two')",
            })
            {
                await ExecutePrimaryKeyMigrationAsync(observer, sql, asynchronous: true, deadline.Token);
            }

            await using (var stale = await connection.BeginTransactionAsync(isolationLevel, deadline.Token))
            {
                Assert.Equal(isolationLevel, stale.IsolationLevel);
                await using var insert = connection.CreateCommand();
                insert.CommandTimeout = 10;
                insert.Transaction = (SndbTransaction)stale;
                insert.CommandText = "INSERT INTO transaction_children(id,parent_id) VALUES(10,1)";
                Assert.Equal(1, await insert.ExecuteNonQueryAsync(deadline.Token));
                await AssertPrimaryKeyMigrationRowsAsync(observer, "SELECT COUNT(*) FROM transaction_children",
                    [typeof(long)], [[0L]], asynchronous: true, deadline.Token);

                // 两个合法键都仍存在，单纯检查新 schema 下的 FK 存在性会错误地把原 id=1 改挂到 id=2。
                await ExecutePrimaryKeyMigrationAsync(observer,
                    "ALTER TABLE transaction_parents ALTER PRIMARY KEY(code)", asynchronous: true, deadline.Token);
                var foreignKey = Assert.Single(observer.GetSchema("ForeignKeys",
                    [null, null, "transaction_children", null]).Rows.Cast<DataRow>());
                Assert.Equal("code", foreignKey["PRINCIPAL_COLUMN_NAME"]);

                Exception? exception = await Record.ExceptionAsync(() => stale.CommitAsync(deadline.Token));
                string errorCode = protocol == "embedded"
                    ? Assert.IsType<TableConstraintException>(exception).ErrorCode
                    : Assert.IsType<SndbServerException>(exception).Error;
                Assert.Equal(TableConstraintException.SerializationConflict, errorCode);
                await Assert.ThrowsAsync<InvalidOperationException>(() => stale.CommitAsync(deadline.Token));
            }

            await AssertPrimaryKeyMigrationRowsAsync(observer, "SELECT id,parent_id FROM transaction_children",
                [typeof(long), typeof(long)], [], asynchronous: true, deadline.Token);
            await AssertPrimaryKeyMigrationRowsAsync(observer,
                "SELECT id,code,label FROM transaction_parents ORDER BY id",
                [typeof(long), typeof(long), typeof(string)], [[1L, 2L, "one"], [2L, 1L, "two"]],
                asynchronous: true, deadline.Token);

            // 在同一连接上开启新事务并复用子行键，证明旧缓冲已整体撤销，新 schema 可以正常提交。
            await using (var fresh = await connection.BeginTransactionAsync(isolationLevel, deadline.Token))
            {
                await using var insert = connection.CreateCommand();
                insert.CommandTimeout = 10;
                insert.Transaction = (SndbTransaction)fresh;
                insert.CommandText = "INSERT INTO transaction_children(id,parent_id) VALUES(10,2)";
                Assert.Equal(1, await insert.ExecuteNonQueryAsync(deadline.Token));
                await AssertPrimaryKeyMigrationRowsAsync(observer, "SELECT COUNT(*) FROM transaction_children",
                    [typeof(long)], [[0L]], asynchronous: true, deadline.Token);
                await fresh.CommitAsync(deadline.Token);
            }
            await AssertPrimaryKeyMigrationRowsAsync(observer,
                "SELECT c.id,c.parent_id,p.id,p.label FROM transaction_children c JOIN transaction_parents p ON c.parent_id=p.code",
                [typeof(long), typeof(long), typeof(long), typeof(string)], [[10L, 2L, 1L, "one"]],
                asynchronous: true, deadline.Token);
        }

        if (protocol != "embedded")
            await ReopenHostAsync(deadline.Token);
        await using (var reopened = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            await reopened.OpenAsync(deadline.Token);
            await AssertPrimaryKeyMigrationRowsAsync(reopened,
                "SELECT c.id,c.parent_id,p.id,p.label FROM transaction_children c JOIN transaction_parents p ON c.parent_id=p.code",
                [typeof(long), typeof(long), typeof(long), typeof(string)], [[10L, 2L, 1L, "one"]],
                asynchronous: true, deadline.Token);
            await AssertPrimaryKeyMigrationRowsAsync(reopened,
                "SELECT id FROM transaction_children WHERE parent_id=1", [typeof(long)], [], asynchronous: true, deadline.Token);
        }
        AssertPrimaryKeyMigrationTransport(protocol);
        if (protocol != "embedded")
        {
            var transactionRequests = _requests.Where(request =>
                request.Path.Contains("/sql/transactions", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(transactionRequests);
            Assert.All(transactionRequests, request =>
                Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
            Assert.Equal(2, transactionRequests.Count(request => request.Path.EndsWith("/commit", StringComparison.Ordinal)));
        }
    }

    private static async Task OpenPrimaryKeyMigrationAsync(
        SndbConnection connection, bool asynchronous, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (asynchronous) await connection.OpenAsync(cancellationToken);
        else connection.Open();
    }

    private static async Task<int> ExecutePrimaryKeyMigrationAsync(
        SndbConnection connection, string sql, bool asynchronous, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = sql;
        return asynchronous ? await command.ExecuteNonQueryAsync(cancellationToken) : command.ExecuteNonQuery();
    }

    private static async Task AssertPrimaryKeyMigrationRowsAsync(SndbConnection connection, string sql,
        Type[] types, object?[][] expected, bool asynchronous, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = sql;
        await using var reader = asynchronous ? await command.ExecuteReaderAsync(cancellationToken) : command.ExecuteReader();
        Assert.Equal(types.Length, reader.FieldCount);
        for (int column = 0; column < types.Length; column++)
            Assert.Equal(types[column], reader.GetFieldType(column));
        foreach (object?[] row in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(asynchronous ? await reader.ReadAsync(cancellationToken) : reader.Read());
            Assert.Equal(types.Length, row.Length);
            for (int column = 0; column < row.Length; column++)
                Assert.Equal(row[column], reader.IsDBNull(column) ? null : reader.GetValue(column));
        }
        Assert.False(asynchronous ? await reader.ReadAsync(cancellationToken) : reader.Read());
    }

    private static void AssertPrimaryKeyMigrationMetadata(
        SndbConnection connection, string primaryKey, string codeType, string referenceType)
    {
        var columns = connection.GetSchema("Columns", [null, null, "migration_parents", null]);
        var rows = columns.Rows.Cast<DataRow>().ToArray();
        Assert.Equal(["id", "code", "label", "version"], rows.Select(row => (string)row["COLUMN_NAME"]).ToArray());
        Assert.Equal(["INT", codeType, "STRING", "INT"], rows.Select(row => (string)row["DATA_TYPE"]).ToArray());
        Assert.Equal(primaryKey, Assert.Single(rows, row => (bool)row["IS_PRIMARY_KEY"])["COLUMN_NAME"]);
        Assert.True((bool)rows[0]["IS_AUTO_INCREMENT"]);
        Assert.True((bool)rows[3]["IS_ROW_VERSION"]);
        Assert.False((bool)rows.Single(row => (string)row["COLUMN_NAME"] == primaryKey)["IS_NULLABLE"]);
        Assert.Equal("'base'", rows[2]["COLUMN_DEFAULT"]);

        var child = connection.GetSchema("Columns", [null, null, "migration_children", "parent_id"]);
        Assert.Equal(referenceType, Assert.Single(child.Rows.Cast<DataRow>())["DATA_TYPE"]);
        var foreignKey = Assert.Single(connection.GetSchema("ForeignKeys",
            [null, null, "migration_children", null]).Rows.Cast<DataRow>());
        Assert.Equal("parent_id", foreignKey["COLUMN_NAME"]);
        Assert.Equal(primaryKey, foreignKey["PRINCIPAL_COLUMN_NAME"]);
        Assert.Equal(["code", "label"], connection.GetSchema("Indexes",
            [null, null, "migration_parents", "ix_migration_code_label"]).Rows.Cast<DataRow>()
            .Select(row => (string)row["COLUMN_NAME"]).ToArray());
        Assert.Equal("id", Assert.Single(connection.GetSchema("Indexes",
            [null, null, "migration_parents", "ix_migration_old_id"]).Rows.Cast<DataRow>())["COLUMN_NAME"]);
        Assert.Equal("parent_id", Assert.Single(connection.GetSchema("Indexes",
            [null, null, "migration_children", "ix_migration_parent"]).Rows.Cast<DataRow>())["COLUMN_NAME"]);
    }

    private static async Task AssertPrimaryKeyMigrationStateAsync(SndbConnection connection,
        bool codeIsInteger, bool referenceIsInteger, bool definitionChanged,
        bool asynchronous, CancellationToken cancellationToken)
    {
        Type codeType = codeIsInteger ? typeof(long) : typeof(string);
        Type referenceType = referenceIsInteger ? typeof(long) : typeof(string);
        object codeOne = codeIsInteger ? 1L : "1";
        object codeTwo = codeIsInteger ? 2L : "2";
        object firstReference = definitionChanged ? referenceIsInteger ? 2L : "2" : 1L;
        object secondReference = definitionChanged ? referenceIsInteger ? 1L : "1" : 2L;
        await AssertPrimaryKeyMigrationRowsAsync(connection,
            "SELECT id,code,label,version FROM migration_parents ORDER BY id",
            [typeof(long), codeType, typeof(string), typeof(long)],
            [[1L, codeTwo, "updated", 2L], [2L, codeOne, "two", 1L]], asynchronous, cancellationToken);
        await AssertPrimaryKeyMigrationRowsAsync(connection,
            "SELECT id,parent_id FROM migration_children ORDER BY id", [typeof(long), referenceType],
            [[10L, firstReference], [11L, secondReference], [12L, null]], asynchronous, cancellationToken);
        await AssertPrimaryKeyMigrationRowsAsync(connection,
            "SELECT code FROM migration_parents WHERE 1=0", [codeType], [], asynchronous, cancellationToken);
        await AssertPrimaryKeyMigrationRowsAsync(connection,
            "SELECT parent_id FROM migration_children WHERE 1=0", [referenceType], [], asynchronous, cancellationToken);
        await AssertPrimaryKeyMigrationRowsAsync(connection,
            "SELECT label FROM migration_parents WHERE id=1", [typeof(string)], [["updated"]], asynchronous, cancellationToken);
        string codePredicate = codeIsInteger ? "2" : "'2'";
        await AssertPrimaryKeyMigrationRowsAsync(connection,
            "SELECT label FROM migration_parents WHERE code=" + codePredicate,
            [typeof(string)], [["updated"]], asynchronous, cancellationToken);
        string referencePredicate = definitionChanged ? referenceIsInteger ? "2" : "'2'" : "1";
        await AssertPrimaryKeyMigrationRowsAsync(connection,
            "SELECT id FROM migration_children WHERE parent_id=" + referencePredicate,
            [typeof(long)], [[10L]], asynchronous, cancellationToken);
    }

    private static async Task AssertRejectedPrimaryKeyMigrationStateAsync(SndbConnection connection,
        PrimaryKeyMigrationFailure item, bool asynchronous, CancellationToken cancellationToken)
    {
        string parent = "rejected_" + item.Name;
        Type codeType = item.CodeType == "INT" ? typeof(long) : typeof(string);
        Type referenceType = item.TargetType is null ? typeof(long) : codeType;
        object? reference = item.TargetType is null ? 1L : item.FirstValue;
        await AssertPrimaryKeyMigrationRowsAsync(connection,
            $"SELECT id,code,label FROM {parent} ORDER BY id", [typeof(long), codeType, typeof(string)],
            [[1L, item.FirstValue, "one"], [2L, item.SecondValue, "two"]], asynchronous, cancellationToken);
        await AssertPrimaryKeyMigrationRowsAsync(connection,
            $"SELECT id,parent_id FROM {parent}_children ORDER BY id", [typeof(long), referenceType],
            [[10L, reference], [11L, null]], asynchronous, cancellationToken);
        string predicate = item.FirstValue is null ? "code IS NULL" : "code=" + item.FirstLiteral;
        object?[][] matches = item.Name == "duplicate" ? [[1L], [2L]] : [[1L]];
        await AssertPrimaryKeyMigrationRowsAsync(connection,
            $"SELECT id FROM {parent} WHERE {predicate} ORDER BY id", [typeof(long)], matches, asynchronous, cancellationToken);
    }

    private static void AssertPrimaryKeyMigrationSnapshots(SndbConnection connection,
        Dictionary<(string Collection, string Table), DataTable> snapshots)
    {
        foreach (var (key, expected) in snapshots)
        {
            var actual = connection.GetSchema(key.Collection, [null, null, key.Table, null]);
            Assert.Equal(expected.Columns.Cast<DataColumn>().Select(column => column.ColumnName),
                actual.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
            Assert.Equal(expected.Rows.Count, actual.Rows.Count);
            for (int row = 0; row < expected.Rows.Count; row++)
                Assert.Equal(expected.Rows[row].ItemArray, actual.Rows[row].ItemArray);
        }
    }

    private static async Task AssertPrimaryKeyMigrationRejectedAsync(SndbConnection connection, string sql,
        string protocol, bool asynchronous, CancellationToken cancellationToken, string? expectedCode = null)
    {
        Exception? exception = await Record.ExceptionAsync(async () =>
        {
            await ExecutePrimaryKeyMigrationAsync(connection, sql, asynchronous, cancellationToken);
        });
        Assert.NotNull(exception);
        if (protocol != "embedded")
            Assert.IsType<SndbServerException>(exception);
        else
            Assert.True(exception is ArgumentException or InvalidOperationException or OverflowException or FormatException,
                exception.ToString());
        if (expectedCode is not null)
        {
            string? actualCode = exception switch
            {
                TableConstraintException embedded => embedded.ErrorCode,
                SndbServerException remote => remote.Error,
                _ => null,
            };
            Assert.Equal(expectedCode, actualCode);
        }
    }

    private void AssertPrimaryKeyMigrationTransport(string protocol)
    {
        if (protocol == "embedded") return;
        var requests = _requests.Where(request =>
            request.Path == $"/v1/db/{DatabaseName}/sql" || request.Path == "/v1/frame").ToArray();
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
        Assert.Contains(requests, request => request.Path == $"/v1/db/{DatabaseName}/sql");
        if (protocol == "frame-http2")
            Assert.Contains(requests, request => request.Path == "/v1/frame");
    }

    private sealed record PrimaryKeyMigrationFailure(string Name, string CodeType, string FirstLiteral,
        object? FirstValue, string SecondLiteral, object? SecondValue, string? TargetType);
}
