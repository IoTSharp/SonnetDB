# GH-Issue #197: INSERT ... RETURNING 合同取证（2026-09-25）

## 范围与发布状态

本次核对仅覆盖关系表 `INSERT ... RETURNING`，不把 UPDATE/DELETE RETURNING 计入 #197。仓库 `origin` 为 `https://github.com/IoTSharp/SonnetDB.git`。完整合同在当前未发布工作区实现，首次完整交付版本为**下一未发布版本，版本号待标签确定**；不能声称已发布的 3.1.0 具备本次合同。

## 实现及可观察合同

- Core 将 `RETURNING` 的声明列保留在 `SelectExecutionResult.ColumnSchema`，空 `INSERT ... SELECT` 也保留列名、类型、可空性、主键、自动递增与 ROWVERSION 属性。
- REST NDJSON `meta` 通过 source-generated `ServerJsonContext` 提供 `columnTypes` 和 `columnSchemas`。嵌入式及远程 ADO 读取器据此提供 `GetFieldType()` 和 `GetSchemaTable()`；旧响应缺少新字段仍按值推断并使用可空属性回退，损坏的新字段会被拒绝。
- `VALUES` 多行结果保持输入顺序；默认值、自动递增键、ROWVERSION 在返回行中可见。`ExecuteScalar` 返回首行首列，`ExecuteReader` 返回全部行且读取结束后 `RecordsAffected` 为实际插入数，`ExecuteNonQuery` 返回插入数；空批次分别返回空读取器、null 和 0。同步、异步及参数化调用均有覆盖。
- 重复复合主键报 `table_unique_violation`，整条语句无部分写入；事务内的 RETURNING 结果可读取，回滚后行不可见。嵌入式抛 `TableConstraintException`，远程 ADO 抛携带同一稳定错误码的 `SndbServerException`。
- 原生 Frame SQL 查询端点拒绝 `INSERT ... RETURNING`，错误码为 `bad_request`，不会写入。`Protocol=frame-http2` 的 ADO 写入使用同一 HTTP/2 连接访问 REST SQL 端点并取得 NDJSON 元数据；这不是 Frame 写入支持。

## 验证

在 Windows / .NET 10 工作区执行，以下命令均退出码 0：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~SndbResultPreviewTests|FullyQualifiedName~SqlInsertReturningContractTests|FullyQualifiedName~SqlReturningTests|FullyQualifiedName~SqlInsertSelectTests" --verbosity quiet
# 34/34 passed

dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-restore --filter "FullyQualifiedName~Query_InsertReturning_RejectedWithoutWriting|FullyQualifiedName~FrameHttp2_InsertReturning|FullyQualifiedName~InsertReturning_AsyncScalarNonQueryAndRollback|FullyQualifiedName~InsertReturning_DuplicateCompositeKey|FullyQualifiedName~InsertReturning_DeclaredSchemaAndEmptyBatch" --verbosity quiet
# 10/10 passed

dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-restore --filter "FullyQualifiedName~RemoteAdoEndToEndTests|FullyQualifiedName~RemoteAdoHttp2TransportTests|FullyQualifiedName~SqlFrameEndpointTests" --verbosity quiet
# 71/71 passed

dotnet build src/SonnetDB/SonnetDB.csproj --no-restore --configuration Release --verbosity quiet
# succeeded; 0 warnings, 0 errors (including enabled IL/AOT analyzers)
```

这些是本机真实 Kestrel/HTTP/2 端到端测试及 AOT 分析构建，不是外部部署或已发布 NuGet 包验证。未执行新的 NativeAOT 发布或生产硬件测试；发布版本和线上 issue 状态应在实际发版时再核定。
