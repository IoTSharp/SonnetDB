namespace SonnetDB.Storage.Segments;

internal readonly record struct VectorIndexBlockMetadata(
    int BlockIndex,
    int Count,
    int Dimension,
    int M,
    int Ef,
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
