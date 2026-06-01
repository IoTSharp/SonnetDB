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
    VectorIndexManifestFlags Flags)
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
