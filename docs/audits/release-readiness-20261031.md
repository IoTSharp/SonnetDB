# SonnetDB 新版本发布评估（2026-10-31）

后续进展：用户已选择 5.0 主版本路线，EXISTS、包依赖和版本策略的修复及实际验证见 [2026-11-01 修复记录](release-repair-20261101.md)。本页保留原评估时点的事实。

结论：**当前 main 不适合直接发布正式版，状态为 NOT_READY。** 存在已复现的访问路径回归、实际打包失败及依赖包缺失；不是仅凭路线图未完成或 Issue 数量否决发布。完成下列修复后，可以准备范围明确的候选版。若按现有路线图宣称完整 4.5，则还须兑现其必选范围与业务验收。

评估基线：`3c40730a35140a24589e606310bcf1e2dae88ec3`，2026-10-29 合入木垒客户端恢复修复后的 main。2026-10-31 已从 `c194debd` fetch 并 fast-forward 到此提交；远端 main 独立回读一致。没有合并、删除或人为改写 parity-results。评估时间约 22:50—22:57，Asia/Shanghai。

机器可读证据：[release-readiness-20261031.json](release-readiness-20261031.json)。原始日志、TRX、发布核验与探针位于本机 `artifacts/release-review-20261031/`；这些被忽略的产物没有作为源码提交。

## 公开版本与本次判断范围

- GitHub 当前最新正式 Release 为 `v4.0.0`，发布时间为北京时间 **2026-09-27 02:02:26**；NuGet Core 最新稳定版也是 4.0.0，公开引擎预览为 `4.5.0-preview.ef266.5`。
- Workbench 最新独立预览为 `4.5.0-preview.1.12`，北京时间 **2026-10-12 00:27:58** 发布；它不代表引擎 4.5 已正式发布。
- 当前 main 的 Docker/Connectors push 工作流成功证明各自构建路径执行过；正式发布门禁仍要求同 commit、同版本的 dispatch 预检，不能用 push 绿灯代替。
- 本次执行 readiness 时以路线图版本 `4.5.0` 查询候选证据；本地探针使用未发布的 `4.5.0-review.20261031`。这些版本参数仅用于评估，没有确定发行版本或创建标签。

## 已确认的阻塞

| 优先级 | 已确认问题 | 对使用者的影响 | 放行条件 |
|---|---|---|---|
| P0 | EXISTS 访问路径回归；最新 CI Windows/Linux 各 7985 PASS、2 FAIL，本地该类 12 PASS、2 FAIL | 参数/关联 NULL 场景增加一次全表扫描；可能放大业务延迟与锁竞争 | 保留 NULL 三值语义和唯一索引保护，修复无谓扫描；原测试通过，最终候选完整 CI 全绿 |
| P0 | Core 实际 `dotnet pack` 返回 1，15 项 CP0001、2 项 CP0002 | 公开类型归属及分页属性签名变化，旧二进制消费者存在兼容风险；发布链目前直接失败 | 冻结版本和兼容合同，修复 4.x 兼容性，或正式评审主版本迁移与相应基线策略；保持真实兼容校验，不用抑制文件掩盖 |
| P0 | Core 包依赖 Protocol，但发布清单遗漏 Protocol；隔离消费者还原 NU1101 | 用户安装包时即失败，不能进入业务使用 | 将 Protocol 纳入同版本打包、库存、发布与 SDK bundle；干净缓存消费者安装及运行通过 |
| P0 | 当前候选十二类门禁仅 2 READY、1 失败、9 缺证 | 不能证明最终发行组合可安装、运行与恢复 | 在最终冻结 commit/version 补齐全部必需工作流、产物及原始报告，readiness 返回 READY |
| 发布必需 | 缺本次候选的真实旧库升级、备份恢复及回滚演练回执 | 新目录 floor 启用后不能简单换旧二进制降级 | 按支持范围验证 4.0 数据副本升级、重启与业务核对；从升级前备份恢复回旧版本，明确停机、备份及混用限制 |

### EXISTS 的证据与根因线索

CI run [40818645990](https://github.com/IoTSharp/SonnetDB/actions/runs/40818645990) 的 Windows Core 与 Ubuntu 同时失败于：

- `RelationalExistsAccessPathTests.Exists_ParameterizedEfShape_PreservesHitMissAndNullSemantics`，第 236 行。
- `RelationalExistsAccessPathTests.Exists_CorrelatedFirstMissThenHit_ProbesPerOuterRowWithoutMemoPollution`，第 265 行。

两处失败都是 `FullScanCount` 的期望 0 / 实际 1；位于其前的结果正确性断言通过。**现有证据没有证明查询返回了错误结果，也不能把失败说成纯 CI 抖动。** 本机重新构建后执行整个类，14 项中 12 PASS、2 FAIL、0 SKIP，复现相同断言。

前一个主干 `7255f010` 的 CI [40687730249](https://github.com/IoTSharp/SonnetDB/actions/runs/40687730249) 为 success；两提交之间未修改此测试文件。静态差异显示索引候选新增 `candidate.IsUnique && values.Contains(null)` 排除保护，这是合理的 IS NULL 正确性约束；推测普通 `= NULL` 与关联绑定后的 NULL 也因此失去点查而走扫描。后续应在完整谓词语义下安全早停，保留 IS NULL 的真实匹配与唯一索引不保存 NULL 的边界。此项根因仍需修复验证，不能只删除扫描断言。

### Protocol 的意义及交付断点

`SonnetDB.Protocol` 承载共享 Frame 信封、操作码及 Span 二进制读写，供引擎和远程客户端共用。`SonnetDB.Client` 只依赖 Protocol，因而能在不携带整个嵌入式引擎的情况下连接远程 Server；拆分的实际价值在这里。它不是独立运行的服务。

目前 `src/SonnetDB.Core/SonnetDB.Core.csproj` 与 `connectors/dotnet/SonnetDB.Client/SonnetDB.Client.csproj` 都引用 Protocol。实际生成的 Core nuspec 声明同版本 `SonnetDB.Protocol` 依赖，包内 lib 只有 Core.dll。

`eng/release.ps1` 的正式 NuGet 清单和 `eng/verify-release-artifacts.ps1` 的严格库存都是 8 包，遗漏 Protocol，也未包含新 Client；仅额外生成 Protocol 而不更新库存校验仍会失败。Protocol 是当前 Core 的必需依赖；Client 是否纳入这次正式发行须在支持矩阵中明确。

2026-10-31 查询 NuGet：Protocol/Client 索引均返回 404，Core 索引返回 200。仓库外临时消费者使用独占包缓存、候选源与 nuget.org，实际 restore 返回 **NU1101：找不到 SonnetDB.Protocol**。首次探针放在仓库内受中央版本管理影响，出现 NU1008；该夹具错误日志保留，已移到仓库外重新验证，不把 NU1008 计作产品缺陷。

### 兼容性与版本选择

Core 打包的 17 项兼容差异包括：`SpanReader`、`SpanWriter`、Frame 类型及操作码移出 Core；`PaginationSpec.Offset/Fetch` 从 `int/int?` 改成 `long/long?`。因此不建议将当前 main 作为普通 4.x 无感升级发布。

**建议保留该拆分时按下一主版本迁移评审，优先考虑 5.0；如继续 4.x，则恢复对 4.0 的兼容承诺。** 改版本号本身不能解决扫描回归、缺依赖或缺验收，当前项目的基线规则也不会因传入 5.0 自动放行。任何主版本基线调整须和迁移说明、旧消费者验证一起评审，不能以关闭检查代替。探针 nupkg 虽已产生，但 pack 校验失败，不能分发为合格包。

## 实际发布门禁

运行现有 `eng/verify-release-readiness.ps1`，精确 commit 下结果如下；原始报告已纳入 JSON 证据。

| 工作流 | 本次结果 |
|---|---|
| CI | FAIL：Windows Core、Ubuntu 测试失败；其余五个作业成功 |
| CodeQL | READY |
| Parity light/full | READY；候选原始证据验证通过，各 51 个场景 |
| Docs Pages | 无当前提交合格记录 |
| Management workbench smoke | 无当前提交合格记录 |
| Document Store Soak | 无当前提交合格记录 |
| Ecosystem Soak | 无当前提交合格记录 |
| M19 Capacity Evidence | 无当前提交合格记录 |
| M39 SQL Trigger Evidence | 无当前提交合格记录 |
| Publish 候选预检 | 无当前提交、候选版本的合格 dispatch |
| Connectors Release 候选预检 | 无合格 dispatch；push 成功不满足此项 |
| Docker Publish 候选预检 | 无合格 dispatch；push 成功不满足此项 |

缺证表示未取得所需候选证明，不代表这些功能全部没有实现。当前 CI 的三平台 AOT、Format、Windows rest 已成功，CodeQL 也成功，应保留这些正面事实。

**Parity 的旧失败结论已过时。** 最近九次 scheduled 为 success；严格 verifier 已验证 10 月 25—31 日连续七天真实 light/full 原始证据，7/7 READY。窗口覆盖多个主干提交，当前 SHA 自 10 月 29 日起有三天，不能说同一 SHA 已运行七天。七天观察自 9 月 27 日起是非阻断项，当前候选 light/full 原始证据仍为硬门禁，无需人为再等待七天。

## 按业务范围决定还需做什么

不应把全部研发计划都变成“永远不能发布”的理由；也不能把尚未兑现的完整 4.5 承诺静默改成已完成。

1. **范围明确的 Core / ADO / EF / Server 维护与迁移版本**：先解决三个已复现技术问题，再冻结支持的平台、模型、API 和客户端/Server 组合；用最终包验证旧库升级、订单式多表事务、唯一键/关联 EXISTS、TTL 维护、事务回执容量与异常重启。新版本既然合入现场恢复补丁，这些路径就是业务验收重点。
2. **按现有路线图宣称完整 4.5**：`ROADMAP.md` 的完成判定仍包含真实 AI 质量/成本、九模型跨端与治理、批流/编码、固定硬件及长期证据；`docs/design/m42-relational-hot-write-concurrency.md` 还将 H01—H07 标为 4.5 必选且 planned。完整 4.5 目前不能宣称完成。若选择较小发行，须显式修订范围和延期项，并通过评审；本次没有擅自改变这些承诺。
3. **特定高负载生产部署**：M19/M25/M42 固定目标硬件、恢复与长稳证据独立必需；hosted 缩规模绿灯只证明对应行为，不能承诺百万规模、P99 或最大吞吐。木垒局部现场通过也不能代替其他客户负载验收。
4. **安装器 / Studio 正式渠道**：交付这些渠道时，补干净 Windows/WebView2、安装、首次配置、升级、端口/生命周期及卸载保留数据的实际验证；只跑压缩包清单不能证明安装成功。
5. **新 SDK 与 Graph 等独立能力**：按实际语言/模型/操作矩阵逐项发布；codec 或一个 HTTP/2 路径成功不等于全语言九模型 SDK 完成。Graph 保持现有 Beta 与独立门禁，未验收的性能和生产承诺不写入正式宣传。

M48 日志平台是独立 R1，路线图明确不自动成为既有 4.5 必选。压缩增强、完整 M49、未列入本批的语言/平台和新 AI 旅程，可以作为后续发行，但须保持当前必选范围的明确处理。十个新 Dependabot PR 也不因尚未合并自动阻塞发行；应按相关安全与兼容事实决定，避免临发布前无必要扩大候选变更面。

## 推荐执行顺序与退出条件

1. 修复 EXISTS 回归，并保留原始失败、NULL/IS NULL/关联查询与现场唯一键场景的证据。
2. 冻结版本与支持范围；决定 4.x 兼容还是主版本迁移，完成 Protocol 必需包和拟发布 Client 的依赖闭包。
3. 运行真正打包、库存验证、仓库外空缓存消费者及匹配 Server，证明安装、查询、事务与取消/未知提交边界。
4. 用 4.0 真实业务数据副本完成升级、重开和备份回退；新 DDL 可建立永久 v19 目录 floor，不能将“换回旧程序”写成通用回滚方法。多表事务、对象数据、Server 实例 MQ 的备份边界分别说明。
5. 冻结最终候选提交，按实际版本完成十二类门禁及适用真实业务验收；`readiness=READY` 后才进入正式标签和渠道发布。后续源码有修改，重新取得对应候选证据。

目前可以继续做候选构建和修复；不建议立刻正式发包，也不建议靠更改版本名绕过这些缺口。

## 本次边界与交接

本次只拉取、检查、定向复现和记录；没有修改产品源码、提交、推送、派发工作流、创建标签、发包、部署或发 Issue 评论。使用了 Cordonix 的 tool_info、Git、原生执行、读取、搜索及编码检查；Store PowerShell 启动被 Cordonix 拒绝后，保存脚本并通过现有终端执行，凭据仅在进程内存中传递，没有写入报告或脚本。

未执行本地最终完整 solution restore/Format 或全套测试，不能将本地定向失败或远端局部 PASS 写成全仓验证。根 HANDOFF 在拉取后已存在 122 行严格 UTF-8 解码失败，本次只按原字节保留历史并追加当前交接，不修复或丢弃未知历史。文档编码问题需要后续单独修复，但不与已复现的产品/发包阻塞混为一谈。

本评估报告、JSON 和 HANDOFF 留在工作树待审阅；未创建提交。若后续提交这些文件，仍须先在最终树通过仓库规定的完整 restore 与 Format Check。
