using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Vector.Primitives;

namespace SonnetDB.SemanticContent;

internal static class VisualEmbeddingValidation
{
    internal static string TargetIdentity(VisualDerivedTarget target)
    {
        var identity = new VisualAppearanceIdentity(target.Source.ObjectRef.Bucket, target.Source.ObjectRef.Key,
            target.Source.ObjectRef.VersionId, target.Source.ObjectRef.ETag, target.DetectorProfileId, target.Id);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity,
            VisualAppearanceIdentityJsonContext.Default.VisualAppearanceIdentity)));
    }

    internal static VisualDetectorProfile FreezeDetector(VisualDetectorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        int count = profile.SupportedModalities?.Count ?? 0;
        if (count is < 1 or > 2)
            throw new ArgumentException("detector 必须声明一至两种模态。");
        var modalities = new SemanticContentModality[count];
        for (int i = 0; i < modalities.Length; i++) modalities[i] = profile.SupportedModalities![i];
        return profile with { SupportedModalities = Array.AsReadOnly(modalities) };
    }

    internal static VisualDerivedTarget FreezeTarget(VisualDerivedTarget target, VisualDetectorProfile detector, CancellationToken token)
    {
        if (!VisualContentValidator.ValidateTarget(target, detector, token).IsValid)
            throw new ArgumentException("派生目标或 detector profile 无效。");
        return target with
        {
            Source = target.Source with { ObjectRef = target.Source.ObjectRef with { } },
            Region = target.Region with { },
            CropObjectRef = target.CropObjectRef is null ? null : target.CropObjectRef with { },
            IndexState = target.IndexState with { },
        };
    }

    internal static void Text(string? value, string name, int maximum = 512)
    {
        if (value is null || value.Length > maximum || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} 必须非空且不超过 {maximum} 字符。");
        _ = new UTF8Encoding(false, true).GetByteCount(value);
    }

    internal static EmbeddingProfile Freeze(EmbeddingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Text(profile.Id, nameof(profile.Id));
        Text(profile.Provider, nameof(profile.Provider));
        Text(profile.Model, nameof(profile.Model));
        Text(profile.Revision, nameof(profile.Revision));
        int modalityCount = profile.SupportedModalities?.Count ?? 0;
        if (profile.Dimensions is < 1 or > 4096 || modalityCount is < 1 or > 2 || profile.DataEgressPolicy is null)
            throw new ArgumentException("视觉 profile 维度必须为 1..4096，并且只允许图片/视频输入。");
        var modalities = new SemanticContentModality[modalityCount];
        for (int i = 0; i < modalities.Length; i++)
        {
            modalities[i] = profile.SupportedModalities![i];
            if (modalities[i] is not (SemanticContentModality.Image or SemanticContentModality.Video))
                throw new ArgumentException("视觉 profile 只允许图片/视频输入。");
        }
        if (profile.DataEgressPolicy.Target is not null)
            Text(profile.DataEgressPolicy.Target, "egressTarget", 4096);
        profile = profile with { SupportedModalities = Array.AsReadOnly(modalities) };
        if (SemanticContentValidator.ValidateProfile(profile).Count != 0)
            throw new ArgumentException("视觉 embedding profile 无效。");
        return profile;
    }

    internal static float[] Vector(EmbeddingProfile profile, IReadOnlyList<float> vector, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(vector);
        int count = vector.Count;
        if (count != profile.Dimensions)
            throw new ArgumentException("向量维度与完整 profile 不一致。");
        var copy = new float[count];
        double norm = 0;
        for (int i = 0; i < copy.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            float value = vector[i];
            if (!float.IsFinite(value) || Math.Abs(value) > 1_000_000)
                throw new ArgumentException("向量包含非有限值或超出受支持数值范围。");
            copy[i] = value;
            norm += (double)value * value;
        }
        if (norm == 0 && profile.Metric == KnnMetric.Cosine
            || profile.Normalization == EmbeddingNormalization.L2 && Math.Abs(norm - 1) > 0.001)
            throw new ArgumentException("向量的零值/归一化状态与 profile 不一致。");
        return copy;
    }

    internal static bool Equal(EmbeddingProfile left, EmbeddingProfile right)
    {
        if (left.Id != right.Id || left.Provider != right.Provider || left.Model != right.Model
            || left.Revision != right.Revision || left.Dimensions != right.Dimensions
            || left.Metric != right.Metric || left.Normalization != right.Normalization
            || left.DataEgressPolicy != right.DataEgressPolicy
            || left.SupportedModalities.Count != right.SupportedModalities.Count)
            return false;
        for (int i = 0; i < left.SupportedModalities.Count; i++)
            if (left.SupportedModalities[i] != right.SupportedModalities[i]) return false;
        return true;
    }
}

internal sealed record VisualAppearanceIdentity(string Bucket, string Key, string? VersionId, string? ETag,
    string DetectorProfileId, string TargetId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VisualAppearanceIdentity))]
internal sealed partial class VisualAppearanceIdentityJsonContext : JsonSerializerContext;
