namespace SonnetDB.Mqtt;

/// <summary>
/// Sparkplug B topic 的消息类型。
/// </summary>
internal enum SparkplugMessageType
{
    NBirth,
    DBirth,
    NData,
    DData,
    NDeath,
    DDeath,
    NCommand,
    DCommand,
}

/// <summary>
/// 已校验的 Sparkplug B topic 路由。
/// </summary>
internal readonly record struct SparkplugTopicRoute(
    string GroupId,
    SparkplugMessageType MessageType,
    string EdgeNodeId,
    string? DeviceId)
{
    /// <summary>当前消息是否为 BIRTH 快照。</summary>
    public bool IsBirth => MessageType is SparkplugMessageType.NBirth or SparkplugMessageType.DBirth;

    /// <summary>当前消息是否为死亡通知。</summary>
    public bool IsDeath => MessageType is SparkplugMessageType.NDeath or SparkplugMessageType.DDeath;

    /// <summary>当前消息是否为下行命令。</summary>
    public bool IsCommand => MessageType is SparkplugMessageType.NCommand or SparkplugMessageType.DCommand;

    /// <summary>当前消息是否属于设备级 topic。</summary>
    public bool IsDevice => MessageType is SparkplugMessageType.DBirth
        or SparkplugMessageType.DData
        or SparkplugMessageType.DDeath
        or SparkplugMessageType.DCommand;

    /// <summary>metric 写入使用的 measurement。</summary>
    public string Measurement => DeviceId ?? EdgeNodeId;
}

/// <summary>
/// Sparkplug B topic namespace 解析器。
/// </summary>
internal static class SparkplugTopicParser
{
    /// <summary>
    /// 校验 edge node 可订阅的精确 NCMD/DCMD 或 Primary Host STATE topic。
    /// </summary>
    public static bool IsCommandOrStateTopic(string? topic)
    {
        if (TryParse(topic, out SparkplugTopicRoute route, out _))
            return route.IsCommand;

        if (string.IsNullOrWhiteSpace(topic))
            return false;

        string[] segments = topic.Split('/', StringSplitOptions.None);
        return segments.Length == 3
            && string.Equals(segments[0], "spBv1.0", StringComparison.Ordinal)
            && string.Equals(segments[1], "STATE", StringComparison.Ordinal)
            && IsValidIdentifier(segments[2]);
    }

    /// <summary>
    /// 解析 <c>spBv1.0/{group}/{messageType}/{edgeNode}/[{device}]</c> topic。
    /// </summary>
    public static bool TryParse(string? topic, out SparkplugTopicRoute route, out string error)
    {
        route = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(topic))
        {
            error = "Sparkplug topic 不可为空。";
            return false;
        }

        string[] segments = topic.Split('/', StringSplitOptions.None);
        if (segments.Length is not (4 or 5)
            || !string.Equals(segments[0], "spBv1.0", StringComparison.Ordinal))
        {
            error = "topic 需匹配 spBv1.0/{group_id}/{message_type}/{edge_node_id}/[{device_id}]。";
            return false;
        }

        if (!TryParseMessageType(segments[2], out var messageType))
        {
            error = $"不支持 Sparkplug 消息类型 '{segments[2]}'。";
            return false;
        }

        bool deviceMessage = messageType is SparkplugMessageType.DBirth
            or SparkplugMessageType.DData
            or SparkplugMessageType.DDeath
            or SparkplugMessageType.DCommand;
        if (deviceMessage != (segments.Length == 5))
        {
            error = deviceMessage
                ? $"{segments[2]} topic 必须包含 device_id。"
                : $"{segments[2]} topic 不应包含 device_id。";
            return false;
        }

        string? deviceId = segments.Length == 5 ? segments[4] : null;
        if (!IsValidIdentifier(segments[1])
            || !IsValidIdentifier(segments[3])
            || (deviceId is not null && !IsValidIdentifier(deviceId)))
        {
            error = "group_id、edge_node_id 和 device_id 必须为 1..255 个合法 topic 字符，且不可包含通配符或点模型保留字符。";
            return false;
        }

        route = new SparkplugTopicRoute(segments[1], messageType, segments[3], deviceId);
        return true;
    }

    private static bool TryParseMessageType(string value, out SparkplugMessageType messageType)
    {
        messageType = value switch
        {
            "NBIRTH" => SparkplugMessageType.NBirth,
            "DBIRTH" => SparkplugMessageType.DBirth,
            "NDATA" => SparkplugMessageType.NData,
            "DDATA" => SparkplugMessageType.DData,
            "NDEATH" => SparkplugMessageType.NDeath,
            "DDEATH" => SparkplugMessageType.DDeath,
            "NCMD" => SparkplugMessageType.NCommand,
            "DCMD" => SparkplugMessageType.DCommand,
            _ => default,
        };
        return value is "NBIRTH" or "DBIRTH" or "NDATA" or "DDATA"
            or "NDEATH" or "DDEATH" or "NCMD" or "DCMD";
    }

    private static bool IsValidIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255)
            return false;

        return value.AsSpan().IndexOfAny("+#,=\n\r\t\"") < 0;
    }
}
