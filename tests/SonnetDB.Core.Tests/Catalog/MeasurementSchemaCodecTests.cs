using SonnetDB.Catalog;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Catalog;

public class MeasurementSchemaCodecTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "sndb-test-" + Guid.NewGuid().ToString("N") + ".tslschema");

    private static MeasurementSchema Make(string name, params MeasurementColumn[] cols)
        => MeasurementSchema.Create(name, cols, createdAtUtcTicks: 638_000_000_000_000_000L);

    [Fact]
    public void Load_WithMissingFile_ReturnsEmpty()
    {
        var path = TempFile();
        var list = MeasurementSchemaCodec.Load(path);
        Assert.Empty(list);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesAllFields()
    {
        var path = TempFile();
        try
        {
            var src = new[]
            {
                Make("cpu",
                    new MeasurementColumn("host", MeasurementColumnRole.Tag, FieldType.String),
                    new MeasurementColumn("region", MeasurementColumnRole.Tag, FieldType.String),
                    new MeasurementColumn("usage", MeasurementColumnRole.Field, FieldType.Float64),
                    new MeasurementColumn("count", MeasurementColumnRole.Field, FieldType.Int64)),
                Make("net",
                    new MeasurementColumn("iface", MeasurementColumnRole.Tag, FieldType.String),
                    new MeasurementColumn("up", MeasurementColumnRole.Field, FieldType.Boolean),
                    new MeasurementColumn("note", MeasurementColumnRole.Field, FieldType.String)),
            };
            MeasurementSchemaCodec.Save(path, src);

            var loaded = MeasurementSchemaCodec.Load(path);
            Assert.Equal(2, loaded.Count);

            var cpu = loaded[0];
            Assert.Equal("cpu", cpu.Name);
            Assert.Equal(638_000_000_000_000_000L, cpu.CreatedAtUtcTicks);
            Assert.Equal(4, cpu.Columns.Count);
            Assert.Equal(MeasurementColumnRole.Tag, cpu.Columns[0].Role);
            Assert.Equal(FieldType.Int64, cpu.Columns[3].DataType);

            var net = loaded[1];
            Assert.Equal(3, net.Columns.Count);
            Assert.Equal(FieldType.Boolean, net.Columns[1].DataType);
            Assert.Equal(FieldType.String, net.Columns[2].DataType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveAndLoad_WithLegacyCaseVariants_PreservesNamesAndAmbiguity()
    {
        var path = TempFile();
        try
        {
            var upper = MeasurementSchema.CreateLoaded("Sensors",
            [
                new MeasurementColumn("Usage", MeasurementColumnRole.Field, FieldType.Int64),
                new MeasurementColumn("usage", MeasurementColumnRole.Field, FieldType.Int64),
            ]);
            var lower = MeasurementSchema.CreateLoaded("sensors",
                [new MeasurementColumn("value", MeasurementColumnRole.Field, FieldType.Float64)]);
            MeasurementSchemaCodec.Save(path, new[] { upper, lower });

            var catalog = new MeasurementCatalog();
            foreach (MeasurementSchema schema in MeasurementSchemaCodec.Load(path))
                catalog.LoadOrReplace(schema);

            Assert.Equal(2, catalog.Count);
            Assert.Throws<InvalidOperationException>(() => catalog.Resolve("Sensors", quoted: false));
            var loaded = catalog.Resolve("Sensors", quoted: true)!;
            Assert.Equal(new[] { "Usage", "usage" }, loaded.Columns.Select(static column => column.Name));
            Assert.Throws<InvalidOperationException>(() => loaded.Resolve("Usage", quoted: false));
            Assert.Equal("Usage", loaded.Resolve("Usage", quoted: true)!.Name);
            Assert.Equal("usage", loaded.Resolve("usage", quoted: true)!.Name);
            Assert.Equal("sensors", catalog.Resolve("sensors", quoted: true)!.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_WithEmptyList_WritesValidFile()
    {
        var path = TempFile();
        try
        {
            MeasurementSchemaCodec.Save(path, []);
            var loaded = MeasurementSchemaCodec.Load(path);
            Assert.Empty(loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_WithCorruptedCrc_Throws()
    {
        var path = TempFile();
        try
        {
            MeasurementSchemaCodec.Save(path, new[] { Make("m",
                new MeasurementColumn("v", MeasurementColumnRole.Field, FieldType.Float64)) });

            // 翻转中间一个字节，破坏 entry 区域的 CRC
            var bytes = File.ReadAllBytes(path);
            bytes[40] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(() => MeasurementSchemaCodec.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_WithBadMagic_Throws()
    {
        var path = TempFile();
        try
        {
            MeasurementSchemaCodec.Save(path, []);
            var bytes = File.ReadAllBytes(path);
            bytes[0] = 0x00;
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(() => MeasurementSchemaCodec.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
