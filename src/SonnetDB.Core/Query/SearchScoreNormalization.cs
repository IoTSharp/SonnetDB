namespace SonnetDB.Query;

// SQL hybrid_search 和持久 RAG reader 共用原有距离映射，保持旧 SQL 计分不变。
internal static class SearchScoreNormalization
{
    internal static double DistanceToScore(KnnMetric metric, double distance)
    {
        if (metric == KnnMetric.Cosine)
            return Math.Clamp(1d - (distance / 2d), 0d, 1d);
        if (metric == KnnMetric.InnerProduct)
        {
            if (distance <= -60d)
                return 1d;
            if (distance >= 60d)
                return 0d;
            return 1d / (1d + Math.Exp(distance));
        }

        return 1d / (1d + Math.Max(0d, distance));
    }
}
