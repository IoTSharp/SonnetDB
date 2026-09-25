# GH-Issue #191: UPDATE JOIN/FROM 追加验收

日期：2026-09-25。独立分支：`codex/issue-191-update-join`。目标：<https://github.com/IoTSharp/SonnetDB/issues/191>。

## 范围

已有执行器支持关系表 `UPDATE ... JOIN ... SET ... WHERE ...` 与 `UPDATE ... SET ... FROM ... WHERE ...`，只修改目标表；重复来源按声明扫描顺序首个匹配，影响数及 `RETURNING` 按目标主键去重。本次未重做该实现，补充以下验收：

- Core 三表 JOIN、BEFORE/AFTER 行触发器、`ROWVERSION` 和最终 `RETURNING` 值；源表不被修改。
- LEFT JOIN 的 SQL NULL 语义与复合主键隔离；唯一索引及外键冲突均保留整条语句前的两行目标状态。
- 真实本机 Kestrel：嵌入式/远程 ADO 同一条多表触发器 SQL，参数绑定、列顺序、行值和 `RecordsAffected` 一致；异步事务先回滚再提交；远程约束错误码和失败后的数据状态。
- REST 执行联接更新后，通过只读 Frame 查询验证目标变化和来源未变。Frame 查询端点不承担 DML 写入。
- 唯一主键或唯一二级索引来源现在采用索引嵌套循环；非唯一来源仍按声明顺序逐行匹配，以保留首行选择语义。

## 验证结果

| 验证 | 命令 | 结果 |
|---|---|---|
| Core 专项 | `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SqlUpdateJoinTests" --no-restore --nologo` | 8/8 通过，含新增 3 项。 |
| Core 相关回归 | `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SqlUpdateJoinTests|FullyQualifiedName~SqlTriggerAdvancedTests|FullyQualifiedName~SqlConstraintTriggerTests|FullyQualifiedName~SqlReturningTests" --no-restore --nologo` | 98/98 通过。 |
| 真实服务专项 | `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --filter "FullyQualifiedName~RemoteAdoEndToEndTests.UpdateJoin|FullyQualifiedName~SqlFrameEndpointTests.UpdateJoin" --no-restore --nologo` | 分别运行 ADO 5/5、Frame 1/1，合计 6/6。 |
| 真实服务相关回归 | `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --filter "FullyQualifiedName~RemoteAdoEndToEndTests|FullyQualifiedName~SqlFrameEndpointTests" --no-restore --nologo` | 57/57 通过。 |
| 差异完整性 | `git diff --check` | 通过。 |

整合进主工作区后，2026-09-25 再次运行上述 Core 相关回归，结果 98/98；真实服务 ADO/Frame 专项 6/6，两个测试类的组合回归 82/82。组合中一项旧约束触发器用例原先精确断言 `InvalidOperationException`，现已改为断言稳定的 `TableConstraintException.UniqueViolation`；该项重跑通过。上述结果属于本地构建与测试，不代表线上 CI。

追加唯一索引路径测试后，`SqlUpdateJoinTests` 为 9/9；包含 JOIN 算法、触发器和 RETURNING 的 Core 组合回归为 121/121。测试直接断言保序关系执行器使用 `index_nested_loop` 和主键索引，再运行同形 `UPDATE ... LEFT JOIN` 验证命中与未命中目标行。真实服务相关组合回归 101/101，包含 HTTP/2 DML 边界。非唯一来源继续使用声明顺序嵌套循环；不宣称该路径也已索引化。

## 边界

真实服务证据来自测试进程启动的 Kestrel，尚未覆盖部署后的双机网络、固定 x64/ARM64 硬件、长时间运行或外部 GitHub issue 状态读回。`UPDATE` 的现有语法要求显式 `WHERE`；同一轻事务里目标表已有缓冲写时，后续联接更新仍明确拒绝。新的唯一索引执行路径属于当前未发布主线，不把本地验收写成线上 issue 已关闭。
