using System.Text.Json;

namespace SonnetDB.Documents;

/// <summary>
/// 从 JSON 文档的指定路径解析向量数组，供文档向量索引与 <c>vector_search</c> 暴力扫描共享。
/// </summary>
internal static class DocumentVectorReader
{
    /// <summary>
    /// 尝试从文档 JSON 的指定路径读取一个 number array 作为 <see cref="float"/> 向量。
    /// </summary>
    /// <param name="json">文档 JSON 文本。</param>
    /// <param name="path">向量字段 JSON path。</param>
    /// <param name="vector">解析出的向量；字段缺失或为 JSON null 时为空数组。</param>
    /// <returns>字段存在且为非空 number array 时返回 true；字段缺失 / 为 null 返回 false。</returns>
    /// <exception cref="InvalidOperationException">字段存在但不是非空 number array 时抛出。</exception>
    public static bool TryReadVector(string json, JsonPath path, out float[] vector)
    {
        vector = [];
        using var document = JsonDocument.Parse(json);
        if (!JsonPathEvaluator.TryResolve(document.RootElement, path, out var element))
            return false;
        if (element.ValueKind == JsonValueKind.Null)
            return false;
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"向量字段 '{path.Text}' 必须是 JSON number array。");

        int length = element.GetArrayLength();
        if (length == 0)
            throw new InvalidOperationException(
                $"向量字段 '{path.Text}' 不能为空数组。");

        var result = new float[length];
        int index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out float value))
                throw new InvalidOperationException(
                    $"向量字段 '{path.Text}' 只能包含 number。");

            result[index++] = value;
        }

        vector = result;
        return true;
    }
}
