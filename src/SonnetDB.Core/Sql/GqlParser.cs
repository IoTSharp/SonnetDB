using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql;

/// <summary>
/// 把 M40 #364 受限 GQL 风格只读查询解析为现有 SQL/PGQ typed AST。
/// </summary>
/// <remarks>
/// 该入口只提供 <c>USE GRAPH ... MATCH ... RETURN</c> 查询语法，不承诺完整 GQL 或 Cypher。
/// 返回的 AST 与等价 <c>GRAPH_TABLE</c> SQL 共用同一规划器和执行器。
/// </remarks>
public static class GqlParser
{
    private static readonly SqlParseCache ParseCache = new(capacity: 128);

    /// <summary>
    /// 解析一条受限 GQL 风格查询，可选使用 <c>EXPLAIN</c> 或 <c>EXPLAIN ANALYZE</c> 前缀。
    /// </summary>
    /// <param name="source">GQL 风格查询文本。</param>
    /// <returns>现有 SQL/PGQ typed AST。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> 为 <c>null</c>。</exception>
    /// <exception cref="SqlParseException">查询不属于公开的受限语法子集。</exception>
    public static SqlStatement Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ParseCache.GetOrParse(source, SqlParser.ParseGql);
    }
}
