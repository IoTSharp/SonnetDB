using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.IO.Hashing;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>关系表注释、UTF-16 长度声明与目录演进的真实持久化合同。</summary>
public sealed class SqlRelationalColumnMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-metadata-" + Guid.NewGuid().ToString("N"));

    /// <summary>各个 DDL 写法保存长度，VARCHAR 与 STRING 使用相同合同。</summary>
    [Theory]
    [InlineData("CREATE TABLE Items(Id INT,Name STRING(4),PRIMARY KEY(Id))", 0)]
    [InlineData("CREATE TABLE Items(Id INT,Name VARCHAR(4),PRIMARY KEY(Id))", 0)]
    [InlineData("ALTER TABLE Items ADD Name STRING(4)", 1)]
    [InlineData("ALTER TABLE Items ADD COLUMN Name VARCHAR(4)", 1)]
    [InlineData("ALTER TABLE Items ALTER Name TYPE STRING(4)", 2)]
    [InlineData("ALTER TABLE Items ALTER COLUMN Name SET DATA TYPE VARCHAR(4)", 2)]
    [InlineData("ALTER TABLE Items ALTER Name VARCHAR(4) NULL", 2)]
    public void Parse_LengthDeclaration_PreservesBound(string sql, int kind)
    {
        var parsed = SqlParser.Parse(sql);
        int? length = kind switch
        {
            0 => Assert.IsType<CreateTableStatement>(parsed).Columns[1].StringLength,
            1 => Assert.IsType<AlterTableAddColumnStatement>(parsed).StringLength,
            _ => Assert.IsType<AlterTableAlterColumnStatement>(parsed).StringLength,
        };
        Assert.Equal(4, length);
    }

    /// <summary>错误长度必须在解析时拒绝，不能降级为无界。</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("NULL")]
    [InlineData("")]
    [InlineData("2,3")]
    public void Parse_InvalidLength_Rejects(string length)
        => Assert.Throws<SqlParseException>(() => SqlParser.Parse(
            $"CREATE TABLE Items(Id INT,Name STRING({length}),PRIMARY KEY(Id))"));

    /// <summary>表列注释精确保留中文、引号和换行，重命名保留，删除和重建不泄漏注释。</summary>
    [Fact]
    public void Comment_UnicodeAndLifecycle_PersistsAcrossReopen()
    {
        const string comment = "中文 '单引号' \"双引号\"\n下一行";
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE Items(Id INT,Name STRING(4),PRIMARY KEY(Id))");
            Execute(db, "COMMENT ON TABLE items IS " + Quote(comment));
            Execute(db, "COMMENT ON COLUMN ITEMS.name IS " + Quote(comment));
            Assert.Equal(comment, db.Tables.Catalog.TryGet("Items")!.Comment);
            Assert.Equal(comment, Scalar(db, "SELECT table_comment FROM information_schema.tables WHERE table_name='Items'"));
            Assert.Equal(comment, Scalar(db, "SELECT column_comment FROM information_schema.columns WHERE table_name='Items' AND column_name='Name'"));
            Execute(db, "ALTER TABLE Items RENAME COLUMN Name TO DisplayName");
            Execute(db, "ALTER TABLE Items RENAME TO Products");
            Assert.Equal(comment, db.Tables.Catalog.TryGet("Products")!.TryGetColumn("DisplayName")!.Comment);
        }
        using (var reopened = Open())
        {
            var schema = reopened.Tables.Catalog.TryGet("Products")!;
            Assert.Equal(comment, schema.Comment);
            Assert.Equal(comment, schema.TryGetColumn("DisplayName")!.Comment);
            Assert.Equal(4, schema.TryGetColumn("DisplayName")!.StringLength);
            Execute(reopened, "COMMENT ON TABLE Products IS ''");
            Execute(reopened, "COMMENT ON COLUMN Products.DisplayName IS ''");
            Assert.Equal(string.Empty, reopened.Tables.Catalog.TryGet("Products")!.Comment);
            Assert.Equal(string.Empty, reopened.Tables.Catalog.TryGet("Products")!.TryGetColumn("DisplayName")!.Comment);
            Execute(reopened, "COMMENT ON TABLE Products IS NULL");
            Execute(reopened, "COMMENT ON COLUMN Products.DisplayName IS NULL");
            Assert.Null(reopened.Tables.Catalog.TryGet("Products")!.Comment);
            Assert.Null(reopened.Tables.Catalog.TryGet("Products")!.TryGetColumn("DisplayName")!.Comment);
            Execute(reopened, "COMMENT ON COLUMN Products.DisplayName IS '旧列'");
            Execute(reopened, "ALTER TABLE Products DROP COLUMN DisplayName");
            Execute(reopened, "ALTER TABLE Products ADD DisplayName STRING(2)");
            Assert.Null(reopened.Tables.Catalog.TryGet("Products")!.TryGetColumn("DisplayName")!.Comment);
            Execute(reopened, "DROP TABLE Products");
            Execute(reopened, "CREATE TABLE Products(Id INT,PRIMARY KEY(Id))");
            Assert.Null(reopened.Tables.Catalog.TryGet("Products")!.Comment);
        }
        using var final = Open();
        Assert.Null(final.Tables.Catalog.TryGet("Products")!.Comment);
        Assert.Null(final.Tables.Catalog.TryGet("Products")!.TryGetColumn("DisplayName"));
    }

    /// <summary>COMMENT 使用与其他 DDL 相同的精确引号名称合同。</summary>
    [Fact]
    public void Comment_QuotedIdentifier_RejectsWrongCaseWithoutMutation()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE \"Device Items\"(Id INT,\"Display Name\" STRING,PRIMARY KEY(Id))");
        Execute(db, "COMMENT ON COLUMN \"Device Items\".\"Display Name\" IS '好'");
        Assert.Throws<InvalidOperationException>(() => Execute(db, "COMMENT ON COLUMN \"device items\".\"Display Name\" IS '坏'"));
        Assert.Throws<InvalidOperationException>(() => Execute(db, "COMMENT ON COLUMN \"Device Items\".\"display name\" IS '坏'"));
        Assert.Equal("好", db.Tables.Catalog.TryGet("Device Items")!.TryGetColumn("Display Name")!.Comment);
    }

    /// <summary>直接 API、批量、SQL 和 UPDATE 都执行上限；NULL、空串和代理对遵守 UTF-16 计数。</summary>
    [Fact]
    public void StringLength_AllWriteEntrypoints_RejectWithoutTruncationAcrossReopen()
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE Items(Id INT,Name STRING(2),PRIMARY KEY(Id))");
            Execute(db, "INSERT INTO Items(Id,Name) VALUES(1,''),(2,NULL),(3,'甲乙'),(4,'😀')");
            AssertLengthFailure(() => Execute(db, "INSERT INTO Items(Id,Name) VALUES(5,'ab'),(6,'abc')"));
            AssertLengthFailure(() => Execute(db, "UPDATE Items SET Name='😀a' WHERE Id=1"));
            var store = db.Tables.Open("Items");
            AssertLengthFailure(() => store.Insert([5L, "abc"]));
            AssertLengthFailure(() => store.Upsert([1L, "abc"]));
            AssertLengthFailure(() => store.InsertMany([[5L, "a"], [6L, "abc"]]));
            Assert.Equal(new object?[] { "", null, "甲乙", "😀" }, Values(db, "SELECT Name FROM Items ORDER BY Id"));
        }
        using var reopened = Open();
        Assert.Equal(2, reopened.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name")!.StringLength);
        AssertLengthFailure(() => reopened.Tables.Open("Items").Insert([5L, "😀a"]));
        Assert.Equal(4L, Scalar(reopened, "SELECT COUNT(*) FROM Items"));
    }

    /// <summary>缩短先检查全部已有行；失败保留目录、原行和索引，合法扩大/缩短/移除上限可重开。</summary>
    [Fact]
    public void AlterStringLength_LaterRowInvalid_RollsBackSchemaRowsAndIndexes()
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE Items(Id INT,Name STRING(4),PRIMARY KEY(Id))");
            Execute(db, "INSERT INTO Items(Id,Name) VALUES(1,'a'),(2,'甲乙丙丁')");
            Execute(db, "CREATE UNIQUE INDEX ix_name ON Items(Name)");
            Execute(db, "COMMENT ON COLUMN Items.Name IS '名称'");
            AssertLengthFailure(() => Execute(db, "ALTER TABLE Items ALTER Name TYPE STRING(2)"));
            Assert.Equal(4, db.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name")!.StringLength);
            Assert.Equal(new object?[] { "a", "甲乙丙丁" }, Values(db, "SELECT Name FROM Items ORDER BY Id"));
            Assert.Equal(2L, Scalar(db, "SELECT Id FROM Items WHERE Name='甲乙丙丁'"));
            Execute(db, "ALTER TABLE Items ALTER Name TYPE STRING(8)");
            Execute(db, "UPDATE Items SET Name='中文' WHERE Id=2");
            Execute(db, "ALTER TABLE Items ALTER Name SET DATA TYPE VARCHAR(2)");
            Execute(db, "ALTER TABLE Items ALTER Name SET DEFAULT '甲乙'");
            AssertLengthFailure(() => Execute(db, "ALTER TABLE Items ALTER Name TYPE STRING(1)"));
            Execute(db, "ALTER TABLE Items ALTER Name SET NOT NULL");
            Assert.Equal(2, db.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name")!.StringLength);
            Execute(db, "ALTER TABLE Items ALTER Name TYPE STRING");
            Execute(db, "INSERT INTO Items(Id,Name) VALUES(3,'unbounded')");
            Assert.Null(db.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name")!.StringLength);
        }
        using var reopened = Open();
        Assert.Null(reopened.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name")!.StringLength);
        Assert.Equal("名称", reopened.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name")!.Comment);
        Assert.Equal("'甲乙'", reopened.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name")!.DefaultExpressionSql);
        Assert.Equal(3L, Scalar(reopened, "SELECT Id FROM Items WHERE Name='unbounded'"));
    }

    /// <summary>建表、ADD 与后续默认值不能绕过长度检查，类型转换也必须全量无损预检。</summary>
    [Fact]
    public void StringLength_DefaultsAndTypeConversion_ValidateBeforePublishing()
    {
        using var db = Open();
        AssertLengthFailure(() => Execute(db, "CREATE TABLE Invalid(Id INT,Name STRING(2) DEFAULT 'abc',PRIMARY KEY(Id))"));
        Assert.Null(db.Tables.Catalog.TryGet("Invalid"));
        Execute(db, "CREATE TABLE Items(Id INT,Number JSON,PRIMARY KEY(Id))");
        Execute(db, "INSERT INTO Items(Id,Number) VALUES(1,'12'),(2,'123')");
        AssertLengthFailure(() => Execute(db, "ALTER TABLE Items ADD Name STRING(2) DEFAULT 'abc'"));
        Assert.Null(db.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name"));
        Execute(db, "ALTER TABLE Items ADD Name STRING(2) DEFAULT '中文'");
        AssertLengthFailure(() => Execute(db, "ALTER TABLE Items ALTER Name SET DEFAULT 'abc'"));
        Assert.Equal("'中文'", db.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name")!.DefaultExpressionSql);
        AssertLengthFailure(() => Execute(db, "ALTER TABLE Items ALTER Number TYPE STRING(2)"));
        Assert.Equal(TableColumnType.Json, db.Tables.Catalog.TryGet("Items")!.TryGetColumn("Number")!.DataType);
        Assert.Equal(new object?[] { "12", "123" }, Values(db, "SELECT Number FROM Items ORDER BY Id"));
        Execute(db, "ALTER TABLE Items ALTER Number TYPE STRING(3)");
        Assert.Equal(new object?[] { "12", "123" }, Values(db, "SELECT Number FROM Items ORDER BY Id"));
    }

    /// <summary>各种克隆保留长度与注释；删列清理、改类型清理长度、重命名迁移属性。</summary>
    [Theory]
    [InlineData("index")]
    [InlineData("drop-index")]
    [InlineData("foreign-key")]
    [InlineData("drop-foreign-key")]
    [InlineData("check")]
    [InlineData("drop-check")]
    [InlineData("add-column")]
    [InlineData("alter-column")]
    [InlineData("rename-column")]
    [InlineData("rename-table")]
    [InlineData("primary-key")]
    [InlineData("drop-column")]
    [InlineData("change-type")]
    public void SchemaClone_Metadata_StaysAttachedToOriginalColumn(string operation)
    {
        var schema = TableSchema.Create("Items", [("Id", TableColumnType.Int64, false), ("Name", TableColumnType.String, true)], ["Id"])
            .WithComment("表注释").WithColumnLength("Name", 4).WithColumnComment("Name", "列注释");
        var index = new TableIndexDefinition("ix_name", ["Name"], false);
        var foreignKey = new TableForeignKeyDefinition("fk_id", ["Id"], "Parent", ["Id"]);
        var check = new TableCheckConstraintDefinition("ck_id", "Id > 0");
        var clone = operation switch
        {
            "index" => schema.WithIndex(index),
            "drop-index" => schema.WithIndex(index).WithoutIndex("ix_name"),
            "foreign-key" => schema.WithForeignKey(foreignKey),
            "drop-foreign-key" => schema.WithForeignKey(foreignKey).WithoutForeignKey("fk_id"),
            "check" => schema.WithCheckConstraint(check),
            "drop-check" => schema.WithCheckConstraint(check).WithoutCheckConstraint("ck_id"),
            "add-column" => schema.WithAddedColumn("Extra", TableColumnType.String, true),
            "alter-column" => schema.WithAlteredColumn("Name", TableColumnType.String, false, null),
            "rename-column" => schema.WithRenamedColumn("Name", "Title"),
            "rename-table" => schema.WithName("Products"),
            "primary-key" => schema.WithPrimaryKey(["Id"]),
            "drop-column" => schema.WithoutColumn("Name"),
            "change-type" => schema.WithAlteredColumn("Name", TableColumnType.Int64, true, null),
            _ => throw new InvalidOperationException("未知克隆。"),
        };
        Assert.Equal("表注释", clone.Comment);
        var column = clone.TryGetColumn(operation == "rename-column" ? "Title" : "Name");
        if (operation == "drop-column") Assert.Null(column);
        else
        {
            Assert.Equal("列注释", column!.Comment);
            Assert.Equal(operation == "change-type" ? null : (int?)4, column.StringLength);
        }
        string path = Path.Combine(_root, "clone.tblschema");
        TableSchemaCodec.Save(path, [clone]);
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(path).AsSpan(8, 4)));
        var loaded = Assert.Single(TableSchemaCodec.Load(path));
        Assert.Equal(clone.Comment, loaded.Comment);
        Assert.Equal(clone.Columns, loaded.Columns);
    }

    /// <summary>准确长度可由 ADO schema、零行读者、别名、CTE 和信息 schema 获取，重开仍相同。</summary>
    [Fact]
    public void AdoSchema_DeclaredLengthAndComments_AreAccurateForEmptyResultsAfterReopen()
    {
        using (var connection = Connect())
        {
            Execute(connection, "CREATE TABLE Items(Id INT,Name STRING(4),Notes STRING,PRIMARY KEY(Id))");
            Execute(connection, "COMMENT ON TABLE Items IS '设备'");
            Execute(connection, "COMMENT ON COLUMN Items.Name IS '名称'");
            AssertSchema(connection);
        }
        using var reopened = Connect();
        AssertSchema(reopened);
        Execute(reopened, "ALTER TABLE Items ALTER Name TYPE STRING(2)");
        Assert.Equal(2, reopened.GetSchema("Columns", [null, null, "Items", "Name"]).Rows[0]["CHARACTER_MAXIMUM_LENGTH"]);
    }

    /// <summary>目录临时文件不可写时，注释和合法缩短也必须回滚内存目录与现有索引。</summary>
    [Theory]
    [InlineData("COMMENT ON TABLE Items IS '新表注释'")]
    [InlineData("COMMENT ON COLUMN Items.Name IS '新列注释'")]
    [InlineData("ALTER TABLE Items ALTER Name TYPE STRING(2)")]
    public void Metadata_CatalogWriteFails_RollsBackMemoryAndPersistentState(string change)
    {
        byte[] before;
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE Items(Id INT,Name STRING(4),PRIMARY KEY(Id))");
            Execute(db, "INSERT INTO Items(Id,Name) VALUES(1,'中文')");
            Execute(db, "CREATE UNIQUE INDEX ix_name ON Items(Name)");
            Execute(db, "COMMENT ON TABLE Items IS '旧表注释'");
            Execute(db, "COMMENT ON COLUMN Items.Name IS '旧列注释'");
            before = File.ReadAllBytes(db.Tables.SchemaPath);
            using (var blocked = new FileStream(db.Tables.SchemaPath + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                Assert.ThrowsAny<IOException>(() => Execute(db, change));
            Assert.Equal(before, File.ReadAllBytes(db.Tables.SchemaPath));
            Assert.Equal("旧表注释", db.Tables.Catalog.TryGet("Items")!.Comment);
            Assert.Equal("旧列注释", db.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name")!.Comment);
            Assert.Equal(4, db.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name")!.StringLength);
            Assert.Equal(1L, Scalar(db, "SELECT Id FROM Items WHERE Name='中文'"));
        }
        using var reopened = Open();
        Assert.Equal("旧表注释", reopened.Tables.Catalog.TryGet("Items")!.Comment);
        Assert.Equal("旧列注释", reopened.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name")!.Comment);
        Assert.Equal(4, reopened.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name")!.StringLength);
        Assert.Equal(1L, Scalar(reopened, "SELECT Id FROM Items WHERE Name='中文'"));
    }

    /// <summary>带正确 CRC 的非法长度或注释存在标记必须拒绝，不能读取成无界声明。</summary>
    [Theory]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("comment")]
    public void Codec_InvalidV12MetadataWithValidChecksum_Rejects(string corruption)
    {
        var schema = TableSchema.Create("Items", [("Name", TableColumnType.String, false)], ["Name"])
            .WithColumnLength("Name", 4);
        string path = Path.Combine(_root, "invalid.tblschema");
        TableSchemaCodec.Save(path, [schema]);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)));
        // Header + table name + creation ticks + column count + absent table comment + column name + flags + absent default.
        int lengthOffset = 32 + 2 + "Items"u8.Length + 8 + 2 + 1 + 2 + "Name"u8.Length + 2 + 2;
        if (corruption == "comment") bytes[lengthOffset + 4] = 2;
        else BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(lengthOffset, 4), corruption == "zero" ? 0 : -2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 16, 4), Crc32.HashToUInt32(bytes.AsSpan(32, bytes.Length - 48)));
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => TableSchemaCodec.Load(path));
    }

    private static void AssertSchema(SndbConnection connection)
    {
        DataRow column = connection.GetSchema("Columns", [null, null, "Items", "Name"]).Rows[0];
        Assert.Equal(4, column["CHARACTER_MAXIMUM_LENGTH"]);
        Assert.Equal("名称", column["REMARKS"]);
        Assert.Equal(int.MaxValue, connection.GetSchema("Columns", [null, null, "Items", "Notes"]).Rows[0]["CHARACTER_MAXIMUM_LENGTH"]);
        Assert.Equal("设备", connection.GetSchema("Tables", [null, null, "Items"]).Rows[0]["REMARKS"]);
        foreach (string sql in new[] { "SELECT Name AS DisplayName FROM Items WHERE Id=0", "WITH Names AS(SELECT Name FROM Items) SELECT Name FROM Names" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            Assert.Equal(4, reader.GetColumnSchema()[0].ColumnSize);
            Assert.Equal(4, reader.GetSchemaTable()!.Rows[0]["ColumnSize"]);
            Assert.False(reader.Read());
        }
        using var metadata = connection.CreateCommand();
        metadata.CommandText = "SELECT character_maximum_length,column_comment FROM information_schema.columns WHERE table_name='Items' AND column_name='Name'";
        using var result = metadata.ExecuteReader();
        Assert.True(result.Read());
        Assert.Equal(4L, result.GetInt64(0));
        Assert.Equal("名称", result.GetString(1));
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private SndbConnection Connect()
    {
        var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        return connection;
    }
    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static object? Execute(Tsdb db, string sql) => SqlExecutor.Execute(db, sql);
    private static void Execute(SndbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static object?[] Values(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(Execute(db, sql)).Rows.Select(static row => row[0]).ToArray();
    private static object? Scalar(Tsdb db, string sql) => Values(db, sql).Single();
    private static void AssertLengthFailure(Action action)
        => Assert.Equal(TableConstraintException.StringLengthViolation, Assert.Throws<TableConstraintException>(action).ErrorCode);

    /// <summary>仅删除本测试在临时目录内创建的独占数据库目录。</summary>
    public void Dispose()
    {
        string absolute = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(absolute) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
            throw new InvalidOperationException("元数据测试目录超出临时目录。");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
