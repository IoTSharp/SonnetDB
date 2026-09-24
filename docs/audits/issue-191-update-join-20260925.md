# GH-Issue #191: UPDATE JOIN/FROM 追加验收

日期：2026-09-25。独立分支：`codex/issue-191-update-join`。目标：<https://github.com/IoTSharp/SonnetDB/issues/191>。

## 范围

已有执行器支持关系表 `UPDATE ... JOIN ... SET ... WHERE ...` 与 `UPDATE ... SET ... FROM ... WHERE ...`，只修改目标表；重复来源按声明扫描顺序首个匹配，影响数及 `RETURNING` 按目标主键去重。本次未重做该实现，补充以下验收：

- Core 三表 JOIN、BEFORE/AFTER 行触发器、`ROWVERSION` 和最终 `RETURNING` 值；源表不被修改。
- LEFT JOIN 的 SQL NULL 语义与复合主键隔离；唯一索引及外键冲突均保留整条语句前的两行目标状态。
- 真实本机 Kestrel：嵌入式/远程 ADO 同一条多表触发器 SQL，参数绑定、列顺序、行值和 `RecordsAffected` 一致；异步事务先回滚再提交；远程约束错误码和失败后的数据状态。
- REST 执行联接更新后，通过只读 Frame 查询验证目标变化和来源未变。Frame 查询端点不承担 DML 写入。

## 验证结果

| 验证 | 命令 | 结果 |
|---|---|---|
| Core 专项 | `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SqlUpdateJoinTests" --no-restore --nologo` | 8/8 通过，含新增 3 项。 |
| Core 相关回归 | `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SqlUpdateJoinTests|FullyQualifiedName~SqlTriggerAdvancedTests|FullyQualifiedName~SqlConstraintTriggerTests|FullyQualifiedName~SqlReturningTests" --no-restore --nologo` | 98/98 通过。 |
| 真实服务专项 | `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --filter "FullyQualifiedName~RemoteAdoEndToEndTests.UpdateJoin|FullyQualifiedName~SqlFrameEndpointTests.UpdateJoin" --no-restore --nologo` | 分别运行 ADO 5/5、Frame 1/1，合计 6/6。 |
| 真实服务相关回归 | `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --filter "FullyQualifiedName~RemoteAdoEndToEndTests|FullyQualifiedName~SqlFrameEndpointTests" --no-restore --nologo` | 57/57 通过。 |
| 差异完整性 | `git diff --check` | 通过。 |

## 边界

真实服务证据来自测试进程启动的 Kestrel，尚未覆盖部署后的双机网络、固定 x64/ARM64 硬件、长时间运行或外部 GitHub issue 状态读回。`UPDATE` 的现有语法要求显式 `WHERE`；同一轻事务里目标表已有缓冲写时，后续联接更新仍明确拒绝。本次没有生产代码变更，也不把本地验收写成线上 issue 已关闭。
