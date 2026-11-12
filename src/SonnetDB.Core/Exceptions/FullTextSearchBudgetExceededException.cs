namespace SonnetDB.Exceptions;

/// <summary>完整全文查询超过扫描、文本、posting 或时间预算；本次查询不返回部分结果。</summary>
public sealed class FullTextSearchBudgetExceededException : Exception
{
    /// <summary>创建带明确预算原因的查询异常。</summary>
    /// <param name="message">超过的预算及原因。</param>
    public FullTextSearchBudgetExceededException(string message) : base(message) { }
}
