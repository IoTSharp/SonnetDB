using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using SonnetDB.Configuration;
using SonnetDB.Diagnostics;
using SonnetDB.Hosting;
using SonnetDB.Ingest;

namespace SonnetDB.Mqtt;

/// <summary>
/// Sparkplug B payload 的共享解码和落库服务。
/// </summary>
internal sealed class SparkplugIngestor
{
    private readonly TsdbRegistry _registry;
    private readonly SparkplugAliasStore _aliases;
    private readonly ServerMetrics _metrics;
    private readonly SparkplugOptions _options;
    private readonly ILogger<SparkplugIngestor> _logger;

    public SparkplugIngestor(
        TsdbRegistry registry,
        SparkplugAliasStore aliases,
        ServerMetrics metrics,
        IOptions<ServerOptions> options,
        ILogger<SparkplugIngestor> logger)
    {
        _registry = registry;
        _aliases = aliases;
        _metrics = metrics;
        _options = options.Value.Mqtt.Sparkplug;
        _logger = logger;
    }

    /// <summary>
    /// 解码并写入一条已经通过 topic 与权限校验的 Sparkplug 消息。
    /// </summary>
    public bool TryIngest(
        in SparkplugTopicRoute route,
        ReadOnlyMemory<byte> payload,
        string topic,
        out BulkIngestResult result,
        out MqttPubAckReasonCode reasonCode,
        out string reason)
    {
        result = default;
        reasonCode = MqttPubAckReasonCode.Success;
        reason = string.Empty;

        if (_options.MaxPayloadBytes <= 0 || payload.Length > _options.MaxPayloadBytes)
        {
            reasonCode = MqttPubAckReasonCode.PayloadFormatInvalid;
            reason = $"Sparkplug payload 超过 {_options.MaxPayloadBytes} 字节限制。";
            return false;
        }

        if (!_registry.TryGet(_options.Database, out var tsdb))
        {
            reasonCode = MqttPubAckReasonCode.ImplementationSpecificError;
            reason = $"Sparkplug 目标数据库 '{_options.Database}' 不存在。";
            return false;
        }

        try
        {
            var reader = new SparkplugPayloadReader(payload, route, _aliases);
            var ingestResult = BulkIngestor.Ingest(
                tsdb,
                reader,
                BulkErrorPolicy.FailFast,
                BulkFlushMode.None);
            result = new BulkIngestResult(
                ingestResult.Written,
                checked(ingestResult.Skipped + reader.SkippedMetrics));
            _metrics.AddInsertedRows(result.Written);
            _metrics.RecordSparkplugIngest(
                result.Skipped,
                reader.OrphanMetrics,
                reader.UnsupportedMetrics);
            _logger.SparkplugIngested(
                topic,
                result.Written,
                result.Skipped,
                reader.OrphanMetrics,
                reader.UnsupportedMetrics);
            return true;
        }
        catch (BulkIngestException ex)
        {
            reasonCode = MqttPubAckReasonCode.PayloadFormatInvalid;
            reason = ex.Message;
            _logger.SparkplugIngestFailed(topic, reason);
            return false;
        }
        catch (ArgumentException ex)
        {
            reasonCode = MqttPubAckReasonCode.PayloadFormatInvalid;
            reason = ex.Message;
            _logger.SparkplugIngestFailed(topic, reason);
            return false;
        }
        catch (OverflowException ex)
        {
            reasonCode = MqttPubAckReasonCode.PayloadFormatInvalid;
            reason = ex.Message;
            _logger.SparkplugIngestFailed(topic, reason);
            return false;
        }
    }
}
