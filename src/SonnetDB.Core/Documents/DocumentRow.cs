namespace SonnetDB.Documents;

/// <summary>
/// JSON 文档集合中的一条文档记录。
/// </summary>
/// <param name="Id">文档 ID。</param>
/// <param name="Json">规范化后的 JSON 文本。</param>
/// <param name="Version">最后一次写入该文档的底层 KV 版本号。</param>
public sealed record DocumentRow(
    string Id,
    string Json,
    long Version);
