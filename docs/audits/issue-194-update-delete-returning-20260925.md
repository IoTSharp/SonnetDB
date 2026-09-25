# GH-Issue #194: UPDATE/DELETE RETURNING 验证记录

日期：2026-09-25。工作分支：`codex/issue-194-returning`。目标仓库：<https://github.com/IoTSharp/SonnetDB/issues/194>。

## 合同与修复

- 关系表 `UPDATE ... RETURNING` 返回完成 BEFORE 触发器改写和 `ROWVERSION` 生成后的目标行；`DELETE ... RETURNING` 返回删除前的目标行。受影响行数只计目标表，不计级联删除的子行。
- 直接 DELETE 的主键命中和条件扫描均携带行版本及完整前像进入事务提交校验。条件中的用户标量函数可回入 SQL 时，谓词仍在锁外求值，由提交校验拒绝过期前像。
- 远程轻事务预览按批内语句位置跳过先前 RETURNING 结果集，只解析当前目标语句的列、行、受影响数与截断标记；响应结果数不符时拒绝。
- Frame SQL 查询端点继续只读：管理员也无法经 `UPDATE/DELETE ... RETURNING` 写入。Frame 对 DML 不提供结果流；写入结果经 REST NDJSON 或 ADO.NET 获取。
- UPDATE/DELETE 的 RETURNING 结果附声明列结构，空结果也向嵌入式 ADO、REST NDJSON 和 HTTP/2 ADO 写入回落报告稳定的类型、可空性、主键与 ROWVERSION 标记。

## 验证

| 层级 | 命令或范围 | 结果 |
|---|---|---|
| Core 专项 | `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SqlUpdateDeleteReturning194Tests|FullyQualifiedName~SqlUpdateDeleteTests|FullyQualifiedName~SqlTableTriggerTests" --no-restore --nologo` | 5/5 通过；覆盖级联计数、并发前像、触发器/版本、回滚和复合键参数。 |
| Core 相关回归 | `dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SqlExecutorDeleteTests|FullyQualifiedName~SqlReturningTests|FullyQualifiedName~SqlDmlReturningAndConflictTests|FullyQualifiedName~SqlUpdateJoinTests|FullyQualifiedName~SqlTriggerAdvancedTests|FullyQualifiedName~SqlConstraintTriggerTests|FullyQualifiedName~SqlUpdateDeleteReturning194Tests" --no-restore --nologo` | 124/124 通过。 |
| 真实服务专项 | `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --filter "FullyQualifiedName~RemoteAdoEndToEndTests.UpdateDeleteReturning|FullyQualifiedName~RemoteAdoEndToEndTests.ExecuteReader_UpdateDeleteReturning|FullyQualifiedName~RemoteAdoEndToEndTests.RemoteNdjson_UpdateDeleteReturning|FullyQualifiedName~SqlFrameEndpointTests.Query_WriteReturningStatement" --no-restore --nologo` | 9/9 通过；真实本机 Kestrel，含嵌入式/远程 ADO、原始 REST NDJSON 和 Frame 拒写。 |
| 真实服务相关回归 | `dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --filter "FullyQualifiedName~RemoteAdoEndToEndTests|FullyQualifiedName~SqlFrameEndpointTests" --no-restore --nologo` | 58/58 通过。 |
| NativeAOT | `dotnet publish src/SonnetDB.Cli/SonnetDB.Cli.csproj -c Release -r win-x64 -p:PublishAot=true /warnaserror -o "$env:TEMP\sonnetdb-issue194-aot"` | 成功，0 warning / 0 error；CLI 引用被修改的远程客户端。 |
| 差异完整性 | `git diff --check` | 通过。 |

补充元数据后复验：`UpdateDeleteReturning_ParameterizedCompositeKey_UsesConsistentAdoResults` 嵌入式/远程 2/2；原始 NDJSON 1/1；UPDATE/DELETE/Frame 拒写定向 10/10；包含其他远程 ADO、HTTP/2 和 Frame 端点的组合回归 101/101。Core JOIN、触发器、RETURNING 相关组合回归 121/121。HTTP/2 写入经 REST SQL 端点，原生 Frame 查询仍拒绝 DML。

## 证据边界

以上真实服务测试使用测试进程启动的 Kestrel，不是已部署服务、固定硬件或长时间运行门禁。`#197` 的 INSERT RETURNING 发布版本要求另行验收。线上状态须在推送后回读，本记录不将本地通过当作 Issue 已关闭。
