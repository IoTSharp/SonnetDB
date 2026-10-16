# #256 Int64 LIMIT / OFFSET 源码与 XCode 候选验收

日期：2026-10-16，Asia/Shanghai。Issue 原文及全部一条评论已读取并保存。本项已实现完整非负 Int64 的 SQL、参数绑定和执行分页；**正式 Core/Data/Server 发布、最低版本声明以及 XCode 上游依赖/PR 更新仍未完成，建议保持 OPEN**。

## 实现和兼容边界

- `PaginationSpec.Offset/Fetch` 改为 `long/long?`，保留 `(int, int?)` 构造函数，增加 `(long, long?)`。属性 getter/init 的 CLR 签名变更要求直接依赖这些属性的二进制消费者重新编译；不能标记为完全二进制兼容。SQL 原 Int32 文本/参数保持兼容，不变更持久化格式和协议版本。
- 解析和绑定接受 `0..Int64.MaxValue`，严格拒绝负数、超界及非整数。UInt64 超界保留原绑定器的 `OverflowException`；其他非法分页值保留相应解析/绑定异常。
- 执行器以 Int64 计数，只有已验证的实际集合下标或可精确表示的优化前缀转成 Int32。超大 OFFSET 与 FETCH 不直接相加分配；有界 Top-N 不适用时使用既有排序/外排，实际物化和内存预算保持独立。
- 文档、时序、图、JSON 文件和向量结果共享正确的长整数分页，但各模型既有查询形状、向量/全文候选上限与预算限制不扩大。通用 `ExpectNonNegativeInt` 仍用于非分页整数选项，SQL 分页已不调用它。

完整用户合同见 [Int64 SQL 分页](../sql-int64-pagination.md)。

## 身份与依赖

独立工作树 `D:/source/SonnetDB-worktrees/issue256-int64-pagination`，分支 `codex/issue256-int64-pagination`，基础提交 `cfa92314b5eeb152f358adc1ea99c177cb6b6a91`，加监督者同步的 #261/#260/#259 依赖和本项未提交增量。程序集的 Git informational suffix 只描述基础提交，**不是整个候选源码身份**；实际文件 SHA 见 JSON。

#259 接收的四个生产 SHA 已由监督者确认等同其最终交付。`RelationalSelectExecutor.cs` 接收基线 SHA256 为 `DAB029172B7D8A8CEF36BAE096FC6DF72CB512290D5747BFC129EC64349EFAB6`；本项只增加五处分页类型/受检转换差异，保留日期、concat、parse_uint64、UInt64 位运算声明接线。单独差异保存在 `rse-pagination-only.patch`，不能用旧基础整文件覆盖其他任务。

## 验证结果

| 范围 | 最终结果 | 证据边界 |
| --- | --- | --- |
| Core、Server tests、XCode 消费者 Release build | 三个均 0 警告、0 错误 | 最终规范化源码，非正式发行 |
| Core SQL 回归 | 3032/3032，0 失败/跳过 | 包含新增 41 项，不和单独运行重复相加 |
| RemoteAdoHttp2TransportTests | 126/126，0 失败/跳过 | 包含新增三协议×同步/异步 6 项 |
| XCode 原始固定提交 | 六次范围保护拒绝被实际观测 | 原 guard 不是功能通过 |
| XCode 两行候选 | embedded/rest/frame-http2 × exercise/reopen，6/6 进程 exit 0，每进程 301 断言 | 实际 PageSplit、DAL.Query/QueryAsync/Select、参数、空表和重开 |
| XCode 原始/候选构建 | 各 105 个既有警告、0 错误 | 没有抹掉上游警告或冒称零警告 |

Core 最终过滤器显式排除 `Select_WithDateScalarFunctions_TransformsDateTimeValues`：该测试的旧 DateTimeOffset 断言已由监督者在独立根提交 `0a51102c` 修正，本工作树不覆盖其改动。3032 不是整个 Core 套件结果；根集成后仍需执行提交前完整 restore/Format 门禁。

新增测试涵盖 0、1、Int32.MaxValue、Int32.MaxValue+1、Int64.MaxValue-1/MaxValue，文字超界/负数、空表和投影声明、命名/位置参数及缓存 AST 不变、普通/过滤/排序/聚合/去重/JOIN/CTE/视图/子查询/UNION/常量查询、强制外排及预算释放。跨模型 7 项仅验证既有支持形状；没有把通用时序非 time 排序或 Graph 排序加物化预算作为新增能力。

最终远程候选使用本轮 Server 快照，逐文件校验 SHA；服务端日志确认 REST 为 HTTP/1.1，Frame 查询为 HTTP/2 且存在 `/v1/frame` 请求。写 SQL 仍走实际 `/v1/db/.../sql`，在 Frame 连接下使用 HTTP/2。四次 Server 进程由脚本持有的精确 Process 实例终止，再打开同一数据库根；记录为终止重开，未冒称优雅停机。所有消费者有独立进程身份、超时和 exit code。

## 修复前与公开包证据

修复前源码快照（基础提交+#261/#260，**尚未同步 #259**）和固定公开包 `SonnetDB 4.5.0-preview.ef266.5` 均实际接受 `LIMIT 2147483647`，并在六组超大文字/参数查询中拒绝输入，连应为空的过滤结果也拒绝。观测程序 exit 0 仅表示记录完成，不算缺陷用例 PASS。

公开 Core/Data informational version 均为 `4.5.0-preview.ef266.5+1af23d8f299febfdf66b040ab3419980396acf46`。Core SHA256 `45D28D5D664AA60F3B6179E6D6181D25E79FEC0ECDC055C138E27A1AFE3236C2`；Data `1238EB255D4AB70D2E1528A533BA92BD0FB16CEB96B99F64F8F13D64CDFB5E4A`。NuGet.org 来源、锁定资产、nupkg、metadata 与 contentHash 都单列到审计 JSON；公开包没有因源码修复而获得支持。

XCode 固定于 PR #101 头 `757fa2a078906f87783e55fe0e9d5f54a9e916b9`，依赖 NewLife.Core `11.19.2026.901`，按上游原有 `netstandard2.1` 构建。仅移除 Int32 guard，没有修改 `maximumRows=0` 的无限制语义、没有提交或更新上游。候选、消费者及真实宿主复现脚本见 [复现目录](issue256-xcode-candidate/README.md)。

## 失败记录、门禁与接续

所有中间失败均保留：首次两处 long→int 编译错误、测试辅助参数可空错误、UInt64 异常类型误断言、三个使用了既有不支持查询形状的跨模型测试、Server 测试产物 RID 路径写错、消费者 XCode HintPath 写错，以及首次补丁换行匹配失败。分别修正实现/测试/调用路径后才取得上述最终结果；没有把旧失败删除或写成通过。

独占文件 whitespace 格式化、LF/UTF-8 无 BOM 规范化和 `git diff --check` 已通过；不把它们代替仓库要求的完整 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`。本代理不提交、不推送、不外发 Issue，不编辑共享 HANDOFF/CHANGELOG；监督者集成最终文件后负责完整门禁、独立提交与批量推送。

证据根：`D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue256-work`。机器清单见 [审计 JSON](issue-256-int64-pagination-20261016.json)，列出源码、依赖、测试 TRX、实际日志/命令、NuGet 和二进制 SHA。只在正式版本及 XCode 上游交付验收满足后重新核对 #256 是否能关闭。
