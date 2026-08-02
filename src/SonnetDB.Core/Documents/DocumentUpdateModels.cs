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
/// <param name="Mul">将数值字段乘以指定数值，字段不存在时写入与操作数类别一致的零。</param>
/// <param name="Pop">从数组头部或尾部移除一个元素；值为 <c>-1</c> 时移除首元素，值为 <c>1</c> 时移除末元素。</param>
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
    IReadOnlyDictionary<string, JsonElement>? CurrentDate = null,
    IReadOnlyDictionary<string, JsonElement>? Mul = null,
    IReadOnlyDictionary<string, JsonElement>? Pop = null)
{
    /// <summary>
    /// 使用既有十种操作符创建局部更新，保留旧版二进制构造入口。
    /// </summary>
    /// <param name="Set">设置值操作。</param>
    /// <param name="Unset">删除字段操作。</param>
    /// <param name="Inc">数值递增操作。</param>
    /// <param name="Min">最小值操作。</param>
    /// <param name="Max">最大值操作。</param>
    /// <param name="Rename">字段重命名操作。</param>
    /// <param name="Push">数组追加操作。</param>
    /// <param name="Pull">数组移除匹配项操作。</param>
    /// <param name="AddToSet">数组去重追加操作。</param>
    /// <param name="CurrentDate">写入当前时间操作。</param>
    public DocumentUpdate(
        IReadOnlyDictionary<string, JsonElement>? Set,
        IReadOnlyDictionary<string, JsonElement>? Unset,
        IReadOnlyDictionary<string, JsonElement>? Inc,
        IReadOnlyDictionary<string, JsonElement>? Min,
        IReadOnlyDictionary<string, JsonElement>? Max,
        IReadOnlyDictionary<string, string>? Rename,
        IReadOnlyDictionary<string, JsonElement>? Push,
        IReadOnlyDictionary<string, JsonElement>? Pull,
        IReadOnlyDictionary<string, JsonElement>? AddToSet,
        IReadOnlyDictionary<string, JsonElement>? CurrentDate)
        : this(Set, Unset, Inc, Min, Max, Rename, Push, Pull, AddToSet, CurrentDate, null, null)
    {
    }

    /// <summary>
    /// 按旧版十字段形态解构更新，忽略新增的 <c>$mul</c> 与 <c>$pop</c>。
    /// </summary>
    /// <param name="Set">设置值操作。</param>
    /// <param name="Unset">删除字段操作。</param>
    /// <param name="Inc">数值递增操作。</param>
    /// <param name="Min">最小值操作。</param>
    /// <param name="Max">最大值操作。</param>
    /// <param name="Rename">字段重命名操作。</param>
    /// <param name="Push">数组追加操作。</param>
    /// <param name="Pull">数组移除匹配项操作。</param>
    /// <param name="AddToSet">数组去重追加操作。</param>
    /// <param name="CurrentDate">写入当前时间操作。</param>
    public void Deconstruct(
        out IReadOnlyDictionary<string, JsonElement>? Set,
        out IReadOnlyDictionary<string, JsonElement>? Unset,
        out IReadOnlyDictionary<string, JsonElement>? Inc,
        out IReadOnlyDictionary<string, JsonElement>? Min,
        out IReadOnlyDictionary<string, JsonElement>? Max,
        out IReadOnlyDictionary<string, string>? Rename,
        out IReadOnlyDictionary<string, JsonElement>? Push,
        out IReadOnlyDictionary<string, JsonElement>? Pull,
        out IReadOnlyDictionary<string, JsonElement>? AddToSet,
        out IReadOnlyDictionary<string, JsonElement>? CurrentDate)
    {
        Set = this.Set;
        Unset = this.Unset;
        Inc = this.Inc;
        Min = this.Min;
        Max = this.Max;
        Rename = this.Rename;
        Push = this.Push;
        Pull = this.Pull;
        AddToSet = this.AddToSet;
        CurrentDate = this.CurrentDate;
    }
}

/// <summary>
/// 文档局部更新执行结果。
/// </summary>
/// <param name="Matched">匹配到的已有文档数量。</param>
/// <param name="Modified">实际发生内容变化的已有文档数量。</param>
/// <param name="Inserted">因 upsert 新增的文档数量。</param>
public sealed record DocumentUpdateResult(int Matched, int Modified, int Inserted);
