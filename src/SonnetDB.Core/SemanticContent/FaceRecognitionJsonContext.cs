using System.Text.Json.Serialization;

namespace SonnetDB.SemanticContent;

/// <summary>人脸合同的 Native AOT 兼容 JSON 元数据。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FaceRecognitionProfile))]
[JsonSerializable(typeof(FaceRecognitionOptions))]
[JsonSerializable(typeof(FaceRecognitionTemplate))]
[JsonSerializable(typeof(FaceRecognitionProbe))]
[JsonSerializable(typeof(FaceVerificationResult))]
[JsonSerializable(typeof(FaceRecognitionCandidate[]))]
[JsonSerializable(typeof(FaceRecognitionAuditEntry[]))]
[JsonSerializable(typeof(FaceRecognitionAuditEntry))]
public sealed partial class FaceRecognitionJsonContext : JsonSerializerContext;
