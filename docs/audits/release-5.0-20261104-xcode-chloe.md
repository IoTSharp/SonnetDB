# XCode / Chloe 5.0 接入收尾核查（2026-11-04）

当前结论：已修复并验证两个独占 fork 的 5.0 接入候选；SonnetDB 最终发行提交及公开包仍待主发布任务冻结并另行复验。候选 PASS、fork 推送、上游合入和 ORM 包发布分别记录，不能据此放行整个 SonnetDB 发行。

## 候选身份与边界

- SonnetDB：候选十包来自 Actions `41348714535`，源码 `455bf3b53b1e56df0064d8f574b0595cbb0b609e`；未当作 NuGet.org 公开包。
- 匹配 NativeAOT Server SHA256：`b3a2991a7bbc4750c2d75f711e3151d5d7059f04b3bad853308e1481a8f22ca4`。
- XCode：基线 `af1d51fa686478688bc05db554ab04dc12e6080e`，独占分支 `codex/sonnetdb-5.0-compat-20261103`；已提交并普通推送 `f21d0c7537955b0bcd79f875b9d649546f762147`，创建独立后续 Draft PR [#103](https://github.com/NewLifeX/NewLife.XCode/pull/103)，未重用已经合并的 #101。
- Chloe：原 fork HEAD `bb7ec8735bd4511c35453eb009f70cc606795703`，分支 `feat/sonnetdb-provider`；已提交并普通推送 `742ea2acb9b0f108e441cd8fc503100a737f9b17`，更新现有 Draft PR [#435](https://github.com/shuxinqin/Chloe/pull/435)。该提交保留原 fork 与上游 `e52d633f3481f0679989cb2a031c850758d70445` 两个 parent。
- 本次交付清单为 67 个本任务 owned 文件（36 个适配/测试/文档加 31 个非英语词表）与 20 个 Chloe 上游导入文件，分别记录；未修改 SonnetDB 共享生产源或索引，未把 host props、NuGet.Config、缓存或 bin/obj 纳入 fork 交付。

## 实质变更与通用钩子

XCode 的 5.0 映射覆盖精确 Decimal、DATETIME2、DATETIMEOFFSET、DURATION、注释及长度/精度元数据、Int64 分页、有界删除、Serializable 和原生 REPLACE。新 DateTime 默认映射保留 100ns ticks；Local 参数及字面量保留 CLR 钟面 ticks，UTC/Unspecified 不改；显式 legacy DATETIME 仍为毫秒合同。

普通 DbSession 的同步/异步读取仅在 `Database.Type == SonnetDB` 时保留 NULL，其它数据库继续原 `DbTable.Read` / `ReadAsync`。迁移另用独立 internal 查询和缓存操作名，未设置共享会话模式。

真实消费者确认旧分页补丁遗漏数字主键默认 `IdExtracter`，SQLite 的 NULL 会迁移成 0。现在仅为涉及 SonnetDB 的默认、精确内置 ID/时间/分页抽取器设置逐实例 internal 查询委托，保留三个原有抽取算法。外部非空回调和派生抽取器继续运行其自己的来源与 `Fetch`；非 SonnetDB 之间的原行为保持。查询委托检查取消、100 万次查询和一小时边界。取消已经实际验证；百万次/一小时上限作源码门禁核对，未以长时间穷举冒充运行验收。

ModelHelper 仅对 SonnetDB 表/列保留物理名称原始大小写，其它模型的 `ignoreNameCase` 合同由实际回归确认。所有 owned 文件已恢复到各自基线 EOL/BOM，原始改动与格式还原回执保留。

XCode 的完整本地化 CI 检查另检出已删除的 12 条 4.0 guard 消息仍残留词表，以及三条当前英文消息缺失词条。本次仅在 31 个非英语词表移除这 12 条旧键，添加三条当前隔离级别/视图元数据消息；复用各语言已有视图/字段术语和占位符。英语继续按现有设计使用源文回退，不增加冗余词条。相同 CI 命令已实际通过 32 种语言、370 个基准词条，README 语言链接检查也通过。该资源更新之后，全部业务 C# 源码与 50/101 的已验冻结完全相同。

Chloe 集成上游 5.57 及 #434 查询过滤器缓存修复，把旧的两条缺陷现状断言改为 SonnetDB / SQLite 正向回归。保留本 PR 所需核心事务失败回滚清理；provider、legacy nuspec 与 CI 的 core/provider 最低源码版本同步 5.57。已经发布的上游 core 不会自动获得 PR 中额外的回滚修复，后续须协调包含该修复的新核心和 provider 版本共同发布。

## 当前验证

| 验证 | 结果 | 范围 |
| --- | --- | --- |
| XCode 完整接入套件，`suite-run-09` | 50/50，0 FAIL、0 SKIP | 原有 39 项加 11 项回归，含真实 REST / 显式 Frame HTTP/2 |
| Chloe 完整套件，`suite-run-09` | 101/101 × 3，0 FAIL、0 SKIP | Embedded / REST / Frame HTTP/2；两条 #434 为修复后正向回归 |
| 独立进程持久化旅程，`durable-run-06` | 每协议 exercise 36 + reopen 23，全部 PASS | 六个独立消费者，四个 NativeAOT Server 均回收，H2 数据请求无 HTTP/1 回退 |
| XCode 通用路径独立消费者，`hooks-run-04` | 18/18 PASS | 双向 NULL、外部精确/派生抽取器、其它库默认及 XML 名称 |
| 新增迁移/时间合同定向验证 | 11/11 PASS | ID/时间/分页两方向均断言真实抽取器类型、NULL、取消和三种 DateTime Kind |
| Chloe 五目标 build / pack | PASS，0 errors | 202 个仓库警告；仅本地包，不是发布证据 |
| XCode 最终多目标 build | PASS，0 errors / 847 个仓库警告 | 最后 Local 时间格式修复后的最终工作树实际重建 |
| 最终 diff 与英文消息门禁 | PASS | 两个 fork 的最终 `git diff --check HEAD` 与 XCode 英文运行时消息检查均退出 0 |
| XCode 本地化及 README 语言检查 | PASS | 32 个语言，370 个基准词条；README 11 个语种链接一致 |
| 最终公开包与最终 Server | NOT_RUN | 等主发布任务提供冻结提交/匹配产物后使用独立缓存复验 |

原 `suite-run-01` 至后续各轮 NO_TESTS / 初始 7 FAIL 证据保留。`hooks-run-03` 是数字主键 NULL 的真实失败；01/02 的探针自身 SQL 形状错误保留且不算产品缺陷。`suite-run-08` 的时间抽取反向 1 FAIL 已修复：该测试新增了反向时间索引和抽取器类型断言，揭出 Local `+08:00` 上界与保存的 UTC Kind 钟面值在字面量转换后形成空窗口。不得用旧 07 的 47 PASS 覆盖这一真实失败。

## 可审查产物及后续

本地证据根为 `artifacts/release-5.0-20261103-xcode-chloe/`。最新清单 `frozen-fork-source-manifest-20261104-v2.json` SHA256 为 `1160ac27ac4a2477d2fe750ebb6c3baeb5821862a937ffe670cd9796ee2e9c43`；XCode 组合 patch 为 `e1f25f005bfe3b3bb55fb676f7d99bdd5e6b682f19ef93fe9e16bea7e2ee64d0`，Chloe 组合 patch 为 `f30f199e6cddd8b8579955f61b105e5eb9b4411c535fae1df6a2bade055acd07`。v2 只为先前业务冻结清单追加上述词表修复，明确 supersede 原 `540e6ce1...`；清单保存每文件原始 SHA256、预期 Git blob、上游导入来源、TRX 和独立重开回执。

## Fork 交付回读

主任务独立复核并授权交付后，提交前再次核对两个完整索引，87 个 blob 逐一与 v2 清单一致，未追加源文件。普通推送后，本地 HEAD、tracking ref、`ls-remote` 和 PR head 四处一致；两个工作树均干净。两个 PR 的正文与本地冻结预稿逐字回读一致，状态均为 OPEN / Draft / MERGEABLE；MERGEABLE 只证明 Git 可合并，不代表远端 CI 已通过。

交付回执为 `fork-delivery-20261104.json`，PR 独立回读为 `fork-pr-verification-20261104.json`。后者 SHA256 为 `31eb5dc29e36a3766d53a539d9b679fe44a20a9b8f77f1b93eae45afc85ad324`。正文已明确 Local DateTime 的 clock-ticks 合同、DateTimeOffset 完整 UTC 时刻及零返回偏移，以及 Chloe 新 Core + Provider 的协调发布要求。未 force push、未合并上游 PR、未发布 ORM 包、未发 Issue 评论。所有本任务有界执行的 owned 子进程树均在相应 Cordonix 回执中确认清理。

初次远端 CI 回读：XCode #103 的 localization 检查成功，完整 solution build 失败。直接读取已完成 job 的原始日志确认原因是 `NU1102: Unable to find package SonnetDB with version (>= 5.0.0)`，NuGet.org 仍只提供最近 `4.5.0-preview.ef266.5`，与最终 5.0 尚未公开的边界一致。该证据在 `xcode-ci-build-log-direct-20261104.json`；其它初始失败/运行中作业保留各自状态，未用这一个根因替代逐项诊断。Chloe #435 当次未返回 check rollup，不记远端 CI PASS。XCode publish-beta 的 PR 路径明确跳过 Publish 步骤，没有执行 ORM 发布。

下一顺序：主发布任务冻结并发布最终 SonnetDB 5.0 包及匹配 Server → 独立缓存重编译和完整复验 → 主任务根据真实结果将两个 PR 转为 Ready。当前保留 Draft，不把候选 455 验证或已推 fork 宣称为最终公开安装组合验收。主任务统一更新 SonnetDB 根 HANDOFF 和发布提交。
