# GH-Issue #198: Int64 / STRING boundary evidence (2026-09-25)

Source: [IoTSharp/SonnetDB #198](https://github.com/IoTSharp/SonnetDB/issues/198), read from the live GitHub API on 2026-09-25. The issue was **open** at read-back. This is bounded implementation evidence, not a closure claim.

## Product Contract

SQL `INT` is signed Int64. Bare integer literals outside `[-9223372036854775808, 9223372036854775807]`, overflowing integer arithmetic and invalid `CAST(... AS INT)` fail deterministically. SQL parameters of CLR type `BigInteger` are rejected at the ADO value setter and Core binder with `SndbParameterTypeException.Code = big_integer_unsupported`, before transport or SQL binding. Applications explicitly range-check and convert to `long`, or store the original text in `STRING`. `STRING` comparison and ordering are lexical, not arbitrary-precision numeric operations. `DECIMAL` is finite precision and is not a `BigInteger` replacement.

SQL `INSERT`, `UPDATE`, `WHERE`, `MIN`, `MAX`, `COUNT(value)` versus `COUNT(*)`, NULL handling and indexed Int64 range filtering have focused Core coverage. REST/NDJSON carries Int64 without Float64 conversion and rejects bare JSON integers outside Int64. The Frame codec now has additive `SqlValueKind.Decimal = 9` for exact decimal results; it rejects out-of-range `ulong` and `BigInteger` instead of converting them to Float64 or string. ADO uses `long` and `DbType.Int64` with precision 19 for non-null Int64 result columns. Source-generated `ServerJsonContext.ResultMeta` serializes optional `columnTypes`; older NDJSON meta without it remains readable.

关系表普通 SELECT 现通过 `SelectColumnInfo` 保留直接列、别名、常量及可静态确认的聚合类型。空结果、全 NULL 列、DISTINCT/分页后的嵌入式 ADO、REST NDJSON 与真实 HTTP/2 Frame ADO 在读取第一行之前均可报告声明类型和可空性；`COUNT` 为非空 Int64，空集的 `MIN`/`AVG`/`SUM` 为 NULL。`SUM(INT)` 在未溢出及空/全 NULL 结果中报告 Int64；后续独立分支 `codex/issue-198-overflow` 将累加溢出收紧为明确拒绝，保留声明 Int64 类型，显式 `CAST(value AS DECIMAL)` 可取得范围内的精确十进制结果。该分支的验证证据见下文，尚不证明已合入远程分支。

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

SQL Frame 旧客户端兼容收口（独立工作树 `codex/issue-198-frame-compat`，基线 `2ba84b8d`）：

- SQL query 帧体不变。HTTP 请求头 `X-SonnetDB-Sql-Result-Version: 2` 显式启用声明列 meta 尾部与 Decimal tag 9；无头或值 `1` 时，服务端输出历史 meta 及 Decimal Float64 tag 2。历史提交 `b8beed14^` 的解码器会拒绝 meta 尾部及大于 GeoPoint tag 8 的值标记；旧格式回归按该拒绝边界建立严格解码器。其他版本返回 `unsupported_result_version` 错误帧。
- `SqlFrameCodec.EncodeQueryRowsFrame` 的原六参数公开签名仍存在并输出旧格式；新增七参数重载仅供已协商 v2 的调用方使用，避免已编译调用方缺少方法。
- 新 ADO SQL Frame 客户端始终请求 v2。旧服务端忽略请求头并返回旧 meta 时，`Protocol=auto` 对只读查询回退 REST；`Protocol=frame-http2` 显式报 `frame_sql_result_version_unsupported`，不把旧 Float64 DECIMAL 报为精确 Decimal。未协商 v2 的旧客户端继续保留历史精度限制。
- `dotnet vstest tests/SonnetDB.Core.Tests/bin/Release/net10.0/SonnetDB.Core.Tests.dll --TestCaseFilter:FullyQualifiedName~SqlFrameCodecTests --logger:console`：**24/24 passed**，含 legacy dense/variant DECIMAL 编码。
- `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-restore --configuration Release --filter "FullyQualifiedName~SqlFrameEndpointTests|FullyQualifiedName~RemoteAdoHttp2TransportTests|FullyQualifiedName~FrameTransportParityTests" --verbosity quiet`：**86/86 passed**。其中真实 HTTP/2 h2c 响应经严格旧 meta/rows 解码器读取，v2 精确 Decimal 值及 ADO 元数据、非法版本错误帧、模拟旧服务端的 `auto` REST 回退与强制 Frame 报错均通过。
- `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-restore --configuration Release --filter "FullyQualifiedName~ObjectFrameTransportParityTests|FullyQualifiedName~TsdbBulkFrameTransportParityTests|FullyQualifiedName~KvObjectDocFrameEndpointTests" --verbosity quiet`：**47/47 passed**，覆盖共享 `FrameChannel` 发送路径上的非 SQL service。
- `dotnet build src/SonnetDB/SonnetDB.csproj --configuration Release --no-restore --verbosity quiet`：**0 warnings, 0 errors**，包含 IL/AOT 分析器。

## Remaining Acceptance

静态类型尚未覆盖所有表达式、递归 CTE、文档/measurement 等更宽查询模型；动态多类型结果仍保守报告 `object`。Frame v2 的最低客户端边界已定义为显式请求头加 v2 meta/tag 9 解码能力；已发布 3.1.0 不包含此能力，首次承载它的 NuGet 版本尚未确定，旧客户端必须保持 v1。Frame v2 的独立工作树未执行 NativeAOT 发布；后续 SUM(INT) 工作树的 Server NativeAOT 发布结果见下文。已发布包与真实旧服务端二进制仍未取证；本次旧服务端回归通过请求头剥离模拟其响应格式。#198 保持 open。

## SUM(INT) Overflow Follow-up

独立工作树 `codex/issue-198-overflow` 在关系表聚合中对 Int64 累加逐步执行 checked 加法，正负越界均抛出稳定的 `InvalidOperationException`（消息 `SUM(INT) 的 Int64 累加发生溢出。`），不返回有损 Double。直接聚合、GROUP BY、嵌套投影及 HAVING 共用该路径；未越界的 `long.MaxValue` 保留 Int64 值与声明类型，`SUM(CAST(value AS DECIMAL))` 对 `long.MaxValue + 1` 保留精确 decimal 值。该合同只适用于关系表 `SUM(INT)`，不把文档/measurement 数值聚合描述为同一合同。

- 初次定向 Core 测试：**5/5 passed**，覆盖上述直接/分组/嵌套/HAVING 正溢出、负溢出与 Int64 元数据。
- 完整 Core Release 测试：**5212/5213 passed**。唯一失败为 `SqlRecursiveCteTests.Execute_AlternatingWideRows_RejectsAtCumulativeStringBudget`，其断言尚未接纳并行 #195 的查询预算优先拒绝；主工作区已有未提交修正，不在本独立工作树基线中。不能把本轮记作完整 Core 通过。
- 增加 DECIMAL 精确替代回归后，Core Release 定向复验：**14/14 passed**；真实 Server 的嵌入式 ADO、REST NDJSON 与 HTTP/2 Frame 声明元数据用例：**4/4 passed**。
- `dotnet publish src/SonnetDB/SonnetDB.csproj --configuration Release -r win-x64 -p:SonnetDbPublishAot=true -m:1 /warnaserror --verbosity quiet`：**exit 0，0 warnings**。首次误用全局 `PublishAot=true` 导致两个 netstandard source generator 报 NETSDK1207；改为仓库规定的项目专用开关后成功。该结果是 NativeAOT 发布构建证据，不是发布二进制运行或已发布包验收。
