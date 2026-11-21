using SonnetDB.Configuration;

namespace SonnetDB.Contracts;

/// <summary>备份任务及运行状态的管理响应。</summary>
public sealed record BackupTaskResponse(
    BackupTaskDefinition Definition,
    BackupTaskState State);

/// <summary>备份任务列表响应。</summary>
public sealed record BackupTaskListResponse(
    List<BackupTaskResponse> Items);

/// <summary>在线恢复请求。</summary>
public sealed record BackupRestoreRequest(
    string? Path = null,
    string? Url = null);

/// <summary>在线恢复结果。恢复总是创建新数据库名称。</summary>
public sealed record BackupRestoreResponse(
    string DatabaseName,
    string Source,
    bool Verified,
    int CheckedFiles,
    long TotalBytes,
    DateTimeOffset CompletedUtc);

/// <summary>数据库重命名请求。</summary>
public sealed record RenameDatabaseRequest(string Name);
