# GH-Issue #197: INSERT ... RETURNING 合同取证（2026-09-25）

## 范围与发布状态

本次核对仅覆盖关系表 `INSERT ... RETURNING`，不把 UPDATE/DELETE RETURNING 计入 #197。仓库 `origin` 为 `https://github.com/IoTSharp/SonnetDB.git`。2026-09-25 从该仓库 GitHub API 重新读取 #197，状态为 open。完整合同在当前未发布工作区实现，首次完整交付版本的**正式版本号待发布决策和标签确定**；不能声称已发布的 3.1.0 具备本次合同。

## 实现及可观察合同

- Core 将 `RETURNING` 的声明列保留在 `SelectExecutionResult.ColumnSchema`，空 `INSERT ... SELECT` 也保留列名、类型、可空性、主键、自动递增与 ROWVERSION 属性。
- REST NDJSON `meta` 通过 source-generated `ServerJsonContext` 提供 `columnTypes` 和 `columnSchemas`。嵌入式及远程 ADO 读取器据此提供 `GetFieldType()` 和 `GetSchemaTable()`；旧响应缺少新字段仍按值推断并使用可空属性回退，损坏的新字段会被拒绝。
- `VALUES` 多行结果保持输入顺序；默认值、自动递增键、ROWVERSION 在返回行中可见。`ExecuteScalar` 返回首行首列，`ExecuteReader` 返回全部行且读取结束后 `RecordsAffected` 为实际插入数，`ExecuteNonQuery` 返回插入数；空批次分别返回空读取器、null 和 0。同步、异步及参数化调用均有覆盖。
- 重复复合主键报 `table_unique_violation`，整条语句无部分写入；事务内的 RETURNING 结果可读取，回滚后行不可见。嵌入式抛 `TableConstraintException`，远程 ADO 抛携带同一稳定错误码的 `SndbServerException`。
- 原生 Frame SQL 查询端点拒绝 `INSERT ... RETURNING`，错误码为 `bad_request`，不会写入。`Protocol=frame-http2` 的 ADO 写入使用同一 HTTP/2 连接访问 REST SQL 端点并取得 NDJSON 元数据；这不是 Frame 写入支持。
- 本合同中的“生成列”指当前支持的引擎生成 `AUTO_INCREMENT` 与 `ROWVERSION`；默认值在返回行中呈现。任意计算生成列表达式尚未实现，不能在 #197 发布说明中宣称支持，见 [SQL 参考](../sql-reference.md)。

## 验收矩阵

| #197 验收项 | 本地证据 | 结论 |
| --- | --- | --- |
| 多行顺序、生成值及默认值可见时机 | `SqlReturningTests`、`SqlInsertReturningContractTests`；`RemoteAdoEndToEndTests.InsertReturning_DeclaredSchemaAndEmptyBatch_AgreeAcrossAdoModes`；`SqlFrameEndpointTests.Rest_InsertReturning_ValuesExposeGeneratedRowsAndAtomicConstraintError` 直接检查 NDJSON 的两行顺序及 `id`、`name`、`rv`、`note` | 当前引擎生成列满足；计算生成列不在已支持 SQL 范围 |
| `ExecuteScalar`、`ExecuteReader`、`ExecuteNonQuery`、异步和影响行数 | `RemoteAdoEndToEndTests` 的 `InsertReturning_*` 及 `RemoteAdoHttp2TransportTests.FrameHttp2_InsertReturning_*`；REST `end.recordsAffected` | 满足本机连接与真实服务端到端合同 |
| 单行、多行、空批次、参数化、回滚、重复键及复合键 | Core、嵌入式/REST ADO、HTTP/2 ADO 测试；原始 REST 冲突返回 HTTP 400、`table_unique_violation`，Frame 只读查询确认无部分写入 | 满足当前支持的连接路径 |
| 列名、类型、值、可空性、schema 和错误码 | 嵌入式/REST ADO `GetName`/`GetFieldType`/`GetValue`/`GetSchemaTable`；原始 REST `columns`/`columnTypes`/`columnSchemas`；HTTP/2 ADO 写入回落覆盖非空和空结果 | 满足；原生 Frame SQL 写入明确拒绝 |
| 首次完整合同的正式发布版本 | 仅 `[Unreleased]` 条目；3.1.0 的 `INSERT RETURNING` 是早期能力 | **未满足**：待确定发布版本、打包发布、标签及发布后复验 |

线上验收文字未定义“生成列”是否包括尚未支持的任意计算列，也未说明“帧协议”是否要求原生 Frame SQL 写入。若按现有产品合同理解为引擎生成列和 `frame-http2` ADO 写入回落，则除正式发版外的本地验收项已有证据；若要求计算列或原生 Frame 写入，则这两项仍是功能缺口，需另行实现并验证后才可关闭 #197。

## 发布说明草稿（待确定版本后使用）

> SonnetDB **[首次完整合同版本待定]** 固化关系表 `INSERT ... RETURNING` 的跨连接结果合同。多行结果按输入顺序返回，`AUTO_INCREMENT`、`ROWVERSION` 和列默认值在返回行中可见。嵌入式、REST/NDJSON 及经 HTTP/2 REST 回落的 ADO.NET 客户端保留返回列名、类型、可空性、schema、实际影响行数和稳定的重复键错误码；同步/异步 `ExecuteScalar`、`ExecuteReader`、`ExecuteNonQuery` 支持参数化、空批次和事务回滚。原生 Frame SQL 查询端点仍只读。任意计算生成列不在本合同范围。已发布 3.1.0 包含早期 `INSERT RETURNING` 能力，但不包含此处的完整跨协议合同。

该段落是待审草稿，不对应已发布包。发版时应将占位版本替换为实际标签，核对包内容并移入正式发布说明；在此之前不关闭 #197。

## 验证

在 Windows / .NET 10 工作区执行，以下命令均退出码 0：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter "FullyQualifiedName~SndbResultPreviewTests|FullyQualifiedName~SqlInsertReturningContractTests|FullyQualifiedName~SqlReturningTests|FullyQualifiedName~SqlInsertSelectTests" --verbosity normal
# 36/36 passed

dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-build --filter "FullyQualifiedName~Query_InsertReturning_RejectedWithoutWriting|FullyQualifiedName~FrameHttp2_InsertReturning|FullyQualifiedName~InsertReturning_AsyncScalarNonQueryAndRollback|FullyQualifiedName~InsertReturning_DuplicateCompositeKey|FullyQualifiedName~InsertReturning_DeclaredSchemaAndEmptyBatch|FullyQualifiedName~Rest_InsertReturning_ValuesExposeGeneratedRowsAndAtomicConstraintError" --verbosity quiet
# 11/11 passed

dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-build --filter "FullyQualifiedName~RemoteAdoEndToEndTests|FullyQualifiedName~RemoteAdoHttp2TransportTests|FullyQualifiedName~SqlFrameEndpointTests" --verbosity quiet
# 106/106 passed

dotnet build src/SonnetDB/SonnetDB.csproj --no-restore --configuration Release --verbosity quiet
# succeeded; 0 warnings, 0 errors (including enabled IL/AOT analyzers)
```

这些是独立工作树中的本机真实 Kestrel/HTTP/2 端到端测试及 Release AOT 分析构建，不是外部部署或已发布 NuGet 包验证。未执行新的 NativeAOT 发布或生产硬件测试。实际发版时仍须指定版本、验证发布包/标签所含实现，并复核线上 issue 状态。
