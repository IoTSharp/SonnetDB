using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Index.Hnsw;

internal sealed record HnswIndexSnapshot<TKey>(
    int Dimensions,
    Metric Metric,
    HnswOptions Options,
    float[] Vectors,
    TKey[] Keys,
    int[] Levels,
    int[][][] Neighbors,
    int[] Tombstones,
    int EntryPoint,
    int EntryLevel)
    where TKey : notnull;
