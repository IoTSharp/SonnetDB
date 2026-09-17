namespace SonnetDB.Exceptions;

/// <summary>检索阶段预算耗尽；不能将已扫描部分伪装成成功结果。</summary>
internal sealed class SemanticSearchBudgetExceededException(string message) : Exception(message);
