using System.Collections.ObjectModel;

namespace SonnetDB.FullText.Scoring;

/// <summary>
/// BM25F 字段权重配置。
/// </summary>
public sealed class Bm25FOptions
{
    private readonly Dictionary<string, double> _fieldWeights;

    /// <summary>
    /// 创建字段权重配置。未指定字段的权重为 1。
    /// </summary>
    /// <param name="fieldWeights">字段名到权重的映射。</param>
    public Bm25FOptions(IReadOnlyDictionary<string, double>? fieldWeights = null)
    {
        _fieldWeights = new Dictionary<string, double>(StringComparer.Ordinal);
        if (fieldWeights is null)
        {
            FieldWeights = new ReadOnlyDictionary<string, double>(_fieldWeights);
            return;
        }

        foreach (KeyValuePair<string, double> weight in fieldWeights)
        {
            ArgumentException.ThrowIfNullOrEmpty(weight.Key);
            if (weight.Value < 0 || double.IsNaN(weight.Value) || double.IsInfinity(weight.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(fieldWeights), weight.Value, "Field weights must be finite and non-negative.");
            }
            _fieldWeights[weight.Key] = weight.Value;
        }

        FieldWeights = new ReadOnlyDictionary<string, double>(_fieldWeights);
    }

    /// <summary>
    /// 默认字段权重配置：所有字段权重均为 1。
    /// </summary>
    public static Bm25FOptions Default { get; } = new();

    /// <summary>
    /// 显式配置的字段权重。
    /// </summary>
    public IReadOnlyDictionary<string, double> FieldWeights { get; }

    /// <summary>
    /// 获取字段权重；未配置字段返回 1。
    /// </summary>
    public double GetWeight(string field)
        => _fieldWeights.TryGetValue(field, out double weight) ? weight : 1.0;
}
