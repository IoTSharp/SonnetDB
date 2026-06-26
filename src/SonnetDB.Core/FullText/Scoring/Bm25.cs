using System;

namespace SonnetDB.FullText.Scoring;

/// <summary>
/// BM25 评分公式实现。
/// </summary>
public static class Bm25
{
    /// <summary>
    /// 计算单个词项对单个文档的 BM25 得分。
    /// </summary>
    /// <param name="termFrequency">该词在文档中的频次。</param>
    /// <param name="documentLength">文档长度（token 数）。</param>
    /// <param name="averageDocumentLength">语料平均文档长度。</param>
    /// <param name="documentCount">语料中文档总数。</param>
    /// <param name="documentFrequency">包含该词的文档数。</param>
    /// <param name="parameters">BM25 参数。</param>
    public static double Score(
        int termFrequency,
        int documentLength,
        double averageDocumentLength,
        int documentCount,
        int documentFrequency,
        Bm25Parameters parameters)
    {
        if (termFrequency <= 0 || documentFrequency <= 0)
        {
            return 0.0;
        }

        // IDF：使用平滑的 Robertson-Sparck Jones 形式 ln(1 + (N - df + 0.5) / (df + 0.5))。
        double idf = Math.Log(1.0 + ((documentCount - documentFrequency + 0.5) / (documentFrequency + 0.5)));

        double avgdl = averageDocumentLength <= 0 ? 1.0 : averageDocumentLength;
        double normLen = 1.0 - parameters.B + (parameters.B * (documentLength / avgdl));
        double tfNorm = termFrequency / (termFrequency + (parameters.K1 * normLen));

        return idf * (parameters.K1 + 1.0) * tfNorm;
    }
}
