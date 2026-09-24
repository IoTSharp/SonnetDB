using System.Collections.ObjectModel;
using Microsoft.Extensions.VectorData;

namespace SonnetDB.Data.VectorData;

/// <summary>
/// 向量检索的执行预设。
/// </summary>
public enum SndbVectorSearchPreset
{
    /// <summary>优先使用可用的 ANN 索引，降低查询延迟。</summary>
    Fast,

    /// <summary>使用 VectorData adapter 的默认执行策略。</summary>
    Balanced,

    /// <summary>强制有界精确扫描，优先保证结果准确性。</summary>
    Accurate,
}

/// <summary>
/// SonnetDB 向量检索扩展选项。
/// </summary>
/// <remarks>
/// 该类型只承载 SonnetDB-specific 执行提示；过滤、阈值、跳过和向量回传仍使用
/// <see cref="VectorSearchOptions{TRecord}"/> 的标准属性。
/// </remarks>
public sealed record SndbVectorSearchExtensionOptions
{
    /// <summary>检索预设。</summary>
    public SndbVectorSearchPreset Preset { get; init; } = SndbVectorSearchPreset.Balanced;

    /// <summary>是否强制使用精确扫描路径。</summary>
    public bool Exact { get; init; }

    /// <summary>批量查询允许的最大查询向量数。</summary>
    public int MaxBatchSize { get; init; } = 1_024;

    internal void Validate()
    {
        if (MaxBatchSize is < 1 or > 8_192)
            throw new ArgumentOutOfRangeException(nameof(MaxBatchSize), "MaxBatchSize 必须位于 1..8192。");
        if (!Enum.IsDefined(Preset))
            throw new ArgumentOutOfRangeException(nameof(Preset), "Preset 不是受支持的值。");
    }
}

/// <summary>
/// SonnetDB VectorData 高层检索选项。
/// </summary>
/// <typeparam name="TRecord">记录类型。</typeparam>
/// <remarks>
/// 该类型继承 Microsoft.Extensions.VectorData 的标准选项，因此过滤、阈值、跳过和向量回传
/// 均沿用 VectorData 合同；<see cref="Preset"/>、<see cref="Exact"/> 和
/// <see cref="MaxBatchSize"/> 是 SonnetDB 的扩展能力。
/// </remarks>
public sealed class SndbVectorSearchOptions<TRecord> : VectorSearchOptions<TRecord>
{
    private SndbVectorSearchExtensionOptions _extensions = new();

    /// <summary>创建默认检索选项。</summary>
    public SndbVectorSearchOptions()
    {
    }

    /// <summary>每个查询返回的最大命中数。</summary>
    public int TopK { get; init; } = 10;

    /// <summary>SonnetDB-specific 执行扩展选项。</summary>
    public SndbVectorSearchExtensionOptions Extensions
    {
        get => _extensions;
        init => _extensions = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>检索预设的便捷写法，等价于设置 Extensions.Preset。</summary>
    public SndbVectorSearchPreset Preset
    {
        get => _extensions.Preset;
        init => _extensions = _extensions with { Preset = value };
    }

    /// <summary>是否强制使用精确扫描路径的便捷写法。</summary>
    /// <remarks>设置为 <see langword="true"/> 时会跳过向量索引，仍保留标准过滤和阈值语义。</remarks>
    public bool Exact
    {
        get => _extensions.Exact;
        init => _extensions = _extensions with { Exact = value };
    }

    /// <summary>批量查询上限的便捷写法，等价于设置 Extensions.MaxBatchSize。</summary>
    public int MaxBatchSize
    {
        get => _extensions.MaxBatchSize;
        init => _extensions = _extensions with { MaxBatchSize = value };
    }

    internal void Validate()
    {
        if (TopK is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(TopK), "TopK 必须位于 1..100000。");
        if (Skip < 0)
            throw new ArgumentOutOfRangeException(nameof(Skip), "Skip 不能为负数。");
        _extensions.Validate();
        if (ScoreThreshold is double threshold && double.IsNaN(threshold))
            throw new ArgumentOutOfRangeException(nameof(ScoreThreshold), "ScoreThreshold 不能是 NaN。");
    }

    internal VectorSearchOptions<TRecord> ToVectorDataOptions()
        => new()
        {
            Filter = Filter,
            VectorProperty = VectorProperty,
            Skip = Skip,
            IncludeVectors = IncludeVectors,
            ScoreThreshold = ScoreThreshold,
        };
}

/// <summary>一个批量向量查询的有序结果。</summary>
/// <typeparam name="TRecord">记录类型。</typeparam>
/// <param name="QueryIndex">输入查询在批次中的零基索引。</param>
/// <param name="Hits">该查询的命中，按距离/分数顺序排列。</param>
public sealed record SndbVectorBatchSearchResult<TRecord>(
    int QueryIndex,
    IReadOnlyList<VectorSearchResult<TRecord>> Hits)
    where TRecord : class
{
    /// <summary>创建批量结果并复制命中列表，避免调用方修改执行结果。</summary>
    public SndbVectorBatchSearchResult(int queryIndex, IEnumerable<VectorSearchResult<TRecord>> hits)
        : this(queryIndex, new ReadOnlyCollection<VectorSearchResult<TRecord>>(
            (hits ?? throw new ArgumentNullException(nameof(hits))).ToArray()))
    {
        ArgumentOutOfRangeException.ThrowIfNegative(queryIndex);
    }
}

/// <summary>VectorData collection 的 SonnetDB 高层检索扩展。</summary>
public static class SonnetDBVectorSearchExtensions
{
    /// <summary>
    /// 使用 SonnetDB 高层选项执行一次向量检索。
    /// </summary>
    /// <typeparam name="TKey">记录键类型。</typeparam>
    /// <typeparam name="TRecord">记录类型。</typeparam>
    /// <typeparam name="TInput">查询向量输入类型。</typeparam>
    /// <param name="collection">VectorData collection。</param>
    /// <param name="searchValue">查询向量，支持 float[]、Memory&lt;float&gt; 或 ReadOnlyMemory&lt;float&gt;。</param>
    /// <param name="options">检索选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按距离/分数顺序产生的检索结果。</returns>
    public static async IAsyncEnumerable<VectorSearchResult<TRecord>> SearchAsync<TKey, TRecord, TInput>(
        this VectorStoreCollection<TKey, TRecord> collection,
        TInput searchValue,
        SndbVectorSearchOptions<TRecord> options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TKey : notnull
        where TRecord : class
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(searchValue);
        options.Validate();

        VectorSearchOptions<TRecord> vectorOptions = options.ToVectorDataOptions();
        if (options.Exact || options.Preset == SndbVectorSearchPreset.Accurate)
        {
            // 有 WHERE 时，SonnetDB 的 vector_search 会跳过 ANN 索引并按过滤后的全集精确计算。
            // 仅在调用方未指定过滤器时补充恒真表达式，保留已有过滤器的原始语义。
            vectorOptions.Filter ??= static _ => true;
        }

        await foreach (var result in collection
            .SearchAsync(searchValue, options.TopK, vectorOptions, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return result;
        }
    }

    /// <summary>
    /// 顺序执行一组有界向量查询，并按输入顺序返回每个查询的命中。
    /// </summary>
    /// <typeparam name="TKey">记录键类型。</typeparam>
    /// <typeparam name="TRecord">记录类型。</typeparam>
    /// <typeparam name="TInput">查询向量输入类型。</typeparam>
    /// <param name="collection">VectorData collection。</param>
    /// <param name="searchValues">查询向量序列。</param>
    /// <param name="options">检索选项；MaxBatchSize 限制本次输入数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入查询一一对应的有序批量结果。</returns>
    public static Task<IReadOnlyList<SndbVectorBatchSearchResult<TRecord>>> SearchBatchAsync<TKey, TRecord, TInput>(
        this VectorStoreCollection<TKey, TRecord> collection,
        IEnumerable<TInput> searchValues,
        SndbVectorSearchOptions<TRecord> options,
        CancellationToken cancellationToken = default)
        where TKey : notnull
        where TRecord : class
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(searchValues);
        return SearchBatchCoreAsync(collection, searchValues, options, cancellationToken);
    }

    /// <summary>
    /// 顺序执行一个异步查询序列，并按输入顺序返回每个查询的命中。
    /// </summary>
    /// <typeparam name="TKey">记录键类型。</typeparam>
    /// <typeparam name="TRecord">记录类型。</typeparam>
    /// <typeparam name="TInput">查询向量输入类型。</typeparam>
    /// <param name="collection">VectorData collection。</param>
    /// <param name="searchValues">异步查询向量序列。</param>
    /// <param name="options">检索选项；MaxBatchSize 限制本次输入数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入查询一一对应的有序批量结果。</returns>
    public static Task<IReadOnlyList<SndbVectorBatchSearchResult<TRecord>>> SearchBatchAsync<TKey, TRecord, TInput>(
        this VectorStoreCollection<TKey, TRecord> collection,
        IAsyncEnumerable<TInput> searchValues,
        SndbVectorSearchOptions<TRecord> options,
        CancellationToken cancellationToken = default)
        where TKey : notnull
        where TRecord : class
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(searchValues);
        return SearchBatchCoreAsync(collection, searchValues, options, cancellationToken);
    }

    private static async Task<IReadOnlyList<SndbVectorBatchSearchResult<TRecord>>> SearchBatchCoreAsync<TKey, TRecord, TInput>(
        VectorStoreCollection<TKey, TRecord> collection,
        IEnumerable<TInput> searchValues,
        SndbVectorSearchOptions<TRecord> options,
        CancellationToken cancellationToken)
        where TKey : notnull
        where TRecord : class
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<SndbVectorBatchSearchResult<TRecord>>();
        int index = 0;
        foreach (var searchValue in searchValues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index >= options.MaxBatchSize)
            {
                throw new InvalidOperationException(
                    $"VectorData 批量查询超过 MaxBatchSize={options.MaxBatchSize}，请拆分请求。");
            }

            var hits = new List<VectorSearchResult<TRecord>>();
            await foreach (var hit in collection.SearchAsync(searchValue, options, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                hits.Add(hit);
            }

            results.Add(new SndbVectorBatchSearchResult<TRecord>(index, hits));
            index++;
        }

        return new ReadOnlyCollection<SndbVectorBatchSearchResult<TRecord>>(results);
    }

    private static async Task<IReadOnlyList<SndbVectorBatchSearchResult<TRecord>>> SearchBatchCoreAsync<TKey, TRecord, TInput>(
        VectorStoreCollection<TKey, TRecord> collection,
        IAsyncEnumerable<TInput> searchValues,
        SndbVectorSearchOptions<TRecord> options,
        CancellationToken cancellationToken)
        where TKey : notnull
        where TRecord : class
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<SndbVectorBatchSearchResult<TRecord>>();
        int index = 0;
        await foreach (var searchValue in searchValues.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index >= options.MaxBatchSize)
            {
                throw new InvalidOperationException(
                    $"VectorData 批量查询超过 MaxBatchSize={options.MaxBatchSize}，请拆分请求。");
            }

            var hits = new List<VectorSearchResult<TRecord>>();
            await foreach (var hit in collection.SearchAsync(searchValue, options, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                hits.Add(hit);
            }

            results.Add(new SndbVectorBatchSearchResult<TRecord>(index, hits));
            index++;
        }

        return new ReadOnlyCollection<SndbVectorBatchSearchResult<TRecord>>(results);
    }
}
