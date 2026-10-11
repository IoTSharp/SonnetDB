using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SonnetDB.Streaming;

namespace SonnetDB.Cdc;

/// <summary>将专用单分区 CDC spool 绑定到一个本地持久订阅。</summary>
/// <param name="BridgeId">桥接实例的稳定标识。</param>
/// <param name="Source">允许接收的源标识。</param>
/// <param name="Entity">允许接收的实体名称。</param>
/// <param name="Schema">允许接收的 schema 名称。</param>
/// <param name="SchemaVersion">允许接收的 schema 版本。</param>
/// <param name="Partition">专用 spool 的唯一分区。</param>
public sealed record CdcStreamingBridgeBinding(
    string BridgeId, string Source, string Entity, string Schema, int SchemaVersion, long Partition)
{
    internal void Validate()
    {
        foreach (string value in new[] { BridgeId, Source, Entity, Schema })
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (Encoding.UTF8.GetByteCount(value) > CdcEventCodec.MaxStringBytes)
                throw new ArgumentException("CDC 桥接标识超过 UTF-8 字节边界。");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(Partition);
        if (SchemaVersion != CdcEventCodec.CurrentSchemaVersion)
            throw new CdcUnsupportedSchemaVersionException(Schema, SchemaVersion);
    }
}

/// <summary>CDC 到持久订阅桥接的一批容量与操作时间边界。</summary>
public sealed record CdcStreamingBridgeOptions
{
    /// <summary>每批最多保存及发布的事件数，最高为 4096。</summary>
    public int MaxBatchEvents { get; init; } = 128;

    /// <summary>每批 CDC 原始正文的最大字节数，最高为 8 MiB。</summary>
    public int MaxBatchBytes { get; init; } = 1024 * 1024;

    /// <summary>含 outbox 的状态文件最大字节数，最高为 16 MiB。</summary>
    public int MaxStateBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>打开、一次推进或恢复的总超时毫秒数，最高为五分钟。</summary>
    public int OperationTimeoutMilliseconds { get; init; } = 30_000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchEvents, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatchEvents, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatchBytes, 8 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxStateBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxStateBytes, 16 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(OperationTimeoutMilliseconds, 50);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(OperationTimeoutMilliseconds, 300_000);
    }
}

/// <summary>桥接状态的一致快照；源位点初始值可为 -1。</summary>
/// <param name="Revision">桥接状态的单调修订号。</param>
/// <param name="CommittedSourceOffset">最近已完成交付和源确认的位点。</param>
/// <param name="LastTargetSequence">最近已完成交付的目标序号。</param>
/// <param name="OutboxEventCount">当前恢复 outbox 的事件数。</param>
/// <param name="PublishedOutboxEvents">outbox 中已保存发布完成记录的事件数。</param>
public sealed record CdcStreamingBridgeState(
    long Revision, long CommittedSourceOffset, long LastTargetSequence,
    int OutboxEventCount, int PublishedOutboxEvents);

/// <summary>一次有界 CDC 桥接推进的结果。</summary>
/// <param name="PublishedEvents">本批已完成交付的事件数，包括恢复时确认的发布。</param>
/// <param name="CommittedSourceOffset">完成后的源分区位点。</param>
/// <param name="LastTargetSequence">完成后的目标序号。</param>
/// <param name="HasMore">源 spool 是否还有未交付事件。</param>
public sealed record CdcStreamingBridgeResult(
    int PublishedEvents, long CommittedSourceOffset, long LastTargetSequence, bool HasMore);

internal sealed record CdcStreamingBridgeIdentity(
    [property: JsonRequired] CdcStreamingBridgeBinding Binding,
    [property: JsonRequired] string BridgeStatePath,
    [property: JsonRequired] string SourcePath,
    [property: JsonRequired] string TargetPath,
    [property: JsonRequired] StreamingSubscriptionDefinition TargetDefinition,
    [property: JsonRequired] FileStreamingSubscriptionOptions TargetOptions,
    [property: JsonRequired] CdcStreamingBridgeOptions Options);

internal sealed record CdcStreamingBridgeOutboxEvent(
    [property: JsonRequired] long SourceOffset,
    [property: JsonRequired] StreamingEvent Event);

internal sealed record CdcStreamingBridgeOutbox(
    [property: JsonRequired] long BaseSourceOffset,
    [property: JsonRequired] long BaseTargetSequence,
    [property: JsonRequired] CdcStreamingBridgeOutboxEvent[] Events,
    [property: JsonRequired] int PublishedCount);

internal sealed record CdcStreamingBridgeFileState(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] CdcStreamingBridgeIdentity Identity,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] long CommittedSourceOffset,
    [property: JsonRequired] long LastTargetSequence,
    [property: JsonRequired] CdcStreamingBridgeOutbox? Outbox);

internal sealed record CdcStreamingBridgeStateEnvelope(
    [property: JsonRequired] CdcStreamingBridgeFileState State,
    [property: JsonRequired] string Sha256);

internal sealed record CdcStreamingBridgeTargetIntent(
    [property: JsonRequired] long PreviousSequence,
    [property: JsonRequired] long Sequence,
    [property: JsonRequired] string EventId,
    [property: JsonRequired] string EventSha256);

internal sealed record CdcStreamingBridgeTargetReceipt(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] string BindingSha256,
    [property: JsonRequired] long LastCompletedSequence,
    [property: JsonRequired] string? LastCompletedEventId,
    [property: JsonRequired] string? LastCompletedEventSha256,
    [property: JsonRequired] CdcStreamingBridgeTargetIntent? Intent);

internal sealed record CdcStreamingBridgeTargetEnvelope(
    [property: JsonRequired] CdcStreamingBridgeTargetReceipt Receipt,
    [property: JsonRequired] string Sha256);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CdcStreamingBridgeIdentity))]
[JsonSerializable(typeof(CdcStreamingBridgeFileState))]
[JsonSerializable(typeof(CdcStreamingBridgeStateEnvelope))]
[JsonSerializable(typeof(CdcStreamingBridgeTargetReceipt))]
[JsonSerializable(typeof(CdcStreamingBridgeTargetEnvelope))]
[JsonSerializable(typeof(StreamingEvent))]
internal sealed partial class CdcStreamingBridgeJsonContext : JsonSerializerContext;

internal static class CdcStreamingBridgeFiles
{
    internal static string Hash<T>(T value, JsonTypeInfo<T> typeInfo)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo)));

    internal static async Task<byte[]> ReadAsync(string path, int maxBytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < 1 || stream.Length > maxBytes)
            throw new InvalidDataException("CDC 桥接状态文件长度越界。");
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (stream.Length != bytes.Length)
            throw new InvalidDataException("CDC 桥接状态在读取期间改变。");
        return bytes;
    }

    internal static async Task WriteAsync(string path, byte[] bytes, int maxBytes, CancellationToken token)
    {
        if (bytes.Length > maxBytes)
            throw new InvalidDataException("CDC 桥接状态文件超过容量边界。");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            Wal.DirectoryFsync.FlushRequired(Path.GetDirectoryName(path)!);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 清理独占临时文件失败不能覆盖存储提交或取消结果。
            }
        }
    }
}
