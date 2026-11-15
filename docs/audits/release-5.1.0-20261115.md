# SonnetDB 5.1.0 稳定版候选审计

核查日期：2026-11-15（Asia/Shanghai）。本报告把 5.0.0 发布源、当前主线和本轮稳定性修复放在同一条证据链中；状态只表示对应证据层，不把源码、单测、包、安装、现场或公开读回互相替代。

## 身份与差异

| 项目 | 当前结论 |
| --- | --- |
| 5.0.0 tag | `v5.0.0` 指向 `84433f9edcceb84605520ebecc4b2879e3c4aa79`；其发布合同是 5.0 主版本迁移边界。 |
| 候选起点 | `main`、`origin/main`、`gitee/main` 在本轮开始时均为 `743fc29ab5bdfef0e9b3466898de07f47e8febac`。 |
| 子模块 | CoAP `1d6fa2c58777140af39e2c5101a76f1e32b5d0ed`；MQTT routing `1f1ec475ff3a364ab75f669467c1e2720fdcc73a`。 |
| parity-results | 保持独立分支；不合并到 `main`，本轮不改写其证据。 |
| 本轮修复 | WAL checkpoint 负值拒绝；Frame 畸形错误终态；tag 恢复有界轮询及文档合同。 |
| 稳定性修复提交 | `af9ee131`（完整 SHA 以 Git 回读为准）。 |
| Tag 后 CI 复核 | `v5.1.0` 的 Ubuntu/Windows REST 失败均为缺少 Release probe 输出；修复已在后续工作树中验证，尚未移动既有 tag。 |

## 本轮改动与证据

| 层级 | 文件/命令 | 状态 | 结论 |
| --- | --- | --- | --- |
| SOURCE | `src/SonnetDB.Core/Wal/WalWriter.cs`、`WalPayloadCodec.cs` | PASS | 写入和回放均拒绝负 checkpoint LSN。 |
| TEST | `tests/SonnetDB.Core.Tests/Wal/*` | PASS | 负值拒绝且拒绝后仍可继续写入；WAL 过滤 377/377。 |
| SOURCE | `connectors/dotnet/SonnetDB.Client/SndbFrameClient.cs` | PASS | 畸形 `Response|Error` 载荷映射为 `frame_terminal`/unknown。 |
| TEST | `tests/SonnetDB.Client.Tests/FrameClientTests.cs` | PASS | 截断和尾部字节 2/2；客户端基线 281/281。 |
| SOURCE/CI | `eng/recover-tagged-release.ps1` | PASS | workflow 状态等待同时受时间和轮询次数限制。 |
| TEST | `eng/test-recover-tagged-release.ps1`、策略/对账/资产测试 | PASS | 恢复合同 PASS；策略 13/13；对账 69/69；资产 20/20。 |
| DOCS | 本报告、`docs/releases/5.1.0.md`、恢复策略说明 | PASS | 公开说明保留未知写入、环境和真实消费者边界。 |

## 5.0 基线到当前主线的状态矩阵

| 路线/Issue | SOURCE | TEST/CI | PACKAGE | INSTALL | NATIVEAOT | PUBLIC CONSUMER | HARDWARE/FIELD | LONG-RUN | PUBLIC READBACK |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| atomic WAL v2 / recovery | PASS | PASS | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN |
| KV-LSM K6-K8 / migration | PASS | PASS（代码与崩溃切片） | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN |
| FullText bounded budget | PASS | PASS（本地/源码消费者切片） | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN |
| Frame typed clients / #397 | PASS（TTL opcode 10 为局部切片） | PASS（本地客户端） | NOT_RUN | NOT_RUN | NOT_RUN | UNKNOWN（真实 HTTP/2 重开未闭环） | NOT_RUN | NOT_RUN | NOT_RUN |
| EF Provider AOT / #396 | PASS（边界探针） | PASS（强制 analyzer 探针） | NOT_RUN | NOT_RUN | NOT_SUPPORTED | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN |
| Clean install / #398 | PASS（脚本/资产合同） | PASS（合成拒绝测试） | UNKNOWN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN |
| Fixed hardware / #399 | PASS（路线与工具入口） | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN |
| Time precision / #401 | NOT_RUN（仍为 ms 合同） | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN |
| Publication recovery / #402 | PASS（离线 admission/fence） | PASS（本地 mock） | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN |
| IoTSharp consumer / #425 | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | UNKNOWN | NOT_RUN | NOT_RUN | NOT_RUN |
| CoAP C12 | PASS（协议子模块与宿主接线） | PASS（focused） | NOT_RUN | NOT_RUN | NOT_RUN | UNKNOWN（peer interop） | NOT_RUN | NOT_RUN | NOT_RUN |
| MQTT R6 | PASS（routing 子模块） | PASS（子模块定向） | NOT_RUN | NOT_RUN | UNKNOWN（trimming/AOT） | UNKNOWN | NOT_RUN | NOT_RUN | NOT_RUN |

## 放行与回滚

候选放行前必须在同一最终 commit 上取得适用 CI、format、AOT、Parity 和发布资产证据；七天 scheduled Parity 是非阻断观察。tag、GitHub/Gitee、NuGet、安装和现场读回分别记账，任何未知外部写入先读回再决定后续动作。

`v5.1.0` tag 触发的 CI 失败已独立回读：Ubuntu 和 Windows REST 的失败测试都是 `FullTextIndependentProcessTests.IndependentServerAndCandidateConsumerCommunicateAndRecoverFromEveryBudget`，错误为 Release probe DLL 缺失。根因是 probe 仅以 `ReferenceOutputAssembly=false` 的项目引用存在，未列入主 solution，solution/shard 构建没有生成它。修复是将 `tests/SonnetDB.FullTextProcessProbe/SonnetDB.FullTextProcessProbe.csproj` 纳入 `SonnetDB.slnx`；本地主 solution Release build、CI REST shard Release build 和聚焦测试均已通过。既有 `v5.1.0` tag 不移动，后续候选必须使用包含修复的新提交。

后续 main 验证中，Windows Core 的 Legacy 测试通过，LSM 测试因原 Build & Test job 的 60 分钟总上限在执行中被取消；该取消没有失败测试断言。工作流修复将总上限调到 90 分钟，保留单项步骤限制，后续候选需重新取得完整 CI 结果。

5.1.1 预检中，Management、Parity、Document soak、M19、M39、Publish、Connectors 和 Docker 均通过；Ecosystem Soak `ci` 唯一失败，根因是测试夹具使用 `WalReader.Open`/`WritePointRecord` 读取原子 Point WAL v2。修复改用 `WalReader.OpenAtomicPoints`/`AtomicPointRecord`，这是测试接线修复，不改变生产 WAL 格式；修复提交后必须重新生成所有同提交候选预检证据。

回滚沿用 5.0 合同：停止写入，保留并核验升级前备份和旧程序；若候选已写入新目录/新段，恢复匹配旧 reader 的升级前副本，不能只替换二进制。Server 实例级 MQ、身份配置和外部对象不由单库备份自动覆盖。
