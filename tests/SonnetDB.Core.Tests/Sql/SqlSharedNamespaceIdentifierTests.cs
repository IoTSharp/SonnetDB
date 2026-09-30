using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Views;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证共享 SQL 数据源名称的大小写、引号和旧目录迁移合同。</summary>
public sealed class SqlSharedNamespaceIdentifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-shared-names-" + Guid.NewGuid().ToString("N"));

    /// <summary>仅回收当前测试独占的数据库目录。</summary>
    public void Dispose()
    {
        string root = Path.GetFullPath(_root);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(temporaryRoot, root, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("sndb-shared-names-", Path.GetFileName(root), StringComparison.Ordinal);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static string Create(string kind, string name) => kind switch
    {
        "table" => $"CREATE TABLE {name} (Id INT, PRIMARY KEY (Id))",
        "measurement" => $"CREATE MEASUREMENT {name} (Value FIELD INT)",
        "view" => $"CREATE VIEW {name} AS SELECT 1 AS Id",
        "materialized" => $"CREATE MATERIALIZED VIEW {name} AS SELECT 1 AS Id",
        _ => throw new ArgumentException("Unknown source kind.", nameof(kind)),
    };

    /// <summary>共享数据源的新名称不能与已有名称仅大小写不同。</summary>
    /// <param name="existingKind">已有数据源类型。</param>
    /// <param name="newKind">拟创建的数据源类型。</param>
    [Theory]
    [InlineData("table", "measurement")]
    [InlineData("measurement", "table")]
    [InlineData("table", "view")]
    [InlineData("view", "table")]
    [InlineData("measurement", "view")]
    [InlineData("view", "measurement")]
    [InlineData("table", "materialized")]
    [InlineData("materialized", "table")]
    [InlineData("measurement", "materialized")]
    [InlineData("materialized", "measurement")]
    [InlineData("view", "materialized")]
    [InlineData("materialized", "view")]
    [InlineData("view", "view")]
    [InlineData("materialized", "materialized")]
    public void Create_CaseVariantOfSharedSource_RejectsNewName(string existingKind, string newKind)
    {
        using var db = Open();
        SqlExecutor.Execute(db, Create(existingKind, "Device"));

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, Create(newKind, "\"device\"")));
    }

    /// <summary>视图引用和依赖保护在重启后保持一致。</summary>
    [Fact]
    public void Execute_ViewReferencesAndDependencies_PreservesRulesAfterReopen()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE Devices (Id INT, PRIMARY KEY (Id))");
            SqlExecutor.Execute(db, "INSERT INTO devices (ID) VALUES (7)");
            SqlExecutor.Execute(db, "CREATE VIEW DeviceView AS SELECT ID FROM DEVICES");
            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM DEVICEVIEW"));
            Assert.Equal(7L, Assert.Single(result.Rows)[0]);
            Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "SELECT * FROM \"deviceview\""));
            Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "DROP TABLE devices"));
        }

        using (var db = Open())
        {
            Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "DROP TABLE devices"));
            SqlExecutor.Execute(db, "DROP VIEW deviceview");
            SqlExecutor.Execute(db, "DROP TABLE devices");
        }
    }

    /// <summary>物化视图的查询与刷新遵循相同引号规则。</summary>
    [Fact]
    public void Execute_MaterializedViewReferences_UsesQuoteRules()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE MATERIALIZED VIEW DeviceSnapshot AS SELECT 1 AS Id");
        SqlExecutor.Execute(db, "REFRESH MATERIALIZED VIEW DEVICESNAPSHOT");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM devicesnapshot"));
        Assert.Equal(1L, Assert.Single(result.Rows)[0]);
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "REFRESH MATERIALIZED VIEW \"devicesnapshot\""));
        SqlExecutor.Execute(db, "DROP MATERIALIZED VIEW devicesnapshot");
    }

    /// <summary>历史视图重名保留引号精确访问能力。</summary>
    [Fact]
    public void Resolve_LegacyViewCaseConflict_RequiresExactQuote()
    {
        var catalog = new ViewCatalog();
        var upper = ViewDefinition.Create("Host", "SELECT 1 AS Id");
        var lower = ViewDefinition.Create("host", "SELECT 2 AS Id");
        catalog.LoadExisting(upper);
        catalog.LoadExisting(lower);

        Assert.Throws<InvalidOperationException>(() => catalog.Resolve("HOST", quoted: false));
        Assert.Same(upper, catalog.Resolve("Host", quoted: true));
        Assert.Same(lower, catalog.Resolve("host", quoted: true));
        Assert.Throws<InvalidOperationException>(() => catalog.Add(ViewDefinition.Create("HOST", "SELECT 3 AS Id")));
    }

    /// <summary>公开目录替换入口不能绕过新名称重名约束。</summary>
    [Fact]
    public void LoadOrReplace_NewViewCaseVariant_RejectsBypass()
    {
        var views = new ViewCatalog();
        views.Add(ViewDefinition.Create("Host", "SELECT 1 AS Id"));
        Assert.Throws<InvalidOperationException>(() =>
            views.LoadOrReplace(ViewDefinition.Create("host", "SELECT 2 AS Id")));

        var materialized = new MaterializedViewCatalog();
        materialized.Add(MaterializedViewDefinition.Create("Host", "SELECT 1 AS Id"));
        Assert.Throws<InvalidOperationException>(() =>
            materialized.LoadOrReplace(MaterializedViewDefinition.Create("host", "SELECT 2 AS Id")));
    }

    /// <summary>历史跨模型重名可用指明类型的 DDL 显式迁移。</summary>
    /// <param name="viewName">与表冲突的视图名称。</param>
    [Theory]
    [InlineData("Device")]
    [InlineData("device")]
    public void Execute_LegacyCrossModelConflict_AllowsTypedMigration(string viewName)
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE Device (Id INT, PRIMARY KEY (Id))");
            SqlExecutor.Execute(db, "INSERT INTO Device (Id) VALUES (7)");
        }
        ViewDefinitionCodec.Save(Path.Combine(_root, "views", ViewDefinitionCodec.FileName),
            [ViewDefinition.Create(viewName, "SELECT 42 AS Id")]);

        using var reopened = Open();
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(reopened, "SELECT * FROM DEVICE"));
        if (string.Equals(viewName, "Device", StringComparison.Ordinal))
            Assert.Throws<InvalidOperationException>(() =>
                SqlExecutor.Execute(reopened, "SELECT * FROM \"Device\""));
        else
        {
            var view = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(reopened, "SELECT * FROM \"device\""));
            Assert.Equal(42L, Assert.Single(view.Rows)[0]);
            var table = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(reopened, "SELECT * FROM \"Device\""));
            Assert.Equal(7L, Assert.Single(table.Rows)[0]);
        }

        SqlExecutor.Execute(reopened, "DROP VIEW \"" + viewName + "\"");
        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(reopened, "SELECT * FROM device"));
        Assert.Equal(7L, Assert.Single(result.Rows)[0]);
    }

    /// <summary>EXPLAIN 在规划前验证引用名称。</summary>
    [Fact]
    public void Explain_UnquotedCaseVariant_BindsBeforePlanning()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE Devices (Id INT, PRIMARY KEY (Id))");

        Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "EXPLAIN SELECT ID FROM DEVICES"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "EXPLAIN SELECT \"id\" FROM Devices"));
    }

    /// <summary>历史无效视图可删除，删除前仍保护其基础源。</summary>
    [Fact]
    public void Drop_LegacyViewWithInvalidColumn_KeepsSourceGuardAndAllowsOwnRemoval()
    {
        using (var db = Open())
            SqlExecutor.Execute(db, "CREATE TABLE Devices (Id INT, PRIMARY KEY (Id))");
        ViewDefinitionCodec.Save(Path.Combine(_root, "views", ViewDefinitionCodec.FileName),
            [ViewDefinition.Create("Broken", "SELECT Missing FROM DEVICES")]);

        using var reopened = Open();
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(reopened, "DROP TABLE devices"));
        SqlExecutor.Execute(reopened, "DROP VIEW broken");
        SqlExecutor.Execute(reopened, "DROP TABLE devices");
    }

    /// <summary>历史视图中的无效列不阻塞无关表迁移。</summary>
    [Fact]
    public void Alter_UnrelatedTableWithLegacyInvalidView_AllowsTypedMigration()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db, "CREATE TABLE Devices (Id INT, PRIMARY KEY (Id))");
            SqlExecutor.Execute(db, "CREATE TABLE Other (Id INT, PRIMARY KEY (Id))");
        }
        ViewDefinitionCodec.Save(Path.Combine(_root, "views", ViewDefinitionCodec.FileName),
            [ViewDefinition.Create("Broken", "SELECT Missing FROM DEVICES")]);

        using var reopened = Open();
        SqlExecutor.Execute(reopened, "ALTER TABLE \"Other\" RENAME TO OtherRenamed");
        SqlExecutor.Execute(reopened, "DROP TABLE otherrenamed");
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(reopened, "DROP TABLE devices"));
    }

    /// <summary>嵌套子查询中的视图来源同样受依赖保护。</summary>
    [Fact]
    public void Drop_SourceReferencedByNestedViewQuery_RejectsTarget()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE Devices (Id INT, PRIMARY KEY (Id))");
        SqlExecutor.Execute(db, "CREATE VIEW Nested AS SELECT (SELECT Id FROM DEVICES) AS ReferenceId");

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "DROP TABLE devices"));
        SqlExecutor.Execute(db, "DROP VIEW nested");
        SqlExecutor.Execute(db, "DROP TABLE devices");
    }

    /// <summary>源名称歧义不阻止删除旧冲突视图并完成迁移。</summary>
    /// <param name="viewName">历史冲突视图名称。</param>
    [Theory]
    [InlineData("Device")]
    [InlineData("device")]
    public void Drop_LegacyCrossModelViewWithConflictingSource_AllowsTypedRemoval(string viewName)
    {
        using (var db = Open())
            SqlExecutor.Execute(db, "CREATE TABLE Device (Id INT, PRIMARY KEY (Id))");
        ViewDefinitionCodec.Save(Path.Combine(_root, "views", ViewDefinitionCodec.FileName),
            [ViewDefinition.Create(viewName, "SELECT Missing FROM Device")]);

        using var reopened = Open();
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(reopened, "SELECT * FROM DEVICE"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(reopened, "ALTER TABLE \"Device\" RENAME TO MigratedDevice"));
        SqlExecutor.Execute(reopened, "DROP VIEW \"" + viewName + "\"");
        SqlExecutor.Execute(reopened, "ALTER TABLE \"Device\" RENAME TO MigratedDevice");
        Assert.NotNull(reopened.Tables.Catalog.TryGet("MigratedDevice"));
    }
}
