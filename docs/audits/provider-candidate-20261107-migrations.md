# 共同 Provider 候选：#392 库存迁移与恢复验收

2026-11-07（Asia/Shanghai）。本专题由 Worker A 准备并独立核读本地验收收据，主监督统一冻结源码、候选、四包、匹配 Server 与原始制品身份；不复用旧 `0d687bd9` 候选的 PASS。准备基线为 `537dc9bb9ea5bc701d722c4663593d10d26e0467`；本次实际本地候选为 `5.1.0-rc.20261107.1`，source=`eb5c0e5f938372109b473db2194670761dc26a96`。本专题后续文档提交不会改变该已冻结制品的 source 或摘要。

## 业务与消费者

`eng/probes/issue392-migration-app/InventoryMigrationApp.csproj` 仅引用精确版本的 `SonnetDB.EntityFrameworkCore` NuGet 包。统一执行器将固定输入复制到仓库外，以独占 `NuGet.Config`、packages 和 http-cache 还原；本地候选 feed、CI 候选及 NuGet.org-only 公开消费者分别记账。旧默认 5.0.0 是历史负控入口，不是本次候选的选择或覆盖目标。

初始模型含自引用分类及产品外键；升级同时增加列、索引、CHECK、发货表和种子 DML。最后一条迁移历史 INSERT 被真实 CHECK 拒绝时，应用核对完整旧状态，移除拒绝条件并重试，继续执行实际 EF 库存查询/保存、重复迁移、Down、重复 Down、Up。

本次消费者增强显式持有连接，通过 EF `DbConnectionInterceptor` 计数同步/异步打开与关闭。历史拒绝后和重试成功后均要求同一 `DbConnection` 对象保持 `Open`，打开一次且从未关闭；当前 `SndbConnection` 不触发标准 `StateChange`，不用该事件的零计数或同一个上下文推定连接连续性。原有所有数据、列、表、索引、外键、自引用、CHECK 和历史断言保留。每次动作有 120 秒取消期限；元数据最多读取 32 行。

## 本地候选的实际核读

共同候选 manifest 为 `D:/source/SonnetDB/artifacts/provider-next-phase-20261107/candidate-manifest.json`，冻结时 `gitHead` 等于上述 source，`dirtyCount=0`。四包版本、nuspec repository commit、包/DLL SHA 和库存消费者记录一致；matching Server 为 `win-x64`、framework-dependent JIT，不是 NativeAOT。Server 原始归档 SHA256 为 `69FE3CC886FF9DF23B92CF4A5A8C0F798BA77D206259181EC8619FB995EBEF12`，manifest 保留逐文件摘要及 Core/Protocol 与包的配对。

真实消费者根为 `C:/Users/mysti/.codex/provider-consumers-20261107-01a1b1e2-r2/`。`result.json` 记录 `LOCAL_CANDIDATE_NOT_PUBLIC`，主监督在 2026-11-07 01:51:46–01:54:29（Asia/Shanghai）完成三线共 69 个步骤，全部 PASS。其中 #392 为 34 个步骤：2 个独立还原/构建、32 个应用运行。库存消费者独占 `migrations/packages`、`migrations/http-cache`、`migrations/NuGet.Config`；四个 SonnetDB 包均从冻结的本地 feed 取得。运行 Provider/ADO 的程序集信息包含候选版本与同一 source，信息版本的 source 后缀被 SDK 重复追加一次；实际身份按精确包版本、nuspec commit 与 DLL SHA 核对，不将重复后缀改写成另一候选。

Worker A 独立读取总收据、候选 manifest、三协议的 roundtrip、after-ACK stdout/marker、强杀后 Down/Up/最终 verify，以及两次 Server 重开后的 verify stdout，并检索两代 Server 请求日志。三份 roundtrip 均出现真实 `CK_BlockShipping` 拒绝、完整旧态核对、重试新态核对和 `SAME_CONNECTION history-rejection/rollback/retry | opens=1 | closes=0 | state=Open`，随后 Down/Up 和 `PASS roundtrip`。没有删除断言或把预期约束拒绝当成未执行。

| 协议 | roundtrip 进程 | after-ACK 强杀进程 | 读回结果 |
| --- | --- | --- | --- |
| embedded | PID 24156 | PID 3124，创建于 `2026-11-06T17:52:55.2163975Z` | 同连接、业务迁移/回退 PASS；marker 与进程身份一致；强杀后 Down/Up/最终独立 verify PASS。 |
| REST | PID 20080 | PID 9244，创建于 `2026-11-06T17:53:34.0218428Z` | 同连接、业务迁移/回退 PASS；marker 与进程身份一致；强杀后 Down/Up/最终独立 verify PASS。 |
| native Frame HTTP/2 | PID 25772 | PID 52080，创建于 `2026-11-06T17:54:08.9127837Z` | 同连接、业务迁移/回退 PASS；marker 与进程身份一致；强杀后 Down/Up/最终独立 verify PASS。 |

三次 after-ACK 消费者强杀收据均为 `killedAfterAck=true`、退出码 `-1`、`rootExited=true`；这是预期强制终止，不改写为自然退出 0。总收据保留每次强杀后的两次独立 `verify-new` 及其不同 PID、退出 0。after-ACK stdout 先完成 `VERIFIED new` 再输出 `READY_FOR_EXTERNAL_KILL after-ack`，ready 文件中的 PID/创建时间与父进程记录逐一一致。

匹配测试 Server 从第一代 PID 2640 关闭并以第二代 PID 14680 重开，父监督 PID 为 6964；两代记录均 `rootExited=true`、强制退出 `-1`。第二代就绪后，REST PID 43948 和 Frame PID 17640 分别完成 `VERIFIED new` / `PASS verify-new`。这是本地测试 Server 在相同数据目录上的进程重开，不能外推为生产或物理掉电。`server-1.stdout.log` 第 35 行及 `server-2.stdout.log` 第 48 行有库存库 REST 请求；第一代第 1919 行及第二代第 160 行有 `HTTP/2 POST .../v1/frame` 请求，均同时读到后续 200 终态。协议证据来自 Server 请求日志，不只来自应用的 `REQUESTED_PROTOCOL`。

## CI 候选四包与 NativeAOT Server 的实际消费

Publish [run 42265079795](https://github.com/IoTSharp/SonnetDB/actions/runs/42265079795)、attempt 1 已完成 SUCCESS，准确 head 仍为 `eb5c0e5f938372109b473db2194670761dc26a96`，版本仍为 `5.1.0-rc.20261107.1`。`D:/source/SonnetDB/artifacts/provider-next-phase-20261107/ci-candidate-manifest.json` 记录 workflow、run/attempt、精确 source、实际 CI 包及 Server 身份，边界为 `CI_CANDIDATE_NOT_PUBLISHED`。该 CI 制品和上节本地构建的 SHA 分开保存，不能因版本/source 相同而交换摘要。

CI 库存消费者使用 `C:/Users/mysti/.codex/provider-consumers-20261107-01a1b1e2-ci/migrations/` 的新项目、独占 packages/http-cache/NuGet.Config。独立核读 `.nupkg.metadata` 确认 Provider 实际还原源为 `D:/source/SonnetDB/artifacts/provider-next-phase-20261107/ci-packages`；总收据中的 Protocol/Core/ADO/Provider 四包版本、repository commit、nupkg/DLL SHA 均与 CI manifest 一致。本轮消费的是实际 CI 四包，不是把本地 feed 重新标成 CI，也不代表其余六包全部取得消费者验收。

实际测试 Server 是 `ci-server-r2/SonnetDB.exe`，CI manifest 标为 `win-x64` NativeAOT，产品版本为 `5.1.0-rc.20261107.1+eb5c0e5f938372109b473db2194670761dc26a96`。Worker A 对实际被执行文件独立计算完整 SHA256：`363A9795A57EB574334BBEBB7E50F8C309FB8985A1716648490C0E23E192364A`，91,418,112 字节，与 manifest 一致且 `file_changed=false`。完整 CI win-x64 Server 原始归档 SHA256 为 `041B4B36A6CE01C87457CF1249B2572FE55CCB7C314CC0D90DCA65DEF5875E1A`。Server 进程收据的 `program` 直接指向该 exe，未使用上一轮的 `dotnet SonnetDB.dll`。

CI 消费者总收据为 `C:/Users/mysti/.codex/provider-consumers-20261107-01a1b1e2-ci/result.json`：2026-11-07 02:19:06–02:21:12（Asia/Shanghai），三线共 69 步 PASS，其中 #392 的 2 个还原/构建与 32 个应用运行共 34 步全 PASS。Worker A 独立核读三协议 roundtrip stdout，均含历史 CHECK 真拒绝、完整旧态/新态、`SAME_CONNECTION ... opens=1 | closes=0 | state=Open`、Down/Up 及 `PASS roundtrip`；独立核读 after-ACK stdout 和 ready 文件，确认下表身份与父监督收据一致。

| 协议 | CI roundtrip PID | CI after-ACK 强杀 PID / 创建时间 | 结果 |
| --- | --- | --- | --- |
| embedded | 41084 | 3956 / `2026-11-06T18:19:58.2277029Z` | 同连接迁移旅程 PASS；marker 身份匹配，预期强杀 `-1`；后续两次 verify/Down/Up/最终 verify 收据全 PASS。 |
| REST | 5884 | 55264 / `2026-11-06T18:20:29.5728976Z` | 同连接迁移旅程 PASS；marker 身份匹配，预期强杀 `-1`；后续两次 verify/Down/Up/最终 verify 收据全 PASS。 |
| native Frame HTTP/2 | 48440 | 43868 / `2026-11-06T18:20:56.3111709Z` | 同连接迁移旅程 PASS；marker 身份匹配，预期强杀 `-1`；后续两次 verify/Down/Up/最终 verify 收据全 PASS。 |

三次强杀均 `killedAfterAck=true`、`rootExited=true`；stdout 先 `VERIFIED new` 再 `READY_FOR_EXTERNAL_KILL after-ack`。本轮存在 OS 重用 PID 的实际记录，进程身份以 PID 加创建时间/命令/父链共同判断，不能按 PID 单独认定同一进程。NativeAOT 测试 Server 为两代 PID 49952 → 57788，父监督 PID 18304，两代结束均记录预期强制退出 `-1` 和 `rootExited=true`。第二代启动后，独立读取的 REST PID 59528 和 Frame PID 44708 stdout 均为 `VERIFIED new` / `PASS verify-new`；总收据的线协议检查为 PASS，本次 Worker A 未重复检索 CI Server 全部请求日志。

这份 NativeAOT 证据覆盖匹配测试 Server 的运行和重开，库存 EF 应用仍是托管 JIT 包消费者，不是 EF Provider/compiled model 的 NativeAOT 验收，不能据此关闭 #396。CI 候选四包真实消费也不等于 NuGet.org-only 公开消费；#392 的公开新包、物理掉电和生产恢复边界继续保留。

## 必须独立记录的门禁

| 门禁 | 当前状态 | 必须保留的收据与限制 |
| --- | --- | --- |
| 最终四包与 source 身份 | PASS，本地候选 | 共同 manifest 与实际消费收据一致；独占缓存和运行 DLL 配对，无 ProjectReference；四包与 Server 原始摘要保留。 |
| 嵌入式业务 roundtrip | PASS，本地候选 | 历史拒绝、完整旧态、同连接重试、EF 库存保存、重复迁移/Down/Up；两个新 OS 进程 `verify-new` 退出 0。 |
| REST 业务 roundtrip | PASS，本地候选 | 相同业务旅程、显式 REST、独立进程会话重开及库存库真实请求日志。 |
| 原生 Frame 业务 roundtrip | PASS，本地候选 | 相同业务旅程、显式 frame-http2、独立进程会话重开及 `/v1/frame`/HTTP2 请求日志。 |
| 包级 after-ACK 强杀 | PASS，本地候选，三协议各一次 | 实际 marker/PID/创建时间核对、退出 `-1`、两次独立 verify、Down/Up/最终 verify；只覆盖已确认提交后的应用强杀。 |
| 精确提交阶段源码强杀 | PASS，Ubuntu/Windows hosted CI | 精确source的手动CI `42268465828` 已下载两平台原始 `ef-migration-crash.trx`：各10/10，0失败/未执行；包含七个提交阶段、两次恢复再杀和undo归属保护。源码/internal钩子与包级after-ACK分开。 |
| 远程引擎重开 | PASS，本地匹配 Server | Server 2640 → 14680 后，REST/Frame 均独立 `verify-new` 退出 0；不以先前的客户端会话重开替代。 |
| 独立 CI 候选四包/NativeAOT Server | PASS，CI 候选 | Publish `42265079795` attempt 1 SUCCESS；实际四包独占新缓存、win-x64 NativeAOT exe 指纹及 34 个库存步骤 PASS，包含三协议/after-ACK/Server 重开；不等于公开包或 EF NativeAOT。 |
| NuGet.org-only 新公开包 | NOT_RUN | 新公共包发布后独占空缓存、无 CandidateFeed 的同业务完整复验；本轮正式发行延期。 |
| 物理掉电、生产恢复 | NOT_RUN | 本轮无相关环境或执行；进程强杀不外推这些层级。 |

监督侧专题证据归属为 `D:/source/SonnetDB/artifacts/provider-next-phase-20261107/migrations/`；本轮真实原始收据和日志在上述仓库外消费者根，不能因归档路径不同写成未执行。关键入口为 `result.json`、`migrations-<protocol>-0-roundtrip.stdout.log`、`kill-<protocol>-after-ack.stdout.log`、`migrations/<protocol>-after-ack.ready`、`kill-<protocol>-2-downgrade.stdout.log`、`kill-<protocol>-3-upgrade.stdout.log`、`kill-<protocol>-4-verify-new.stdout.log`、`migrations-{rest,frame-http2}-server-reopen.stdout.log` 和 `server-{1,2}.stdout.log`。

Worker A 仅复核证据并更新本专题，不启动测试 Server、不运行重负载、不创建常驻进程；主监督持共享独占锁串行执行。上述 PID/退出/回收边界来自执行器收据；不将两个根进程退出扩大成全机无孤儿进程。原始失败轮次、精确提交阶段源码强杀、CI、公开包与实际环境各自保留独立结果。

## 支持、拒绝和旧目录边界

受支持关系 DDL/DML 且没有 transaction-suppressed 命令的迁移组共享原子边界；序列、schema、显式事务外 SQL 保持独立提交组，完整操作矩阵见 [#392 原审计](issue-392-20261105.md)。重复 `Migrate` 读取历史与通用 idempotent SQL script 是两项合同，后者仍明确拒绝。Down 的固定库存回补仅属于此受控样例，不适用于升级后已有新业务写入的实际库存库。

旧 5.0.0 可能在历史写入失败前已经提交部分 DDL/DML。新候选能原子执行新迁移，不等于能够自动修复旧部分迁移；旧库须独立对账历史、业务、schema 和原始失败。不得删除 journal、手改历史/版本或盲目重放迁移来宣称恢复。升级前保留完整备份、应用及 Server 版本；Down 不等于引擎降级，目录 floor 与段格式限制继续适用。

公开包或实际环境门禁未满足时，#392 保持 OPEN。本地与CI候选分别有本次精确制品的PASS；精确提交阶段源码强杀已有上述Ubuntu/Windows原始TRX。公开包、EF NativeAOT、物理掉电或生产恢复不因候选通过而改变。
