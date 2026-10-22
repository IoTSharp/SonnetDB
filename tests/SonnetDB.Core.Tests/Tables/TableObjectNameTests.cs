using SonnetDB.Kv;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

public sealed class TableObjectNameTests
{
    [Fact]
    public void DefaultValue_IsSafeForEqualityHashAndDisplay()
    {
        TableObjectName value = default;

        Assert.False(value == new TableObjectName("main", "x"));
        _ = value.GetHashCode();
        Assert.Equal("\"\".\"\"", value.ToString());
    }

    [Fact]
    public void TableSchema_MetadataCopies_PreserveQualifiedIdentity()
    {
        TableSchema schema = TableSchema.Create(
            "Orders",
            [("Id", TableColumnType.Int64, false), ("Name", TableColumnType.String, true)],
            ["Id"])
            .WithSchemaName("Sales");

        TableSchema commented = schema.WithComment("qualified");
        TableSchema renamedColumn = schema.WithRenamedColumn("Name", "Label");
        TableSchema renamedTable = schema.WithName("ArchivedOrders");

        Assert.Equal(new TableObjectName("Sales", "Orders"), commented.ObjectName);
        Assert.Equal(new TableObjectName("Sales", "Orders"), renamedColumn.ObjectName);
        Assert.Equal(new TableObjectName("Sales", "ArchivedOrders"), renamedTable.ObjectName);
    }

    [Fact]
    public void Constructor_UnqualifiedName_UsesCanonicalMainSchema()
    {
        var name = new TableObjectName("legacy.with.dot");

        Assert.Equal("main", name.SchemaName);
        Assert.Equal("legacy.with.dot", name.Name);
        Assert.True(name.IsDefaultSchema);
        Assert.Equal("\"main\".\"legacy.with.dot\"", name.ToString());
    }

    [Fact]
    public void Catalog_SameTableNameInDifferentSchemas_CoexistsAndResolvesIndependently()
    {
        var catalog = new TableCatalog();
        TableSchema main = CreateSchema("main", "Items");
        TableSchema sales = CreateSchema("Sales", "Items");

        catalog.Add(main);
        catalog.Add(sales);

        Assert.Same(main, catalog.Resolve("items", quoted: false));
        Assert.Same(sales, catalog.Resolve("sales", false, "items", false));
        Assert.Null(catalog.Resolve("Sales", true, "items", true));
        Assert.Same(sales, catalog.Resolve("Sales", true, "Items", true));
        Assert.Equal(2, catalog.Count);
    }

    [Fact]
    public void Catalog_CaseOnlyDuplicateWithinSchema_RejectsButOtherSchemaAllows()
    {
        var catalog = new TableCatalog();
        catalog.Add(CreateSchema("main", "DeviceID"));

        Assert.Throws<InvalidOperationException>(() => catalog.Add(CreateSchema("main", "deviceid")));
        catalog.Add(CreateSchema("archive", "deviceid"));

        Assert.Equal(2, catalog.Count);
    }

    [Fact]
    public void Catalog_CanonicalizesSchemaSpellingAcrossDifferentTables()
    {
        var catalog = new TableCatalog();
        TableSchema first = CreateSchema("Sales", "Orders");
        TableSchema second = CreateSchema("sales", "Invoices");

        catalog.Add(first);
        catalog.Add(second);

        Assert.Equal("Sales", catalog.Resolve("SALES", false, "invoices", false)!.SchemaName);
        Assert.Throws<InvalidOperationException>(() => catalog.Add(CreateSchema("SALES", "ORDERS")));
    }

    [Fact]
    public void TableManager_TwoSchemasSameTableName_PersistSeparateRowsAndReopen()
    {
        string root = Path.Combine(Path.GetTempPath(), "sndb-object-name-" + Guid.NewGuid().ToString("N"));
        var mainName = new TableObjectName("Items");
        var salesName = new TableObjectName("sales", "Items");
        try
        {
            using (var manager = new TableManager(root, KvOptions.Default))
            {
                manager.Create(CreateSchema(mainName.SchemaName, mainName.Name));
                manager.Create(CreateSchema(salesName.SchemaName, salesName.Name));
                manager.Open(mainName).Insert([1L]);
                manager.Open(salesName).Insert([2L]);
            }

            using var reopened = new TableManager(root, KvOptions.Default);
            Assert.Equal(1L, reopened.Open(mainName).GetByPrimaryKey([1L])!.Values[0]);
            Assert.Equal(2L, reopened.Open(salesName).GetByPrimaryKey([2L])!.Values[0]);
            Assert.Equal(18, TableSchemaCodec.ReadFormatVersion(reopened.SchemaPath));
            Assert.Equal(2, reopened.Catalog.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Comparers_ApplyCaseContractIndependentlyPerPart()
    {
        var stored = new TableObjectName("Sales", "OrderItems");
        var other = new TableObjectName("sales", "orderitems");

        Assert.False(TableObjectName.ExactComparer.Equals(stored, other));
        Assert.True(TableObjectName.UnquotedComparer.Equals(stored, other));
        Assert.False(TableObjectName.CreateComparer(schemaIsQuoted: true, nameIsQuoted: false).Equals(stored, other));
        Assert.False(TableObjectName.CreateComparer(schemaIsQuoted: false, nameIsQuoted: true).Equals(stored, other));
        Assert.True(TableObjectName.CreateComparer(schemaIsQuoted: false, nameIsQuoted: false).Equals(stored, other));
    }

    [Theory]
    [InlineData("Sales.Region", "Order\"Items")]
    [InlineData("租户.甲", "订单\"明细")]
    public void IdentityCodec_QualifiedName_RoundTripsWithoutDelimiterAmbiguity(string schemaName, string tableName)
    {
        var expected = new TableObjectName(schemaName, tableName);

        byte[] encoded = TableObjectNameCodec.EncodeIdentity(expected);
        TableObjectName actual = TableObjectNameCodec.DecodeIdentity(encoded);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IdentityCodec_DifferentPartBoundaries_ProduceDifferentBytes()
    {
        byte[] first = TableObjectNameCodec.EncodeIdentity(new TableObjectName("a.bc", "d"));
        byte[] second = TableObjectNameCodec.EncodeIdentity(new TableObjectName("a", "bc.d"));

        Assert.NotEqual(Convert.ToHexString(first), Convert.ToHexString(second));
    }

    [Fact]
    public void StoragePath_MainNameWithDot_RemainsOneLegacyName()
    {
        var expected = new TableObjectName("legacy.with.dot");

        string[] path = TableObjectNameCodec.EncodeStoragePath(expected);
        TableObjectName actual = TableObjectNameCodec.DecodeStoragePath(path);

        Assert.Single(path);
        Assert.Equal(expected, actual);
        Assert.Equal("main", actual.SchemaName);
        Assert.Equal("legacy.with.dot", actual.Name);
    }

    [Fact]
    public void StoragePath_NonMain_UsesReservedNamespaceAndRoundTrips()
    {
        var expected = new TableObjectName("Sales.Region", "Order\"Items");

        string[] path = TableObjectNameCodec.EncodeStoragePath(expected);
        TableObjectName actual = TableObjectNameCodec.DecodeStoragePath(path);

        Assert.Equal(2, path.Length);
        Assert.Equal(TableObjectNameCodec.QualifiedStorageDirectory, path[0]);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecodeIdentity_TruncatedPayload_Rejects()
    {
        byte[] encoded = TableObjectNameCodec.EncodeIdentity(new TableObjectName("sales", "orders"));

        Assert.Throws<InvalidDataException>(() => TableObjectNameCodec.DecodeIdentity(encoded.AsSpan(0, encoded.Length - 1)));
    }

    private static TableSchema CreateSchema(string schemaName, string name)
        => TableSchema.Create(name, [("Id", TableColumnType.Int64, false)], ["Id"])
            .WithSchemaName(schemaName);
}
