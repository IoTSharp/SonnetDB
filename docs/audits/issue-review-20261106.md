# PR #405 与 open issues 核查（2026-11-06）

核查基线：GitHub origin/main `2a2f77c762badcad2a16dc2cad2c8e9abd09eebe`。本轮已读根 HANDOFF、AGENTS、当前工作树/远端、#405 全部 review/comment，以及当时 12 个 open issues 的完整描述与评论。时间按 Asia/Shanghai。主目录既有文档、发布和人物资产 dirty 保留；三个实现任务使用独立托管工作树。

## Copilot 建议

#405 实际只有一条 Copilot 问题：[discussion_r4479778545](https://github.com/IoTSharp/SonnetDB/pull/405#discussion_r4479778545)。

**结论：建议正确，属于查询正确性缺陷，必须采纳。** `Int64Rle` 是独立值编码，`QueryEngine.CanAggregateRawBlock` 和 `CanFuseDeltaTimestampInline` 只排除 `DeltaValue`，导致新压缩值进入固定 8 字节读取。整块元数据聚合、MemTable 查询及 count 专路可能掩盖问题；仅做 codec round-trip、全范围查询或压缩率测量不足以覆盖。

触发条件是显式 `ValueEncoding=DeltaValue` 后实际选用了更小的 Int64Rle 并完成 Flush；默认 raw 值编码不会写出 RLE，不能扩大表述为全部默认写入/查询均受影响。

独立复现新增 14 项实际 Flush / 重开测试，旧代码完整结果 **14 FAIL / 0 SKIP**，涵盖 raw/delta-of-delta 时间戳、SIMD 开关、局部区间、跨桶、共享多聚合和扩展百分位 accumulator。第一次因输出上限中断的执行与之后完整 TRX 分别留存，没有记作通过。

新缺陷 [#407](https://github.com/IoTSharp/SonnetDB/issues/407) 跟踪最小修复：两个入口排除 RLE，沿用已有 BlockDecoder。段格式、codec 与压缩选择合同不变。修复后回归与 PR/CI 状态在本报告收口时补齐。

修复后扩大 Query/Int64 存储回归 **472/472 PASS**。本机完整 Core 的900秒预算到界，未取得完成TRX，状态为 **TIMED_OUT**；执行器确认7个剩余子进程完整回收且0残留。该结果不计完整Core通过，后续以独立PR精确head的远端完整CI补验。

独立审阅后追加2项极小静默错值回归：4点 `[5,5,9,9]` 写为18字节RLE，单查第二点。旧代码 raw/delta 时间戳均出现 **Expected 5 / Actual 2306**（2 FAIL）；修复后最终新增回归 **16/16 PASS**。原14项红灯、追加2项红灯、扩大472项绿灯与最终16项绿灯不相加为不同测试总数。

## Issue 判断及下一步

| Issue | 核实结果 | 当前处理/继续顺序 | 可否关闭 |
| --- | --- | --- | --- |
| [#230](https://github.com/IoTSharp/SonnetDB/issues/230) | SonnetDB 5.0 已交付；FreeSql #2291 实时仍 OPEN，Provider NuGet 官方索引实际404 | 已回复实时依据；上游合入与公开 Core/Provider 后，独占空缓存复跑三协议/重开与身份核验 | 否，公开组合未验收 |
| [#392](https://github.com/IoTSharp/SonnetDB/issues/392) | main 的 EndDdlStatement 已保留事务，序列/schema 独立；已有迁移原子性源码切片 | 保留已完成项；继续新公开 Provider、实际应用迁移与剩余中断恢复证据 | 否 |
| [#394](https://github.com/IoTSharp/SonnetDB/issues/394) | OwnsMany 跟踪更新已实现；EF10 嵌套 ordinal 变化后仍需 Clear/重载 | 不重做已交付更新；应用生命周期/公开包验收与上游 identifying-key 限制分别处理 | 否 |
| [#395](https://github.com/IoTSharp/SonnetDB/issues/395) | 固定标量 ExecuteUpdate 已有；集合下标此前被整体拒绝 | 独立 worker 实现固定非负下标的集合元素标量原子更新，保留动态/无效索引拒绝 | 否，母项还有子树/类型/公开包范围 |
| [#396](https://github.com/IoTSharp/SonnetDB/issues/396) | Provider 明确 IsAotCompatible=false；官方 NativeAOT/预编译生成仍有实验性边界 | 已回复具体最小应用、compiled model/预编译、实际 publish 与 CRUD/重开顺序，禁止靠切开关或 suppression 过关 | 否，实际 Provider AOT 未验 |
| [#397](https://github.com/IoTSharp/SonnetDB/issues/397) | Protocol/Client 拆分与 SQL Execute 已有，九模型 typed SDK 未完整 | 独立 worker 按 M49-C03 实现 .NET typed KV Get/Put/单页Scan，真实纯 HTTP/2 Frame 验证 | 否，九模型/语言矩阵未完整 |
| [#398](https://github.com/IoTSharp/SonnetDB/issues/398) | 安装包/宿主合同已实现，缺干净系统安装升级卸载实物 | 既有 WorkBuddy 执行表继续使用；先固定 OS/旧新包身份，再真实安装/升级/失败回退/卸载/重装与数据核验 | 否，构建检查不替代安装 |
| [#399](https://github.com/IoTSharp/SonnetDB/issues/399) | 固定硬件/阈值/长稳证据未闭环 | 先冻结目标硬件/数据/确认语义/阈值/维护窗口，再原矩阵测量；不在普通桌面擅跑高负载 | 否 |
| [#401](https://github.com/IoTSharp/SonnetDB/issues/401) | measurement 仍为毫秒；M50 尚未冻结 T01 实际 API/版本/矩阵 | 已回复 T01→T02→T03～T06→T07～T11 的具体次序；完整嵌入式闭环前不开放高精度入口 | 否 |
| [#402](https://github.com/IoTSharp/SonnetDB/issues/402) | 13+69 源码合同已合入，恢复 workflow/可信 attempt/双 verifier 未接线 | 先取得原始 tag-push 可信 manifest/attempt 记录，再只读适配与双政策 verifier；实际预发布恢复/公开读回单独授权执行 | 否 |
| [#403](https://github.com/IoTSharp/SonnetDB/issues/403) | WorkBuddy0.2.0/43工具说明已在main；正式HTTPS/真实宿主/平台送审未验 | 复用原 W02/W03/W04/W07/W08/W12～W17 执行交接，逐项回填source/包/宿主/平台回执 | 否 |
| [#404](https://github.com/IoTSharp/SonnetDB/issues/404) | 上游 XCode #103 实时 CLOSED/未合入；维护者架构拒绝意见仍有效 | 外部 Provider 优先已有 OnFill virtual/override；确需通用架构改动单独PR，新head重跑原50项与公开组合 | 否 |

本轮未将母 issue 的局部实现或历史候选测试等同于完整交付，也未关闭任何仍缺明确验收条件的原有 issue。#407 的处理以独立修复 PR、精确 CI 和实际合入状态收口。

### #407 已闭环（2026-11-06 11:11，Asia/Shanghai）

#408全部适用检查成功后，已正常合入main `1420b1b14278216f396943213129c2c39958dd42`；#407于11:11:38自动关闭，issue状态和完整完成勾选均已独立读回。原#405评论已[回复具体证据](https://github.com/IoTSharp/SonnetDB/pull/405#discussion_r4488646234)，review thread `PRRT_kwDOSGOGbM60Clur` 已resolve并独立查询`isResolved=true`。

PR head `9ad35c12`的CI、Windows/Ubuntu完整测试、三平台AOT、Format、CodeQL及连接器构建全部成功；连接器资产上传属于PR预期SKIPPED，不算正式资产已发布。原12个母issue仍各有未完成条件，不因新缺陷关闭而批量关闭。

#409/#410在main前移后仅因前置HANDOFF记录出现合并冲突，已分别完成唯一一次正常main整合、restore/Format和新head CI：#409 head `c8fc1247`→main merge `4114164a`，#410 head `17b367bd`→main merge `bee8b35c`。旧head绿灯没有转移；新head均重新取得适用测试、AOT、Format、CodeQL和连接器成功，连接器资产上传按策略SKIPPED。#395/#397只关闭各自切片的实现范围，母issue按剩余条件继续OPEN。

## 验证与外部边界

### 已创建的独立 PR

| PR | 提交 | 当前交付 |
| --- | --- | --- |
| [#408](https://github.com/IoTSharp/SonnetDB/pull/408) | `9ad35c12c470cfc16acf8a2d2e366bdb00642e99` | 两处聚合编码判断及16项回归，Fixes #407 |
| [#409](https://github.com/IoTSharp/SonnetDB/pull/409) | `3dfd8af31e27ba7041c2bf3c6f902234c548c679` | 共享Protocol/轻量Client的typed KV首片，Refs #397 |
| [#410](https://github.com/IoTSharp/SonnetDB/pull/410) | `5f7bc23cba4651677d174c6adb0b8b12c4416c8b` | OwnsMany固定元素标量更新首片，Refs #395 |

三树均在最终源码完成后取得完整restore和规定Format退出0，分别313.7/176.3/191.0秒的Format工作区加载警告保留；各提交后的本地HEAD、upstream、远端分支与PR head已独立核对一致，提交时工作树均干净。每个实现PR均带自己的CHANGELOG/HANDOFF与审计；HANDOFF原889609字节历史后缀逐字节保留。

另一个智能体对#395/#397做独立只读交叉审阅，未发现阻断项。三个PR的Copilot复审均COMMENTED并给出Approval recommended/No open issues；这属于工具审阅意见，未写成独立人工批准。远端检查与合并状态以本报告收口记录为准。

#408精确head的Ubuntu CI原始12份TRX已下载并解析：Core8105/8105、EF360/360、Server1595/1595，所有12报告均0失败/0未执行。该报告补足本机完整Core900秒超时后的Linux完整套件证据，但不改写原TIMED_OUT；Windows结果继续单独核实。路径`artifacts/issue-review-20261106/ci408-ubuntu/parsed-trx-summary.json`，来源CI42152451820。

### 本轮功能切片的本机结果

- #395：修复前真实三协议3/3 FAIL；最终 ExecuteUpdate 扩大组50/50 PASS，完整EF388/388 PASS、0失败/跳过，新增28项。固定对象集合下标与数字属性名分别处理，primitive集合、动态/负数、转换器、数组读取右值及不适用ElementAtOrDefault仍明确拒绝。缺失父级跨行更新整条原子失败。公开包与实际应用仍未验。
- #397：Client234/234、真实产品Server扩大组42/42、Core原KV协议56/56，均0失败/跳过。真实数据请求严格为HTTP/2的`/v1/frame`，只有测试资源准备使用控制面。覆盖二进制/空值、UTC ticks过期、单页游标、两次重开、401/403、取消/RequestAborted、提交后断连未知结果不重放、请求/响应预算。
- #397原始失败边界：首次Server依赖冷编译300秒到界，无完成测试；随后setup误用HTTP/1导致6项失败；改用ExactHTTP2后5 PASS/1 FAIL，剩余是16B预算小于自身17B请求的夹具错误，改为32B后真实到达响应预算拒绝。完整失败与最终42项绿灯TRX分别保留，未放宽产品预算或终态检查。

- 本轮实时验证：GitHub #405 已合入、Copilot 实际评论1条、原 open issues12条及全评论、FreeSql #2291 OPEN、XCode #103 CLOSED/未合入、FreeSql Provider NuGet404。
- 起始 GitHub直连被重置，使用逐命令 `HTTP_PROXY/HTTPS_PROXY=http://127.0.0.1:7890` 后认证和API正常；没有改全局代理。直连 auth 的 invalid 不能归因于真实凭据失效，代理后的认证成功已经纠正此边界。
- 所有代码提交须在最终树执行完整 restore 与规定 Format。具体 worker 的修复后测试、提交、PR、CI、进程清理回执在后续收口记录。
- 本轮没有正式发包、移动标签、部署、修改生产、执行固定硬件负载或平台送审。公开旧5.0.0的行为不会因本地源码或新PR改变。
- 原始读取、issue写入正文和初始清单在 `artifacts/issue-review-20261106/`。每次外部comment/create仅一次写入并独立回读，不盲重试。

