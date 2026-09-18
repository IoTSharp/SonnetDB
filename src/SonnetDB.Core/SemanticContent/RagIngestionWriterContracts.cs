using System.Text.Json.Serialization;
using SonnetDB.Generations;

namespace SonnetDB.SemanticContent;

/// <summary>持久化 RAG writer 的单次工作与 provider 重试预算。</summary>
public sealed record RagIngestionWriterOptions
{
    /// <summary>完整快照允许的最大内容数。</summary>
    public int MaxManifests { get; init; } = 10_000;

    /// <summary>完整快照允许的最大分块数。</summary>
    public int MaxChunks { get; init; } = 100_000;

    /// <summary>完整快照正文与分块合计的最大 UTF-16 字符数。</summary>
    public int MaxTextCharacters { get; init; } = 4 * 1024 * 1024;

    /// <summary>持久化任务 JSON 的最大字节数；还受数据库 KV value 预算约束。</summary>
    public int MaxCheckpointBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>每个分块调用 provider 的最大尝试次数，包含首次调用，取值 1 至 10。</summary>
    public int MaxEmbeddingAttempts { get; init; } = 3;

    /// <summary>瞬时 provider 失败后的重试间隔，最大一分钟。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>单次写入或续跑的取消期限，最大一小时；provider 必须遵守传入的取消令牌。</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>已经完整发布的 RAG 快照与本次执行统计。</summary>
/// <param name="Generation">可通过 generation 查询租约读取的完整版本。</param>
/// <param name="AddedContents">本版本相对前一版本新增的内容数。</param>
/// <param name="UpdatedContents">本版本相对前一版本更新的内容数。</param>
/// <param name="DeletedContents">本版本相对前一版本删除的内容数。</param>
/// <param name="EmbeddedChunks">本次成功生成 embedding 的分块数。</param>
/// <param name="ReusedChunks">本次从旧版本或恢复进度复用的分块数。</param>
public sealed record RagIngestionWriteResult(
    DatabaseGeneration Generation,
    int AddedContents,
    int UpdatedContents,
    int DeletedContents,
    int EmbeddedChunks,
    int ReusedChunks);

internal sealed record RagIngestionWriterCheckpoint
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    [JsonRequired]
    public string Stream { get; init; } = string.Empty;
    [JsonRequired]
    public string GenerationId { get; init; } = string.Empty;
    [JsonRequired]
    public long ExpectedRevision { get; init; }
    [JsonRequired]
    public EmbeddingProfile Profile { get; init; } = new();
    [JsonRequired]
    public RagIngestionSnapshot Snapshot { get; init; } = new();
    public int AddedContents { get; init; }
    public int UpdatedContents { get; init; }
    public int DeletedContents { get; init; }
    public bool RebuildAllVectors { get; init; }
}

internal sealed record RagIngestionChunkDocument
{
    public string ContentId { get; init; } = string.Empty;
    public string ChunkId { get; init; } = string.Empty;
    public string ProfileId { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string? Source { get; init; }
    public string? Section { get; init; }
    public int Ordinal { get; init; }
    public float[] Embedding { get; init; } = [];
}
