using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Auth;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.ObjectStorage;
using SonnetDB.SemanticContent;

namespace SonnetDB.SemanticSearch;

/// <summary>统一约束语义 provider 的内容外发、输入边界和持久调用审计。</summary>
internal sealed class SemanticEmbeddingService
{
    private readonly IMultimodalEmbeddingProvider _provider;
    private readonly IObjectEmbeddingProvider _objects;
    private readonly SemanticSearchOptions _options;
    private readonly SemanticDataEgressPolicy _policy;
    private readonly IHttpContextAccessor? _httpContext;

    public SemanticEmbeddingService(IMultimodalEmbeddingProvider provider, IObjectEmbeddingProvider objects,
        IOptions<ServerOptions> options, IHttpContextAccessor? httpContext = null)
    {
        _provider = provider;
        _objects = objects;
        _options = options.Value.SemanticSearch;
        _policy = _options.DataEgressPolicy ?? throw new ArgumentException("外发策略不能为空。", nameof(options));
        _httpContext = httpContext;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.EmbeddingTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.EmbeddingTimeoutSeconds, 300);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxObjectEmbeddingBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaxObjectEmbeddingBytes, 100 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxTextEmbeddingBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaxTextEmbeddingBytes, 4 * 1024 * 1024);
        if (!Enum.IsDefined(_policy.Mode) || (_policy.Mode != SemanticDataEgressMode.LocalOnly
            && string.IsNullOrWhiteSpace(_policy.Target)))
            throw new ArgumentException("外发模式无效或缺少明确目标。", nameof(options));
    }

    internal IObjectEmbeddingProvider ObjectProvider => _objects;

    internal Task<float[]> EmbedTextAsync(Tsdb tsdb, string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        int bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > _options.MaxTextEmbeddingBytes)
            throw new ArgumentOutOfRangeException(nameof(text), "文本超过 embedding 输入上限。");
        return InvokeAsync(tsdb, _provider.Info, "text", bytes, null,
            token => _provider.EmbedTextAsync(text, token), cancellationToken);
    }

    internal Task<float[]> EmbedImageAsync(Tsdb tsdb, ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        if (image.IsEmpty || image.Length > _options.MaxImageBytes)
            throw new ArgumentOutOfRangeException(nameof(image), "图片超过 embedding 输入上限或为空。");
        return InvokeAsync(tsdb, _provider.Info, "image", image.Length, null,
            token => _provider.EmbedImageAsync(image, token), cancellationToken);
    }

    internal Task<float[]> EmbedStoredObjectAsync(Tsdb tsdb, SndbObjectInfo source,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var reference = new SemanticObjectReference(source.Bucket, source.Key, source.VersionId, source.ETag);
        if (content.Length != source.SizeBytes)
            throw new InvalidDataException("对象内容长度与固定版本不一致。");
        // 既有 Bucket 图片摄取保留 MaxImageBytes 合同；新对象入口的独立大小限制不反向收紧它。
        ValidateObjectInput(source, applyObjectByteLimit: false);
        if (_objects.Info.Profile != _provider.Info.Profile || _objects.Info.Dimensions != _provider.Info.Dimensions)
            throw new InvalidOperationException("对象 provider 的向量空间与图片检索 profile 不兼容。");
        return InvokeAsync(tsdb, _objects.Info, "object", content.Length, reference,
            token => _objects.EmbedObjectAsync(reference, source.ContentType, content, token), cancellationToken);
    }

    internal async Task<ObjectEmbeddingResponse> EmbedObjectAsync(Tsdb tsdb, SemanticObjectReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Key);
        if (string.IsNullOrWhiteSpace(reference.VersionId) && string.IsNullOrWhiteSpace(reference.ETag))
            throw new ArgumentException("对象 embedding 必须指定版本或 ETag。", nameof(reference));
        var store = new SndbObjectStore(tsdb);
        var info = store.HeadObject(reference.Bucket, reference.Key, reference.VersionId)
            ?? throw new KeyNotFoundException("对象版本不存在。");
        if (reference.ETag is not null && !string.Equals(reference.ETag, info.ETag, StringComparison.Ordinal))
            throw new SemanticObjectVersionException();
        ValidateObjectInput(info);
        var resolved = new SemanticObjectReference(info.Bucket, info.Key, info.VersionId, info.ETag);
        float[] vector = await InvokeAsync(tsdb, _objects.Info, "object", checked((int)info.SizeBytes), resolved,
            async token =>
            {
                var read = store.OpenRead(info.Bucket, info.Key, range: null, info.VersionId)
                    ?? throw new KeyNotFoundException("对象版本不存在。");
                await using var stream = read.Content;
                if (read.Info.VersionId != info.VersionId || read.Info.ETag != info.ETag
                    || read.Info.SizeBytes != info.SizeBytes || read.Length != info.SizeBytes)
                    throw new InvalidDataException("读取的对象版本与已解析引用不一致。");
                byte[] content = new byte[checked((int)info.SizeBytes)];
                await stream.ReadExactlyAsync(content, token).ConfigureAwait(false);
                return await _objects.EmbedObjectAsync(resolved, info.ContentType, content, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        return new ObjectEmbeddingResponse(resolved, _objects.Info.Name, _objects.Info.Profile, vector);
    }

    private void ValidateObjectInput(SndbObjectInfo source, bool applyObjectByteLimit = true)
    {
        if (source.SizeBytes <= 0 || (applyObjectByteLimit && source.SizeBytes > _options.MaxObjectEmbeddingBytes))
            throw new ArgumentOutOfRangeException(nameof(source), "对象超过 embedding 输入上限或为空。");
        string mediaType = MultimodalObjectEmbeddingProvider.NormalizeContentType(source.ContentType);
        if (!_objects.ContentTypes.Contains(mediaType, StringComparer.Ordinal))
            throw new NotSupportedException("对象媒体类型不在 provider 能力清单中。");
        if ((mediaType.StartsWith("text/", StringComparison.Ordinal) && source.SizeBytes > _options.MaxTextEmbeddingBytes)
            || (mediaType.StartsWith("image/", StringComparison.Ordinal) && source.SizeBytes > _options.MaxImageBytes))
            throw new ArgumentOutOfRangeException(nameof(source), "对象超过对应模态的 embedding 输入上限。");
    }

    private async Task<float[]> InvokeAsync(Tsdb tsdb, MultimodalEmbeddingProviderInfo info,
        string kind, int inputBytes, SemanticObjectReference? source,
        Func<CancellationToken, ValueTask<float[]>> invoke, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.EmbeddingTimeoutSeconds));
        HttpContext? context = _httpContext?.HttpContext;
        string principal = context is null ? "background"
            : BearerAuthMiddleware.GetUser(context)?.UserName
                ?? "role:" + (BearerAuthMiddleware.GetRole(context) ?? "unknown");
        var entry = new SemanticEmbeddingAuditEntry(Guid.NewGuid(), DateTimeOffset.UtcNow,
            principal.Length <= 256 ? principal : principal[..256], info.Name, info.Profile, kind, inputBytes,
            source is null ? null : HashIdentity(source), _policy.Mode, info.IsLocal, "started", 0, null);
        var audit = new SemanticEmbeddingAuditStore(tsdb);
        bool allowed = IsAllowed(info, _policy);
        if (!allowed)
        {
            audit.Write(entry with { Status = "denied", ErrorCode = "semantic_egress_denied" }, deadline.Token);
            throw new SemanticEmbeddingPolicyException();
        }
        // 即使策略没有要求审计也保留调用记录。started 必须成功提交后才能把内容交给 provider。
        audit.Write(entry, deadline.Token);
        long started = Stopwatch.GetTimestamp();
        float[] vector;
        try
        {
            if (!_options.Enabled || !info.Ready)
                throw new InvalidOperationException("语义 embedding provider 未就绪。");
            vector = await invoke(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (vector.Length != info.Dimensions || vector.Length == 0 || vector.Any(static value => !float.IsFinite(value)))
                throw new InvalidDataException("provider 返回的向量维度或数值无效。");
        }
        catch (OperationCanceledException)
        {
            bool cancelled = cancellationToken.IsCancellationRequested;
            WriteTerminal(audit, entry, started, cancelled ? "cancelled" : "timed_out",
                cancelled ? "semantic_cancelled" : "semantic_timeout");
            if (!cancelled)
                throw new TimeoutException("语义 embedding 调用超时。");
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException)
        {
            WriteTerminal(audit, entry, started, "failed", "semantic_invalid_input");
            throw new ArgumentException("provider 无法处理输入内容。");
        }
        catch (Exception)
        {
            WriteTerminal(audit, entry, started, "failed", "semantic_provider_failed");
            throw new InvalidOperationException("语义 embedding provider 调用失败；详见脱敏调用审计。");
        }
        WriteTerminal(audit, entry, started, "succeeded", null);
        return vector;
    }

    internal static bool IsAllowed(MultimodalEmbeddingProviderInfo info, SemanticDataEgressPolicy policy)
        => policy.Mode switch
        {
            SemanticDataEgressMode.LocalOnly => info.IsLocal,
            SemanticDataEgressMode.ConfiguredProvider => !string.IsNullOrWhiteSpace(policy.Target)
                && string.Equals(policy.Target, info.Name, StringComparison.Ordinal),
            SemanticDataEgressMode.ExternalProvider => !string.IsNullOrWhiteSpace(policy.Target)
                && string.Equals(policy.Target, info.Target, StringComparison.Ordinal),
            _ => false,
        };

    private static void WriteTerminal(SemanticEmbeddingAuditStore audit, SemanticEmbeddingAuditEntry entry,
        long started, string status, string? errorCode)
    {
        // 调用取消后仍以独立五秒预算写终态；失败不返回向量，started 保留供恢复审查。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        audit.Write(entry with { Status = status, ErrorCode = errorCode,
            DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds }, deadline.Token);
    }

    private static string HashIdentity(SemanticObjectReference source)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{source.Bucket.Length}:{source.Bucket}{source.Key.Length}:{source.Key}{source.VersionId}")));
}

internal sealed class SemanticEmbeddingPolicyException() : InvalidOperationException("内容外发策略拒绝当前 provider。");
internal sealed class SemanticObjectVersionException() : InvalidOperationException("对象 ETag 与请求不一致。");
