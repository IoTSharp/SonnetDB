namespace SonnetDB.SemanticContent;

/// <summary>人脸服务的保留资源名称；远程通用数据 API 必须拒绝读取和改写。</summary>
public static class FaceReservedResourceNames
{
    /// <summary>人脸模板、profile 和审计共同使用的原子持久 keyspace。</summary>
    public const string KeyspaceName = "__face_recognition";

    /// <summary>识别保留名称，包括大小写和 Windows 尾点/空格别名。</summary>
    /// <param name="name">资源名称。</param>
    /// <returns>是否为人脸服务的保留资源。</returns>
    public static bool IsReserved(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.AsSpan().TrimEnd(['.', ' ']).Equals(KeyspaceName, StringComparison.OrdinalIgnoreCase);
    }
}
