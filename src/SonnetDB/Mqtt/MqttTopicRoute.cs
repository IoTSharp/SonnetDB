using SonnetDB.Hosting;

namespace SonnetDB.Mqtt;

/// <summary>
/// SonnetDB MQTT topic 解析结果。
/// </summary>
internal readonly record struct MqttTopicRoute(
    MqttTopicKind Kind,
    string Db,
    string Resource);

/// <summary>
/// SonnetDB MQTT topic 类型。
/// </summary>
internal enum MqttTopicKind
{
    /// <summary>非 SonnetDB 受管 topic。</summary>
    Unknown,

    /// <summary><c>db/{db}/m/{measurement}</c>，设备发布遥测入库。</summary>
    Measurement,

    /// <summary><c>db/{db}/mq/{topic}</c>，设备发布/订阅 SonnetMQ 消息。</summary>
    Mq,
}

/// <summary>
/// MQTT topic 解析与基本防御。
/// </summary>
internal static class MqttTopicParser
{
    /// <summary>
    /// 解析 SonnetDB 受管 topic。当前 #242 仅接受无通配符的精确 topic。
    /// </summary>
    public static bool TryParse(string? topic, out MqttTopicRoute route, out string error)
    {
        route = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(topic))
        {
            error = "topic 不可为空。";
            return false;
        }

        if (topic.Contains('+', StringComparison.Ordinal) || topic.Contains('#', StringComparison.Ordinal))
        {
            error = "SonnetDB MQTT topic 当前仅支持精确订阅，不支持 +/# 通配符。";
            return false;
        }

        string[] segments = topic.Split('/', StringSplitOptions.None);
        if (segments.Length != 4 || !string.Equals(segments[0], "db", StringComparison.Ordinal))
        {
            error = "topic 需匹配 db/{db}/m/{measurement} 或 db/{db}/mq/{topic}。";
            return false;
        }

        string db = Uri.UnescapeDataString(segments[1]);
        string marker = segments[2];
        string resource = Uri.UnescapeDataString(segments[3]);

        if (!TsdbRegistry.IsValidName(db))
        {
            error = $"非法数据库名 '{db}'。";
            return false;
        }

        if (string.Equals(marker, "m", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(resource) || resource.Length > 255)
            {
                error = $"非法 measurement 名 '{resource}'。";
                return false;
            }

            route = new MqttTopicRoute(MqttTopicKind.Measurement, db, resource);
            return true;
        }

        if (string.Equals(marker, "mq", StringComparison.Ordinal))
        {
            if (!IsValidMqTopicName(resource))
            {
                error = $"非法 topic 名 '{resource}'。";
                return false;
            }

            route = new MqttTopicRoute(MqttTopicKind.Mq, db, resource);
            return true;
        }

        error = "topic 需匹配 db/{db}/m/{measurement} 或 db/{db}/mq/{topic}。";
        return false;
    }

    private static bool IsValidMqTopicName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 128)
            return false;

        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            bool valid =
                ch is >= 'a' and <= 'z' ||
                ch is >= 'A' and <= 'Z' ||
                ch is >= '0' and <= '9' ||
                ch is '_' or '-' or '.';
            if (!valid)
                return false;
        }

        return true;
    }
}
