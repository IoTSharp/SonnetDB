namespace SonnetDB.Configuration;

/// <summary>
/// 服务器配置。绑定路径：<c>"SonnetDBServer"</c>。
/// </summary>
public sealed class ServerOptions
{
    /// <summary>
    /// 数据库根目录。每个 db 在该目录下占一个子目录。
    /// </summary>
    public string DataRoot { get; set; } = "./sonnetdb-data";

    /// <summary>
    /// 启动时若 <see cref="DataRoot"/> 下存在子目录，是否自动作为已存在的数据库注册。
    /// </summary>
    public bool AutoLoadExistingDatabases { get; set; } = true;

    /// <summary>
    /// 可选的已有嵌入式数据库目录。指定时只挂载这一目录，控制面仍位于 <see cref="DataRoot"/>。
    /// </summary>
    public string? MountedDatabasePath { get; set; }

    /// <summary>
    /// 已挂载嵌入式数据库在 Server 中显示的名称。
    /// </summary>
    public string? MountedDatabaseName { get; set; }

    /// <summary>
    /// 启动时可并行冷开的关系表数量。默认串行逐表恢复，优先限制恢复期的内存和 WAL 峰值。
    /// </summary>
    public int RelationalTableWarmupConcurrency { get; set; } = 1;

    /// <summary>
    /// Bearer token → 角色映射。允许的角色：<c>admin</c>、<c>readwrite</c>、<c>readonly</c>。
    /// </summary>
    public Dictionary<string, string> Tokens { get; set; } = new();

    /// <summary>
    /// 是否对 <c>/healthz</c>、<c>/healthz/live</c>、<c>/healthz/ready</c> 与 <c>/metrics</c> 端点豁免认证。默认 <c>true</c>。
    /// </summary>
    public bool AllowAnonymousProbes { get; set; } = true;

    /// <summary>
    /// 帮助文档静态站点根目录。若为空，则默认使用 <c>AppContext.BaseDirectory/wwwroot/help</c>。
    /// </summary>
    public string? HelpDocsRoot { get; set; }

    /// <summary>
    /// 旧版慢查询开关。仅用于兼容尚未迁移到
    /// <c>SonnetDBServer:Observability:SlowQueryLog:Enabled</c> 的配置文件。
    /// </summary>
    public bool SlowQueryEnabled { get; set; } = true;

    /// <summary>
    /// 旧版慢查询基础阈值。仅用于兼容平铺配置。
    /// </summary>
    public int SlowQueryThresholdMs { get; set; } = 10_000;

    /// <summary>
    /// 旧版慢查询警告级阈值。仅用于兼容平铺配置。
    /// </summary>
    public int SlowQueryWarningThresholdMs { get; set; } = 30_000;

    /// <summary>
    /// 旧版慢查询严重级阈值。仅用于兼容平铺配置。
    /// </summary>
    public int SlowQueryCriticalThresholdMs { get; set; } = 60_000;

    /// <summary>
    /// SSE <c>metrics</c> 通道的快照推送周期（秒）。默认 <c>5</c>。
    /// </summary>
    public int MetricsTickSeconds { get; set; } = 5;

    /// <summary>
    /// REST 与 frame-http2 SQL 请求的数据库级并发准入配置。
    /// </summary>
    public SqlHttpAdmissionOptions SqlHttpAdmission { get; set; } = new();

    /// <summary>
    /// 每个数据库实例的 SQL 内部执行资源配置。该配置约束阻塞算子内存和查询内部 worker，
    /// 不等同于 <see cref="SqlHttpAdmission"/> 的 HTTP 请求并发准入。
    /// </summary>
    public SqlExecutionResourceOptions SqlExecution { get; set; } = new();

    /// <summary>
    /// KV 存储维护配置。这里只放服务器部署需要覆盖的有界恢复预算，日常写入预算仍使用 Core 默认值。
    /// </summary>
    public KvStorageOptions Kv { get; set; } = new();

    /// <summary>
    /// 对象正文存储卷配置。设置后，每个数据库的对象正文写入该卷根目录下的独立数据库目录。
    /// </summary>
    public ObjectStorageOptions ObjectStorage { get; set; } = new();

    /// <summary>
    /// 运行时备份任务配置。任务定义保存于 <c>&lt;DataRoot&gt;/.system/backup-tasks.json</c>，
    /// 这里仅配置任务调度的安全根目录和下载/解包预算。
    /// </summary>
    public BackupRuntimeOptions Backup { get; set; } = new();

    /// <summary>
    /// Measurement 自动 schema 增长上限与默认/按名称策略。绑定路径：<c>SonnetDBServer:MeasurementSchema</c>。
    /// </summary>
    public MeasurementSchemaServerOptions MeasurementSchema { get; set; } = new();

    /// <summary>
    /// 可观测性配置（M17）。绑定路径：<c>"SonnetDBServer:Observability"</c>。
    /// </summary>
    public ObservabilityOptions Observability { get; set; } = new();

    /// <summary>
    /// MQTT 接入配置（M28 P5b #242/#243）。绑定路径：<c>"SonnetDBServer:Mqtt"</c>。
    /// </summary>
    public MqttBrokerOptions Mqtt { get; set; } = new();

    /// <summary>
    /// CoAP 接入配置（M30 #265/#266）。绑定路径：<c>"SonnetDBServer:Coap"</c>。
    /// </summary>
    public CoapServerOptions Coap { get; set; } = new();

    /// <summary>
    /// Line Protocol UDP 接入配置（M30 #267）。绑定路径：<c>"SonnetDBServer:LineProtocolUdp"</c>。
    /// </summary>
    public LineProtocolUdpOptions LineProtocolUdp { get; set; } = new();

    /// <summary>
    /// Modbus TCP 协议运行时配置（M34）。全局门禁默认关闭，catalog 中的 <c>ENABLED</c>
    /// 不能单独启动网络连接。
    /// </summary>
    public ModbusRuntimeOptions Modbus { get; set; } = new();

    /// <summary>
    /// Copilot 子系统配置。
    /// </summary>
    public CopilotOptions Copilot { get; set; } = new();

    /// <summary>
    /// 语义图片检索配置。模型文件由部署者提供，SonnetDB 不在运行时下载模型。
    /// </summary>
    public SemanticSearchOptions SemanticSearch { get; set; } = new();
}

/// <summary>
/// KV 写入、读快照及关系表索引恢复的独立有界预算。
/// </summary>
public sealed class KvStorageOptions
{
    /// <summary>
    /// KV keyspace 的存储引擎，默认 <c>Lsm</c>。设为 <c>Legacy</c> 时新目录使用 legacy 布局；已是 LSM 布局的目录总是以 LSM 打开。
    /// </summary>
    public SonnetDB.Kv.KvStorageEngine StorageEngine { get; set; } = SonnetDB.Kv.KvStorageEngine.Lsm;

    /// <summary>
    /// 选择 LSM 时已有 legacy 数据目录的处理方式，默认 <c>Auto</c>：首次打开时自动迁移（旧文件在保留期内可回滚）；
    /// <c>Disabled</c> 时继续以 legacy 打开。
    /// </summary>
    public SonnetDB.Kv.KvLegacyMigrationMode LegacyMigration { get; set; } = SonnetDB.Kv.KvLegacyMigrationMode.Auto;

    /// <summary>
    /// 普通 KV 自动检查点允许的 WAL 最大字节数，默认 256 MiB；
    /// 服务端绑定限制在 1 MiB 到 64 GiB，避免部署配置导致无界 WAL 增长。
    /// </summary>
    public long MaxWalBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// 普通 KV 自动检查点允许的可变覆盖层最大条目数，默认 100,000；
    /// 服务端绑定限制在 1 到 50,000,000，避免内存覆盖层无界增长。
    /// </summary>
    public int MaxOverlayEntries { get; set; } = 100_000;

    /// <summary>
    /// 单次读快照允许复制的可变与冻结覆盖层合计条目数，默认 100,000；
    /// 独立于写入预算，服务端绑定限制在 1 到 50,000,000。
    /// </summary>
    public int MaxSnapshotOverlayEntries { get; set; } = 100_000;

    /// <summary>
    /// 同一数据库请求侧物理 KV 读并发数，默认 8；另保留一个 checkpoint 维护读槽。
    /// </summary>
    public int MaxConcurrentStateReads { get; set; } = 8;

    /// <summary>
    /// 同一数据库物理 KV 读等待队列最大长度，默认 64；超出后请求快速失败，避免无界堆积。
    /// </summary>
    public int MaxQueuedStateReads { get; set; } = 64;

    /// <summary>
    /// 单次请求读等待许可的最长毫秒数，默认 5 秒；绑定限制在 1 到 120,000 毫秒。
    /// </summary>
    public int StateReadWaitTimeoutMilliseconds { get; set; } = 5000;

    /// <summary>
    /// 索引重建允许的 WAL 最大字节数，默认与日常预算一致为 256 MiB；
    /// 服务端绑定会把非正数修正为 1 MiB，避免部署配置意外取消恢复上限。
    /// </summary>
    public long IndexRebuildMaxWalBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// 索引重建允许的可变覆盖层最大条目数，默认与日常预算一致为 100,000；
    /// 服务端绑定会把非正数修正为 1，避免部署配置意外取消恢复上限。
    /// </summary>
    public int IndexRebuildMaxOverlayEntries { get; set; } = 100_000;
}

/// <summary>
/// Server 级对象正文存储卷配置。
/// </summary>
public sealed class ObjectStorageOptions
{
    /// <summary>
    /// 对象卷根目录。设置后数据库 <c>name</c> 的对象正文位于
    /// <c>&lt;RootDirectory&gt;/name/objects</c>。为空时沿用数据库目录下的 <c>objects</c>。
    /// </summary>
    public string? RootDirectory { get; set; }
}

/// <summary>SonnetDB 运行时备份调度的全局边界。</summary>
public sealed class BackupRuntimeOptions
{
    /// <summary>是否启用已保存的备份任务调度。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否开放服务端目录、上传与 URL 在线恢复。默认为关闭。</summary>
    public bool EnableRestore { get; set; }

    /// <summary>允许下载备份包的 HTTP(S) 来源，精确匹配协议、主机及端口。默认禁止 URL 恢复。</summary>
    public List<string> AllowedRestoreUrlOrigins { get; set; } = [];

    /// <summary>所有任务输出的安全根目录。为空时使用 <c>&lt;DataRoot&gt;/.system/backups</c>。</summary>
    public string? DestinationRoot { get; set; }

    /// <summary>调度器检查任务的周期，默认一分钟。</summary>
    public int SchedulerTickSeconds { get; set; } = 60;

    /// <summary>上传或 HTTP 下载的单个备份包最大字节数，默认 20 GiB。</summary>
    public long MaxTransferBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    /// <summary>下载、解包和校验的单次最长秒数，默认 30 分钟。</summary>
    public int TransferTimeoutSeconds { get; set; } = 30 * 60;
}

/// <summary>任务级备份范围。名称集合为空表示选择该模型的全部资源。</summary>
public sealed class BackupScopeOptions
{
    /// <summary>是否包含时序 measurement。</summary>
    public bool IncludeMeasurements { get; set; } = true;

    /// <summary>是否包含关系表。</summary>
    public bool IncludeTables { get; set; } = true;

    /// <summary>是否包含 KV keyspace。默认关闭以避免把缓存和状态纳入备份。</summary>
    public bool IncludeKeyValue { get; set; }

    /// <summary>是否包含 JSON 文档集合。</summary>
    public bool IncludeDocuments { get; set; } = true;

    /// <summary>是否包含对象桶正文。默认关闭，对象正文应由独立卷策略负责。</summary>
    public bool IncludeObjects { get; set; }

    /// <summary>是否包含 SonnetDB 实例级消息队列快照。</summary>
    public bool IncludeMessageQueue { get; set; }

    /// <summary>是否包含原生属性图。</summary>
    public bool IncludeGraphs { get; set; } = true;

    /// <summary>是否包含文档全文索引。关闭后恢复时可由主数据重建。</summary>
    public bool IncludeFullTextIndexes { get; set; } = true;

    /// <summary>选择的 measurement 名称。为空表示全部。</summary>
    public List<string> Measurements { get; set; } = [];

    /// <summary>选择的关系表名称。为空表示全部。</summary>
    public List<string> Tables { get; set; } = [];

    /// <summary>选择的 KV keyspace 名称。为空表示全部。</summary>
    public List<string> Keyspaces { get; set; } = [];

    /// <summary>选择的文档集合名称。为空表示全部。</summary>
    public List<string> DocumentCollections { get; set; } = [];

    /// <summary>选择的对象 bucket 名称。为空表示全部。</summary>
    public List<string> Buckets { get; set; } = [];

    /// <summary>选择的原生属性图名称。为空表示全部。</summary>
    public List<string> Graphs { get; set; } = [];
}

/// <summary>一个持久化备份任务的定义。</summary>
public sealed class BackupTaskDefinition
{
    /// <summary>任务唯一标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>任务显示名称。</summary>
    public string Name { get; set; } = "daily";

    /// <summary>是否执行该任务。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>每日执行时间，格式为 HH:mm。</summary>
    public string LocalTime { get; set; } = "02:00";

    /// <summary>执行时间的 IANA 或系统时区标识。</summary>
    public string TimeZoneId { get; set; } = "Asia/Shanghai";

    /// <summary>任务输出目录，必须位于全局 DestinationRoot 下。</summary>
    public string DestinationRoot { get; set; } = string.Empty;

    /// <summary>数据库名称集合。为空表示全部已加载数据库。</summary>
    public List<string> DatabaseNames { get; set; } = [];

    /// <summary>备份范围。</summary>
    public BackupScopeOptions Scope { get; set; } = new();

    /// <summary>按创建时间保留的最长天数。</summary>
    public int RetentionDays { get; set; } = 15;

    /// <summary>最多保留的备份批次数。</summary>
    public int RetentionCount { get; set; } = 15;

    /// <summary>创建后是否立即校验 manifest 和文件摘要。</summary>
    public bool VerifyAfterCreate { get; set; } = true;
}

/// <summary>持久化任务最近一次运行结果。</summary>
public sealed class BackupTaskState
{
    /// <summary>任务 ID。</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>最近一次启动时间。</summary>
    public DateTimeOffset? LastStartedUtc { get; set; }

    /// <summary>最近一次完成时间。</summary>
    public DateTimeOffset? LastCompletedUtc { get; set; }

    /// <summary>最近一次运行结果。</summary>
    public string Status { get; set; } = "never";

    /// <summary>最近一次输出目录。</summary>
    public string? LastOutputDirectory { get; set; }

    /// <summary>最近一次错误消息。</summary>
    public string? LastError { get; set; }
}

/// <summary>备份任务持久化文件。</summary>
public sealed class BackupTaskFile
{
    /// <summary>任务定义集合。</summary>
    public List<BackupTaskDefinition> Tasks { get; set; } = [];

    /// <summary>任务运行状态集合。</summary>
    public List<BackupTaskState> States { get; set; } = [];
}

/// <summary>可由服务器配置系统绑定的 measurement schema 策略。</summary>
public sealed class MeasurementSchemaServerOptions
{
    /// <summary>未覆盖时的自动 schema 模式。</summary>
    public SonnetDB.Engine.MeasurementSchemaMode Mode { get; set; }
        = SonnetDB.Engine.MeasurementSchemaMode.CreateAndEvolve;

    /// <summary>按 measurement 名称覆盖的模式，名称区分大小写。</summary>
    public Dictionary<string, SonnetDB.Engine.MeasurementSchemaMode> MeasurementModes { get; set; }
        = new(StringComparer.Ordinal);

    /// <summary>一个数据库允许的 measurement 总数。</summary>
    public int MaxMeasurements { get; set; } = SonnetDB.Engine.MeasurementSchemaPolicy.Default.MaxMeasurements;

    /// <summary>单个 measurement 允许的列总数。</summary>
    public int MaxColumnsPerMeasurement { get; set; }
        = SonnetDB.Engine.MeasurementSchemaPolicy.Default.MaxColumnsPerMeasurement;

    /// <summary>一次写入规划允许新增的列总数。</summary>
    public int MaxNewColumnsPerWrite { get; set; }
        = SonnetDB.Engine.MeasurementSchemaPolicy.Default.MaxNewColumnsPerWrite;

    internal SonnetDB.Engine.MeasurementSchemaPolicy ToPolicy()
    {
        ArgumentNullException.ThrowIfNull(MeasurementModes);
        return new SonnetDB.Engine.MeasurementSchemaPolicy
        {
            Mode = Mode,
            MeasurementModes = new Dictionary<string, SonnetDB.Engine.MeasurementSchemaMode>(
                MeasurementModes, StringComparer.Ordinal),
            MaxMeasurements = MaxMeasurements,
            MaxColumnsPerMeasurement = MaxColumnsPerMeasurement,
            MaxNewColumnsPerWrite = MaxNewColumnsPerWrite,
        };
    }
}

/// <summary>
/// SigLIP2 文本/图片 embedding 与图片检索配置。
/// </summary>
public sealed class SemanticSearchOptions
{
    /// <summary>语义查询的 ANN、候选扫描、精确补偿与协作超时预算。</summary>
    public SemanticSearchQueryOptions Query { get; set; } = new();

    /// <summary>内容外发策略；默认只允许明确声明本地执行的 provider。</summary>
    public SonnetDB.SemanticContent.SemanticDataEgressPolicy DataEgressPolicy { get; set; } = new();

    /// <summary>单次 provider 调用及对象读取的超时秒数，范围 1 到 300。</summary>
    public int EmbeddingTimeoutSeconds { get; set; } = 30;

    /// <summary>对象 embedding 输入的最大字节数，范围 1 到 100 MiB。</summary>
    public int MaxObjectEmbeddingBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>文本 embedding 输入的 UTF-8 最大字节数，范围 1 到 4 MiB。</summary>
    public int MaxTextEmbeddingBytes { get; set; } = 1024 * 1024;

    /// <summary>对象缩略图与语义派生任务的持久队列、恢复和负载边界。</summary>
    public ObjectProcessingOptions ObjectProcessing { get; set; } = new();

    /// <summary>是否启用语义图片检索端点。默认关闭。</summary>
    public bool Enabled { get; set; }

    /// <summary>多模态 provider 名称。当前支持 <c>siglip2-onnx</c>。</summary>
    public string Provider { get; set; } = "siglip2-onnx";

    /// <summary>写入图片索引的 embedding profile 标识。</summary>
    public string Profile { get; set; } = "siglip2-base-patch16-224";

    /// <summary>SigLIP2 文本编码器 ONNX 文件路径。</summary>
    public string TextModelPath { get; set; } = string.Empty;

    /// <summary>SigLIP2 视觉编码器 ONNX 文件路径。</summary>
    public string VisionModelPath { get; set; } = string.Empty;

    /// <summary>SentencePiece <c>tokenizer.model</c> 文件路径。</summary>
    public string TokenizerModelPath { get; set; } = string.Empty;

    /// <summary>文本与图片 embedding 维度。SigLIP2 base 默认 768。</summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>文本编码最大 token 数，包含 EOS 与右侧 padding。</summary>
    public int MaxTextTokens { get; set; } = 64;

    /// <summary>输入图片统一缩放的宽高。SigLIP2 patch16-224 默认 224。</summary>
    public int ImageSize { get; set; } = 224;

    /// <summary>单张输入图片最大字节数。默认 20 MiB。</summary>
    public int MaxImageBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>文本模型输入 tensor 名称。</summary>
    public string TextInputName { get; set; } = "input_ids";

    /// <summary>文本模型输出 tensor 名称。</summary>
    public string TextOutputName { get; set; } = "pooler_output";

    /// <summary>视觉模型输入 tensor 名称。</summary>
    public string VisionInputName { get; set; } = "pixel_values";

    /// <summary>视觉模型输出 tensor 名称。</summary>
    public string VisionOutputName { get; set; } = "pooler_output";

    /// <summary>
    /// ANN 后端。支持 <c>auto</c>、<c>managed</c> 与 <c>usearch</c>；默认在受支持平台优先
    /// USearch，否则使用托管 HNSW。
    /// </summary>
    public string Backend { get; set; } = "auto";

    /// <summary>USearch 不受当前 RID 支持或加载失败时，是否回退到托管 HNSW。</summary>
    public bool FallbackToManaged { get; set; } = true;

    /// <summary>搜索默认返回条数。</summary>
    public int DefaultTopK { get; set; } = 10;

    /// <summary>单次搜索允许的最大返回条数。</summary>
    public int MaxTopK { get; set; } = 100;
}

/// <summary>语义图片查询预算；服务创建时将配置限制在文档声明的安全范围内。</summary>
public sealed class SemanticSearchQueryOptions
{
    /// <summary>filtered ANN 候选预算，默认 512，范围 1～100000。预算不足时转精确路径。</summary>
    public int AnnCandidateLimit { get; set; } = 512;

    /// <summary>允许物化与精确补偿的过滤 ID 数量，默认 4096，范围 0～100000。</summary>
    public int ExactCompensationLimit { get; set; } = 4096;

    /// <summary>预过滤与精确扫描的单页行数，默认 256，范围 1～4096。</summary>
    public int CandidatePageSize { get; set; } = 256;

    /// <summary>单次查询预过滤与精确扫描累计读取行数上限，默认 100000，范围 1～10000000。</summary>
    public int MaxScannedCandidates { get; set; } = 100_000;

    /// <summary>检索阶段协作超时毫秒数，默认 30000，范围 1～600000；不包含 embedding 生成。</summary>
    public int TimeoutMilliseconds { get; set; } = 30_000;

    internal SemanticSearchQueryOptions BoundedCopy() => new()
    {
        AnnCandidateLimit = Math.Clamp(AnnCandidateLimit, 1, 100_000),
        ExactCompensationLimit = Math.Clamp(ExactCompensationLimit, 0, 100_000),
        CandidatePageSize = Math.Clamp(CandidatePageSize, 1, 4096),
        MaxScannedCandidates = Math.Clamp(MaxScannedCandidates, 1, 10_000_000),
        TimeoutMilliseconds = Math.Clamp(TimeoutMilliseconds, 1, 600_000),
    };
}

/// <summary>
/// <c>POST /v1/db/{db}/sql</c>、<c>/sql/batch</c> 与 frame-http2 SQL query 的
/// 数据库级并发准入配置。
/// 每个数据库拥有独立的许可和等待队列，避免慢存储路径耗尽服务器线程与内存。
/// </summary>
public sealed class SqlHttpAdmissionOptions
{
    /// <summary>每个数据库可同时执行的 SQL HTTP 请求数。默认 <c>4</c>。</summary>
    public int PermitLimit { get; set; } = 4;

    /// <summary>
    /// 每个数据库允许异步等待许可的请求数。默认 <c>8</c>；超出后 REST 返回 503，
    /// frame-http2 返回 <c>sql_overloaded</c> 错误帧。
    /// </summary>
    public int QueueLimit { get; set; } = 8;

    /// <summary>
    /// 数据库 SQL 准入最长等待时间（毫秒），默认 <c>30000</c>，有效范围为 1～300000。
    /// 超时沿用 REST 503 / frame-http2 <c>sql_overloaded</c>；不限制已取得许可的执行时间。
    /// </summary>
    public int QueueTimeoutMilliseconds { get; set; } = 30_000;
}

/// <summary>
/// 每个数据库实例的 SQL 阻塞算子内存和查询内部并行资源配置。
/// Server 启动时会把这些可绑定属性冻结为 Core <see cref="SonnetDB.Engine.SqlMemoryOptions"/>。
/// </summary>
public sealed class SqlExecutionResourceOptions
{
    /// <summary>单次过程与触发器调用链的 body 语句预算，默认 64；批量触发器须显式按容量验证配置。</summary>
    public int MaxRoutineStatements { get; set; } = 64;

    /// <summary>过程与触发器嵌套深度预算，默认 8。</summary>
    public int MaxRoutineDepth { get; set; } = 8;

    /// <summary>过程累计结果行数预算，包含 INSERT RETURNING，默认 10000。</summary>
    public int MaxRoutineResultRows { get; set; } = 10_000;

    /// <summary>
    /// REST SQL 显式预览请求的最大行数，默认 10000；
    /// 未请求预览时保留完整结果语义。
    /// </summary>
    public int MaxResultRows { get; set; } = 10_000;

    /// <summary>
    /// REST SQL 显式预览中可早停单表 SELECT 的保留行估算字节上限，默认 16 MiB；
    /// 不约束阻塞查询、其它模型或 RETURNING，也不代表响应字节数或 CLR heap 硬上限。
    /// </summary>
    public long MaxPreviewBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>每条调用链同时存活的 transition set 行数上限。</summary>
    public int MaxTriggerTransitionRows { get; set; } = 100_000;

    /// <summary>每条调用链 transition set 的保守内存预算（字节）。</summary>
    public long MaxTriggerTransitionBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>单事务延迟触发器调用数量上限。</summary>
    public int MaxDeferredTriggerInvocations { get; set; } = 100_000;

    /// <summary>单事务延迟触发器队列内存上限（字节）。</summary>
    public long MaxDeferredTriggerBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>提交锁等待与延迟执行的协作超时（毫秒）。</summary>
    public int TransactionCommitTimeoutMilliseconds { get; set; } = 30_000;

    /// <summary>单条 SQL 的阻塞算子内存上限，默认 64 MiB。</summary>
    public long QueryLimitBytes { get; set; } = SonnetDB.Engine.SqlMemoryOptions.Default.QueryLimitBytes;

    /// <summary>同一数据库全部并发 SQL 共享的阻塞算子内存上限，默认 256 MiB。</summary>
    public long GlobalLimitBytes { get; set; } = SonnetDB.Engine.SqlMemoryOptions.Default.GlobalLimitBytes;

    /// <summary>
    /// 同一数据库可同时使用的查询内部 worker 上限。默认取 Core 根据处理器数量计算的有界值。
    /// </summary>
    public int MaxParallelWorkers { get; set; } = SonnetDB.Engine.SqlMemoryOptions.Default.MaxParallelWorkers;

    /// <summary>估算输入行数达到该阈值后才评估查询内部并行收益，默认 2048 行。</summary>
    public long ParallelismMinRows { get; set; } = SonnetDB.Engine.SqlMemoryOptions.Default.ParallelismMinRows;

    /// <summary>每个查询内部 worker 从查询和全局预算中预留的额度，默认 64 KiB。</summary>
    public long ParallelWorkerMemoryBytes { get; set; } = SonnetDB.Engine.SqlMemoryOptions.Default.ParallelWorkerMemoryBytes;
}

/// <summary>
/// 可观测性配置（M17 #90/#91）。指标 / 追踪默认开启（无导出目标时近零开销）；
/// Prometheus 与 Diagnostic Dump 端点默认关闭，需显式启用。
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>Prometheus 拉取端点配置。</summary>
    public PrometheusOptions Prometheus { get; set; } = new();

    /// <summary>慢查询日志与 Top-N 统计配置。</summary>
    public SlowQueryLogOptions SlowQueryLog { get; set; } = new();

    /// <summary>管理员 Diagnostic Dump 端点配置。</summary>
    public DiagnosticDumpOptions DiagnosticDump { get; set; } = new();
}

/// <summary>
/// Diagnostic Dump 端点配置。端点默认不映射，需显式启用。
/// </summary>
public sealed class DiagnosticDumpOptions
{
    /// <summary>是否启用 <c>GET /v1/diagnostics/dump</c>。默认 <c>false</c>。</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// 慢查询日志配置。达到基础阈值的 SQL 会进入进程内环形缓冲、结构化日志、
/// Activity 事件以及既有 SSE <c>slow_query</c> 通道。
/// </summary>
public sealed class SlowQueryLogOptions
{
    /// <summary>是否启用慢查询采集。默认 <c>true</c>。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 慢查询基础阈值（毫秒）。默认 <c>10000</c>；<c>0</c> 表示记录全部 SQL，
    /// 负数表示关闭采集。
    /// </summary>
    public int ThresholdMs { get; set; } = 10_000;

    /// <summary>警告级阈值（毫秒）。默认 <c>30000</c>；小于等于 0 表示禁用该级别。</summary>
    public int WarningThresholdMs { get; set; } = 30_000;

    /// <summary>严重级阈值（毫秒）。默认 <c>60000</c>；小于等于 0 表示禁用该级别。</summary>
    public int CriticalThresholdMs { get; set; } = 60_000;

    /// <summary>进程内慢查询环形缓冲容量。默认 <c>256</c>，有效范围为 16～4096。</summary>
    public int Capacity { get; set; } = 256;

    /// <summary>进程内查询指纹聚合分组上限。默认 <c>1024</c>，有效范围为 16～16384。</summary>
    public int AggregateCapacity { get; set; } = 1024;
}

/// <summary>
/// Prometheus 拉取端点配置。启用后 <c>/metrics</c> 由 OpenTelemetry Prometheus exporter 接管，
/// 暴露 <c>SonnetDB.Core</c> / <c>SonnetDB.Server</c> Meter 与 ASP.NET Core 指标；
/// 关闭（默认）时保留原有最小指标集文本端点。
/// </summary>
public sealed class PrometheusOptions
{
    /// <summary>是否启用 OpenTelemetry Prometheus 拉取端点。默认 <c>false</c>。</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// MQTT 接入配置。MQTT 协议栈仅位于 Server 层，Core 不感知 MQTT。
/// </summary>
public sealed class MqttBrokerOptions
{
    /// <summary>
    /// 是否启用内建 MQTT broker。默认关闭；发布配置可显式打开。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// MQTT TCP 监听端口。默认 <c>1883</c>。
    /// </summary>
    public int Port { get; set; } = 1883;

    /// <summary>
    /// MQTT over WebSocket 路径。为空时不映射 WebSocket 入口。
    /// </summary>
    public string WebSocketPath { get; set; } = "/mqtt";

    /// <summary>
    /// 每个 MQTT 客户端最多桥接到 SonnetMQ 的订阅数量。
    /// </summary>
    public int MaxMqSubscriptionsPerClient { get; set; } = 32;

    /// <summary>
    /// Sparkplug B 工业遥测接入配置。
    /// </summary>
    public SparkplugOptions Sparkplug { get; set; } = new();

    /// <summary>
    /// 订阅外部 MQTT broker 的 client 配置（M28 P5b #243）。
    /// </summary>
    public MqttExternalClientOptions ExternalClient { get; set; } = new();
}

/// <summary>
/// Sparkplug B payload 解码和目标数据库配置。
/// </summary>
public sealed class SparkplugOptions
{
    /// <summary>
    /// 是否处理 <c>spBv1.0/...</c> topic。默认关闭，需显式指定目标数据库后启用。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Sparkplug metric 写入的 SonnetDB 数据库。数据库需预先创建。
    /// </summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>
    /// 单条 Sparkplug MQTT payload 最大字节数。默认 1 MiB。
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Primary Host Application 标识，用于 retained <c>spBv1.0/STATE/{hostId}</c>。
    /// </summary>
    public string HostId { get; set; } = "sonnetdb-primary";

    /// <summary>
    /// 是否发布 primary host 的 ONLINE/OFFLINE STATE。默认开启。
    /// </summary>
    public bool PublishHostState { get; set; } = true;

    /// <summary>
    /// 自动 Rebirth 队列最多保留的唯一 edge node 数；等待项和正在发布项共同计入容量。
    /// </summary>
    public int RebirthQueueCapacity { get; set; } = 1024;

    /// <summary>
    /// 单条自动 Rebirth 命令允许占用发布 worker 的最长毫秒数，超时后继续处理下一节点。
    /// </summary>
    public int RebirthPublishTimeoutMilliseconds { get; set; } = 5_000;

    /// <summary>
    /// 是否允许外部 MQTT 管理员发布 NCMD/DCMD。默认关闭；开启后仍需显式审批属性。
    /// </summary>
    public bool AllowCommands { get; set; }
}

/// <summary>
/// 外部 MQTT broker 订阅配置。Server 作为 MQTT client 拉取既有 EMQX/Mosquitto 等 broker 的消息。
/// </summary>
public sealed class MqttExternalClientOptions
{
    /// <summary>
    /// 是否启用外部 MQTT client 订阅。默认关闭。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 外部 MQTT broker 主机名或 IP。
    /// </summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>
    /// 外部 MQTT broker 端口。默认 <c>1883</c>。
    /// </summary>
    public int Port { get; set; } = 1883;

    /// <summary>
    /// 是否使用 TLS 连接外部 broker。
    /// </summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// 连接外部 broker 时使用的 client id。为空时使用默认稳定 id。
    /// </summary>
    public string ClientId { get; set; } = "sonnetdb-external-client";

    /// <summary>
    /// 外部 broker 用户名。为空表示不发送用户名/密码。
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// 外部 broker 密码。仅用于连接外部 broker，不映射为 SonnetDB 用户。
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// 是否使用 clean start 会话。默认 <c>true</c>，避免重启后重放外部 broker 积压消息。
    /// </summary>
    public bool CleanStart { get; set; } = true;

    /// <summary>
    /// 首次重连等待秒数。连接失败后按指数退避增长到 <see cref="MaxReconnectDelaySeconds"/>。
    /// </summary>
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>
    /// 最大重连等待秒数。
    /// </summary>
    public int MaxReconnectDelaySeconds { get; set; } = 60;

    /// <summary>
    /// 向外部 broker 订阅的 topic filter 列表。收到的实际 topic 仍需匹配
    /// <c>db/{db}/m/{measurement}</c> 后才会落库。
    /// </summary>
    public List<MqttExternalSubscriptionOptions> Subscriptions { get; set; } = [];
}

/// <summary>
/// 外部 MQTT broker 的单个订阅项。
/// </summary>
public sealed class MqttExternalSubscriptionOptions
{
    /// <summary>
    /// MQTT topic filter，可使用 broker 支持的 <c>+</c> / <c>#</c> 通配符。
    /// </summary>
    public string TopicFilter { get; set; } = "db/+/m/+";

    /// <summary>
    /// 订阅 QoS。当前支持 <c>0</c> / <c>1</c>，默认 <c>1</c>。
    /// </summary>
    public int Qos { get; set; } = 1;
}

/// <summary>
/// CoAP 设备写入配置。CoAP 协议栈仅位于 Server 层，Core 不感知 CoAP。
/// </summary>
public sealed class CoapServerOptions
{
    /// <summary>
    /// 是否启用明文 CoAP UDP 服务端。默认关闭，需显式开启。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 明文 CoAP UDP 监听端口。默认 <c>5683</c>。
    /// </summary>
    public int Port { get; set; } = 5683;

    /// <summary>
    /// 单个 CoAP payload 最大字节数。默认 1MiB，块传输重组后仍受此限制。
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// DTLS/coaps 配置。默认关闭，启用后监听 <see cref="CoapDtlsOptions.Port"/>。
    /// </summary>
    public CoapDtlsOptions Dtls { get; set; } = new();
}

/// <summary>
/// CoAP DTLS PSK 传输配置。
/// </summary>
public sealed class CoapDtlsOptions
{
    /// <summary>
    /// 是否启用 <c>coaps</c> DTLS 监听。默认关闭。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// DTLS/coaps UDP 监听端口。默认 <c>5684</c>。
    /// </summary>
    public int Port { get; set; } = 5684;

    /// <summary>
    /// PSK identity 到明文 key 的映射。当前实现只支持 PSK，RPK/证书留作增量。
    /// </summary>
    public Dictionary<string, string> PskKeys { get; set; } = new();

    /// <summary>
    /// DTLS 会话空闲超时秒数。超时后清理远端会话状态。
    /// </summary>
    public int SessionIdleSeconds { get; set; } = 300;
}

/// <summary>
/// Line Protocol UDP 监听配置。UDP 是 fire-and-forget 入口，无鉴权、无响应和应用层背压，默认关闭。
/// </summary>
public sealed class LineProtocolUdpOptions
{
    /// <summary>
    /// 是否启用 Line Protocol UDP 监听。默认关闭，需显式开启并限定在可信内网。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// UDP 监听端口。默认 <c>8089</c>，对齐常见 InfluxDB UDP listener 配置。
    /// </summary>
    public int Port { get; set; } = 8089;

    /// <summary>
    /// 数据报写入的目标数据库名。UDP 包本身没有查询参数，启用时必须显式配置。
    /// </summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>
    /// 单个 UDP 数据报最大字节数。默认 65,507 字节（IPv4 UDP payload 上限）。
    /// </summary>
    public int MaxDatagramBytes { get; set; } = 65_507;

    /// <summary>
    /// Line Protocol timestamp 精度。支持 <c>n/ns</c>、<c>u/us/µs</c>、<c>ms</c>、<c>s</c>；
    /// 默认 <c>ns</c>，对齐 InfluxDB 写入语义。
    /// </summary>
    public string Precision { get; set; } = "ns";
}

/// <summary>
/// Modbus TCP 运行时配置，统一控制主动轮询 master/client 与被动监听 slave/server。
/// </summary>
public sealed class ModbusRuntimeOptions
{
    /// <summary>
    /// 是否启用 Modbus TCP 协议运行时。默认关闭；启用后仍只运行 catalog 中
    /// <c>ENABLED TRUE</c> 的 source 与 endpoint。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 扫描数据库与 catalog 变化的周期，单位为毫秒。默认 <c>250</c>。
    /// </summary>
    public int DiscoveryIntervalMilliseconds { get; set; } = 250;

    /// <summary>
    /// 单轮请求内首次重试的退避时间，单位为毫秒。后续重试指数增长。
    /// </summary>
    public int RetryBaseDelayMilliseconds { get; set; } = 100;

    /// <summary>单轮请求内重试退避的最大时间，单位为毫秒。</summary>
    public int MaxRetryDelayMilliseconds { get; set; } = 2_000;

    /// <summary>
    /// 一轮采集彻底失败后首次重连的退避时间，单位为毫秒。
    /// </summary>
    public int ReconnectBaseDelayMilliseconds { get; set; } = 1_000;

    /// <summary>连续失败时重连退避的最大时间，单位为毫秒。</summary>
    public int MaxReconnectDelayMilliseconds { get; set; } = 30_000;

    /// <summary>
    /// Endpoint 外部写请求在待审批队列中的有效期，单位为秒。默认 <c>900</c>（15 分钟），
    /// 最大按 24 小时处理。
    /// </summary>
    public int EndpointWriteRequestLifetimeSeconds { get; set; } = 900;

    /// <summary>
    /// 每个数据库允许同时保留的 endpoint 待审批写请求上限。默认 <c>4096</c>。
    /// 达到上限后新请求返回 Modbus <c>Server Device Busy</c>，不会改变关系表。
    /// </summary>
    public int MaxPendingEndpointWrites { get; set; } = 4_096;
}

/// <summary>
/// 三角色定义。
/// </summary>
public static class ServerRoles
{
    /// <summary>具备所有权限。</summary>
    public const string Admin = "admin";

    /// <summary>可读写数据，但不可创建/删除数据库。</summary>
    public const string ReadWrite = "readwrite";

    /// <summary>仅可执行 SELECT。</summary>
    public const string ReadOnly = "readonly";
}
