using System.Text.Json;
using SonnetDB.Contracts;
using SonnetDB.Engine;

namespace SonnetDB.Diagnostics;

/// <summary>将强类型选项展开为有界、脱敏的参数清单；不读取环境变量或任意配置文件。</summary>
internal static class ConfigurationSnapshotBuilder
{
    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tokens", "ApiKey", "Password", "UserName", "Secret", "Headers", "Credentials",
        "CloudAccessToken", "CloudRefreshToken", "CloudDeviceLocalId", "PrivateKey", "ConnectionString",
    };

    private static readonly HashSet<string> DisplayStrings = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mode", "TimestampEncoding", "ValueEncoding", "Strategy", "Provider", "Backend", "Metric",
        "Tokenizer", "StorageMode", "Profile", "Model", "ModelId", "Revision", "Pooling", "PaddingSide",
        "TempFileSuffix", "Host", "ClientId", "WebSocketPath", "TopicFilter", "Database", "HostId",
    };

    /// <summary>返回标量参数及明确的脱敏状态；集合有界，未知字符串默认隐藏。</summary>
    /// <param name="value">通过 source-generated context 得到的选项 JSON。</param>
    /// <param name="prefix">参数路径前缀。</param>
    /// <param name="serverAdmin">是否允许查看实例路径。</param>
    /// <param name="changeMode">真实配置修改方式。</param>
    internal static IReadOnlyList<ConfigurationEntry> Flatten(
        JsonElement value, string prefix, bool serverAdmin, string changeMode)
    {
        var entries = new List<ConfigurationEntry>();
        Visit(value, prefix, string.Empty, 0);
        return entries;

        void Visit(JsonElement element, string path, string name, int depth)
        {
            if (depth > 16 || entries.Count >= 1024)
                throw new InvalidOperationException("配置快照超过展示预算。");
            if (SecretNames.Contains(name)
                || name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase))
            {
                Add(IsConfigured(element) ? "已配置（已隐藏）" : "未配置", true);
                return;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    // 任意字典的键也可能是凭据或其它租户标识，不将其用作响应路径。
                    if (name is "MeasurementModes")
                    {
                        Add($"{element.EnumerateObject().Count()} 项覆盖", true);
                        return;
                    }
                    foreach (var property in element.EnumerateObject())
                        Visit(property.Value, path + ":" + property.Name, property.Name, depth + 1);
                    break;
                case JsonValueKind.Array:
                    Add($"{element.GetArrayLength()} 项（内容隐藏）", true);
                    break;
                case JsonValueKind.String:
                    var text = element.GetString() ?? string.Empty;
                    if (text.Length == 0) Add("未配置", false);
                    else if (DisplayStrings.Contains(name)
                        || (serverAdmin && name is "DataRoot" or "RootDirectory" or "MountedDatabasePath" or "MountedDatabaseName" or "HelpDocsRoot")
                        || TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out _))
                        Add(text, false);
                    else if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                        Add(new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.GetLeftPart(UriPartial.Path), true);
                    else Add("已配置（已隐藏）", true);
                    break;
                case JsonValueKind.Null:
                    Add("未配置", false);
                    break;
                default:
                    Add(element.GetRawText(), false);
                    break;
            }

            void Add(string display, bool redacted) => entries.Add(new ConfigurationEntry(path, display, redacted, changeMode));
        }
    }

    private static bool IsConfigured(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => false,
        JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
        JsonValueKind.Object => value.EnumerateObject().Any(),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        _ => true,
    };

    /// <summary>
    /// 展开引擎实际采用的安全配置子集。
    /// <para>TsdbOptions 包含测试用委托（例如 Retention.NowFn），不能整体序列化；这里显式列出可展示参数，避免把委托或路径泄露到 API。</para>
    /// </summary>
    internal static IReadOnlyList<ConfigurationEntry> FlattenEngine(TsdbOptions options, bool serverAdmin, string changeMode)
    {
        var entries = new List<ConfigurationEntry>();
        Add("Engine:RootDirectory", serverAdmin ? options.RootDirectory : "已配置（已隐藏）", !serverAdmin);
        Add("Engine:WalBufferSize", options.WalBufferSize);
        Add("Engine:SyncWalOnEveryWrite", options.SyncWalOnEveryWrite);
        Add("Engine:FlushWalToOsOnWrite", options.FlushWalToOsOnWrite);
        Add("Engine:UseSimdNumericAggregates", options.UseSimdNumericAggregates);
        Add("Engine:AllowUserFunctions", options.AllowUserFunctions);

        Add("Engine:FlushPolicy:MaxBytes", options.FlushPolicy.MaxBytes);
        Add("Engine:FlushPolicy:HardCapBytes", options.FlushPolicy.HardCapBytes);
        Add("Engine:FlushPolicy:MaxPoints", options.FlushPolicy.MaxPoints);
        AddTime("Engine:FlushPolicy:MaxAge", options.FlushPolicy.MaxAge);

        Add("Engine:SegmentWriterOptions:BufferSize", options.SegmentWriterOptions.BufferSize);
        Add("Engine:SegmentWriterOptions:FsyncOnCommit", options.SegmentWriterOptions.FsyncOnCommit);
        Add("Engine:SegmentWriterOptions:TempFileSuffix", options.SegmentWriterOptions.TempFileSuffix);
        Add("Engine:SegmentWriterOptions:TimestampEncoding", options.SegmentWriterOptions.TimestampEncoding);
        Add("Engine:SegmentWriterOptions:ValueEncoding", options.SegmentWriterOptions.ValueEncoding);
        Add("Engine:SegmentReaderOptions:VerifyIndexCrc", options.SegmentReaderOptions.VerifyIndexCrc);
        Add("Engine:SegmentReaderOptions:VerifyBlockCrc", options.SegmentReaderOptions.VerifyBlockCrc);
        Add("Engine:SegmentReaderOptions:DecodeBlockCacheMaxBytes", options.SegmentReaderOptions.DecodeBlockCacheMaxBytes);
        Add("Engine:SegmentReaderOptions:VectorIndexCacheMaxBytes", options.SegmentReaderOptions.VectorIndexCacheMaxBytes);
        Add("Engine:SegmentReaderOptions:UseMemoryMappedFileForLargeSegments", options.SegmentReaderOptions.UseMemoryMappedFileForLargeSegments);
        Add("Engine:SegmentReaderOptions:MemoryMappedFileThresholdBytes", options.SegmentReaderOptions.MemoryMappedFileThresholdBytes);

        Add("Engine:WalGroupCommit:Enabled", options.WalGroupCommit.Enabled);
        AddTime("Engine:WalGroupCommit:FlushWindow", options.WalGroupCommit.FlushWindow);
        Add("Engine:BackgroundFlush:Enabled", options.BackgroundFlush.Enabled);
        AddTime("Engine:BackgroundFlush:PollInterval", options.BackgroundFlush.PollInterval);
        AddTime("Engine:BackgroundFlush:ShutdownTimeout", options.BackgroundFlush.ShutdownTimeout);
        Add("Engine:Compaction:Enabled", options.Compaction.Enabled);
        Add("Engine:Compaction:MinTierSize", options.Compaction.MinTierSize);
        Add("Engine:Compaction:TierSizeRatio", options.Compaction.TierSizeRatio);
        Add("Engine:Compaction:FirstTierMaxBytes", options.Compaction.FirstTierMaxBytes);
        AddTime("Engine:Compaction:PollInterval", options.Compaction.PollInterval);
        AddTime("Engine:Compaction:ShutdownTimeout", options.Compaction.ShutdownTimeout);
        Add("Engine:WalRolling:Enabled", options.WalRolling.Enabled);
        Add("Engine:WalRolling:MaxBytesPerSegment", options.WalRolling.MaxBytesPerSegment);
        Add("Engine:WalRolling:MaxRecordsPerSegment", options.WalRolling.MaxRecordsPerSegment);
        Add("Engine:Retention:Enabled", options.Retention.Enabled);
        AddTime("Engine:Retention:Ttl", options.Retention.Ttl);
        AddTime("Engine:Retention:PollInterval", options.Retention.PollInterval);
        Add("Engine:Retention:MaxTombstonesPerRound", options.Retention.MaxTombstonesPerRound);
        AddTime("Engine:Retention:ShutdownTimeout", options.Retention.ShutdownTimeout);
        Add("Engine:Retention:TtlInTimestampUnits", options.Retention.TtlInTimestampUnits);
        Add("Engine:TombstoneCheckpoint:Enabled", options.TombstoneCheckpoint.Enabled);
        Add("Engine:TombstoneCheckpoint:MaxDeletesSinceCheckpoint", options.TombstoneCheckpoint.MaxDeletesSinceCheckpoint);
        AddTime("Engine:TombstoneCheckpoint:MaxInterval", options.TombstoneCheckpoint.MaxInterval);

        Add("Engine:Kv:WalBufferSize", options.Kv.WalBufferSize);
        Add("Engine:Kv:SyncWalOnEveryWrite", options.Kv.SyncWalOnEveryWrite);
        Add("Engine:Kv:AutoCheckpointEnabled", options.Kv.AutoCheckpointEnabled);
        Add("Engine:Kv:MaxWalBytes", options.Kv.MaxWalBytes);
        Add("Engine:Kv:MaxOverlayEntries", options.Kv.MaxOverlayEntries);
        Add("Engine:Kv:MaxSnapshotOverlayEntries", options.Kv.MaxSnapshotOverlayEntries);
        Add("Engine:Kv:IndexRebuildMaxWalBytes", options.Kv.IndexRebuildMaxWalBytes);
        Add("Engine:Kv:IndexRebuildMaxOverlayEntries", options.Kv.IndexRebuildMaxOverlayEntries);
        Add("Engine:Kv:MaxKeyBytes", options.Kv.MaxKeyBytes);
        Add("Engine:Kv:MaxValueBytes", options.Kv.MaxValueBytes);
        Add("Engine:Kv:MaxConcurrentStateReads", options.Kv.MaxConcurrentStateReads);
        Add("Engine:Kv:MaxQueuedStateReads", options.Kv.MaxQueuedStateReads);
        Add("Engine:Kv:StateReadWaitTimeoutMilliseconds", options.Kv.StateReadWaitTimeoutMilliseconds);
        Add("Engine:Kv:DefaultScanLimit", options.Kv.DefaultScanLimit);
        Add("Engine:Kv:BatchDeleteMaxKeys", options.Kv.BatchDeleteMaxKeys);
        Add("Engine:Kv:BatchDeleteMaxBytes", options.Kv.BatchDeleteMaxBytes);
        Add("Engine:Kv:ExpirerEnabled", options.Kv.ExpirerEnabled);
        AddTime("Engine:Kv:ExpirerPollInterval", options.Kv.ExpirerPollInterval);
        Add("Engine:Kv:ExpirerBatchSize", options.Kv.ExpirerBatchSize);
        Add("Engine:Kv:CleanupEnabled", options.Kv.CleanupEnabled);
        AddTime("Engine:Kv:CleanupPollInterval", options.Kv.CleanupPollInterval);
        Add("Engine:Kv:CleanupMaxFilesPerRound", options.Kv.CleanupMaxFilesPerRound);
        Add("Engine:Kv:CleanupPauseWhenQueriesActive", options.Kv.CleanupPauseWhenQueriesActive);
        Add("Engine:Kv:CleanupPauseWhenFlushPending", options.Kv.CleanupPauseWhenFlushPending);
        Add("Engine:Kv:CleanupMaxCpuPercent", options.Kv.CleanupMaxCpuPercent);
        Add("Engine:Kv:CleanupMaxMemoryLoadPercent", options.Kv.CleanupMaxMemoryLoadPercent);

        Add("Engine:SqlMemory:QueryLimitBytes", options.SqlMemory.QueryLimitBytes);
        Add("Engine:SqlMemory:GlobalLimitBytes", options.SqlMemory.GlobalLimitBytes);
        Add("Engine:SqlMemory:MaxParallelWorkers", options.SqlMemory.MaxParallelWorkers);
        Add("Engine:SqlMemory:ParallelismMinRows", options.SqlMemory.ParallelismMinRows);
        Add("Engine:SqlMemory:ParallelWorkerMemoryBytes", options.SqlMemory.ParallelWorkerMemoryBytes);
        Add("Engine:MeasurementSchemaPolicy:Mode", options.MeasurementSchemaPolicy.Mode);
        Add("Engine:MeasurementSchemaPolicy:MeasurementModes", $"{options.MeasurementSchemaPolicy.MeasurementModes.Count} 项覆盖", true);
        Add("Engine:MeasurementSchemaPolicy:MaxMeasurements", options.MeasurementSchemaPolicy.MaxMeasurements);
        Add("Engine:MeasurementSchemaPolicy:MaxColumnsPerMeasurement", options.MeasurementSchemaPolicy.MaxColumnsPerMeasurement);
        Add("Engine:MeasurementSchemaPolicy:MaxNewColumnsPerWrite", options.MeasurementSchemaPolicy.MaxNewColumnsPerWrite);
        return entries;

        void Add(string path, object? value, bool redacted = false)
            => entries.Add(new ConfigurationEntry(path, value?.ToString() ?? "未配置", redacted, changeMode));
        void AddTime(string path, TimeSpan value) => Add(path, value.ToString());
    }
}
