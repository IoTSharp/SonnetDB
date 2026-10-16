using System.Data;
using System.Data.Common;
using System.Globalization;

internal sealed record DeclaredColumn(string Name, Type Type, string SqlType, bool? Nullable, int? Precision = null, int? Scale = null);
internal sealed record ViewCase(string Name, DeclaredColumn[] Columns, object?[][] Rows);

internal static class MetadataCases
{
    internal static readonly DeclaredColumn Id = new("ItemID", typeof(long), "Int64", false, 19, 0);
    internal static readonly DeclaredColumn Name = new("Name", typeof(string), "String", true);
    internal static readonly DeclaredColumn Amount = new("Amount", typeof(decimal), "Decimal", true, 20, 6);
    internal static readonly DeclaredColumn Payload = new("Payload", typeof(byte[]), "Blob", true);
    internal static readonly DeclaredColumn[] Basic = [Id, Name, Amount, Payload];
    internal static readonly object?[][] Data = [[1L, null, null, null], [2L, "第二行", 12.345678m, new byte[] { 1, 2 }]];
    internal static readonly string[] Create =
    [
        "CREATE TABLE items(id INT, name STRING, PRIMARY KEY(id))",
        "INSERT INTO items(id,name) VALUES(1,'first')",
        "CREATE VIEW item_view AS SELECT id,name FROM items",
        "CREATE TABLE ViewItems(DeviceID INT, Name STRING, Amount DECIMAL(20,6), Payload BLOB, PRIMARY KEY(DeviceID))",
        "CREATE TABLE RightItems(id INT, PRIMARY KEY(id))",
        "CREATE VIEW ItemView AS SELECT DeviceID AS ItemID, Name, Amount, Payload FROM ViewItems",
        "CREATE VIEW NestedView AS SELECT * FROM ItemView",
        "CREATE VIEW EmptyView AS SELECT * FROM ItemView WHERE ItemID < 0",
        "CREATE VIEW NullView AS SELECT Name, Amount, Payload FROM ViewItems WHERE DeviceID = 1",
        "CREATE VIEW ExprView AS SELECT DeviceID + 1 AS shifted, DeviceID / 2 AS divided, Amount > 0 AS positive, CASE WHEN DeviceID > 0 THEN Amount ELSE NULL END AS chosen, COALESCE(DeviceID,0) AS filled, CAST(Amount AS FLOAT) AS floated FROM ViewItems",
        "CREATE VIEW MixedView AS SELECT CASE WHEN DeviceID > 0 THEN DeviceID ELSE CAST(DeviceID AS FLOAT) END AS mixed FROM ViewItems",
        "CREATE VIEW UnknownView AS SELECT concat('id=', CAST(DeviceID AS STRING)) AS Rendered FROM ViewItems",
        "CREATE VIEW LeftView AS SELECT l.DeviceID AS LeftID, r.id AS RightID FROM ViewItems l LEFT JOIN RightItems r ON l.DeviceID=r.id",
        "CREATE VIEW RightView AS SELECT l.DeviceID AS LeftID, r.id AS RightID FROM ViewItems l RIGHT JOIN RightItems r ON l.DeviceID=r.id",
        "CREATE VIEW FullView AS SELECT l.DeviceID AS LeftID, r.id AS RightID FROM ViewItems l FULL JOIN RightItems r ON l.DeviceID=r.id",
        "CREATE VIEW InnerView AS SELECT l.DeviceID AS LeftID, r.id AS RightID FROM ViewItems l INNER JOIN RightItems r ON l.DeviceID=r.id",
        "CREATE VIEW AggregateView AS SELECT COUNT(*) AS total, SUM(DeviceID) AS summed, AVG(Amount) AS average, MIN(Amount) AS minimum FROM ViewItems",
        "CREATE VIEW GroupedView AS SELECT Name, COUNT(*) AS total FROM ViewItems GROUP BY Name",
        "CREATE MATERIALIZED VIEW EmptySnapshot AS SELECT * FROM EmptyView",
        "REFRESH MATERIALIZED VIEW EmptySnapshot",
        "CREATE MATERIALIZED VIEW SnapshotView AS SELECT * FROM NestedView",
        "REFRESH MATERIALIZED VIEW SnapshotView"
    ];
    internal static readonly string[] Populate =
    [
        "INSERT INTO ViewItems(DeviceID,Name,Amount,Payload) VALUES(1,NULL,NULL,NULL),(2,'第二行',12.345678,X'0102')",
        "INSERT INTO RightItems(id) VALUES(2),(3)",
        "REFRESH MATERIALIZED VIEW SnapshotView"
    ];

    internal static ViewCase[] Cases(bool populated) =>
    [
        new("item_view", [new("id",typeof(long),"Int64",false,19,0), new("name",typeof(string),"String",true)], [[1L,"first"]]),
        new("ItemView", Basic, populated ? Data : []),
        new("NestedView", Basic, populated ? Data : []),
        new("EmptyView", Basic, []),
        new("NullView", [Name, Amount, Payload], populated ? [[null,null,null]] : []),
        new("ExprView", [new("shifted",typeof(long),"Int64",false,19,0), new("divided",typeof(double),"Float64",false), new("positive",typeof(bool),"Boolean",true), new("chosen",typeof(decimal),"Decimal",true,20,6), new("filled",typeof(long),"Int64",false,19,0), new("floated",typeof(double),"Float64",true)], populated ? [[2L,0.5d,null,null,1L,null],[3L,1d,true,12.345678m,2L,12.345678d]] : []),
        new("MixedView", [new("mixed",typeof(object),"Object",false)], populated ? [[1L],[2L]] : []),
        new("UnknownView", [new("Rendered",typeof(object),"Object",null)], populated ? [["id=1"],["id=2"]] : []),
        new("LeftView", Join(false,true), populated ? [[1L,null],[2L,2L]] : []),
        new("RightView", Join(true,false), populated ? [[2L,2L],[null,3L]] : []),
        new("FullView", Join(true,true), populated ? [[1L,null],[2L,2L],[null,3L]] : []),
        new("InnerView", Join(false,false), populated ? [[2L,2L]] : []),
        new("AggregateView", [new("total",typeof(long),"Int64",false,19,0),new("summed",typeof(long),"Int64",true,19,0),new("average",typeof(decimal),"Decimal",true),new("minimum",typeof(decimal),"Decimal",true,20,6)], populated ? [[2L,3L,12.345678m,12.345678m]] : [[0L,null,null,null]]),
        new("GroupedView", [Name,new("total",typeof(long),"Int64",false,19,0)], populated ? [[null,1L],["第二行",1L]] : []),
        new("EmptySnapshot", Basic, []),
        new("SnapshotView", Basic, populated ? Data : [])
    ];

    private static DeclaredColumn[] Join(bool left, bool right) => [new("LeftID",typeof(long),"Int64",left,19,0),new("RightID",typeof(long),"Int64",right,19,0)];

    internal static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + label);
    }

    internal static void AssertSchema(DbDataReader reader, ViewCase item, string state)
    {
        Require(reader.FieldCount == item.Columns.Length, item.Name + "/" + state + "/count");
        var columns = reader.GetColumnSchema();
        using var schema = reader.GetSchemaTable() ?? throw new InvalidOperationException("Missing schema.");
        Require(schema.Rows.Count == item.Columns.Length, "schema-count");
        for (int i = 0; i < item.Columns.Length; i++)
        {
            var expected = item.Columns[i];
            var column = columns[i];
            var row = schema.Rows[i];
            string label = item.Name + "/" + state + "/" + expected.Name;
            Require(reader.GetName(i) == expected.Name && column.ColumnName == expected.Name && Equals(row["ColumnName"],expected.Name), label + "/name");
            Require(column.ColumnOrdinal == i && Equals(row["ColumnOrdinal"],i), label + "/ordinal");
            Require(reader.GetFieldType(i) == expected.Type && column.DataType == expected.Type && Equals(row["DataType"],expected.Type), label + "/type: " + reader.GetFieldType(i));
            Require(reader.GetDataTypeName(i) == expected.SqlType && column.DataTypeName == expected.SqlType && Equals(row["DataTypeName"],expected.SqlType), label + "/SQL-type: " + reader.GetDataTypeName(i));
            Require(column.AllowDBNull == expected.Nullable && Equals(row["AllowDBNull"], (object?)expected.Nullable ?? DBNull.Value), label + "/nullability: " + column.AllowDBNull);
            Require(column.NumericPrecision == expected.Precision && Equals(row["NumericPrecision"], expected.Precision is int p ? (short)p : DBNull.Value), label + "/precision: " + column.NumericPrecision);
            Require(column.NumericScale == expected.Scale && Equals(row["NumericScale"], expected.Scale is int s ? (short)s : DBNull.Value), label + "/scale");
            Require(column.ColumnSize is null && Equals(row["ColumnSize"], DBNull.Value), label + "/unbounded-size");
        }
        Console.WriteLine($"SCHEMA|{item.Name}|{state}|" + string.Join(';', columns.Select(c => $"{c.ColumnOrdinal}:{c.ColumnName}:{c.DataType?.FullName}:{c.DataTypeName}:nullable={c.AllowDBNull}:p={c.NumericPrecision}:s={c.NumericScale}:size={c.ColumnSize}")));
    }

    internal static void AssertRows(IEnumerable<object?[]> actual, object?[][] expected, string label)
    {
        string[] found = actual.Select(Row).Order(StringComparer.Ordinal).ToArray();
        string[] wanted = expected.Select(Row).Order(StringComparer.Ordinal).ToArray();
        Require(found.SequenceEqual(wanted), label + "/rows actual=" + string.Join(';',found) + " expected=" + string.Join(';',wanted));
        Console.WriteLine($"ROWS|{label}|{found.Length}|" + string.Join(';',found));
    }

    private static string Row(object?[] row) => string.Join('|', row.Select(v => v switch
    {
        null or DBNull => "NULL", byte[] bytes => "bytes:" + Convert.ToHexString(bytes),
        decimal d => "decimal:" + d.ToString("G29",CultureInfo.InvariantCulture),
        double d => "double:" + d.ToString("R",CultureInfo.InvariantCulture),
        _ => v.GetType().Name + ":" + Convert.ToString(v,CultureInfo.InvariantCulture)
    }));
}
