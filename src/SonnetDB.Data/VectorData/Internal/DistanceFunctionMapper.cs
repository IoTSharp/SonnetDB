using Microsoft.Extensions.VectorData;

namespace SonnetDB.Data.VectorData.Internal;

internal static class DistanceFunctionMapper
{
    public static string ToKnnMetric(string? distanceFunction)
    {
        if (string.IsNullOrEmpty(distanceFunction))
            return "cosine";

        return distanceFunction switch
        {
            DistanceFunction.CosineDistance => "cosine",
            DistanceFunction.CosineSimilarity => "cosine",
            DistanceFunction.EuclideanDistance => "l2",
            DistanceFunction.EuclideanSquaredDistance => "l2",
            DistanceFunction.DotProductSimilarity => "inner_product",
            DistanceFunction.NegativeDotProductSimilarity => "inner_product",
            _ => throw new NotSupportedException(
                $"SonnetDB VectorData 暂不支持 DistanceFunction = '{distanceFunction}'。"),
        };
    }
}
