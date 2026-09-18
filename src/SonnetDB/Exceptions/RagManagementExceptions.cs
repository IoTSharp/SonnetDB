namespace SonnetDB.Exceptions;

internal sealed class RagManagementProfileException() : Exception("profile 与可信配置不匹配。");

internal sealed class RagManagementAuditCompletionException(Exception innerException)
    : Exception("RAG 完成审计未能持久化，操作结果需要通过状态核对。", innerException);

internal sealed class RagManagementEmbeddingException(Exception innerException)
    : Exception("可信模型调用未完成。", innerException);
