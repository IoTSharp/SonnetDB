using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证字符串长度 CHECK 的实际写入、空值与持久化合同。</summary>
public sealed class TableLengthCheckTests
{
    /// <summary>长度以 UTF-16 代码单元计数，越界写入在数据库重新打开后仍被拒绝。</summary>
    [Theory]
    [InlineData("length")]
    [InlineData("CHAR_LENGTH")]
    public void Check_StringLengthBoundaries_EnforcesWritesAfterSchemaReload(string function)
    {
        using var directory = new TestDirectory();
        using (var database = directory.Open())
        {
            directory.Execute(database, $"""
                CREATE TABLE Names (
                    Id INT,
                    Value STRING,
                    PRIMARY KEY (Id),
                    CONSTRAINT CK_Name_Length CHECK ({function}(Value) <= 2)
                )
                """);
            directory.Execute(database, """
                INSERT INTO Names (Id, Value)
                VALUES (1, ''), (2, 'a'), (3, 'ab'), (4, NULL), (5, '😀')
                """);
            var lengths = Assert.IsType<SelectExecutionResult>(directory.Execute(
                database, $"SELECT {function}(Value) FROM Names ORDER BY Id"));
            Assert.Equal(new object?[] { 0L, 1L, 2L, null, 2L }, lengths.Rows.Select(row => row[0]));

            AssertLengthViolation(directory, database, "INSERT INTO Names (Id, Value) VALUES (6, 'abc')");
            AssertLengthViolation(directory, database, "INSERT INTO Names (Id, Value) VALUES (7, '😀a')");
            AssertLengthViolation(directory, database, "INSERT INTO Names (Id, Value) VALUES (8, 'ab ')");
            directory.Execute(database, "UPDATE Names SET Value = '甲乙' WHERE Id = 1");
            AssertLengthViolation(directory, database, "UPDATE Names SET Value = 'abc' WHERE Id = 2");
            AssertRowsUnchanged(directory, database);
        }

        using (var reopened = directory.Open())
        {
            Assert.Equal("CK_Name_Length", Assert.Single(reopened.Tables.Catalog.TryGet("Names")!.CheckConstraints).Name);
            AssertRowsUnchanged(directory, reopened);
            AssertLengthViolation(directory, reopened, "INSERT INTO Names (Id, Value) VALUES (6, '😀a')");
            AssertLengthViolation(directory, reopened, "UPDATE Names SET Value = 'abc' WHERE Id = 1");
            AssertRowsUnchanged(directory, reopened);
        }
    }

    /// <summary>仅允许单参数长度函数，星号、错误参数数量和其他函数不能绕过 CHECK 白名单。</summary>
    [Theory]
    [InlineData("length() <= 2")]
    [InlineData("char_length(Value, Value) <= 2")]
    [InlineData("length(*) <= 2")]
    [InlineData("trim(Value) = Value")]
    public void CreateTable_InvalidLengthOrUnsupportedFunction_RejectsDefinition(string expression)
    {
        using var directory = new TestDirectory();
        using var database = directory.Open();
        Assert.Throws<NotSupportedException>(() => directory.Execute(database,
            $"CREATE TABLE Names (Id INT, Value STRING, PRIMARY KEY (Id), CHECK ({expression}))"));
        Assert.Null(database.Tables.Catalog.TryGet("Names"));
    }

    private static void AssertLengthViolation(TestDirectory directory, Tsdb database, string sql)
    {
        var error = Assert.Throws<TableConstraintException>(() => directory.Execute(database, sql));
        Assert.Equal(TableConstraintException.CheckViolation, error.ErrorCode);
        Assert.Equal("CK_Name_Length", error.ConstraintName);
    }

    private static void AssertRowsUnchanged(TestDirectory directory, Tsdb database)
    {
        var rows = Assert.IsType<SelectExecutionResult>(directory.Execute(database, "SELECT Id, Value FROM Names ORDER BY Id"));
        Assert.Equal(new object?[] { 1L, 2L, 3L, 4L, 5L }, rows.Rows.Select(row => row[0]));
        Assert.Equal(new object?[] { "甲乙", "a", "ab", null, "😀" }, rows.Rows.Select(row => row[1]));
    }

    private sealed class TestDirectory : IDisposable
    {
        private const string Prefix = "sndb-length-check-";
        private readonly string _owner = Guid.NewGuid().ToString("N");
        private readonly string _path;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
        private int _statementCount;

        internal TestDirectory()
        {
            _path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + _owner));
            if (Directory.Exists(_path))
                throw new InvalidOperationException("拒绝复用已存在的测试目录。");
            Directory.CreateDirectory(_path);
            File.WriteAllText(Path.Combine(_path, "test-owner"), _owner);
        }

        internal Tsdb Open()
        {
            _timeout.Token.ThrowIfCancellationRequested();
            return Tsdb.Open(new TsdbOptions
            {
                RootDirectory = _path,
                BackgroundFlush = new BackgroundFlushOptions { Enabled = false }
            });
        }

        internal object? Execute(Tsdb database, string sql)
        {
            _timeout.Token.ThrowIfCancellationRequested();
            if (++_statementCount > 24)
                throw new InvalidOperationException("测试超过最多 24 条 SQL 的预算。");
            return SqlExecutor.Execute(database, null, sql, null, null,
                new SqlExecutionOptions { CancellationToken = _timeout.Token });
        }

        /// <summary>仅回收路径及所有权标记均匹配的本次测试目录。</summary>
        public void Dispose()
        {
            _timeout.Dispose();
            if (!Directory.Exists(_path))
                return;
            string absolute = Path.GetFullPath(_path);
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            string marker = Path.Combine(absolute, "test-owner");
            if (!string.Equals(Path.GetDirectoryName(absolute), temp, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(absolute), Prefix + _owner, StringComparison.Ordinal)
                || !File.Exists(marker)
                || File.ReadAllText(marker) != _owner)
            {
                throw new InvalidOperationException("拒绝删除绝对路径或所有权标记不匹配的测试目录。");
            }
            Directory.Delete(absolute, recursive: true);
        }
    }
}
