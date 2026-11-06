# PR #413 审查、分支整合与后续验收

审查日期：2026-11-06 至 2026-11-07（Asia/Shanghai）。主干基线 `6305a1bf9bd9a33c53bed204bfbd5913582fb08d`，原 PR head `70cd29750e35cc22c13431a4c3e73001fbd39120`。用户授权审查合并、开发分支清理、issue 回复/实际完成项关闭及路线更新。本轮不发布新包、移动正式 tag 或部署生产。

## 审查结论

关系批次背压及 FIFO 有实际入口与回归，容量 API 为认证只读/source-generated JSON，未改存储格式或新增依赖，值得整合。与 main 仅 CHANGELOG/HANDOFF 冲突，保留双方新增记录及历史原始字节。原 head 必须先修复以下三点：

1. `Monitor.Wait` 重取锁时可超过期限，原代码会先 admission 再判断时间，从而迟到提交。每次 admission 前检查共同期限，覆盖首次锁等待及重新取得锁；WAL 开始后仍不自动重试或因取消伪称未提交。
2. Windows `DriveInfo(directory)` 会退回盘符根。将完整正文目录直接传给 `GetDiskFreeSpaceExW`，由 OS 跟随 junction/目录挂载查询实际卷；沿用仓库 Safe-only 简单字符串/整数 P/Invoke，不启用 unsafe。Unix 按目录查询。
3. HTTP/嵌入式 SDK 原先构造对象门面，先重建目录再检查容量。改走共同静态只读入口，不创建目录或打开对象元数据；丢失目录明确失败，HTTP 为 `503 storage_capacity_unavailable`。

保留直接 KV 超限拒绝、批次原子性，FIFO 的残余过滤先于分页、完整并列组、列/别名绑定、混合方向/表达式/事务 overlay 回退。

## 验证与证据

- 原合并候选 Core 定向 175/175 PASS，零失败/跳过；不能证明三项审查问题不存在。
- 修复前新增的超时 admission 和嵌入式丢目录回归 2/2 真实 FAIL，均未抛出预期异常；原 TRX 保留。
- 修复后扩大 Core 178/178 PASS，零失败/跳过，含正文目录链接 C:→D: 的实际目标容量、SDK 丢目录及迟到 admission。未在固定现场对象卷运行本次修复。
- 真实 HTTP 对象端点/传输取消 14/14、完整 EF 组合 438/438 PASS，零失败/跳过。HTTP 新用例首轮因断言误用 `code` 字段为13/14，按既有 `ErrorResponse.Error` 合同改成 `error` 后14/14；原失败保留。
- 最终 restore/Format 和精确 head 的远端 CI 按本批最终收据及 PR checks 独立记录；历史 5.1 候选不转移到新源码。
- 初次 restore 因隔离 worktree 缺 CoAP/MQTT 两个登记子模块 FAIL，初始化固定 gitlink 后完整 restore PASS，未改子模块指针。
- 本机原始日志/TRX、命令、refs 快照、Git bundle 及外部写入读回保留在 `artifacts/pr413-integration-20261107/`；不提交构建产物。长命令持独占重负载锁、CPU2/单 MSBuild 节点/禁持久服务，由 Cordonix 回收所属 Job。

## Issue 后续矩阵

当前 12 项仍有未满足的条件；此前完成并关闭的 #367/#393/#400/#407 保持关闭，不重复关闭或重开。

| Issue | 已有交付 | 接下来与关闭条件 |
| --- | --- | --- |
| #392 | #411 迁移工作区/恢复、真实强杀、库存应用。 | 全部分支最终 source 共同候选→新公开 Provider/Server→无 CandidateFeed 的 NuGet.org-only 空缓存迁移/Down/Up/重开。旧5.0部分提交须独立对账恢复。 |
| #395 | #410/#412 标量/子树/固定集合目标、类型编码和补货应用。 | 与#392重新冻结最终 source，公开新包/匹配Server及补货应用并发/回滚/重开；`0d687bd9`候选不代表加入#413后的结果。 |
| #394 | 跟踪集合、根/嵌套元素路径及生命周期实现。 | 明确父 ordinal 重排后 Clear/重载限制，公开新包实际应用生命周期；ExecuteUpdate不替代跟踪验收。 |
| #397 | #409 轻量 .NET typed KV Get/Put/单页 Scan。 | M49 下一操作/模型/语言包、原生HTTP/2、TLS/ALPN、ABI/取消/未知写入/重开、公开消费者。 |
| #230 | FreeSql#2291候选及公开SonnetDB5.0三协议消费者。 | 上游合入并公开新Core/Provider，再验完全公开组合。 |
| #404 | XCode#104架构与#105 Provider及本地候选消费者。 | #104先审查接受→#105合入→公开Core/Provider→全公开新缓存消费者。 |
| #396 | EF Provider保留非AOT边界。 | 固定静态模型，compiled model/预编译查询→真实NativeAOT publish及原始诊断→CRUD/事务/重开；首个具体缺口单独修。 |
| #402 | 来源政策、tag fence、payload/资产对账与白名单基础。 | 可信run/attempt/artifact来源及双verifier接线→受控预发布部分失败恢复→公开读回/新缓存消费。 |
| #398 | 安装器及包库存合同。 | 干净Windows/Linux及精确安装器身份→首装/升级/失败回退/卸载重装/配置数据保留；Studio/WebView2单列。 |
| #399 | hosted及局部现场证据各保留原范围。 | 负载前冻结硬件/原规模/fsync/ACK/阈值/基线/窗口→M19/M25/M42容量/性能/恢复/长稳原始时序。 |
| #403 | 0.2.0、43 MCP工具及五文件静态合同。 | 正式HTTPS、真实WorkBuddy宿主/权限/九模型旅程、当前包解析/送审/批准/公开安装逐层回执。 |
| #401 | M50独立规划，measurement仍为毫秒。 | T01冻结→T02精确换算→T03–T06精度元数据/格式/WAL/恢复→T07–T11查询闭环→协议/语言/迁移/硬件。 |

正式发布沿用已记录延期决定，未来从本次全部整合后的最终 source 重新冻结并验证；不重打或覆盖5.0.0。

## 分支治理

先保存refs/dirty/worktree状态并验证Git bundle，仅采用已审查且通过适用验证的增量。删除前核对main祖先关系及预期SHA，保留worktree文件与证据；共享dirty不混入提交。`parity-results`本地和两端远端引用独立保留，不合入开发分支。实际最终SHA/删除清单/远端读回以本批完成收据为准。
