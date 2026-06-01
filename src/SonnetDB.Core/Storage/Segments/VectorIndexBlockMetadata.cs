namespace SonnetDB.Storage.Segments;

internal readonly record struct VectorIndexBlockMetadata(
    int BlockIndex,
    int Count,
    int Dimension,
    int M,
    int Ef,
    uint BlockCrc32);
