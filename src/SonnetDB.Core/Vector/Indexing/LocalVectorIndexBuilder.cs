using SonnetDB.Vector.Core;
using SonnetDB.Vector.Index.DiskAnn;
using SonnetDB.Vector.Index.Flat;
using SonnetDB.Vector.Index.Hnsw;
using SonnetDB.Vector.Index.Ivf;
using SonnetDB.Vector.Primitives;

namespace SonnetDB.Vector.Indexing;

/// <summary>
/// 基于 SonnetDB 内部 ANN 实现的向量索引构建器。
/// </summary>
public sealed class LocalVectorIndexBuilder : IVectorIndexBuilder
{
    /// <summary>
    /// 全局共享的默认构建器实例。
    /// </summary>
    public static LocalVectorIndexBuilder Instance { get; } = new();

    /// <inheritdoc />
    public IVectorIndexReader Build(VectorIndexBuildInput input)
    {
        ValidateInput(input);

        return input.Algorithm switch
        {
            VectorIndexAlgorithm.Hnsw => BuildHnsw(input),
            VectorIndexAlgorithm.Flat => BuildFlat(input),
            VectorIndexAlgorithm.IvfFlat => BuildIvfFlat(input),
            VectorIndexAlgorithm.IvfPq => BuildIvfPq(input),
            VectorIndexAlgorithm.Vamana => BuildVamana(input),
            _ => throw new NotSupportedException($"不支持的向量索引算法：{input.Algorithm}。"),
        };
    }

    private static IVectorIndexReader BuildFlat(VectorIndexBuildInput input)
    {
        var index = new FlatIndex<int>(
            input.Dimension,
            VectorDistance.ToVectorMetric(input.Metric),
            input.Count);
        AddRows(index, input.Vectors.Span, input.Count, input.Dimension);
        return new LocalVectorIndexReader(index, input.Algorithm, input.Metric);
    }

    private static IVectorIndexReader BuildHnsw(VectorIndexBuildInput input)
    {
        var options = (input.Hnsw ?? VectorIndexHnswOptions.Default).ToHnswOptions();
        options.Validate();

        var index = new HnswIndex<int>(
            input.Dimension,
            VectorDistance.ToVectorMetric(input.Metric),
            options,
            input.Count);
        AddRows(index, input.Vectors.Span, input.Count, input.Dimension);
        return new LocalVectorIndexReader(index, input.Algorithm, input.Metric);
    }

    private static IVectorIndexReader BuildIvfFlat(VectorIndexBuildInput input)
    {
        var options = ToIvfOptions(input.Ivf ?? new VectorIndexIvfOptions());
        var index = new IvfFlatIndex<int>(
            input.Dimension,
            VectorDistance.ToVectorMetric(input.Metric),
            options,
            input.Count);
        AddRows(index, input.Vectors.Span, input.Count, input.Dimension);
        return new LocalVectorIndexReader(index, input.Algorithm, input.Metric);
    }

    private static IVectorIndexReader BuildIvfPq(VectorIndexBuildInput input)
    {
        var options = ToIvfPqOptions(input.IvfPq ?? new VectorIndexIvfPqOptions());
        var index = new IvfPqIndex<int>(
            input.Dimension,
            VectorDistance.ToVectorMetric(input.Metric),
            options,
            input.Count);
        AddRows(index, input.Vectors.Span, input.Count, input.Dimension);
        return new LocalVectorIndexReader(index, input.Algorithm, input.Metric);
    }

    private static IVectorIndexReader BuildVamana(VectorIndexBuildInput input)
    {
        var options = ToVamanaOptions(input.Vamana ?? new VectorIndexVamanaOptions());
        var index = new VamanaIndex<int>(
            input.Dimension,
            VectorDistance.ToVectorMetric(input.Metric),
            options,
            input.Count);
        AddRows(index, input.Vectors.Span, input.Count, input.Dimension);
        return new LocalVectorIndexReader(index, input.Algorithm, input.Metric);
    }

    private static IvfOptions ToIvfOptions(VectorIndexIvfOptions source)
    {
        var options = new IvfOptions
        {
            NList = source.NList,
            NProbe = source.NProbe,
            MaxIterations = source.MaxIterations,
            Seed = source.Seed,
        };
        options.Validate();
        return options;
    }

    private static IvfPqOptions ToIvfPqOptions(VectorIndexIvfPqOptions source)
    {
        var options = new IvfPqOptions
        {
            NList = source.NList,
            NProbe = source.NProbe,
            MaxIterations = source.MaxIterations,
            M = source.M,
            NBits = source.NBits,
            Seed = source.Seed,
        };
        options.Validate();
        return options;
    }

    private static VamanaOptions ToVamanaOptions(VectorIndexVamanaOptions source)
    {
        var options = new VamanaOptions
        {
            MaxDegree = source.MaxDegree,
            SearchListSize = source.SearchListSize,
            Alpha = source.Alpha,
            BeamWidth = source.BeamWidth,
            Seed = source.Seed,
        };
        options.Validate();
        return options;
    }

    private static void AddRows(IIndex<int> index, ReadOnlySpan<float> vectors, int count, int dimension)
    {
        for (int row = 0; row < count; row++)
            index.Add(row, vectors.Slice(row * dimension, dimension));
    }

    private static void ValidateInput(VectorIndexBuildInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.Dimension);
        ArgumentOutOfRangeException.ThrowIfNegative(input.Count);

        int expectedLength = checked(input.Count * input.Dimension);
        if (input.Vectors.Length != expectedLength)
        {
            throw new ArgumentException(
                $"向量载荷长度必须等于 Count * Dimension（期望 {expectedLength}，实际 {input.Vectors.Length}）。",
                nameof(input));
        }
    }
}
