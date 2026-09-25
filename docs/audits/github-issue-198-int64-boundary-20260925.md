# GH-Issue #198: Int64 / STRING boundary evidence (2026-09-25)

Source: [IoTSharp/SonnetDB #198](https://github.com/IoTSharp/SonnetDB/issues/198), read from the live GitHub API on 2026-09-25. The issue was **open** at read-back. This is bounded implementation evidence, not a closure claim.

## Product Contract

SQL `INT` is signed Int64. Bare integer literals outside `[-9223372036854775808, 9223372036854775807]`, overflowing integer arithmetic and invalid `CAST(... AS INT)` fail deterministically. SQL parameters of CLR type `BigInteger` are rejected at the ADO value setter and Core binder with `SndbParameterTypeException.Code = big_integer_unsupported`, before transport or SQL binding. Applications explicitly range-check and convert to `long`, or store the original text in `STRING`. `STRING` comparison and ordering are lexical, not arbitrary-precision numeric operations. `DECIMAL` is finite precision and is not a `BigInteger` replacement.

SQL `INSERT`, `UPDATE`, `WHERE`, `MIN`, `MAX`, `COUNT(value)` versus `COUNT(*)`, NULL handling and indexed Int64 range filtering have focused Core coverage. REST/NDJSON carries Int64 without Float64 conversion and rejects bare JSON integers outside Int64. The Frame codec now has additive `SqlValueKind.Decimal = 9` for exact decimal results; it rejects out-of-range `ulong` and `BigInteger` instead of converting them to Float64 or string. ADO uses `long` and `DbType.Int64` with precision 19 for non-null Int64 result columns. Source-generated `ServerJsonContext.ResultMeta` serializes optional `columnTypes`; older NDJSON meta without it remains readable.

关系表普通 SELECT 现通过 `SelectColumnInfo` 保留直接列、别名、常量及可静态确认的聚合类型。空结果、全 NULL 列、DISTINCT/分页后的嵌入式 ADO、REST NDJSON 与真实 HTTP/2 Frame ADO 在读取第一行之前均可报告声明类型和可空性；`COUNT` 为非空 Int64，空集的 `MIN`/`AVG`/`SUM` 为 NULL。`SUM(INT)` 在未溢出及空/全 NULL 结果中报告 Int64；既有求值器在累加溢出时提升为 Double，检测到提升后不宣称固定 Int64。该提升不是任意精度算术证据。

关系表 DATETIME、TIME 和 BLOB 的 REST/NDJSON 与 HTTP/2 Frame ADO 已用真实 Kestrel 验证：读取首行前分别声明 `DateTime`、`TimeOnly`、`byte[]`，非空行的 `GetValue()` 返回对应 CLR 类型及完整值，后续 NULL 行仍保留声明类型。Frame TIME 沿用 String 值标记，以不受区域设置影响的七位小数字符串编码，并由声明类型还原为 `TimeOnly`，没有增加新的值标记。

## Verification

- `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter 'FullyQualifiedName~SqlInt64BoundaryTests|FullyQualifiedName~SndbBigIntegerBoundaryTests|FullyQualifiedName~SqlFrameCodecTests' --no-restore --verbosity minimal`: **39/39 passed**.
- `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --filter 'FullyQualifiedName~NdjsonInt64BoundaryTests|FullyQualifiedName~Remote_Int64Boundary_RoundTripsAndReportsAdoMetadata|FullyQualifiedName~FrameHttp2_Int64Boundary_AsyncReadAndBigIntegerPreflight|FullyQualifiedName~ExecuteReader_InsertSelectReturning|FullyQualifiedName~FrameHttp2_InsertSelectAsync' --no-restore --verbosity minimal`: **9/9 passed**, including actual Kestrel REST and HTTP/2 Frame paths and adjacent #195 acceptance tests.
- The server and Core projects have `IsAotCompatible=true` in `Directory.Build.props`, so these builds ran trim/AOT analyzers with warnings treated as errors. The source-generated `ResultMeta` path compiled with **0 IL/AOT warnings**. NativeAOT publish was not run.
- `git diff --check`: passed.

追加的关系 SELECT 元数据验收：

- `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~SqlSelectColumnMetadataTests|FullyQualifiedName~SqlInt64BoundaryTests|FullyQualifiedName~SqlFrameCodecTests" --verbosity quiet`：**35/35 passed**，含新旧 Frame meta 解码、空/全 NULL、别名、聚合与分页、`SUM(INT)` 溢出边界。
- `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-restore --filter "FullyQualifiedName~Select_EmptyAndAllNullRows_KeepDeclaredTypesAndAggregateSchema|FullyQualifiedName~FrameHttp2_EmptyAndAllNullSelect_UsesDeclaredMetaBeforeRows|FullyQualifiedName~Rest_EmptyAndAllNullSelect_StreamsDeclaredColumnTypes|FullyQualifiedName~NdjsonInt64BoundaryTests" --verbosity quiet`：**7/7 passed**，含真实 Kestrel REST、原始 NDJSON meta 和 exact HTTP/2 Frame 客户端。
- `dotnet build src/SonnetDB/SonnetDB.csproj --no-restore --configuration Release --verbosity quiet`：**0 warnings, 0 errors**，包括 IL/AOT 分析器；JSON 仍由 `ServerJsonContext.ResultMeta` source generation 处理。
- `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~SndbResultPreviewTests|FullyQualifiedName~SqlSelectColumnMetadataTests" --verbosity quiet`：**21/21 passed**；显式 `columnTypes:["object"]` 跨 Int64/Double 行始终报告 `object`，旧 meta 完全缺失类型字段时仍按当前行推断。
- `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-restore --configuration Release --filter "FullyQualifiedName~Rest_DateTimeTimeAndBlob_SelectValuesMatchDeclaredTypes|FullyQualifiedName~FrameHttp2_DateTimeTimeAndBlob_SelectValuesMatchDeclaredTypes" --verbosity quiet`：**2/2 passed**；真实 Kestrel REST 与 HTTP/2 Frame 均核对首行前类型、非空值、BLOB 字节、NULL 行及 `GetBytes()`。
- `dotnet build src/SonnetDB/SonnetDB.csproj --no-restore --configuration Release --verbosity quiet`（TIME 帧转换后复验）：**0 warnings, 0 errors**，包括 IL/AOT 分析器。
- 相关 `git diff --check`：**passed**。

## Remaining Acceptance

静态类型尚未覆盖所有表达式、递归 CTE、文档/measurement 等更宽查询模型；动态多类型结果仍保守报告 `object`。`SUM(INT)` 的溢出提升应由后续独立合同收紧。SQL Frame meta 在列名后增加可选声明信息尾部；新解码器兼容旧帧，但已发布的旧解码器可能拒绝新帧尾部。既有 Frame decimal tag 9 同样需要协议协商或明确最低客户端版本。NativeAOT 发布、已发布包和更宽模型兼容性尚未取证，#198 保持 open。
