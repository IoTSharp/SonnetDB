using System.Collections.Generic;

namespace SonnetDB.FullText.Query;

/// <summary>
/// 查询节点抽象基类。AST 不可变。
/// </summary>
public abstract class Query
{
}

/// <summary>
/// 单词项查询：匹配指定字段的单个 token。
/// </summary>
public sealed class TermQuery : Query
{
    /// <summary>
    /// 创建词项查询。
    /// </summary>
    /// <param name="field">字段名。</param>
    /// <param name="term">词项文本。</param>
    public TermQuery(string field, string term)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);
        ArgumentException.ThrowIfNullOrEmpty(term);
        Field = field;
        Term = term;
    }

    /// <summary>字段名。</summary>
    public string Field { get; }

    /// <summary>词项文本。</summary>
    public string Term { get; }
}

/// <summary>
/// 短语查询：按 token 位置精确连续匹配。
/// </summary>
public sealed class PhraseQuery : Query
{
    /// <summary>
    /// 创建短语查询。
    /// </summary>
    /// <param name="field">字段名。</param>
    /// <param name="terms">短语内的 token 序列。</param>
    public PhraseQuery(string field, IReadOnlyList<string> terms)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count == 0)
        {
            throw new ArgumentException("Phrase query must contain at least one term.", nameof(terms));
        }

        string[] snapshot = new string[terms.Count];
        for (int i = 0; i < terms.Count; i++)
        {
            ArgumentException.ThrowIfNullOrEmpty(terms[i]);
            snapshot[i] = terms[i];
        }

        Field = field;
        Terms = Array.AsReadOnly(snapshot);
    }

    /// <summary>字段名。</summary>
    public string Field { get; }

    /// <summary>短语 token 序列。</summary>
    public IReadOnlyList<string> Terms { get; }
}

/// <summary>
/// 近邻查询：要求一组 token 出现在指定距离窗口内。
/// </summary>
public sealed class NearQuery : Query
{
    /// <summary>
    /// 创建近邻查询。
    /// </summary>
    /// <param name="field">字段名。</param>
    /// <param name="terms">参与近邻匹配的 token 序列。</param>
    /// <param name="maxDistance">窗口内最大位置距离。例如 1 等价于双词相邻。</param>
    /// <param name="inOrder">是否要求按 <paramref name="terms"/> 顺序出现。</param>
    public NearQuery(string field, IReadOnlyList<string> terms, int maxDistance, bool inOrder = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count == 0)
        {
            throw new ArgumentException("NEAR query must contain at least one term.", nameof(terms));
        }
        if (maxDistance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistance), maxDistance, "Distance must be non-negative.");
        }

        string[] snapshot = new string[terms.Count];
        for (int i = 0; i < terms.Count; i++)
        {
            ArgumentException.ThrowIfNullOrEmpty(terms[i]);
            snapshot[i] = terms[i];
        }

        Field = field;
        Terms = Array.AsReadOnly(snapshot);
        MaxDistance = maxDistance;
        InOrder = inOrder;
    }

    /// <summary>字段名。</summary>
    public string Field { get; }

    /// <summary>近邻 token 序列。</summary>
    public IReadOnlyList<string> Terms { get; }

    /// <summary>窗口内最大位置距离。</summary>
    public int MaxDistance { get; }

    /// <summary>是否要求顺序匹配。</summary>
    public bool InOrder { get; }
}

/// <summary>
/// 布尔 OR 组合（Should 列表，至少匹配一项）。
/// </summary>
public sealed class OrQuery : Query
{
    /// <summary>
    /// 创建 OR 查询。
    /// </summary>
    public OrQuery(IReadOnlyList<Query> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        Query[] snapshot = new Query[clauses.Count];
        for (int i = 0; i < clauses.Count; i++)
        {
            if (clauses[i] is not { } clause)
            {
                throw new ArgumentException("Clauses cannot contain null.", nameof(clauses));
            }
            snapshot[i] = clause;
        }
        Clauses = Array.AsReadOnly(snapshot);
    }

    /// <summary>子句列表。</summary>
    public IReadOnlyList<Query> Clauses { get; }
}

/// <summary>
/// 布尔 AND 组合（必须全部匹配）。
/// </summary>
public sealed class AndQuery : Query
{
    /// <summary>
    /// 创建 AND 查询。
    /// </summary>
    public AndQuery(IReadOnlyList<Query> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        Query[] snapshot = new Query[clauses.Count];
        for (int i = 0; i < clauses.Count; i++)
        {
            if (clauses[i] is not { } clause)
            {
                throw new ArgumentException("Clauses cannot contain null.", nameof(clauses));
            }
            snapshot[i] = clause;
        }
        Clauses = Array.AsReadOnly(snapshot);
    }

    /// <summary>子句列表。</summary>
    public IReadOnlyList<Query> Clauses { get; }
}
