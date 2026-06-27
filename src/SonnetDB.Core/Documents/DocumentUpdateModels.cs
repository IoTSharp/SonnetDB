using System.Text.Json;

namespace SonnetDB.Documents;

/// <summary>
/// JSON 文档局部更新操作符集合。
/// </summary>
/// <param name="Set">将 JSON path 设置为指定值。</param>
/// <param name="Unset">删除 JSON path；字典值会被忽略。</param>
/// <param name="Inc">对数值字段递增指定数值，字段不存在时从 0 开始。</param>
/// <param name="Min">仅当现有值大于指定值时写入指定值。</param>
/// <param name="Max">仅当现有值小于指定值时写入指定值。</param>
/// <param name="Rename">将源 JSON path 重命名到目标 JSON path。</param>
/// <param name="Push">向数组字段追加指定值，字段不存在时创建数组。</param>
/// <param name="Pull">从数组字段移除等于指定值的元素。</param>
/// <param name="AddToSet">向数组字段追加指定值，但已存在等值元素时不重复追加。</param>
/// <param name="CurrentDate">将 JSON path 写为当前 UTC 时间；值为 <c>true</c> 或 <c>"date"</c> 时写 ISO-8601 字符串，值为 <c>"timestamp"</c> 时写 Unix 毫秒。</param>
public sealed record DocumentUpdate(
    IReadOnlyDictionary<string, JsonElement>? Set = null,
    IReadOnlyDictionary<string, JsonElement>? Unset = null,
    IReadOnlyDictionary<string, JsonElement>? Inc = null,
    IReadOnlyDictionary<string, JsonElement>? Min = null,
    IReadOnlyDictionary<string, JsonElement>? Max = null,
    IReadOnlyDictionary<string, string>? Rename = null,
    IReadOnlyDictionary<string, JsonElement>? Push = null,
    IReadOnlyDictionary<string, JsonElement>? Pull = null,
    IReadOnlyDictionary<string, JsonElement>? AddToSet = null,
    IReadOnlyDictionary<string, JsonElement>? CurrentDate = null);

/// <summary>
/// 文档局部更新执行结果。
/// </summary>
/// <param name="Matched">匹配到的已有文档数量。</param>
/// <param name="Modified">实际发生内容变化的已有文档数量。</param>
/// <param name="Inserted">因 upsert 新增的文档数量。</param>
public sealed record DocumentUpdateResult(int Matched, int Modified, int Inserted);
