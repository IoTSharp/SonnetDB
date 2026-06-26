namespace SonnetDB.FullText.Index;

/// <summary>
/// 文档主键。索引内部使用稠密自增的 <c>int</c> 文档号，对外通过本类型映射用户主键。
/// </summary>
/// <param name="Value">外部主键字符串。</param>
public readonly record struct DocumentId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}
