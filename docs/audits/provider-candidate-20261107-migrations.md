# 共同 Provider 候选：#392 库存迁移与恢复验收

2026-11-07（Asia/Shanghai）。本专题由 Worker A 准备，主监督统一冻结源码、候选、四包、匹配 Server 与原始制品身份；不复用旧 `0d687bd9` 候选的 PASS。准备基线为 `537dc9bb9ea5bc701d722c4663593d10d26e0467`；包含本次消费者增量的最终 source、精确版本与制品摘要以共同候选 manifest 为准。

## 业务与消费者

`eng/probes/issue392-migration-app/InventoryMigrationApp.csproj` 仅引用精确版本的 `SonnetDB.EntityFrameworkCore` NuGet 包。统一执行器将固定输入复制到仓库外，以独占 `NuGet.Config`、packages 和 http-cache 还原；本地候选 feed、CI 候选及 NuGet.org-only 公开消费者分别记账。旧默认 5.0.0 是历史负控入口，不是本次候选的选择或覆盖目标。

初始模型含自引用分类及产品外键；升级同时增加列、索引、CHECK、发货表和种子 DML。最后一条迁移历史 INSERT 被真实 CHECK 拒绝时，应用核对完整旧状态，移除拒绝条件并重试，继续执行实际 EF 库存查询/保存、重复迁移、Down、重复 Down、Up。

本次消费者增强显式持有连接，通过 EF `DbConnectionInterceptor` 计数同步/异步打开与关闭。历史拒绝后和重试成功后均要求同一 `DbConnection` 对象保持 `Open`，打开一次且从未关闭；当前 `SndbConnection` 不触发标准 `StateChange`，不用该事件的零计数或同一个上下文推定连接连续性。原有所有数据、列、表、索引、外键、自引用、CHECK 和历史断言保留。每次动作有 120 秒取消期限；元数据最多读取 32 行。

## 必须独立记录的门禁

| 门禁 | 当前准备状态 | 必须保留的收据与限制 |
| --- | --- | --- |
| 最终四包与 source 身份 | NOT_RUN | 主监督 manifest、nuspec repository commit、nupkg/DLL SHA、实际缓存来源及运行 DLL；不能使用 ProjectReference。 |
| 嵌入式业务 roundtrip | NOT_RUN | 完整命令/退出码、`EXPECTED_FAILURE`、`SAME_CONNECTION`、旧/新状态核对，以及两个新 OS 进程的 `verify-new`。 |
| REST 业务 roundtrip | NOT_RUN | 上述业务收据、显式 `Protocol=rest` 及匹配 Server 请求记录；仅应用配置标记不足以证明线协议。 |
| 原生 Frame 业务 roundtrip | NOT_RUN | 上述业务收据、显式 `Protocol=frame-http2`、`/v1/frame`/HTTP2 请求记录；不能用自动探测或 REST 结果替代。 |
| 包级 after-ACK 强杀 | NOT_RUN | `initialize` → `upgrade-await-kill` → 确认 new-only ready marker、PID/创建时间/命令/父链 → 强杀退出码 → 两次独立 `verify-new` → Down/Up/再次 verify。仅覆盖已确认提交后的应用强杀。 |
| 精确提交阶段源码强杀 | NOT_RUN | `EfMigrationCrashTests` 的七个提交阶段和两次恢复中再次强杀，以及 undo 归属保护；引用源码/internal 钩子，与包级 after-ACK 证据分开。 |
| 远程引擎重开 | NOT_RUN | 由唯一 Server 所有者关闭并重启同数据目录的匹配测试 Server 后核对；两个客户端新进程只能证明会话重开。 |
| NuGet.org-only 新公开包 | NOT_RUN | 新公共包发布后独占空缓存、无 CandidateFeed 的同业务完整复验；本轮正式发行延期。 |
| 物理掉电、生产恢复 | NOT_RUN | 本轮无相关环境或执行；进程强杀不外推这些层级。 |

原始证据归属为 `D:/source/SonnetDB/artifacts/provider-next-phase-20261107/migrations/`。Worker A 不启动测试 Server、不运行重负载、不创建常驻进程；主监督持共享独占锁串行执行，负责精确候选及完成状态更新。

## 支持、拒绝和旧目录边界

受支持关系 DDL/DML 且没有 transaction-suppressed 命令的迁移组共享原子边界；序列、schema、显式事务外 SQL 保持独立提交组，完整操作矩阵见 [#392 原审计](issue-392-20261105.md)。重复 `Migrate` 读取历史与通用 idempotent SQL script 是两项合同，后者仍明确拒绝。Down 的固定库存回补仅属于此受控样例，不适用于升级后已有新业务写入的实际库存库。

旧 5.0.0 可能在历史写入失败前已经提交部分 DDL/DML。新候选能原子执行新迁移，不等于能够自动修复旧部分迁移；旧库须独立对账历史、业务、schema 和原始失败。不得删除 journal、手改历史/版本或盲目重放迁移来宣称恢复。升级前保留完整备份、应用及 Server 版本；Down 不等于引擎降级，目录 floor 与段格式限制继续适用。

公开包或实际环境门禁未满足时，#392 保持 OPEN。此文的 NOT_RUN 不因历史 CI 或其它候选通过而改变。
