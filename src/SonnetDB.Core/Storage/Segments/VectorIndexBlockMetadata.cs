namespace SonnetDB.Storage.Segments;

internal readonly record struct VectorIndexBlockMetadata(
    int BlockIndex,
    int Count,
    int Dimension,
    int IndexKind,
    int M,
    int Ef,
    int Extra1,
    int Extra2,
    int Extra3,
    uint BlockCrc32,
    long BlobOffset,
    int BlobLength,
    uint BlobCrc32,
    VectorIndexManifestFlags Flags,
    // #223（SDBVIDX v4 起）：为所有向量索引持久化建图度量与 HNSW efConstruction；
    // v3 段读入时 Metric 默认 Cosine（旧版一律按 cosine 建图，语义正确）、EfConstruction 取 max(Ef, 200)。
    int Metric = 0,
    int EfConstruction = 0)
{
    public bool HasPersistentBlob => (Flags & VectorIndexManifestFlags.PersistentBlob) != 0;

    public bool CanRebuildFromBlockPayload => (Flags & VectorIndexManifestFlags.RebuildFromBlockPayload) != 0;
}

[Flags]
internal enum VectorIndexManifestFlags
{
    None = 0,
    PersistentBlob = 1,
    RebuildFromBlockPayload = 2,
}

internal sealed record VectorIndexBlock(VectorIndexBlockMetadata Metadata, byte[] Blob);
