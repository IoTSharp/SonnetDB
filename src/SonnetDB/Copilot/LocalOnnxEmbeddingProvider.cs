using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using SonnetDB.Configuration;

namespace SonnetDB.Copilot;

/// <summary>
/// 使用显式模型 profile 执行本地 ONNX 文本 embedding。
/// </summary>
/// <remarks>
/// 不同模型的 tokenizer、输入 tensor 和输出 pooling 语义并不统一，因此 provider
/// 只在配置了 <see cref="CopilotEmbeddingModelProfile"/> 后创建 ONNX Runtime session。
/// 缺少 profile 或运行时不可用时，会退回可观测的 384 维本地 hash provider；该结果
/// 不应被当作真实语义模型输出。
/// </remarks>
public sealed class LocalOnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private const int DefaultFallbackDimension = BuiltinHashEmbeddingProvider.VectorDimension;
    private const int MaximumTokenCount = 32_768;
    private const int MaximumEmbeddingDimension = 65_536;

    private readonly object _initializationSync = new();
    private readonly string? _modelPath;
    private readonly string? _tokenizerPath;
    private readonly CopilotEmbeddingModelProfile? _profile;
    private readonly int _intraOpThreads;
    private readonly int _interOpThreads;
    private InferenceSession? _session;
    private ExecutionPlan? _executionPlan;
    private Tokenizer? _tokenizer;
    private BuiltinHashEmbeddingProvider? _fallbackProvider;
    private string? _fallbackReason;
    private readonly string? _configurationError;
    private LocalOnnxExecutionState _executionState;
    private long _inferenceRunCount;
    private volatile bool _disposed;

    /// <summary>
    /// 构造本地 ONNX embedding provider。
    /// </summary>
    /// <param name="options">本地 embedding 配置。</param>
    public LocalOnnxEmbeddingProvider(CopilotEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _profile = options.ModelProfile?.CreateSnapshot();
        _intraOpThreads = options.IntraOpThreads;
        _interOpThreads = options.InterOpThreads;
        _executionState = LocalOnnxExecutionState.NotInitialized(_intraOpThreads, _interOpThreads);
        // Resolve relative resources once so a later host/plugin call that changes
        // the process current directory cannot silently switch the model contract.
        _modelPath = SnapshotPath(options.LocalModelPath);
        _tokenizerPath = SnapshotPath(_profile?.TokenizerModelPath);

        string? configurationError = ValidateInitialConfiguration();
        if (configurationError is not null && IsUnavailableConfiguration(configurationError))
            EnsureFallback(configurationError);
        else
            _configurationError = configurationError;
    }

    /// <summary>
    /// 是否使用了内置 hash 降级实现。
    /// </summary>
    public bool IsFallback => Volatile.Read(ref _fallbackProvider) is not null;

    /// <summary>
    /// 当前 profile 是否通过静态配置检查且尚未触发运行时 fallback。
    /// </summary>
    public bool IsConfigured => _profile is not null && _configurationError is null && !IsFallback;

    /// <summary>
    /// 最近一次进入降级路径的原因；使用真实 ONNX 时为空。
    /// </summary>
    public string? FallbackReason => Volatile.Read(ref _fallbackReason);

    /// <summary>
    /// 当前实际输出向量的维度；进入 hash fallback 时为兼容的 384。
    /// </summary>
    public int VectorDimension
        => IsFallback || _profile?.Dimensions is not > 0
            ? DefaultFallbackDimension
            : _profile.Dimensions;

    /// <summary>
    /// 当前使用的模型 profile；未配置时为 <see langword="null"/>。
    /// </summary>
    public CopilotEmbeddingModelProfile? ModelProfile => _profile?.CreateSnapshot();

    /// <summary>
    /// 当前 ONNX session 的线程与执行模式状态。
    /// </summary>
    public LocalOnnxExecutionState ExecutionState => Volatile.Read(ref _executionState);

    internal long InferenceRunCount => Interlocked.Read(ref _inferenceRunCount);

    /// <summary>
    /// 为文本生成 embedding。
    /// </summary>
    /// <param name="text">待编码的非空文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按 profile 约定生成的 embedding 向量。</returns>
    public async ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<float[]> embeddings = await EmbedBatchAsync([text], cancellationToken).ConfigureAwait(false);
        return embeddings[0];
    }

    /// <summary>
    /// 使用一次 ONNX Runtime <c>Run</c> 为一批文本生成 embedding。
    /// </summary>
    /// <param name="texts">待编码的非空文本集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与输入顺序一致的 embedding 向量。</returns>
    public async ValueTask<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            throw new ArgumentException("Embedding batch cannot be empty.", nameof(texts));
        for (var index = 0; index < texts.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(texts[index]))
                throw new ArgumentException($"Embedding input at index {index} cannot be empty.", nameof(texts));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_configurationError is not null)
            throw new InvalidDataException(_configurationError);

        var fallback = Volatile.Read(ref _fallbackProvider);
        if (fallback is not null)
            return await EmbedFallbackBatchAsync(fallback, texts, cancellationToken).ConfigureAwait(false);

        BuiltinHashEmbeddingProvider? runtimeFallback = null;
        IReadOnlyList<float[]>? embeddings = null;

        // Keep plan construction, tokenizer encoding, input preparation and execution
        // under the same lifecycle lock. A runtime failure can dispose the native
        // session and clear the plan; doing the transition while this lock is held
        // prevents another request from observing a stale plan/session pair.
        lock (_initializationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            runtimeFallback = Volatile.Read(ref _fallbackProvider);
            if (runtimeFallback is null)
            {
                try
                {
                    var plan = EnsureExecutionPlan();
                    var tokenizer = EnsureTokenizer();
                    var prepared = PrepareInputs(tokenizer, plan, texts, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    embeddings = RunInference(plan, prepared, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ObjectDisposedException)
                {
                    throw;
                }
                catch (InvalidDataException)
                {
                    // A profile/model contract mismatch is actionable configuration
                    // data. Do not hide it behind hash fallback, otherwise an operator
                    // could mistake a vector with the wrong semantics for success.
                    throw;
                }
                catch (Exception exception) when (IsRuntimeFallbackException(exception))
                {
                    // Only platform/runtime loading failures may degrade to hash.
                    // Profile and model-contract errors remain visible to the caller.
                    var reason = string.IsNullOrWhiteSpace(exception.Message)
                        ? $"Local ONNX execution failed ({exception.GetType().Name})."
                        : $"Local ONNX execution failed: {exception.Message}";
                    runtimeFallback = EnsureFallback(reason);
                }
            }
        }

        if (runtimeFallback is not null)
            return await EmbedFallbackBatchAsync(runtimeFallback, texts, cancellationToken).ConfigureAwait(false);

        return embeddings ?? throw new InvalidOperationException("Local ONNX embedding execution did not produce a result.");
    }

    /// <summary>
    /// 释放 tokenizer、ONNX session 和 fallback provider 引用。
    /// </summary>
    public void Dispose()
    {
        lock (_initializationSync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _session?.Dispose();
            _session = null;
            _executionPlan = null;
            _tokenizer = null;
            _fallbackProvider = null;
            _executionState = LocalOnnxExecutionState.NotInitialized(_intraOpThreads, _interOpThreads);
        }
    }

    private string? ValidateInitialConfiguration()
    {
        if (_intraOpThreads < 0)
            return "Local ONNX IntraOpThreads must be non-negative.";
        if (_interOpThreads < 0)
            return "Local ONNX InterOpThreads must be non-negative.";

        if (_profile is null)
            return "Local ONNX model profile is not configured.";

        if (string.IsNullOrWhiteSpace(_modelPath))
            return "Copilot local embedding model path is missing.";

        if (!TryGetFullPath(_modelPath, out var modelPath, out var pathError))
            return $"Copilot local embedding model path is invalid: {pathError}";
        if (!File.Exists(modelPath))
            return "Copilot local embedding model file was not found.";

        if (string.IsNullOrWhiteSpace(_profile.TokenizerModelPath))
            return "Local ONNX tokenizer model path is missing.";
        if (!TryGetFullPath(_tokenizerPath, out var tokenizerPath, out pathError))
            return $"Local ONNX tokenizer model path is invalid: {pathError}";
        if (!File.Exists(tokenizerPath))
            return "Local ONNX tokenizer model file was not found.";

        if (_profile.MaxTokens <= 0 || _profile.MaxTokens > MaximumTokenCount)
            return $"Local ONNX MaxTokens must be between 1 and {MaximumTokenCount}.";
        if (_profile.Dimensions <= 0 || _profile.Dimensions > MaximumEmbeddingDimension)
            return $"Local ONNX embedding dimensions must be between 1 and {MaximumEmbeddingDimension}.";

        if (_profile.PadTokenId is < 0)
            return "Local ONNX padding token id must be non-negative.";

        if (!TryNormalizeTokenizerType(_profile.TokenizerType, out _))
            return $"Unsupported local ONNX tokenizer type '{_profile.TokenizerType}'.";
        if (!TryNormalizePooling(_profile.Pooling, out _))
            return $"Unsupported local ONNX pooling mode '{_profile.Pooling}'.";
        if (!TryNormalizePaddingSide(_profile.PaddingSide, out _))
            return $"Unsupported local ONNX padding side '{_profile.PaddingSide}'.";

        if (_profile.IgnoredInputNames is null
            || _profile.IgnoredInputNames.Any(string.IsNullOrWhiteSpace))
        {
            return "Local ONNX ignored input names cannot be empty.";
        }

        if (TryNormalizeTokenizerType(_profile.TokenizerType, out _))
        {
            var minimumTokenCount = _profile.GetMinimumContentTokenCount();
            if (minimumTokenCount > 1 && _profile.MaxTokens < minimumTokenCount)
            {
                return $"Local ONNX MaxTokens must be at least {minimumTokenCount} to retain configured special tokens and one content token.";
            }
        }

        return null;
    }

    private static bool IsUnavailableConfiguration(string error)
        => error is "Local ONNX model profile is not configured."
            or "Copilot local embedding model path is missing."
            or "Copilot local embedding model file was not found."
            or "Local ONNX tokenizer model path is missing."
            or "Local ONNX tokenizer model file was not found."
            || error.StartsWith("Copilot local embedding model path is invalid:", StringComparison.Ordinal)
            || error.StartsWith("Local ONNX tokenizer model path is invalid:", StringComparison.Ordinal);

    private BuiltinHashEmbeddingProvider EnsureFallback(string reason)
    {
        lock (_initializationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _fallbackReason ??= reason;

            // A provider can discover a native/runtime failure after lazily creating
            // its session. Release that native graph before retaining the lightweight
            // deterministic fallback for the rest of the process lifetime.
            _session?.Dispose();
            _session = null;
            _executionPlan = null;
            _tokenizer = null;
            _executionState = LocalOnnxExecutionState.NotInitialized(_intraOpThreads, _interOpThreads);
            _fallbackProvider ??= new BuiltinHashEmbeddingProvider(
                new CopilotEmbeddingOptions { Provider = "local" });
            return _fallbackProvider;
        }
    }

    private InferenceSession EnsureSession()
    {
        lock (_initializationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
                return _session;
            if (_profile is null)
                throw new InvalidOperationException("Local ONNX model profile is not configured.");

            if (!TryGetFullPath(_modelPath, out var modelPath, out var pathError))
                throw new InvalidOperationException($"Copilot local embedding model path is invalid: {pathError}");
            if (!File.Exists(modelPath))
                throw new FileNotFoundException("Copilot local embedding model file was not found.", modelPath);

            using var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                EnableCpuMemArena = true,
                EnableMemoryPattern = true,
                IntraOpNumThreads = _intraOpThreads,
                InterOpNumThreads = _interOpThreads,
                ExecutionMode = _interOpThreads > 0
                    ? Microsoft.ML.OnnxRuntime.ExecutionMode.ORT_PARALLEL
                    : Microsoft.ML.OnnxRuntime.ExecutionMode.ORT_SEQUENTIAL,
            };

            var session = new InferenceSession(modelPath, sessionOptions);
            try
            {
                _executionPlan = BuildExecutionPlan(session, _profile);
                _session = session;
                _executionState = LocalOnnxExecutionState.Initialized(_intraOpThreads, _interOpThreads);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
    }

    private ExecutionPlan EnsureExecutionPlan()
    {
        lock (_initializationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_executionPlan is not null)
                return _executionPlan;

            _ = EnsureSession();
            return _executionPlan
                ?? throw new InvalidDataException("Local ONNX execution plan was not initialized.");
        }
    }

    private Tokenizer EnsureTokenizer()
    {
        lock (_initializationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tokenizer is not null)
                return _tokenizer;
            if (_profile is null)
                throw new InvalidOperationException("Local ONNX model profile is not configured.");
            if (!TryGetFullPath(_tokenizerPath, out var tokenizerPath, out var pathError))
                throw new InvalidOperationException($"Local ONNX tokenizer model path is invalid: {pathError}");

            if (!TryNormalizeTokenizerType(_profile.TokenizerType, out var tokenizerType))
                throw new InvalidDataException($"Unsupported local ONNX tokenizer type '{_profile.TokenizerType}'.");

            try
            {
                _tokenizer = tokenizerType switch
                {
                    TokenizerKind.BertWordPiece => BertTokenizer.Create(
                        tokenizerPath,
                        new BertOptions
                        {
                            LowerCaseBeforeTokenization = _profile.LowerCaseBeforeTokenization,
                            ApplyBasicTokenization = _profile.ApplyBasicTokenization,
                            IndividuallyTokenizeCjk = _profile.IndividuallyTokenizeCjk,
                            UnknownToken = ResolveBertSpecialToken(_profile.UnknownToken, "[UNK]"),
                            ClassificationToken = ResolveBertSpecialToken(_profile.ClassificationToken, "[CLS]"),
                            SeparatorToken = ResolveBertSpecialToken(_profile.SeparatorToken, "[SEP]"),
                            PaddingToken = ResolveBertSpecialToken(_profile.PaddingToken, "[PAD]"),
                            // [MASK] is only used by training-oriented tokenizer helpers.
                            // Compact inference vocabularies commonly omit it, so use the
                            // configured token and otherwise the unknown token as a benign
                            // construction fallback.
                            MaskingToken = ResolveBertMaskingToken(tokenizerPath, _profile),
                        }),
                    TokenizerKind.SentencePiece => CreateSentencePieceTokenizer(tokenizerPath),
                    _ => throw new InvalidDataException($"Unsupported local ONNX tokenizer type '{_profile.TokenizerType}'."),
                };
                return _tokenizer;
            }
            catch (ArgumentException exception)
            {
                // Tokenizer constructors use ArgumentException for missing or
                // inconsistent vocabulary special tokens. Surface those as the
                // provider's stable profile-contract error instead of leaking a
                // library-specific exception to direct callers.
                throw new InvalidDataException(
                    $"Local ONNX tokenizer profile is invalid: {exception.Message}",
                    exception);
            }
        }
    }

    private SentencePieceTokenizer CreateSentencePieceTokenizer(string tokenizerPath)
    {
        if (_profile is null)
            throw new InvalidOperationException("Local ONNX model profile is not configured.");

        using var modelStream = File.OpenRead(tokenizerPath);
        return SentencePieceTokenizer.Create(
            modelStream,
            _profile.AddBeginningOfSentence,
            _profile.AddEndOfSentence,
            // Do not pass null: the tokenizer treats this map as an extension
            // vocabulary and expects a concrete (possibly empty) dictionary.
            specialTokens: new Dictionary<string, int>());
    }

    private static ExecutionPlan BuildExecutionPlan(
        InferenceSession session,
        CopilotEmbeddingModelProfile profile)
    {
        var inputIds = ResolveInputBinding(
            session.InputMetadata,
            profile.InputIdsName,
            ["input_ids", "inputIds", "ids", "input"],
            required: true,
            "input ids")!;
        var ignoredInputs = ResolveIgnoredInputNames(session.InputMetadata, profile.IgnoredInputNames);
        var attentionMask = ResolveOptionalInputBinding(
            session.InputMetadata,
            profile.AttentionMaskName,
            ["attention_mask", "attentionMask", "input_mask", "mask"],
            profile.SendAttentionMask,
            "attention mask",
            ignoredInputs);
        var tokenTypeIds = ResolveOptionalInputBinding(
            session.InputMetadata,
            profile.TokenTypeIdsName,
            ["token_type_ids", "tokenTypeIds", "segment_ids", "token_types"],
            profile.SendTokenTypeIds,
            "token type ids",
            ignoredInputs);
        var positionIds = ResolveOptionalInputBinding(
            session.InputMetadata,
            profile.PositionIdsName,
            ["position_ids", "positionIds", "positions", "position"],
            profile.SendPositionIds,
            "position ids",
            ignoredInputs);

        ValidateAllInputsBound(
            session.InputMetadata,
            ignoredInputs,
            inputIds,
            attentionMask,
            tokenTypeIds,
            positionIds);

        if (attentionMask is not null && string.Equals(attentionMask.Name, inputIds.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ONNX attention mask input must differ from input ids input.");
        if (tokenTypeIds is not null && (string.Equals(tokenTypeIds.Name, inputIds.Name, StringComparison.OrdinalIgnoreCase)
                || (attentionMask is not null && string.Equals(tokenTypeIds.Name, attentionMask.Name, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("ONNX token type ids input must have a distinct tensor name.");
        if (positionIds is not null && (string.Equals(positionIds.Name, inputIds.Name, StringComparison.OrdinalIgnoreCase)
                || (attentionMask is not null && string.Equals(positionIds.Name, attentionMask.Name, StringComparison.OrdinalIgnoreCase))
                || (tokenTypeIds is not null && string.Equals(positionIds.Name, tokenTypeIds.Name, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("ONNX position ids input must have a distinct tensor name.");

        var (fixedBatchSize, sequenceLength) = ValidateAndResolveInputShape(inputIds.Metadata, profile);
        if (attentionMask is not null)
            ValidateSequenceShape(attentionMask.Metadata, fixedBatchSize, sequenceLength, "attention mask");
        if (tokenTypeIds is not null)
            ValidateSequenceShape(tokenTypeIds.Metadata, fixedBatchSize, sequenceLength, "token type ids");
        if (positionIds is not null)
            ValidateSequenceShape(positionIds.Metadata, fixedBatchSize, sequenceLength, "position ids");

        var outputName = ResolveOutputName(session.OutputMetadata, profile.OutputName);
        if (!session.OutputMetadata.TryGetValue(outputName, out var outputMetadata))
            throw new InvalidDataException($"ONNX output tensor '{outputName}' was not found.");
        var outputKind = ResolveOutputKind(outputMetadata, outputName);
        ValidateOutputShape(outputMetadata, outputName);

        if (!TryNormalizePooling(profile.Pooling, out var pooling))
            throw new InvalidDataException($"Unsupported local ONNX pooling mode '{profile.Pooling}'.");

        return new ExecutionPlan(
            inputIds,
            attentionMask,
            tokenTypeIds,
            positionIds,
            fixedBatchSize,
            sequenceLength,
            outputName,
            outputKind,
            pooling);
    }

    private static InputBinding? ResolveInputBinding(
        IReadOnlyDictionary<string, NodeMetadata> metadata,
        string? requestedName,
        IReadOnlyList<string> candidates,
        bool required,
        string description,
        bool allowUniqueIntegerFallback = true)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            var explicitName = FindMetadataName(metadata, requestedName);
            if (explicitName is null)
            {
                if (!required)
                    throw new InvalidDataException($"ONNX {description} input '{requestedName}' was not found.");
                throw new InvalidDataException($"ONNX required {description} input '{requestedName}' was not found; available inputs: {FormatNames(metadata.Keys)}.");
            }

            return CreateInputBinding(explicitName, metadata[explicitName], description);
        }

        var candidateMatches = metadata.Keys
            .Where(name => candidates.Any(candidate => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (candidateMatches.Length > 1)
        {
            throw new InvalidDataException(
                $"ONNX {description} input auto-binding is ambiguous; matching inputs: {FormatNames(candidateMatches)}. Set the profile name explicitly.");
        }
        if (candidateMatches.Length == 1)
            return CreateInputBinding(candidateMatches[0], metadata[candidateMatches[0]], description);

        if (required && allowUniqueIntegerFallback)
        {
            var integerInputs = metadata
                .Where(static pair => pair.Value.IsTensor && IsIntegerType(pair.Value.ElementType))
                .Select(static pair => pair.Key)
                .ToArray();
            if (integerInputs.Length == 1)
                return CreateInputBinding(integerInputs[0], metadata[integerInputs[0]], description);

            throw new InvalidDataException($"ONNX required {description} input was not found; available inputs: {FormatNames(metadata.Keys)}.");
        }

        if (required)
            throw new InvalidDataException($"ONNX required {description} input was not found; expected one of: {FormatNames(candidates)}; available inputs: {FormatNames(metadata.Keys)}.");

        return null;
    }

    private static InputBinding? ResolveOptionalInputBinding(
        IReadOnlyDictionary<string, NodeMetadata> metadata,
        string? requestedName,
        IReadOnlyList<string> candidates,
        bool? send,
        string description,
        HashSet<string> ignoredInputs)
    {
        if (send is false)
        {
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                var explicitName = FindMetadataName(metadata, requestedName);
                if (explicitName is null)
                    throw new InvalidDataException($"ONNX disabled {description} input '{requestedName}' was not found.");
            }
            // Explicitly disabling a role does not waive the model input contract.
            // If the graph still declares that tensor, callers must list it in
            // IgnoredInputNames so the omission is deliberate and reviewable.
            return null;
        }

        // A true switch requires an input. A null switch performs conservative
        // name-based auto-binding and lets the final unbound-input check fail closed.
        return ResolveInputBinding(
            metadata,
            requestedName,
            candidates,
            required: send is true,
            description,
            allowUniqueIntegerFallback: false);
    }

    private static HashSet<string> ResolveIgnoredInputNames(
        IReadOnlyDictionary<string, NodeMetadata> metadata,
        IReadOnlyList<string>? configuredNames)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (configuredNames is null)
            return ignored;

        foreach (var configuredName in configuredNames)
        {
            if (string.IsNullOrWhiteSpace(configuredName))
                throw new InvalidDataException("ONNX ignored input names cannot be empty.");
            var actualName = FindMetadataName(metadata, configuredName);
            if (actualName is null)
                throw new InvalidDataException($"ONNX ignored input '{configuredName}' was not found; available inputs: {FormatNames(metadata.Keys)}.");
            ignored.Add(actualName);
        }

        return ignored;
    }

    private static void ValidateAllInputsBound(
        IReadOnlyDictionary<string, NodeMetadata> metadata,
        HashSet<string> ignoredInputs,
        params InputBinding?[] bindings)
    {
        var bound = new HashSet<string>(ignoredInputs, StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            if (binding is null)
                continue;
            if (!bound.Add(binding.Name))
                throw new InvalidDataException($"ONNX input '{binding.Name}' is bound or ignored more than once.");
        }

        var unbound = metadata.Keys
            .Where(name => !bound.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (unbound.Length > 0)
            throw new InvalidDataException(
                $"ONNX model has unbound input tensor(s): {FormatNames(unbound)}. Bind them in the profile or add them to IgnoredInputNames.");
    }

    private static InputBinding CreateInputBinding(string name, NodeMetadata metadata, string description)
    {
        if (!metadata.IsTensor)
            throw new InvalidDataException($"ONNX {description} input '{name}' is not a tensor.");
        var kind = ResolveIntegerKind(metadata, name, description);
        return new InputBinding(name, kind, metadata);
    }

    private static (int FixedBatchSize, int SequenceLength) ValidateAndResolveInputShape(
        NodeMetadata metadata,
        CopilotEmbeddingModelProfile profile)
    {
        ValidateSequenceShape(metadata, expectedBatchSize: 0, expectedSequenceLength: null, "input ids");
        var dimensions = metadata.Dimensions;
        var fixedBatchSize = dimensions[0] > 0 ? dimensions[0] : 0;
        var fixedSequenceLength = dimensions[1] > 0 ? dimensions[1] : 0;
        if (fixedSequenceLength > 0 && profile.MaxTokens != fixedSequenceLength)
        {
            throw new InvalidDataException(
                $"ONNX input ids sequence dimension is fixed at {fixedSequenceLength}, but profile MaxTokens is {profile.MaxTokens}; fixed-shape models require an exact match.");
        }

        return (fixedBatchSize, fixedSequenceLength > 0 ? fixedSequenceLength : profile.MaxTokens);
    }

    private static void ValidateSequenceShape(
        NodeMetadata metadata,
        int expectedBatchSize,
        int? expectedSequenceLength,
        string description)
    {
        if (!metadata.IsTensor || metadata.Dimensions is null || metadata.Dimensions.Length != 2)
            throw new InvalidDataException($"ONNX {description} input must have rank 2 [batch, sequence].");

        var batch = metadata.Dimensions[0];
        if (batch == 0 || batch < -1)
            throw new InvalidDataException($"ONNX {description} input has an invalid batch dimension {batch}.");
        if (expectedBatchSize > 0 && batch > 0 && batch != expectedBatchSize)
        {
            throw new InvalidDataException(
                $"ONNX {description} batch dimension is {batch}, expected {expectedBatchSize}.");
        }

        var sequence = metadata.Dimensions[1];
        if (sequence == 0 || sequence < -1)
            throw new InvalidDataException($"ONNX {description} input has an invalid sequence dimension {sequence}.");
        if (expectedSequenceLength is > 0 && sequence > 0 && sequence != expectedSequenceLength.Value)
        {
            throw new InvalidDataException(
                $"ONNX {description} sequence dimension is {sequence}, expected {expectedSequenceLength.Value}.");
        }
    }

    private static void ValidateOutputShape(NodeMetadata metadata, string outputName)
    {
        if (!metadata.IsTensor || metadata.Dimensions is null || metadata.Dimensions.Length is < 1 or > 3)
            throw new InvalidDataException($"ONNX output '{outputName}' must have rank 1, 2 or 3.");

        var dimensions = metadata.Dimensions;
        for (var i = 0; i < dimensions.Length; i++)
        {
            if (dimensions[i] == 0 || dimensions[i] < -1)
                throw new InvalidDataException($"ONNX output '{outputName}' has an invalid dimension {dimensions[i]}.");
        }
    }

    private static string ResolveOutputName(
        IReadOnlyDictionary<string, NodeMetadata> metadata,
        string? requestedName)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            var explicitName = FindMetadataName(metadata, requestedName);
            if (explicitName is null)
                throw new InvalidDataException($"ONNX output tensor '{requestedName}' was not found; available outputs: {FormatNames(metadata.Keys)}.");
            return explicitName;
        }

        var candidates = new[] { "sentence_embedding", "pooler_output", "last_hidden_state", "embedding", "output" };
        var candidateMatches = metadata
            .Where(pair => pair.Value.IsTensor
                && IsSupportedOutputType(pair.Value.ElementType)
                && candidates.Any(candidate => string.Equals(pair.Key, candidate, StringComparison.OrdinalIgnoreCase)))
            .Select(static pair => pair.Key)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (candidateMatches.Length > 1)
        {
            throw new InvalidDataException(
                $"ONNX output auto-binding is ambiguous; matching outputs: {FormatNames(candidateMatches)}. Set the profile OutputName explicitly.");
        }
        if (candidateMatches.Length == 1)
            return candidateMatches[0];

        var floatOutputs = metadata
            .Where(static pair => pair.Value.IsTensor && IsSupportedOutputType(pair.Value.ElementType))
            .Select(static pair => pair.Key)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        return floatOutputs.Length switch
        {
            0 => throw new InvalidDataException($"ONNX model has no float output tensor; available outputs: {FormatNames(metadata.Keys)}."),
            1 => floatOutputs[0],
            _ => throw new InvalidDataException(
                $"ONNX output auto-binding is ambiguous; float outputs: {FormatNames(floatOutputs)}. Set the profile OutputName explicitly."),
        };
    }

    private static TensorKind ResolveIntegerKind(NodeMetadata metadata, string name, string description)
    {
        if (metadata.ElementType == typeof(long))
            return TensorKind.Int64;
        if (metadata.ElementType == typeof(int))
            return TensorKind.Int32;
        throw new InvalidDataException(
            $"ONNX {description} input '{name}' must use int32 or int64, actual type is {metadata.ElementType?.Name ?? "unknown"}.");
    }

    private static TensorKind ResolveOutputKind(NodeMetadata metadata, string name)
    {
        if (metadata.ElementType == typeof(float))
            return TensorKind.Float32;
        if (metadata.ElementType == typeof(double))
            return TensorKind.Float64;
        throw new InvalidDataException(
            $"ONNX output '{name}' must use float32 or float64, actual type is {metadata.ElementType?.Name ?? "unknown"}.");
    }

    private static bool IsSupportedOutputType(Type? type)
        => type == typeof(float) || type == typeof(double);

    private static bool IsIntegerType(Type? type)
        => type == typeof(int) || type == typeof(long);

    private PreparedInputs PrepareInputs(
        Tokenizer tokenizer,
        ExecutionPlan plan,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.FixedBatchSize > 0 && texts.Count != plan.FixedBatchSize)
        {
            throw new InvalidDataException(
                $"ONNX input batch dimension is fixed at {plan.FixedBatchSize}, but the request contains {texts.Count} text(s).");
        }

        var padTokenId = ResolvePaddingTokenId(tokenizer, _profile!);
        if (!TryNormalizePaddingSide(_profile!.PaddingSide, out var paddingSide))
            throw new InvalidDataException($"Unsupported local ONNX padding side '{_profile.PaddingSide}'.");

        var elementCount = checked(texts.Count * plan.SequenceLength);
        var inputIds = new int[elementCount];
        var attention = new int[elementCount];
        var poolingMasks = new bool[texts.Count][];
        var positionIds = plan.PositionIds is null ? null : new int[elementCount];
        Array.Fill(inputIds, padTokenId);

        for (var batchIndex = 0; batchIndex < texts.Count; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<int> ids = EncodeTokenIds(tokenizer, texts[batchIndex], _profile!, plan.SequenceLength);
            var tokenCount = ids.Count;
            if (tokenCount <= 0 || tokenCount > plan.SequenceLength)
                throw new InvalidDataException($"Tokenizer returned {tokenCount} token(s), outside the configured sequence length {plan.SequenceLength}.");

            var poolingMask = new bool[plan.SequenceLength];
            poolingMasks[batchIndex] = poolingMask;
            var batchOffset = checked(batchIndex * plan.SequenceLength);
            var contentOffset = paddingSide == PaddingSide.Left
                ? plan.SequenceLength - tokenCount
                : 0;

            for (var tokenIndex = 0; tokenIndex < tokenCount; tokenIndex++)
            {
                var row = contentOffset + tokenIndex;
                var slot = batchOffset + row;
                inputIds[slot] = ids[tokenIndex];
                attention[slot] = 1;
                poolingMask[row] = true;
                if (positionIds is not null)
                    positionIds[slot] = tokenIndex;
            }

            if (_profile!.ExcludeSpecialTokensFromPooling && plan.Pooling != PoolingMode.Cls)
            {
                var specialCount = GetLeadingSpecialTokenCount(tokenizer, _profile);
                if (specialCount > 0)
                    for (var index = 0; index < Math.Min(specialCount, tokenCount); index++)
                        poolingMask[contentOffset + index] = false;

                var trailingSpecialCount = GetTrailingSpecialTokenCount(tokenizer, _profile);
                for (var index = 0; index < trailingSpecialCount && index < tokenCount; index++)
                    poolingMask[contentOffset + tokenCount - 1 - index] = false;
            }
        }

        var inputs = new List<NamedOnnxValue>(capacity: 4)
        {
            CreateIntegerTensor(plan.InputIds, inputIds, texts.Count, plan.SequenceLength),
        };
        if (plan.AttentionMask is not null)
            inputs.Add(CreateIntegerTensor(plan.AttentionMask, attention, texts.Count, plan.SequenceLength));
        if (plan.TokenTypeIds is not null)
            inputs.Add(CreateIntegerTensor(plan.TokenTypeIds, new int[elementCount], texts.Count, plan.SequenceLength));
        if (plan.PositionIds is not null)
            inputs.Add(CreateIntegerTensor(plan.PositionIds, positionIds!, texts.Count, plan.SequenceLength));

        return new PreparedInputs(inputs, poolingMasks);
    }

    private static IReadOnlyList<int> EncodeTokenIds(
        Tokenizer tokenizer,
        string text,
        CopilotEmbeddingModelProfile profile,
        int sequenceLength)
    {
        IReadOnlyList<int> encoded = tokenizer switch
        {
            BertTokenizer bert => bert.EncodeToIds(
                text,
                maxTokenCount: sequenceLength,
                addSpecialTokens: profile.AddSpecialTokens,
                normalizedText: out _,
                charsConsumed: out _,
                considerPreTokenization: profile.ConsiderPreTokenization,
                considerNormalization: profile.ConsiderNormalization),
            SentencePieceTokenizer sentencePiece => sentencePiece.EncodeToIds(
                text,
                addBeginningOfSentence: profile.AddBeginningOfSentence,
                addEndOfSentence: profile.AddEndOfSentence,
                maxTokenCount: sequenceLength,
                normalizedText: out _,
                charsConsumed: out _,
                considerPreTokenization: profile.ConsiderPreTokenization,
                considerNormalization: profile.ConsiderNormalization),
            _ => tokenizer.EncodeToIds(
                text,
                maxTokenCount: sequenceLength,
                normalizedText: out _,
                charsConsumed: out _,
                considerPreTokenization: profile.ConsiderPreTokenization,
                considerNormalization: profile.ConsiderNormalization),
        };

        if (encoded.Count < sequenceLength)
            return encoded;

        if (encoded.Count > sequenceLength)
            encoded = encoded.Take(sequenceLength).ToArray();

        var endTokenId = GetEndTokenId(tokenizer, profile);
        if (endTokenId is not { } eos || !ShouldKeepTrailingSpecialToken(tokenizer, profile) || sequenceLength <= 0)
            return encoded;

        // The bounded tokenizer API may consume the budget before appending EOS/SEP.
        // Replace only the final slot, preserving the tokenizer's bounded allocation.
        if (encoded[^1] == eos)
            return encoded;
        var withEndToken = encoded.ToArray();
        withEndToken[^1] = eos;
        return withEndToken;
    }

    private static int ResolvePaddingTokenId(Tokenizer tokenizer, CopilotEmbeddingModelProfile profile)
    {
        if (profile.PadTokenId is { } configured)
            return configured;
        if (tokenizer is BertTokenizer bert && bert.PaddingTokenId >= 0)
            return bert.PaddingTokenId;
        if (tokenizer is SentencePieceTokenizer sentencePiece)
        {
            var paddingToken = string.IsNullOrWhiteSpace(profile.PaddingToken)
                ? "<pad>"
                : profile.PaddingToken!;
            if (sentencePiece.Vocabulary.TryGetValue(paddingToken, out var vocabularyId))
                return vocabularyId;
            if (sentencePiece.SpecialTokens is { } specialTokens
                && specialTokens.TryGetValue(paddingToken, out var specialId))
                return specialId;
            throw new InvalidDataException(
                $"SentencePiece vocabulary does not define padding token '{paddingToken}'. Set PadTokenId explicitly in the model profile.");
        }

        throw new InvalidDataException("Local ONNX tokenizer does not expose a padding token. Set PadTokenId explicitly in the model profile.");
    }

    private static int? GetEndTokenId(Tokenizer tokenizer, CopilotEmbeddingModelProfile profile)
    {
        if (tokenizer is BertTokenizer bert && profile.AddSpecialTokens && bert.SeparatorTokenId >= 0)
            return bert.SeparatorTokenId;
        if (tokenizer is SentencePieceTokenizer sentencePiece
            && profile.AddEndOfSentence
            && sentencePiece.EndOfSentenceId >= 0)
            return sentencePiece.EndOfSentenceId;
        return null;
    }

    private static bool ShouldKeepTrailingSpecialToken(Tokenizer tokenizer, CopilotEmbeddingModelProfile profile)
        => tokenizer is BertTokenizer && profile.AddSpecialTokens
            || tokenizer is SentencePieceTokenizer && profile.AddEndOfSentence;

    private static int GetLeadingSpecialTokenCount(Tokenizer tokenizer, CopilotEmbeddingModelProfile profile)
        => tokenizer is BertTokenizer && profile.AddSpecialTokens
            || tokenizer is SentencePieceTokenizer && profile.AddBeginningOfSentence
            ? 1
            : 0;

    private static int GetTrailingSpecialTokenCount(Tokenizer tokenizer, CopilotEmbeddingModelProfile profile)
        => tokenizer is BertTokenizer && profile.AddSpecialTokens
            || tokenizer is SentencePieceTokenizer && profile.AddEndOfSentence
            ? 1
            : 0;

    private IReadOnlyList<float[]> RunInference(
        ExecutionPlan plan,
        PreparedInputs prepared,
        CancellationToken cancellationToken)
    {
        // InferenceSession is shared by the singleton provider. Keep the native session
        // alive for the complete Run/output-materialization window so application
        // shutdown cannot dispose it while a request is reading OrtValue memory.
        lock (_initializationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var session = _session ?? throw new InvalidOperationException("Local ONNX session is not initialized.");
            try
            {
                Interlocked.Increment(ref _inferenceRunCount);
                using var outputs = session.Run(prepared.Inputs, [plan.OutputName]);
                var output = outputs.FirstOrDefault(value => string.Equals(value.Name, plan.OutputName, StringComparison.Ordinal))
                    ?? throw new InvalidDataException($"ONNX inference result is missing output tensor '{plan.OutputName}'.");

                return plan.OutputKind switch
                {
                    TensorKind.Float32 => PoolAndNormalizeBatch(
                        output.AsTensor<float>().Dimensions.ToArray(),
                        output.AsEnumerable<float>().ToArray(),
                        prepared.PoolingMasks,
                        plan),
                    TensorKind.Float64 => PoolAndNormalizeBatch(
                        output.AsTensor<double>().Dimensions.ToArray(),
                        output.AsEnumerable<double>().Select(static value => (float)value).ToArray(),
                        prepared.PoolingMasks,
                        plan),
                    _ => throw new InvalidDataException($"Unsupported ONNX output type for '{plan.OutputName}'."),
                };
            }
            catch (OnnxRuntimeException exception)
            {
                // Session construction failures are eligible for the explicit native
                // runtime fallback, but a failure during an already validated run is
                // a model/input contract error. Preserve it as fail-closed data so the
                // outer fallback filter cannot silently replace a bad vector with hash.
                throw new InvalidDataException(
                    $"ONNX inference failed for output '{plan.OutputName}': {exception.Message}",
                    exception);
            }
        }
    }

    private IReadOnlyList<float[]> PoolAndNormalizeBatch(
        int[] shape,
        float[] values,
        bool[][] poolingMasks,
        ExecutionPlan plan)
    {
        if (shape.Length is < 1 or > 3)
            throw new InvalidDataException($"ONNX output '{plan.OutputName}' must have rank 1, 2 or 3.");
        if (shape.Any(static dimension => dimension <= 0))
            throw new InvalidDataException($"ONNX output '{plan.OutputName}' contains a non-positive runtime dimension.");

        var (isSequence, sequenceLength, dimensions) = ResolveOutputLayout(
            shape,
            poolingMasks.Length,
            plan.OutputName);
        var expectedValues = checked(
            poolingMasks.Length * (isSequence ? sequenceLength : 1) * dimensions);
        if (values.Length != expectedValues)
            throw new InvalidDataException(
                $"ONNX output '{plan.OutputName}' shape contains {expectedValues} values, actual count is {values.Length}.");
        if (isSequence && poolingMasks.Any(mask => mask.Length != sequenceLength))
            throw new InvalidDataException(
                $"ONNX output '{plan.OutputName}' sequence length is {sequenceLength}, expected {plan.SequenceLength} to match the input sequence.");
        if (dimensions != _profile!.Dimensions)
            throw new InvalidDataException(
                $"ONNX output dimension is {dimensions}, profile requires {_profile.Dimensions}.");

        var results = new float[poolingMasks.Length][];
        for (var batchIndex = 0; batchIndex < poolingMasks.Length; batchIndex++)
        {
            bool[] poolingMask = poolingMasks[batchIndex];
            var batchOffset = checked(batchIndex * (isSequence ? sequenceLength : 1) * dimensions);
            float[] result;
            if (!isSequence)
            {
                result = values.AsSpan(batchOffset, dimensions).ToArray();
            }
            else if (plan.Pooling == PoolingMode.Cls)
            {
                var firstIncludedRow = Array.FindIndex(poolingMask, static included => included);
                if (firstIncludedRow < 0 || firstIncludedRow >= sequenceLength)
                    throw new InvalidDataException("ONNX CLS pooling received an empty attention mask.");
                result = values.AsSpan(
                    checked(batchOffset + firstIncludedRow * dimensions),
                    dimensions).ToArray();
            }
            else
            {
                // Auto and mean pooling follow each input's mask independently so
                // padding from one batch row cannot affect another row's denominator.
                result = new float[dimensions];
                var included = 0;
                for (var row = 0; row < sequenceLength; row++)
                {
                    if (!poolingMask[row])
                        continue;
                    var rowOffset = checked(batchOffset + row * dimensions);
                    for (var column = 0; column < dimensions; column++)
                        result[column] += values[rowOffset + column];
                    included++;
                }

                if (included == 0)
                    throw new InvalidDataException("ONNX mean pooling received an empty attention mask.");
                var reciprocal = 1f / included;
                for (var column = 0; column < dimensions; column++)
                    result[column] *= reciprocal;
            }

            ValidateAndNormalize(result);
            results[batchIndex] = result;
        }

        return results;
    }

    private void ValidateAndNormalize(float[] result)
    {
        for (var index = 0; index < result.Length; index++)
        {
            if (float.IsNaN(result[index]) || float.IsInfinity(result[index]))
                throw new InvalidDataException("ONNX embedding contains NaN or infinity.");
        }

        if (!_profile!.Normalize)
            return;

        double sumSquares = 0;
        for (var index = 0; index < result.Length; index++)
            sumSquares += (double)result[index] * result[index];
        if (sumSquares <= 0d || double.IsNaN(sumSquares) || double.IsInfinity(sumSquares))
            throw new InvalidDataException("ONNX embedding is a zero or invalid vector.");

        var norm = Math.Sqrt(sumSquares);
        if (!(norm > 0d) || double.IsNaN(norm) || double.IsInfinity(norm))
            throw new InvalidDataException("ONNX embedding has an invalid L2 norm.");

        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (float)(result[index] / norm);
            if (float.IsNaN(result[index]) || float.IsInfinity(result[index]))
                throw new InvalidDataException("ONNX normalized embedding contains NaN or infinity.");
        }
    }

    private static (bool IsSequence, int SequenceLength, int Dimensions) ResolveOutputLayout(
        int[] shape,
        int batchSize,
        string outputName)
    {
        return shape.Length switch
        {
            1 when batchSize == 1 => (false, 1, shape[0]),
            1 => throw new InvalidDataException(
                $"ONNX output '{outputName}' omitted the batch dimension for a batch of {batchSize}."),
            2 when shape[0] == batchSize => (false, 1, shape[1]),
            2 when batchSize == 1 => (true, shape[0], shape[1]),
            2 => throw new InvalidDataException(
                $"ONNX output '{outputName}' batch dimension is {shape[0]}, expected {batchSize}."),
            3 when shape[0] == batchSize => (true, shape[1], shape[2]),
            3 => throw new InvalidDataException(
                $"ONNX output '{outputName}' batch dimension is {shape[0]}, expected {batchSize}."),
            _ => throw new InvalidDataException($"ONNX output '{outputName}' must have rank 1, 2 or 3."),
        };
    }

    private static string? FindMetadataName<T>(IReadOnlyDictionary<string, T> metadata, string requestedName)
        where T : class
    {
        if (metadata.ContainsKey(requestedName))
            return requestedName;
        return metadata.Keys.FirstOrDefault(name => string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatNames(IEnumerable<string> names)
        => string.Join(", ", names.OrderBy(static name => name, StringComparer.Ordinal));

    private static bool TryGetFullPath(string? path, out string fullPath, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            fullPath = string.Empty;
            error = "path is empty";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            fullPath = string.Empty;
            error = exception.Message;
            return false;
        }
    }

    private static string? SnapshotPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        return TryGetFullPath(path, out var fullPath, out _)
            ? fullPath
            : path;
    }

    private static bool TryNormalizeTokenizerType(string? value, out TokenizerKind kind)
    {
        var normalized = value?.Trim().ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
        switch (normalized)
        {
            case "bertwordpiece":
            case "bert":
            case "wordpiece":
                kind = TokenizerKind.BertWordPiece;
                return true;
            case "sentencepiece":
            case "sentencepiecebpe":
                kind = TokenizerKind.SentencePiece;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static bool TryNormalizePooling(string? value, out PoolingMode pooling)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "mean":
                pooling = PoolingMode.Mean;
                return true;
            case "cls":
            case "first":
            case "firsttoken":
                pooling = PoolingMode.Cls;
                return true;
            case "auto":
            case null:
            case "":
                pooling = PoolingMode.Auto;
                return true;
            default:
                pooling = default;
                return false;
        }
    }

    private static bool TryNormalizePaddingSide(string? value, out PaddingSide paddingSide)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "right":
            case null:
            case "":
                paddingSide = PaddingSide.Right;
                return true;
            case "left":
                paddingSide = PaddingSide.Left;
                return true;
            default:
                paddingSide = default;
                return false;
        }
    }

    private static string ResolveBertMaskingToken(
        string tokenizerPath,
        CopilotEmbeddingModelProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.MaskingToken))
            return profile.MaskingToken;

        // BertTokenizer validates every special token against vocab.txt. Keep the
        // conventional [MASK] when present, while allowing inference-only compact
        // vocabularies that intentionally omit this training token.
        try
        {
            foreach (var line in File.ReadLines(tokenizerPath))
            {
                if (string.Equals(line, "[MASK]", StringComparison.Ordinal))
                    return "[MASK]";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The tokenizer constructor will report the underlying file error with
            // its normal actionable exception if the vocabulary cannot be read.
        }

        return ResolveBertSpecialToken(profile.UnknownToken, "[UNK]");
    }

    private static string ResolveBertSpecialToken(string? configured, string fallback)
        => string.IsNullOrWhiteSpace(configured) ? fallback : configured;

    private static async ValueTask<IReadOnlyList<float[]>> EmbedFallbackBatchAsync(
        BuiltinHashEmbeddingProvider fallback,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var embeddings = new float[texts.Count][];
        for (var index = 0; index < texts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            embeddings[index] = await fallback.EmbedAsync(texts[index], cancellationToken).ConfigureAwait(false);
        }

        return embeddings;
    }

    private static bool IsRuntimeFallbackException(Exception exception)
    {
        if (exception is OutOfMemoryException or StackOverflowException or AccessViolationException)
            return false;

        return exception switch
        {
            DllNotFoundException or
            BadImageFormatException or
            EntryPointNotFoundException or
            FileLoadException or
            FileNotFoundException or
            DirectoryNotFoundException or
            UnauthorizedAccessException or
            OnnxRuntimeException => true,
            TypeInitializationException typeInitializationException
                when typeInitializationException.InnerException is not null
                => IsRuntimeFallbackException(typeInitializationException.InnerException),
            _ => false,
        };
    }

    private enum TokenizerKind : byte
    {
        BertWordPiece,
        SentencePiece,
    }

    private enum TensorKind : byte
    {
        Int32,
        Int64,
        Float32,
        Float64,
    }

    private enum PoolingMode : byte
    {
        Auto,
        Mean,
        Cls,
    }

    private enum PaddingSide : byte
    {
        Right,
        Left,
    }

    private sealed record InputBinding(string Name, TensorKind Kind, NodeMetadata Metadata);

    private sealed record ExecutionPlan(
        InputBinding InputIds,
        InputBinding? AttentionMask,
        InputBinding? TokenTypeIds,
        InputBinding? PositionIds,
        int FixedBatchSize,
        int SequenceLength,
        string OutputName,
        TensorKind OutputKind,
        PoolingMode Pooling);

    private sealed record PreparedInputs(List<NamedOnnxValue> Inputs, bool[][] PoolingMasks);

    private static NamedOnnxValue CreateIntegerTensor(
        InputBinding binding,
        int[] values,
        int batchSize,
        int sequenceLength)
    {
        return binding.Kind switch
        {
            TensorKind.Int64 => NamedOnnxValue.CreateFromTensor(
                binding.Name,
                new DenseTensor<long>(
                    values.Select(static value => (long)value).ToArray(),
                    [batchSize, sequenceLength])),
            TensorKind.Int32 => NamedOnnxValue.CreateFromTensor(
                binding.Name,
                new DenseTensor<int>(values, [batchSize, sequenceLength])),
            _ => throw new InvalidDataException($"ONNX input '{binding.Name}' is not an integer tensor."),
        };
    }
}

/// <summary>
/// 描述本地 ONNX provider 当前实际创建的 session 执行配置。
/// </summary>
public sealed class LocalOnnxExecutionState
{
    private LocalOnnxExecutionState(
        bool sessionInitialized,
        bool appliedToSession,
        int requestedIntraOpThreads,
        int requestedInterOpThreads,
        int? effectiveIntraOpThreads,
        int? effectiveInterOpThreads,
        string executionMode)
    {
        SessionInitialized = sessionInitialized;
        AppliedToSession = appliedToSession;
        RequestedIntraOpThreads = requestedIntraOpThreads;
        RequestedInterOpThreads = requestedInterOpThreads;
        EffectiveIntraOpThreads = effectiveIntraOpThreads;
        EffectiveInterOpThreads = effectiveInterOpThreads;
        ExecutionMode = executionMode;
    }

    /// <summary>
    /// 是否已成功创建并保留 ONNX Runtime session。
    /// </summary>
    public bool SessionInitialized { get; }

    /// <summary>
    /// 是否已使用请求值创建 session options；session 未创建或已 fallback 时为 <see langword="false"/>。
    /// </summary>
    public bool AppliedToSession { get; }

    /// <summary>
    /// 请求的算子内线程数；<c>0</c> 表示运行时默认值。
    /// </summary>
    public int RequestedIntraOpThreads { get; }

    /// <summary>
    /// 请求的算子间线程数；<c>0</c> 表示顺序执行。
    /// </summary>
    public int RequestedInterOpThreads { get; }

    /// <summary>
    /// 显式应用的算子内线程数；运行时默认值无法读取时为 <see langword="null"/>。
    /// </summary>
    public int? EffectiveIntraOpThreads { get; }

    /// <summary>
    /// 显式应用的算子间线程数；顺序模式下为 <see langword="null"/>。
    /// </summary>
    public int? EffectiveInterOpThreads { get; }

    /// <summary>
    /// 实际 session 执行模式：<c>parallel</c>、<c>sequential</c> 或 <c>not-initialized</c>。
    /// </summary>
    public string ExecutionMode { get; }

    internal static LocalOnnxExecutionState NotInitialized(int intraOpThreads, int interOpThreads)
        => new(
            sessionInitialized: false,
            appliedToSession: false,
            intraOpThreads,
            interOpThreads,
            effectiveIntraOpThreads: null,
            effectiveInterOpThreads: null,
            executionMode: "not-initialized");

    internal static LocalOnnxExecutionState Initialized(int intraOpThreads, int interOpThreads)
        => new(
            sessionInitialized: true,
            appliedToSession: true,
            intraOpThreads,
            interOpThreads,
            effectiveIntraOpThreads: intraOpThreads > 0 ? intraOpThreads : null,
            effectiveInterOpThreads: interOpThreads > 0 ? interOpThreads : null,
            executionMode: interOpThreads > 0 ? "parallel" : "sequential");
}
