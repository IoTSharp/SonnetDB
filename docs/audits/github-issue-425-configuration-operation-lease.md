# 配置操作租约的跨实例事务验收

2026-11-28（Asia/Shanghai）。SonnetDB 工作树基于 `c42e7044b6661027a1570c91a23bc09ddf71344d`，IoTSharp 工作树基于 `a8a26743a523405ca37d41df792931f37b11db26`。两仓的交付分支均为 `codex/issue-425-cross-instance-lease`。[SonnetDB #425](https://github.com/IoTSharp/SonnetDB/issues/425)

| 工作树 | 路径 |
| --- | --- |
| SonnetDB | `C:\Users\mysti\.codex\worktrees\issue-425-cross-instance-lease\SonnetDB` |
| IoTSharp | `D:\GitHub\IoTSharp-issue-425-cross-instance-lease` |

## 事务合同

- 启用端在真正的远程 `Serializable` 会话中运行。客户端要求服务端确认 `serializable`。旧服务端的预演只计算预览行数，无法提供这项确认。REST 与 `frame-http2` 配置下的事务都由 HTTP 事务会话端点执行。
- IoTSharp 候选实现先以 `FOR UPDATE NOWAIT` 锁定 Gateway 锚点，再执行 nullable GUID、UTC DateTime、布尔和三字段 setter 的条件更新。获取须影响 1 行。释放须匹配本次 leaseId，影响总数须等于批次目标数。已过期租约可以被接管。
- `ReadCommitted` 的行值检查可允许先写租约再释放为原值的两个事务都提交。显式 `Serializable` 执行表级版本校验，`table_serialization_conflict` 会中止整个事务。需要重试时须开启新事务并重查所有业务条件。NOWAIT 的 `sql_lock_not_available` 映射为现有忙碌拒绝。
- 提交响应失效后，客户端查询原会话终态。只有已确认仍为 `active` 的同一会话才可按条件重试一次。显式存储提交未知或状态不可取得时，保留未知结果，依据业务行查验。公开 5.2.0 的异步 Begin 只检查调用前取消，且提交前取消会结束本地 ADO 事务句柄。候选源码把取消传到会话创建请求，并在提交前取消时保留原事务供回滚。
- 存储层进入事务恢复时，服务端候选返回 `routine_commit_unknown` 和会话终态 `unknown`，同一会话的主动回滚会被拒绝。重启后事务会话状态可能不可查询。持久化对账以配置版本、assignment、task、receipt 或 audit 的实际业务标识为准。直接保存配置可通过调用方传入的唯一 `SourceId` 过滤查询配置版本，API 允许省略或复用该字段，因此无法保证每个请求都有唯一可查键。

## 结果

| 验收 | 本轮证据 |
| --- | --- |
| 正式 5.2.0 包和匹配镜像 | PASS。独立 .NET 10 真实消费者使用 NuGet.org 产物。包与 OCI revision 同为 `cbbe5ed2e64879ea6fb7650d1d7b3ff4eee2909d`，Linux/amd64 镜像摘要为 `sha256:bc0c473ba905d60c7d4db4f0d48b53dccb0069f31884a3d1ccadb5a2cc5da449`。包和 DLL 哈希见[正式包探针](../../eng/probes/issue425-configuration-lease/README.md)。 |
| 两个实际客户端进程和重启读回 | PASS。两个客户端各自得到影响 1 行。仅一个提交成功，另一个收到序列化冲突。服务重启后新进程读到唯一获胜的 assignment 和 receipt，租约字段为空。 |
| 当前源码的服务器、ADO.NET 与故障验证 | PASS，29/29。涵盖真实 REST 和 HTTP/2 会话、旧端拒绝、竞争、租约过期、条件释放、批次回滚、连接会话撤销、取消，以及提交响应丢失和存储层提交未知两类重启对账。 |
| 当前源码的 EF 翻译和迁移回归 | PASS，7/7。REST 与 HTTP/2 真实会话均验证 GUID 谓词、UTC 时间边界、布尔筛选、获取和批量释放的精确行数、事务外隔离和回滚。 |
| 当前源码的 solution restore 和 CI 格式 | PASS，均退出 0。首次格式检查在四分钟上限结束。带诊断输出的复跑约十分钟，检查 2,828 个文件、需改动 0 个。格式工具报告 `.dcproj` 无关联语言。 |
| 正式 5.2.0 的独立五表故障矩阵 | 14 PASS、2 FAIL。五阶段回滚、串行化竞争、超时、断连、丢失提交响应、已提交和未提交两类重启对账、旧端拒绝和 Frame 入口通过。ReadCommitted 是预期反例。提交前取消是正式产物缺陷，未发送 COMMIT，却报告未知并关闭客户端事务，服务端会话须另行回滚。源码候选的对应回归已通过。 |
| 正式 5.2.0 的 IoTSharp 后端业务回归 | PASS，最终整组串行 25/25。测试宿主采用候选服务，覆盖模板发布与发布中心竞争、回滚与新发布竞争、过时重试拒绝、错 owner 和批次中止。原 provider 默认拒绝也通过。两个故障用例分别验证提交前回滚，以及提交成功后收到 HTTP 500 时用新认证客户端按唯一 `SourceId` 查回版本，并用独立上下文确认 assignment 和租约。此前 25 项整组运行到五分钟上限中断，两个故障定向用例复验为 2/2。 |

GitHub 的基线运行 `45964776153` 的 Windows REST 分片在 .NET 11 的 CAP 启动故障用例中超时。同一分片 EF 448/448、Server 1665/1665、IoTSharpCompat 1/1 已通过。[原始 CI 作业](https://github.com/IoTSharp/SonnetDB/actions/runs/45964776153/job/140390745612)

## 验证命令

在 SonnetDB 专用工作树执行。测试使用 `DOTNET_PROCESSOR_COUNT=2`、单 MSBuild 节点和关闭节点复用。最终格式命令在同一检查参数上额外使用 `--verbosity diagnostic`。

```powershell
dotnet restore SonnetDB.slnx
dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~SqlSerializableTransportTests|FullyQualifiedName~RemoteTransactionCommitRecoveryTests|FullyQualifiedName~ConnectionSession_RevocationRollsBackConfigurationTransaction|FullyQualifiedName~RowLocks_RemoteWaitAndCancellation_ReleaseRequestAndSession' --logger 'trx;LogFileName=issue425-server-final.trx'
dotnet test tests/SonnetDB.EntityFrameworkCore.Tests/SonnetDB.EntityFrameworkCore.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~SonnetDbConfigurationLeaseTests|FullyQualifiedName~SonnetDbMigrationTransactionTests' --logger 'trx;LogFileName=issue425-ef.trx'
```

IoTSharp 工作树通过显式测试服务注册启用候选实现。生产 DI 的 provider 拒绝用例仍校验连接尚未打开。构建用的 SonnetDB 包来自单独核验的 NuGet.org 包目录，正式业务验证使用上述镜像摘要。源码候选修复尚未进入公开包和匹配镜像，Provider 准入及 #425 关闭仍待该发布组合验收。

IoTSharp 的后端业务与独立五表故障矩阵各有运行记录及产物哈希。第一次应用业务组合运行共享测试数据，设备列表断言受新增 Gateway 影响。改用独立应用 fixture 后整组通过。后端构建跳过前端 npm 步骤，验证范围为 HTTP 和数据库流程。

| 复核入口 | 路径（相对于对应工作树） |
| --- | --- |
| SonnetDB Server TRX | `tests/SonnetDB.Tests/TestResults/issue425-server-final.trx` |
| SonnetDB EF TRX | `tests/SonnetDB.EntityFrameworkCore.Tests/TestResults/issue425-ef.trx` |
| IoTSharp HTTP 验证和运行命令 | `HANDOFF.md`、`IoTSharp.Test/obj/issue425-official/iotsharp-lease-official-final-serial.trx` |
| IoTSharp 五表矩阵和运行命令 | `tools/IoTSharp.SonnetDb.Contracts/README.md`、`obj/sonnetdb-contract-520-44089c3/full/results.json` |

当前源码的 Server 与 EF 验证构建使用仓库原有 net11.0 测试框架。ADO.NET 的 net10.0 和 net11.0 资产均在定向构建中编译。正式 NuGet 消费和 IoTSharp 后端项目保留 net10.0，依赖仍沿用 10.0 系列。完整解决方案测试、NativeAOT 发布、公开新版本和生产部署均为 NOT_RUN。

## 关闭条件中尚未完成的项目

- 将候选修复纳入同提交的正式 NuGet 包和服务镜像后，需针对该配对重跑发布验收。当前正式 5.2.0 的预取消缺陷已由公开消费者直接复现。
- 本轮跨进程竞争使用独立 .NET 10 消费者，业务测试使用真实 IoTSharp HTTP 路径和独立请求或数据库作用域。两个完整 IoTSharp 应用进程共享同一数据库时的故障行为仍为 NOT_RUN。
- 五阶段故障矩阵逐阶段写入并核对五类业务行。真实应用内的故障注入覆盖配置提交边界，发布、回滚、receipt 和 audit 的各阶段注入仍缺真实 IoTSharp 用例。直接保存的所有请求均可按唯一操作标识对账也尚未证明。
