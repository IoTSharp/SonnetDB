using System.Buffers.Binary;
using System.IO.Hashing;
using SonnetDB.Graphs;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphCatalogCodecTests : IDisposable
{
    private const int HeaderSize = 32;
    private const int FooterSize = 16;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-graph-catalog-" + Guid.NewGuid().ToString("N"));

    public GraphCatalogCodecTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SaveLoad_WithDefinitions_RoundTripsRevisionAndStableIdentity()
    {
        string path = Path.Combine(_root, GraphCatalogCodec.FileName);
        GraphDefinition alpha = GraphDefinition.Restore(
            "alpha",
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            638_900_000_000_000_000L,
            GraphDefinition.CurrentRecordFormatVersion);
        GraphDefinition beta = GraphDefinition.Restore(
            "beta",
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            638_900_000_000_000_001L,
            GraphDefinition.CurrentRecordFormatVersion);

        GraphCatalogCodec.Save(path, new GraphCatalogState(7, [beta, alpha]));
        GraphCatalogState loaded = GraphCatalogCodec.Load(path);

        Assert.Equal(7, loaded.Revision);
        Assert.Equal(["alpha", "beta"], loaded.Definitions.Select(static item => item.Name));
        GraphDefinition restored = loaded.Definitions[0];
        Assert.Equal(alpha.StorageId, restored.StorageId);
        Assert.Equal(alpha.CreatedAtUtcTicks, restored.CreatedAtUtcTicks);
        Assert.Equal(alpha.RecordFormatVersion, restored.RecordFormatVersion);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyRevisionZeroState()
    {
        GraphCatalogState loaded = GraphCatalogCodec.Load(Path.Combine(_root, "missing.sdbgraph"));

        Assert.Equal(0, loaded.Revision);
        Assert.Empty(loaded.Definitions);
    }

    [Fact]
    public void Load_FutureVersionCrcTruncationAndTrailingBytes_RejectsCorruption()
    {
        string validPath = WriteValidCatalog();
        byte[] valid = File.ReadAllBytes(validPath);

        string futurePath = Path.Combine(_root, "future.sdbgraph");
        byte[] future = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(future.AsSpan(8, 4), 2);
        File.WriteAllBytes(futurePath, future);
        Assert.Contains(
            "版本 2",
            Assert.Throws<InvalidDataException>(() => GraphCatalogCodec.Load(futurePath)).Message,
            StringComparison.Ordinal);

        string crcPath = Path.Combine(_root, "bad-crc.sdbgraph");
        byte[] badCrc = (byte[])valid.Clone();
        badCrc[HeaderSize + sizeof(int)] ^= 0x20;
        File.WriteAllBytes(crcPath, badCrc);
        Assert.Contains(
            "CRC32",
            Assert.Throws<InvalidDataException>(() => GraphCatalogCodec.Load(crcPath)).Message,
            StringComparison.Ordinal);

        string headerPath = Path.Combine(_root, "header-truncated.sdbgraph");
        File.WriteAllBytes(headerPath, valid[..20]);
        Assert.Contains(
            "header",
            Assert.Throws<InvalidDataException>(() => GraphCatalogCodec.Load(headerPath)).Message,
            StringComparison.Ordinal);

        string footerPath = Path.Combine(_root, "footer-truncated.sdbgraph");
        File.WriteAllBytes(footerPath, valid[..^1]);
        Assert.Throws<InvalidDataException>(() => GraphCatalogCodec.Load(footerPath));

        string trailingPath = Path.Combine(_root, "trailing.sdbgraph");
        File.WriteAllBytes(trailingPath, [.. valid, 0x7f]);
        Assert.Contains(
            "尾随",
            Assert.Throws<InvalidDataException>(() => GraphCatalogCodec.Load(trailingPath)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ValidRevisionHeaderMutation_IsRejectedByCatalogCrc()
    {
        string validPath = WriteValidCatalog();
        byte[] corrupted = File.ReadAllBytes(validPath);
        BinaryPrimitives.WriteInt64LittleEndian(corrupted.AsSpan(16, sizeof(long)), 2);
        string path = Path.Combine(_root, "header-revision-corrupt.sdbgraph");
        File.WriteAllBytes(path, corrupted);

        Assert.Contains(
            "CRC32",
            Assert.Throws<InvalidDataException>(() => GraphCatalogCodec.Load(path)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Load_DuplicateNameAndInvalidUtf8_RejectsPayloadEvenWithValidCrc()
    {
        string duplicateSource = Path.Combine(_root, "duplicate-source.sdbgraph");
        GraphDefinition alpha = GraphDefinition.Restore(
            "alpha",
            Guid.NewGuid(),
            DateTime.UtcNow.Ticks,
            GraphDefinition.CurrentRecordFormatVersion);
        GraphDefinition bravo = GraphDefinition.Restore(
            "bravo",
            Guid.NewGuid(),
            DateTime.UtcNow.Ticks,
            GraphDefinition.CurrentRecordFormatVersion);
        GraphCatalogCodec.Save(duplicateSource, new GraphCatalogState(2, [alpha, bravo]));

        byte[] duplicate = File.ReadAllBytes(duplicateSource);
        int firstEntrySize = sizeof(int) + 5 + 16 + sizeof(long) + sizeof(int);
        int secondNameOffset = HeaderSize + firstEntrySize + sizeof(int);
        "alpha"u8.CopyTo(duplicate.AsSpan(secondNameOffset, 5));
        RecomputeCatalogCrc(duplicate);
        string duplicatePath = Path.Combine(_root, "duplicate.sdbgraph");
        File.WriteAllBytes(duplicatePath, duplicate);
        Assert.Contains(
            "duplicate graph 'alpha'",
            Assert.Throws<InvalidDataException>(() => GraphCatalogCodec.Load(duplicatePath)).Message,
            StringComparison.Ordinal);

        byte[] invalidUtf8 = File.ReadAllBytes(duplicateSource);
        invalidUtf8[HeaderSize + sizeof(int)] = 0xff;
        RecomputeCatalogCrc(invalidUtf8);
        string invalidUtf8Path = Path.Combine(_root, "invalid-utf8.sdbgraph");
        File.WriteAllBytes(invalidUtf8Path, invalidUtf8);
        Assert.Contains(
            "UTF-8",
            Assert.Throws<InvalidDataException>(() => GraphCatalogCodec.Load(invalidUtf8Path)).Message,
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string WriteValidCatalog()
    {
        string path = Path.Combine(_root, "valid.sdbgraph");
        GraphDefinition definition = GraphDefinition.Restore(
            "alpha",
            Guid.NewGuid(),
            DateTime.UtcNow.Ticks,
            GraphDefinition.CurrentRecordFormatVersion);
        GraphCatalogCodec.Save(path, new GraphCatalogState(1, [definition]));
        return path;
    }

    private static void RecomputeCatalogCrc(byte[] bytes)
    {
        uint crc = Crc32.HashToUInt32(bytes.AsSpan(0, bytes.Length - FooterSize));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - FooterSize, 4), crc);
    }
}
