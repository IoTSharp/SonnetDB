namespace SonnetDB.Configuration;

/// <summary>
/// Copilot 子系统配置。绑定路径：<c>"SonnetDBServer:Copilot"</c>。
/// </summary>
public sealed class CopilotOptions
{
    /// <summary>
    /// 是否启用 Copilot 子系统。默认 <c>true</c>。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Embedding provider 配置。
    /// </summary>
    public CopilotEmbeddingOptions Embedding { get; set; } = new();

    /// <summary>
    /// Chat provider 配置。
    /// </summary>
    public CopilotChatOptions Chat { get; set; } = new();

    /// <summary>
    /// 文档摄入 / 检索配置。
    /// </summary>
    public CopilotDocsOptions Docs { get; set; } = new();

    /// <summary>
    /// 技能库（PR #65）配置。
    /// </summary>
    public CopilotSkillsOptions Skills { get; set; } = new();
}

/// <summary>
/// 技能库摄入 / 检索配置（PR #65）。
/// </summary>
public sealed class CopilotSkillsOptions
{
    /// <summary>
    /// 服务端启动后是否自动执行一次后台技能库增量摄入。默认 <c>false</c>；
    /// 在线 Copilot 的知识与技能由 ai.sonnetdb.com 云端维护。
    /// </summary>
    public bool AutoIngestOnStartup { get; set; } = false;

    /// <summary>
    /// 技能根目录。默认 <c>./copilot/skills</c>。
    /// </summary>
    public string Root { get; set; } = "./copilot/skills";
}

/// <summary>
/// Embedding provider 配置。
/// </summary>
public sealed class CopilotEmbeddingOptions
{
    /// <summary>
    /// provider 名称：<c>builtin</c>（默认，零依赖 hash 投影） / <c>local</c>（本地 ONNX） / <c>openai</c>。
    /// 默认使用 <c>builtin</c>，保证首次启动不需要任何外部依赖即可使 Copilot 就绪。
    /// </summary>
    public string Provider { get; set; } = "builtin";

    /// <summary>
    /// 本地 ONNX 模型路径。
    /// </summary>
    public string? LocalModelPath { get; set; }

    /// <summary>
    /// 本地 ONNX 文本模型的输入与后处理约定。
    /// 未配置时，<c>local</c> provider 保持可观测的 hash fallback，避免猜测模型语义。
    /// </summary>
    public CopilotEmbeddingModelProfile? ModelProfile { get; set; }

    /// <summary>
    /// <see cref="ModelProfile"/> 的兼容别名，便于旧配置迁移。
    /// </summary>
    public CopilotEmbeddingModelProfile? Profile
    {
        get => ModelProfile;
        set => ModelProfile = value;
    }

    /// <summary>
    /// ONNX Runtime 单个算子内部并行线程数；<c>0</c> 使用运行时默认值。
    /// </summary>
    public int IntraOpThreads { get; set; }

    /// <summary>
    /// ONNX Runtime 算子之间并行线程数；<c>0</c> 使用顺序执行模式，正数启用并行执行模式。
    /// </summary>
    public int InterOpThreads { get; set; }

    /// <summary>
    /// OpenAI-compatible 服务端点。
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// OpenAI-compatible API Key。
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// embedding 模型名。
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// 请求超时（秒）。默认 <c>60</c>。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// 描述一个本地文本 embedding ONNX 模型的完整执行语义。
/// </summary>
/// <remarks>
/// 模型文件、tokenizer 和 tensor 名称必须由部署者按目标模型填写；provider 不会
/// 根据模型文件名猜测 tokenizer 或 pooling 规则。配置该 profile 后，provider
/// 才会创建 ONNX Runtime session 并执行真实推理。
/// </remarks>
public sealed class CopilotEmbeddingModelProfile
{
    /// <summary>
    /// tokenizer 类型：<c>bert-wordpiece</c> 或 <c>sentencepiece</c>。
    /// </summary>
    public string TokenizerType { get; set; } = "bert-wordpiece";

    /// <summary>
    /// tokenizer 模型路径。BERT 使用 vocab.txt，SentencePiece 使用 model 文件。
    /// </summary>
    public string? TokenizerModelPath { get; set; }

    /// <summary>
    /// 输入 token id tensor 名称；为空时从常用名称或唯一整数输入中解析。
    /// </summary>
    public string? InputIdsName { get; set; } = "input_ids";

    /// <summary>
    /// attention mask tensor 名称；为空时不强制输入，存在常用名称时自动绑定。
    /// </summary>
    public string? AttentionMaskName { get; set; }

    /// <summary>
    /// token type id tensor 名称；为空时不强制输入，存在常用名称时自动绑定。
    /// </summary>
    public string? TokenTypeIdsName { get; set; }

    /// <summary>
    /// 是否发送 attention mask。<see langword="null"/> 表示按常用名称自动绑定，
    /// <see langword="true"/> 表示必须绑定，<see langword="false"/> 表示明确不发送。
    /// </summary>
    public bool? SendAttentionMask { get; set; }

    /// <summary>
    /// 是否发送 token type ids。<see langword="null"/> 表示按常用名称自动绑定，
    /// <see langword="true"/> 表示必须绑定，<see langword="false"/> 表示明确不发送。
    /// </summary>
    public bool? SendTokenTypeIds { get; set; }

    /// <summary>
    /// position id tensor 名称；常见名称存在且未显式禁用时可自动绑定。
    /// </summary>
    public string? PositionIdsName { get; set; }

    /// <summary>
    /// 是否发送 position ids。<see langword="null"/> 表示按常用名称自动绑定，
    /// <see langword="true"/> 表示必须绑定，<see langword="false"/> 表示明确不发送。
    /// </summary>
    public bool? SendPositionIds { get; set; }

    /// <summary>
    /// 明确允许 provider 不发送的模型输入 tensor 名称。
    /// 未列入此集合且没有被 profile 绑定的输入会使模型合同校验失败，避免把
    /// 必需输入延迟到 ONNX Runtime 才暴露。
    /// </summary>
    public List<string> IgnoredInputNames { get; set; } = new();

    /// <summary>
    /// 最大 token 数；固定 shape 模型必须与其一致，动态 shape 模型使用该值补齐输入。
    /// </summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>
    /// padding 方向：<c>right</c>（默认）或 <c>left</c>。左填充时有效 token
    /// 仍按自身顺序使用从 0 开始的 position ids，padding 槽 position id 为 0。
    /// </summary>
    public string PaddingSide { get; set; } = "right";

    /// <summary>
    /// 输出 pooling：<c>mean</c>、<c>cls</c> 或 <c>auto</c>。
    /// </summary>
    public string Pooling { get; set; } = "mean";

    /// <summary>
    /// 输出 embedding tensor 名称；为空时按稳定的常用名称顺序自动选择。
    /// </summary>
    public string? OutputName { get; set; }

    /// <summary>
    /// 是否对最终向量执行 L2 归一化。默认开启。
    /// </summary>
    public bool Normalize { get; set; } = true;

    /// <summary>
    /// 期望输出维度。默认 384，与内置 Copilot 知识库 schema 兼容。
    /// </summary>
    public int Dimensions { get; set; } = 384;

    /// <summary>
    /// 是否让 BERT tokenizer 自动添加 [CLS]/[SEP] 等特殊 token。默认开启。
    /// </summary>
    public bool AddSpecialTokens { get; set; } = true;

    /// <summary>
    /// BERT 未知 token 名称；为空时使用标准 <c>[UNK]</c>。
    /// </summary>
    public string? UnknownToken { get; set; }

    /// <summary>
    /// BERT 分类 token 名称；为空时使用标准 <c>[CLS]</c>。
    /// </summary>
    public string? ClassificationToken { get; set; }

    /// <summary>
    /// BERT 分隔 token 名称；为空时使用标准 <c>[SEP]</c>。
    /// </summary>
    public string? SeparatorToken { get; set; }

    /// <summary>
    /// BERT padding token 名称；为空时使用标准 <c>[PAD]</c>。
    /// </summary>
    public string? PaddingToken { get; set; }

    /// <summary>
    /// BERT masking token 名称；为空时优先使用词表中的 <c>[MASK]</c>，否则回退到未知 token。
    /// </summary>
    public string? MaskingToken { get; set; }

    /// <summary>
    /// BERT tokenizer 是否在分词前转换为小写。默认开启，匹配常见 uncased vocab。
    /// </summary>
    public bool LowerCaseBeforeTokenization { get; set; } = true;

    /// <summary>
    /// BERT tokenizer 是否执行基础空白/标点分词。默认开启。
    /// </summary>
    public bool ApplyBasicTokenization { get; set; } = true;

    /// <summary>
    /// BERT tokenizer 是否逐字切分 CJK 字符。默认开启。
    /// </summary>
    public bool IndividuallyTokenizeCjk { get; set; } = true;

    /// <summary>
    /// 是否让 tokenizer 执行其预分词器。默认开启。
    /// </summary>
    public bool ConsiderPreTokenization { get; set; } = true;

    /// <summary>
    /// 是否让 tokenizer 执行其标准化器。默认开启。
    /// </summary>
    public bool ConsiderNormalization { get; set; } = true;

    /// <summary>
    /// <see cref="LowerCaseBeforeTokenization"/> 的兼容简写别名。
    /// </summary>
    public bool LowerCase
    {
        get => LowerCaseBeforeTokenization;
        set => LowerCaseBeforeTokenization = value;
    }

    /// <summary>
    /// SentencePiece 是否添加 beginning-of-sentence token。默认关闭。
    /// </summary>
    public bool AddBeginningOfSentence { get; set; }

    /// <summary>
    /// SentencePiece 是否添加 end-of-sentence token。默认开启。
    /// </summary>
    public bool AddEndOfSentence { get; set; } = true;

    /// <summary>
    /// padding token id；为空时使用 tokenizer 能够明确提供的 id；若无法确定则拒绝执行。
    /// </summary>
    public int? PadTokenId { get; set; }

    /// <summary>
    /// 是否从 mean pooling 中排除 tokenizer 特殊 token。默认不排除，保持模型原生 mask 语义。
    /// </summary>
    public bool ExcludeSpecialTokensFromPooling { get; set; }

    /// <summary>
    /// <see cref="TokenizerModelPath"/> 的兼容别名。
    /// </summary>
    public string? TokenizerPath
    {
        get => TokenizerModelPath;
        set => TokenizerModelPath = value;
    }

    /// <summary>
    /// <see cref="Dimensions"/> 的兼容单数别名。
    /// </summary>
    public int Dimension
    {
        get => Dimensions;
        set => Dimensions = value;
    }

    /// <summary>
    /// <see cref="MaxTokens"/> 的兼容别名。
    /// </summary>
    public int MaxSequenceLength
    {
        get => MaxTokens;
        set => MaxTokens = value;
    }

    /// <summary>
    /// 创建供 provider 使用的配置快照，避免运行期间外部修改列表或属性后改变已验证合同。
    /// </summary>
    internal CopilotEmbeddingModelProfile CreateSnapshot()
    {
        var snapshot = (CopilotEmbeddingModelProfile)MemberwiseClone();
        snapshot.IgnoredInputNames = IgnoredInputNames is null
            ? null!
            : new List<string>(IgnoredInputNames);
        return snapshot;
    }

    /// <summary>
    /// 返回当前 tokenizer 配置为保留一个正文 token 所需的最小序列长度。
    /// </summary>
    internal int GetMinimumContentTokenCount()
    {
        var tokenizerType = TokenizerType?.Trim().ToLowerInvariant()
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .Replace(" ", string.Empty);
        var specialTokenCount = tokenizerType switch
        {
            "bertwordpiece" or "bert" or "wordpiece" when AddSpecialTokens => 2,
            "sentencepiece" or "sentencepiecebpe" =>
                (AddBeginningOfSentence ? 1 : 0) + (AddEndOfSentence ? 1 : 0),
            _ => 0,
        };
        return specialTokenCount + 1;
    }
}

/// <summary>
/// Chat provider 配置。
/// </summary>
public sealed class CopilotChatOptions
{
    /// <summary>
    /// provider 名称：当前仅支持 <c>openai</c>。
    /// </summary>
    public string Provider { get; set; } = "openai";

    /// <summary>
    /// OpenAI-compatible 服务端点。
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// OpenAI-compatible API Key。
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// chat 模型名。
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// （M8）可供前端 CopilotDock 下拉选择的模型列表。仅用于 UI 预填，实际能否调用取决于上游服务。
    /// </summary>
    public List<string> AvailableModels { get; set; } = new();

    /// <summary>
    /// 请求超时（秒）。默认 <c>60</c>。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// 文档摄入 / 检索配置。
/// </summary>
public sealed class CopilotDocsOptions
{
    /// <summary>
    /// 服务端启动后是否自动执行一次后台增量摄入。默认 <c>false</c>；
    /// 在线 Copilot 不再依赖本地知识库作为兜底。
    /// </summary>
    public bool AutoIngestOnStartup { get; set; } = false;

    /// <summary>
    /// 文档根目录列表。默认优先扫描仓库源码文档 <c>./docs</c>，其次兼容 <c>./web/help</c> 与运行时生成目录。
    /// </summary>
    public List<string> Roots { get; set; } =
    [
        "./docs",
        "./web/help",
        "./src/SonnetDB/wwwroot/help",
    ];

    /// <summary>
    /// 单块最大字符数。默认 <c>800</c>。
    /// </summary>
    public int ChunkSize { get; set; } = 800;

    /// <summary>
    /// 相邻块重叠字符数。默认 <c>100</c>。
    /// </summary>
    public int ChunkOverlap { get; set; } = 100;
}
