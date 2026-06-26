using System.Collections.Generic;

namespace SonnetDB.FullText.Index;

/// <summary>
/// 待索引文档：由若干字段组成，每个字段是字符串。
/// </summary>
public sealed class Document
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);

    /// <summary>
    /// 创建新文档。
    /// </summary>
    /// <param name="id">文档主键。</param>
    public Document(DocumentId id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id.Value);
        Id = id;
    }

    /// <summary>
    /// 文档主键。
    /// </summary>
    public DocumentId Id { get; }

    /// <summary>
    /// 设置一个字段的内容。
    /// </summary>
    public Document Set(string field, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);
        ArgumentNullException.ThrowIfNull(value);
        _fields[field] = value;
        return this;
    }

    /// <summary>
    /// 取出某字段的内容；不存在时返回空字符串。
    /// </summary>
    public string Get(string field) => _fields.TryGetValue(field, out string? v) ? v : string.Empty;

    /// <summary>
    /// 该文档持有的所有字段。
    /// </summary>
    public IReadOnlyDictionary<string, string> Fields => _fields;
}
