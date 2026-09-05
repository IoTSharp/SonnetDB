using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SonnetDB.Data.Remote;
using SonnetDB.Kv;
using SonnetDB.Protocol;

namespace SonnetDB.Data.Kv;

public sealed partial class SndbKvClient
{
    /// <summary>原子条件写入。NX/XX 不满足时返回未写入；发送后不自动重试。</summary>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="namespace">逻辑命名空间；空字符串表示 root。</param>
    /// <param name="key">命名空间内的非空 key。</param>
    /// <param name="value">原始值；允许空数组。</param>
    /// <param name="condition">Always、NX 或 XX 条件。</param>
    /// <param name="expiresAtUtc">UTC 过期时间；为空表示移除 TTL。</param>
    /// <param name="cancellationToken">取消令牌；发送后取消可能留下未知提交结果。</param>
    /// <returns>是否写入以及成功写入版本。</returns>
    public async Task<SndbKvSetResult> SetConditionalAsync(string keyspace, string @namespace, string key,
        byte[] value, KvSetCondition condition, DateTimeOffset? expiresAtUtc = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateNames(keyspace, @namespace);
        ValidateKey(@namespace, key);
        ArgumentNullException.ThrowIfNull(value);
        ValidateExpiry(expiresAtUtc);
        if (condition is < KvSetCondition.Always or > KvSetCondition.IfExists)
            throw new ArgumentOutOfRangeException(nameof(condition));
        if (_embedded is not null)
        {
            var result = _embedded.Keyspaces.Open(keyspace).Namespace(@namespace).Set(key, value, condition, expiresAtUtc, cancellationToken);
            return new SndbKvSetResult(result.Applied, result.Version);
        }
        if (_frames is { } frames && frames.ShouldTryFrames())
        {
            var writer = new ArrayBufferWriter<byte>();
            KvFrameCodec.EncodeAtomicWriteRequest(writer, 1, KvFrameOp.SetConditional, _database, keyspace,
                KvValueCodec.EncodeUtf8(Qualify(@namespace, key)), value, condition, expiresAtUtc: expiresAtUtc);
            var frame = await frames.SendUnaryAsync(writer.WrittenMemory, cancellationToken, allowFallback: false).ConfigureAwait(false);
            if (frame is { } responseFrame)
            {
                var result = KvFrameCodec.DecodeConditionalSetResponse(responseFrame.Payload);
                return new SndbKvSetResult(result.Applied, result.Version);
            }
        }
        using var response = await PostJsonAsync(KvUrl(keyspace, "set-conditional"),
            new KvConditionalSetRequest(Qualify(@namespace, key), value, condition, expiresAtUtc),
            RemoteJsonContext.Default.KvConditionalSetRequest, cancellationToken).ConfigureAwait(false);
        var body = await ReadAtomicJsonAsync(response, RemoteJsonContext.Default.KvConditionalSetResponse, cancellationToken).ConfigureAwait(false);
        if (body.Applied is null || body.Version is <= 0 || body.Applied.Value != body.Version.HasValue)
            throw new InvalidDataException("KV 条件写响应缺少字段或版本不一致；写入结果未知。");
        return new SndbKvSetResult(body.Applied.Value, body.Version);
    }

    /// <summary>原子读取旧记录并写入新值；不保留旧 TTL，也不自动重试。</summary>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="namespace">逻辑命名空间。</param>
    /// <param name="key">非空 key。</param>
    /// <param name="value">新值，允许空数组。</param>
    /// <param name="expiresAtUtc">新值的 UTC 过期时间；为空表示永不过期。</param>
    /// <param name="cancellationToken">取消令牌；提交后取消不回滚已提交写入。</param>
    /// <returns>旧记录与本次写入版本。</returns>
    public Task<SndbKvExchangeResult> GetAndSetAsync(string keyspace, string @namespace, string key, byte[] value,
        DateTimeOffset? expiresAtUtc = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ExchangeAsync(keyspace, @namespace, key, value, expiresAtUtc, cancellationToken);
    }

    /// <summary>原子读取并删除；缺失或过期 key 不产生删除版本，不自动重试。</summary>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="namespace">逻辑命名空间。</param>
    /// <param name="key">非空 key。</param>
    /// <param name="cancellationToken">取消令牌；发送后取消可能留下未知提交结果。</param>
    /// <returns>旧记录与删除版本；缺失时两者均为空。</returns>
    public Task<SndbKvExchangeResult> GetAndDeleteAsync(string keyspace, string @namespace, string key,
        CancellationToken cancellationToken = default) => ExchangeAsync(keyspace, @namespace, key, null, null, cancellationToken);

    private async Task<SndbKvExchangeResult> ExchangeAsync(string keyspace, string @namespace, string key,
        byte[]? value, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateNames(keyspace, @namespace);
        ValidateKey(@namespace, key);
        ValidateExpiry(expiresAtUtc);
        if (_embedded is not null)
        {
            var ns = _embedded.Keyspaces.Open(keyspace).Namespace(@namespace);
            return ToExchange(key, value is null ? ns.GetAndDelete(key, cancellationToken)
                : ns.GetAndSet(key, value, expiresAtUtc, cancellationToken));
        }
        if (_frames is { } frames && frames.ShouldTryFrames())
        {
            var writer = new ArrayBufferWriter<byte>();
            var encodedKey = KvValueCodec.EncodeUtf8(Qualify(@namespace, key));
            if (value is null)
                KvFrameCodec.EncodeAtomicKeyRequest(writer, 1, KvFrameOp.GetAndDelete, _database, keyspace, encodedKey);
            else
                KvFrameCodec.EncodeAtomicWriteRequest(writer, 1, KvFrameOp.GetAndSet, _database, keyspace,
                    encodedKey, value, expiresAtUtc: expiresAtUtc);
            var frame = await frames.SendUnaryAsync(writer.WrittenMemory, cancellationToken, allowFallback: false).ConfigureAwait(false);
            if (frame is { } responseFrame)
                return ValidateExchange(ToExchange(key, KvFrameCodec.DecodeExchangeResponse(responseFrame.Payload)), value is not null);
        }
        using var response = value is null
            ? await PostJsonAsync(KvUrl(keyspace, "get-and-delete"), new KvDeleteRequest(Qualify(@namespace, key)),
                RemoteJsonContext.Default.KvDeleteRequest, cancellationToken).ConfigureAwait(false)
            : await PostJsonAsync(KvUrl(keyspace, "get-and-set"), new KvSetRequest(Qualify(@namespace, key), value, expiresAtUtc),
                RemoteJsonContext.Default.KvSetRequest, cancellationToken).ConfigureAwait(false);
        var result = await ReadAtomicJsonAsync(response, RemoteJsonContext.Default.KvExchangeResponse, cancellationToken).ConfigureAwait(false);
        if (result.Previous is not { Found: not null } previous
            || (!previous.Found.Value && (previous.Value is not null || previous.Version is not null || previous.ExpiresAtUtc is not null)))
            throw new InvalidDataException("KV 交换响应缺少旧值存在性或字段不一致；写入结果未知。");
        return ValidateExchange(new SndbKvExchangeResult(previous.Found.Value
            ? new SndbKvEntry(key, previous.Value ?? throw new InvalidDataException("KV 旧值缺少 value。"),
                previous.Version ?? throw new InvalidDataException("KV 旧值缺少 version。"), previous.ExpiresAtUtc)
            : null, result.MutationVersion), value is not null);
    }

    private static SndbKvExchangeResult ValidateExchange(SndbKvExchangeResult result, bool isSet)
    {
        if (result.MutationVersion is <= 0
            || result.MutationVersion.HasValue != (isSet || result.PreviousEntry is not null)
            || result.PreviousEntry is { Version: <= 0 }
            || result.PreviousEntry?.ExpiresAtUtc is { Offset: var offset } && offset != TimeSpan.Zero)
            throw new InvalidDataException("KV 交换响应的版本、存在性或 UTC 过期时间不一致；写入结果未知。");
        return result;
    }

    private static async Task<T> ReadAtomicJsonAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        try { return await ReadJsonAsync(response, typeInfo, cancellationToken).ConfigureAwait(false); }
        catch (JsonException exception)
        {
            throw new InvalidDataException("KV 原子响应不是有效的合同 JSON；写入结果未知。", exception);
        }
    }

    private static SndbKvExchangeResult ToExchange(string key, KvExchangeResult result) => new(
        result.PreviousEntry is { } previous ? new SndbKvEntry(key, previous.Value.ToArray(), previous.Version, previous.ExpiresAtUtc) : null,
        result.MutationVersion);

    private async Task<FrameMessage?> SendAtomicKeyAsync(KvFrameOp op, string keyspace, string @namespace, string key,
        CancellationToken cancellationToken)
    {
        if (_frames is not { } frames || !frames.ShouldTryFrames()) return null;
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodeAtomicKeyRequest(writer, 1, op, _database, keyspace, KvValueCodec.EncodeUtf8(Qualify(@namespace, key)));
        return await frames.SendUnaryAsync(writer.WrittenMemory, cancellationToken, allowFallback: false).ConfigureAwait(false);
    }

    private static void ValidateExpiry(DateTimeOffset? expiresAtUtc)
    {
        if (expiresAtUtc is { Offset: var offset } && offset != TimeSpan.Zero)
            throw new ArgumentException("过期时间必须使用 UTC。", nameof(expiresAtUtc));
    }

    private static readonly System.Text.UTF8Encoding StrictKeyUtf8 = new(false, true);

    private static void ValidateKey(string @namespace, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0 && @namespace.Length == 0)
            throw new ArgumentException("KV key 不能为空。", nameof(key));
        int length = checked(StrictKeyUtf8.GetByteCount(key) + (@namespace.Length == 0 ? 0 : StrictKeyUtf8.GetByteCount(@namespace) + 1));
        if (length > KvFrameCodec.MaxKeyBytes)
            throw new ArgumentOutOfRangeException(nameof(key), "KV key 超过 64 KiB 上限。");
    }
}
