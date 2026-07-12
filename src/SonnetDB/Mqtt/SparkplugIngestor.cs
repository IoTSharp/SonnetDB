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
    private readonly SparkplugLifecycleStore _lifecycle;
    private readonly ServerMetrics _metrics;
    private readonly SparkplugOptions _options;
    private readonly ILogger<SparkplugIngestor> _logger;

    public SparkplugIngestor(
        TsdbRegistry registry,
        SparkplugAliasStore aliases,
        SparkplugLifecycleStore lifecycle,
        ServerMetrics metrics,
        IOptions<ServerOptions> options,
        ILogger<SparkplugIngestor> logger)
    {
        _registry = registry;
        _aliases = aliases;
        _lifecycle = lifecycle;
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
        out bool requiresRebirth,
        out MqttPubAckReasonCode reasonCode,
        out string reason)
    {
        result = default;
        requiresRebirth = false;
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
            SparkplugLifecycleDecision decision = _lifecycle.Process(
                route,
                reader.Sequence,
                reader.BirthDeathSequence);
            requiresRebirth = decision.RequiresRebirth;
            if (!decision.Accepted)
            {
                if (requiresRebirth)
                    _metrics.RecordSparkplugSequenceGap();
                reason = decision.Reason;
                return true;
            }

            if (route.IsDeath)
            {
                _metrics.RecordSparkplugLifecycleMessage();
                return true;
            }

            reader.CommitBirthAliases();
            if (route.IsBirth)
                _metrics.RecordSparkplugLifecycleMessage();
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
            if (reader.OrphanMetrics > 0)
            {
                requiresRebirth = _lifecycle.MarkRebirthRequired(route);
                if (requiresRebirth)
                    _metrics.RecordSparkplugSequenceGap();
            }
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

    /// <summary>
    /// 在下行命令进入 broker 分发前校验 payload 大小和 protobuf 结构。
    /// </summary>
    public bool TryValidateCommand(
        in SparkplugTopicRoute route,
        ReadOnlyMemory<byte> payload,
        out MqttPubAckReasonCode reasonCode,
        out string reason)
    {
        reasonCode = MqttPubAckReasonCode.Success;
        reason = string.Empty;
        if (_options.MaxPayloadBytes <= 0 || payload.Length > _options.MaxPayloadBytes)
        {
            reasonCode = MqttPubAckReasonCode.PayloadFormatInvalid;
            reason = $"Sparkplug payload 超过 {_options.MaxPayloadBytes} 字节限制。";
            return false;
        }

        try
        {
            var reader = new SparkplugPayloadReader(payload, route, _aliases);
            if (reader.MetricCount == 0)
                throw new BulkIngestException("Sparkplug NCMD/DCMD payload 至少需要一个 metric。");
            return true;
        }
        catch (Exception ex) when (ex is BulkIngestException or ArgumentException or OverflowException)
        {
            reasonCode = MqttPubAckReasonCode.PayloadFormatInvalid;
            reason = ex.Message;
            return false;
        }
    }
}
