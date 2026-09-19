namespace SonnetDB.Exceptions;

/// <summary>内部预检预算拒绝；由预检入口转换成明确的未检查结果。</summary>
internal sealed class TimeSeriesPreflightBudgetException(string message) : Exception(message);
