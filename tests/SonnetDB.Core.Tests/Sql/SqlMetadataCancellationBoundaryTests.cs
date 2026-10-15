using System.Text;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Routines;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>关系表元数据校验的取消点和注释持久化字节边界。</summary>
public sealed class SqlMetadataCancellationBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-metadata-boundary-" + Guid.NewGuid().ToString("N"));

    /// <summary>校验回调取消后，在下一行或下一页检查中停止，保留原目录和索引。</summary>
    /// <param name="cancelAfter">取消前已校验的行数。</param>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AlterStringLength_CancelDuringValidation_PreservesSchemaRowsAndIndexes(int cancelAfter)
    {
        using (var database = Open())
        {
            Create(database);
            var schema = database.Tables.Catalog.TryGet("Items")!;
            var store = database.Tables.Open("Items");
            byte[] before = File.ReadAllBytes(database.Tables.SchemaPath);
            using var stop = new CancellationTokenSource();
            int validated = 0;
            using (RoutineExecutionContext.EnterRoot(new SqlExecutionOptions { CancellationToken = stop.Token }))
            {
                var error = Assert.Throws<RoutineExecutionException>(() => database.Tables.AlterTableAlterColumn(
                    "Items", "Name", TableColumnType.String, isNullable: true, defaultExpressionSql: null,
                    convertValue: null, validateDecimalValues: (_, _) =>
                    {
                        validated++;
                        if (validated == cancelAfter)
                            stop.Cancel();
                    }, stringLength: 2, changeStringLength: true));
                Assert.Equal(RoutineErrorCodes.Cancelled, error.Code);
            }
            Assert.Equal(cancelAfter, validated);
            Assert.Same(schema, database.Tables.Catalog.TryGet("Items"));
            Assert.Same(schema, store.Schema);
            Assert.Equal(before, File.ReadAllBytes(database.Tables.SchemaPath));
            AssertOriginalMetadataAndRows(database);
        }
        using var reopened = Open();
        AssertOriginalMetadataAndRows(reopened);
    }

    /// <summary>元数据发布前的最终取消点不能更改内存或磁盘中的表列注释。</summary>
    /// <param name="columnComment">是否修改列注释。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Comment_CancelBeforeMetadataPublication_PreservesMemoryAndCatalog(bool columnComment)
    {
        using (var database = Open())
        {
            Create(database);
            var schema = database.Tables.Catalog.TryGet("Items")!;
            var store = database.Tables.Open("Items");
            byte[] before = File.ReadAllBytes(database.Tables.SchemaPath);
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            using (RoutineExecutionContext.EnterRoot(new SqlExecutionOptions { CancellationToken = stop.Token }))
            {
                var error = Assert.Throws<RoutineExecutionException>(() =>
                    database.Tables.SetComment("Items", columnComment ? "Name" : null, "新注释"));
                Assert.Equal(RoutineErrorCodes.Cancelled, error.Code);
            }
            Assert.Same(schema, database.Tables.Catalog.TryGet("Items"));
            Assert.Same(schema, store.Schema);
            Assert.Equal(before, File.ReadAllBytes(database.Tables.SchemaPath));
            AssertOriginalMetadataAndRows(database);
        }
        using var reopened = Open();
        AssertOriginalMetadataAndRows(reopened);
    }

    /// <summary>65535 个 UTF-8 字节可持久化，65536 字节拒绝后表列目录和重开结果保持。</summary>
    /// <param name="multibyte">是否使用三个 UTF-8 字节的中文字符。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Comment_Utf8ByteBoundary_RejectsOversizeWithoutChangingCatalog(bool multibyte)
    {
        string accepted = multibyte ? new string('汉', ushort.MaxValue / 3) : new string('a', ushort.MaxValue);
        string rejected = accepted + "a";
        Assert.Equal(ushort.MaxValue, Encoding.UTF8.GetByteCount(accepted));
        Assert.Equal(ushort.MaxValue + 1, Encoding.UTF8.GetByteCount(rejected));
        using (var database = Open())
        {
            Create(database);
            Execute(database, "COMMENT ON TABLE Items IS '" + accepted + "'");
            Execute(database, "COMMENT ON COLUMN Items.Name IS '" + accepted + "'");
            var schema = database.Tables.Catalog.TryGet("Items")!;
            var store = database.Tables.Open("Items");
            byte[] before = File.ReadAllBytes(database.Tables.SchemaPath);
            foreach (string target in new[] { "TABLE Items", "COLUMN Items.Name" })
            {
                Assert.Throws<InvalidDataException>(() => Execute(database, "COMMENT ON " + target + " IS '" + rejected + "'"));
                Assert.Same(schema, database.Tables.Catalog.TryGet("Items"));
                Assert.Same(schema, store.Schema);
                Assert.Equal(before, File.ReadAllBytes(database.Tables.SchemaPath));
                AssertComments(database, accepted, accepted);
                AssertRowsAndLength(database);
            }
        }
        using var reopened = Open();
        AssertComments(reopened, accepted, accepted);
        AssertRowsAndLength(reopened);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static void Create(Tsdb database)
    {
        Execute(database, "CREATE TABLE Items(Id INT,Name STRING(4),PRIMARY KEY(Id))");
        Execute(database, "INSERT INTO Items(Id,Name) VALUES(1,'a'),(2,'b'),(3,'c')");
        Execute(database, "CREATE UNIQUE INDEX ix_name ON Items(Name)");
        Execute(database, "COMMENT ON TABLE Items IS '旧表注释'");
        Execute(database, "COMMENT ON COLUMN Items.Name IS '旧列注释'");
    }

    private static void AssertOriginalMetadataAndRows(Tsdb database)
    {
        AssertComments(database, "旧表注释", "旧列注释");
        AssertRowsAndLength(database);
    }

    private static void AssertComments(Tsdb database, string tableComment, string columnComment)
    {
        var schema = database.Tables.Catalog.TryGet("Items")!;
        Assert.Equal(tableComment, schema.Comment);
        Assert.Equal(columnComment, schema.TryGetColumn("Name")!.Comment);
    }

    private static void AssertRowsAndLength(Tsdb database)
    {
        Assert.Equal(4, database.Tables.Catalog.TryGet("Items")!.TryGetColumn("Name")!.StringLength);
        var rows = Assert.IsType<SelectExecutionResult>(Execute(database, "SELECT Id FROM Items ORDER BY Id"));
        Assert.Equal(new object?[] { 1L, 2L, 3L }, rows.Rows.Select(static row => row[0]).ToArray());
        var indexed = Assert.IsType<SelectExecutionResult>(Execute(database, "SELECT Id FROM Items WHERE Name='b'"));
        Assert.Equal(2L, Assert.Single(indexed.Rows)[0]);
    }

    private static object? Execute(Tsdb database, string sql) => SqlExecutor.Execute(database, sql);

    /// <summary>只回收本测试在临时目录下创建的独占数据库。</summary>
    public void Dispose()
    {
        string absolute = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(absolute) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
            throw new InvalidOperationException("元数据边界测试目录超出临时目录。");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
