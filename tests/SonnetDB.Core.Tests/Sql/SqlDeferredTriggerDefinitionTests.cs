using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Exceptions;
using SonnetDB.Routines;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlDeferredTriggerDefinitionTests : IDisposable
{
    private const long CreatedAt = 638900000000000000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-m39-deferred-definition-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        string path = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("sndb-m39-deferred-definition-", StringComparison.Ordinal))
            throw new InvalidOperationException("测试目录不属于当前任务。");
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    [Theory]
    [InlineData("INSERT", "NEW.id")]
    [InlineData("UPDATE", "OLD.id")]
    [InlineData("DELETE", "OLD.id")]
    public void Parse_DeferredConstraint_RecordsExactPhaseAndRowContext(string triggerEvent, string rowId)
    {
        var statement = Assert.IsType<CreateTriggerStatement>(SqlParser.Parse($"""
            CREATE CONSTRAINT TRIGGER verify_order AFTER {triggerEvent} ON orders
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW FOLLOWS preceding_check
            WHEN ({rowId} > 0) LANGUAGE SQL AS BEGIN
                INSERT INTO audit_outbox (id) VALUES ({rowId});
            END
            """));

        Assert.True(statement.IsConstraint);
        Assert.True(statement.InitiallyDeferred);
        Assert.Equal(SqlTriggerTiming.After, statement.Timing);
        Assert.Equal(SqlTriggerLevel.Row, statement.Level);
        Assert.Equal("preceding_check", statement.RelativeTo);
        Assert.False(statement.Precedes);
        TriggerDefinition definition = TriggerDefinition.Create(statement, CreatedAt);
        Assert.True(definition.IsConstraint);
        Assert.True(definition.InitiallyDeferred);
        Assert.Contains("id", definition.RowColumns);
    }

    [Theory]
    [InlineData("CREATE CONSTRAINT ignored verify_order AFTER INSERT ON orders DEFERRABLE INITIALLY DEFERRED FOR EACH ROW")]
    [InlineData("CREATE CONSTRAINT TRIGGER verify_order AFTER INSERT ON orders FOR EACH ROW")]
    [InlineData("CREATE CONSTRAINT TRIGGER verify_order AFTER INSERT ON orders DEFERRABLE FOR EACH ROW")]
    [InlineData("CREATE CONSTRAINT TRIGGER verify_order AFTER INSERT ON orders INITIALLY DEFERRED FOR EACH ROW")]
    [InlineData("CREATE CONSTRAINT TRIGGER verify_order AFTER INSERT ON orders DEFERRABLE INITIALLY IMMEDIATE FOR EACH ROW")]
    [InlineData("CREATE CONSTRAINT TRIGGER verify_order BEFORE INSERT ON orders DEFERRABLE INITIALLY DEFERRED FOR EACH ROW")]
    [InlineData("CREATE CONSTRAINT TRIGGER verify_order AFTER INSERT ON orders DEFERRABLE INITIALLY DEFERRED FOR EACH STATEMENT")]
    [InlineData("CREATE CONSTRAINT TRIGGER verify_order AFTER INSERT ON orders DEFERRABLE INITIALLY DEFERRED REFERENCING NEW TABLE AS incoming FOR EACH ROW")]
    [InlineData("CREATE TRIGGER verify_order AFTER INSERT ON orders DEFERRABLE FOR EACH ROW")]
    [InlineData("CREATE TRIGGER verify_order AFTER INSERT ON orders DEFERRABLE INITIALLY DEFERRED FOR EACH ROW")]
    [InlineData("CREATE TRIGGER verify_order AFTER COMMIT ON orders FOR EACH ROW")]
    public void Parse_UnsupportedOrMalformedConstraint_RejectsDeclaration(string declaration)
        => Assert.Throws<SqlParseException>(() => SqlParser.Parse(
            declaration + " LANGUAGE SQL AS BEGIN INSERT INTO audit_outbox (id) VALUES (1); END"));

    [Theory]
    [InlineData(true, false, SqlTriggerTiming.After, SqlTriggerLevel.Row)]
    [InlineData(false, true, SqlTriggerTiming.After, SqlTriggerLevel.Row)]
    [InlineData(true, true, SqlTriggerTiming.Before, SqlTriggerLevel.Row)]
    [InlineData(true, true, SqlTriggerTiming.After, SqlTriggerLevel.Statement)]
    public void Create_InconsistentAstFlags_RejectsDefinition(bool constraint, bool deferred, SqlTriggerTiming timing, SqlTriggerLevel level)
    {
        var statement = ParseRegular("verify_order") with
        {
            IsConstraint = constraint,
            InitiallyDeferred = deferred,
            Timing = timing,
            Level = level,
        };
        Assert.Throws<ArgumentException>(() => TriggerDefinition.Create(statement));
    }

    [Fact]
    public void Catalog_DeferredDefinitionAndLifecycle_RoundTripsVersionFour()
    {
        var manager = new RoutineManager(_root);
        manager.Create(Deferred("check_first"));
        manager.Create(Deferred("check_second"), "check_first", precedes: true);
        manager.Alter(new AlterTriggerStatement("check_second", SqlAlterTriggerAction.Disable));
        manager.Alter(new AlterTriggerStatement("check_second", SqlAlterTriggerAction.Rename, "check_renamed"));

        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(manager.CatalogPath).AsSpan(8)));
        var reopened = new RoutineManager(_root);
        TriggerDefinition renamed = Assert.IsType<TriggerDefinition>(reopened.TryGetTrigger("check_renamed"));
        Assert.True(renamed.IsConstraint);
        Assert.True(renamed.InitiallyDeferred);
        Assert.False(renamed.Enabled);
        Assert.Equal(CreatedAt, renamed.CreatedAtUtcTicks);
        Assert.Equal(0, renamed.ExecutionOrder);
        Assert.Equal(1, reopened.TryGetTrigger("check_first")!.ExecutionOrder);
        Assert.Equal(new[] { "audit_outbox", "orders" }, renamed.ObjectDependencies);
    }

    [Theory]
    [InlineData(SqlAlterTriggerAction.Follows)]
    [InlineData(SqlAlterTriggerAction.Precedes)]
    public void Alter_RelativeOrderAcrossCommitPhases_RejectsWithoutPublishing(SqlAlterTriggerAction action)
    {
        var manager = new RoutineManager(_root);
        manager.Create(TriggerDefinition.Create(ParseRegular("immediate_action"), CreatedAt));
        manager.Create(Deferred("deferred_check"));
        byte[] before = File.ReadAllBytes(manager.CatalogPath);

        var error = Assert.Throws<RoutineExecutionException>(() => manager.Alter(
            new AlterTriggerStatement("deferred_check", action, "immediate_action")));
        Assert.Equal(RoutineErrorCodes.Dependency, error.Code);
        Assert.Equal(before, File.ReadAllBytes(manager.CatalogPath));
        Assert.Equal(0, manager.TryGetTrigger("immediate_action")!.ExecutionOrder);
        Assert.Equal(0, manager.TryGetTrigger("deferred_check")!.ExecutionOrder);
    }

    [Fact]
    public void Create_RelativeOrderAcrossCommitPhases_RejectsWithoutPublishing()
    {
        var manager = new RoutineManager(_root);
        manager.Create(TriggerDefinition.Create(ParseRegular("immediate_action"), CreatedAt));
        byte[] before = File.ReadAllBytes(manager.CatalogPath);
        var error = Assert.Throws<RoutineExecutionException>(() => manager.Create(Deferred("deferred_check"), "immediate_action"));
        Assert.Equal(RoutineErrorCodes.Dependency, error.Code);
        Assert.Null(manager.TryGetTrigger("deferred_check"));
        Assert.Equal(before, File.ReadAllBytes(manager.CatalogPath));
        Assert.Single(new RoutineManager(_root).ListTriggers());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Catalog_IndependentLegacyFixture_PreservesSemanticsAndUpgrades(int version)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, RoutineCatalogCodec.FileName);
        File.WriteAllBytes(path, CreateFixture(version));
        RoutineCatalogSnapshot legacy = RoutineCatalogCodec.Load(path);
        TriggerDefinition definition = Assert.Single(legacy.Triggers);
        Assert.False(definition.IsConstraint);
        Assert.False(definition.InitiallyDeferred);
        Assert.Equal(CreatedAt, definition.CreatedAtUtcTicks);
        Assert.Equal(version == 1, definition.Enabled);
        Assert.Equal(version == 1 ? CreatedAt : 7, definition.ExecutionOrder);
        Assert.Equal(version >= 3 ? SqlTriggerLevel.Statement : SqlTriggerLevel.Row, definition.Level);
        Assert.Equal(version >= 3 ? "incoming" : null, definition.NewTableName);

        RoutineCatalogCodec.Save(path, legacy.Procedures, legacy.Triggers);
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(path).AsSpan(8)));
        TriggerDefinition restored = Assert.Single(RoutineCatalogCodec.Load(path).Triggers);
        Assert.Equal(definition.BodySql, restored.BodySql);
        Assert.Equal(definition.WhenSql, restored.WhenSql);
        Assert.Equal(definition.Enabled, restored.Enabled);
        Assert.Equal(definition.ExecutionOrder, restored.ExecutionOrder);
        Assert.Equal(definition.NewTableName, restored.NewTableName);
        Assert.Equal(definition.ObjectDependencies, restored.ObjectDependencies);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(255)]
    public void Catalog_InvalidVersionFourFlags_RejectsEvenWithValidCrc(byte flags)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, RoutineCatalogCodec.FileName);
        File.WriteAllBytes(path, CreateFixture(4, flags));
        Assert.Throws<InvalidDataException>(() => RoutineCatalogCodec.Load(path));
    }

    private static CreateTriggerStatement ParseRegular(string name)
        => Assert.IsType<CreateTriggerStatement>(SqlParser.Parse($"""
            CREATE TRIGGER {name} AFTER INSERT ON orders FOR EACH ROW LANGUAGE SQL AS BEGIN
                INSERT INTO audit_outbox (id) VALUES (NEW.id);
            END
            """));

    private static TriggerDefinition Deferred(string name)
        => TriggerDefinition.Create(ParseRegular(name) with { IsConstraint = true, InitiallyDeferred = true }, CreatedAt);

    private static byte[] CreateFixture(int version, byte flags = 0)
    {
        // 此固定小型 fixture 按历史布局独立编码，不通过现行序列化器删字节生成。
        bool statement = version == 3;
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            WriteString(writer, "legacy_check");
            WriteString(writer, "orders");
            writer.Write((byte)SqlTriggerEvent.Insert);
            writer.Write(CreatedAt);
            WriteString(writer, statement ? null : "NEW.id > 0");
            WriteString(writer, statement
                ? "INSERT INTO audit_outbox (id) SELECT id FROM incoming;"
                : "INSERT INTO audit_outbox (id) VALUES (NEW.id);");
            if (version >= 2)
            {
                writer.Write((byte)0);
                writer.Write(7L);
            }
            if (version >= 3)
            {
                writer.Write((byte)SqlTriggerTiming.After);
                writer.Write((byte)(statement ? SqlTriggerLevel.Statement : SqlTriggerLevel.Row));
                WriteString(writer, null);
                WriteString(writer, statement ? "incoming" : null);
            }
            if (version >= 4) writer.Write(flags);
        }

        byte[] payloadBytes = payload.ToArray();
        byte[] fixture = new byte[32 + payloadBytes.Length + 16];
        "SDBRTN01"u8.CopyTo(fixture);
        BinaryPrimitives.WriteInt32LittleEndian(fixture.AsSpan(8), version);
        BinaryPrimitives.WriteInt32LittleEndian(fixture.AsSpan(12), 32);
        BinaryPrimitives.WriteInt32LittleEndian(fixture.AsSpan(20), 1);
        payloadBytes.CopyTo(fixture.AsSpan(32));
        Span<byte> footer = fixture.AsSpan(fixture.Length - 16);
        BinaryPrimitives.WriteUInt32LittleEndian(footer, Crc32.HashToUInt32(payloadBytes));
        "SDBRTN01"u8.CopyTo(footer[4..]);
        return fixture;
    }

    private static void WriteString(BinaryWriter writer, string? value)
    {
        byte[] bytes = value is null ? [] : Encoding.UTF8.GetBytes(value);
        writer.Write(value is null ? -1 : bytes.Length);
        writer.Write(bytes);
    }
}
