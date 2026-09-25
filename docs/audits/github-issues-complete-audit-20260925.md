# GitHub Issues 逐项核对（2026-09-25）

仓库：[`IoTSharp/SonnetDB`](https://github.com/IoTSharp/SonnetDB)。通过认证的 GitHub Issues API 逐条回读 #89、#91 和 #171-#198，共 30 条：26 条 `closed/completed`，4 条 open。此表记录线上状态与可核查的源码/测试证据；`closed` 只表示该 Issue 既定范围的验收，不表示整项 Milestone、正式发布、固定硬件或生产现场验收。旧审计中写的 open/pending 是当时快照，不能覆盖本次线上状态。

| Issue | 当前状态 | 完成范围或尚缺验收 | 证据 |
| --- | --- | --- | --- |
| [#89](https://github.com/IoTSharp/SonnetDB/issues/89) | closed/completed | MCP Server 设计文档范围已交付；运行环境另按 M27 核对。 | [M27 Studio/MCP 记录](m27-studio-native-closure-20260923.md) |
| [#91](https://github.com/IoTSharp/SonnetDB/issues/91) | **open/reopened** | Studio 已能直接挂载已有嵌入式目录并读写持久数据；缺干净无网 Windows 安装、WebView2 Runtime 和首次启动证据。 | [本机旅程](issue-91-offline-studio-20260925.md) |
| [#171](https://github.com/IoTSharp/SonnetDB/issues/171) | closed/completed | 非递归 CTE 输出列名、空结果宽度与重复列名验收。 | [CTE 审计](github-issue-171-cte-columns-20260925.md) |
| [#172](https://github.com/IoTSharp/SonnetDB/issues/172) | closed/completed | 关系窗口分区、多列排序及默认累计帧；显式 frame 仍是拒绝边界。 | [窗口审计](issue-172-window-20260925.md) |
| [#173](https://github.com/IoTSharp/SonnetDB/issues/173) | closed/completed | CAST 类型转换与不支持类型诊断，Core 专项 9/9。 | [路线图验收](../github-issues-roadmap.md) |
| [#174](https://github.com/IoTSharp/SonnetDB/issues/174) | closed/completed | 字符串函数及后续 `position(search IN value)`。 | [补充审计](github-issue-174-position-20260925.md) |
| [#175](https://github.com/IoTSharp/SonnetDB/issues/175) | closed/completed | 日期差值和格式化的有界本地合同。 | [路线图验收](../github-issues-roadmap.md) |
| [#176](https://github.com/IoTSharp/SonnetDB/issues/176) | closed/completed | 单字段聚合 DISTINCT、NULL 和重复消除。 | [路线图验收](../github-issues-roadmap.md) |
| [#177](https://github.com/IoTSharp/SonnetDB/issues/177) | closed/completed | 关系 JOIN 类型与不支持的时序外连接诊断；真实服务 23/23。 | [SQL Provider 关闭记录](sql-provider-closure-20260923.md) |
| [#178](https://github.com/IoTSharp/SonnetDB/issues/178) | closed/completed | UNION ALL、INTERSECT、EXCEPT 优先级、类型和预算；不承诺 CLR 堆峰值上界。 | [集合运算审计](github-issue-178-set-precedence-20260925.md) |
| [#179](https://github.com/IoTSharp/SonnetDB/issues/179) | closed/completed | BETWEEN/ILIKE 与 NULL、通配符的有界本地合同。 | [路线图验收](../github-issues-roadmap.md) |
| [#180](https://github.com/IoTSharp/SonnetDB/issues/180) | closed/completed | JSON 查询和索引协作，Core 14/14、真实协议 24/24。 | [SQL Provider 关闭记录](sql-provider-closure-20260923.md) |
| [#181](https://github.com/IoTSharp/SonnetDB/issues/181) | closed/completed | System.Decimal 范围内精确类型、持久化及 ADO 元数据。 | [路线图验收](../github-issues-roadmap.md) |
| [#182](https://github.com/IoTSharp/SonnetDB/issues/182) | closed/completed | TIME 本地语义、持久化及嵌入式 ADO；跨日不支持。 | [路线图验收](../github-issues-roadmap.md) |
| [#183](https://github.com/IoTSharp/SonnetDB/issues/183) | closed/completed | 关系 SQL 整数位运算，专项 158/158。 | [路线图验收](../github-issues-roadmap.md) |
| [#184](https://github.com/IoTSharp/SonnetDB/issues/184) | **open** | 原子 UPSERT 与远程事务会话已在源码；正式包兼容和部署中的多实例/重启/丢失提交响应尚未验收。 | [会话审计](github-issue-184-remote-session-20260925.md)、[发布草稿](issue-184-197-release-readiness-20260925.md) |
| [#185](https://github.com/IoTSharp/SonnetDB/issues/185) | closed/completed | GetSchema 对视图、外键和文档集合的统一投影。 | [线上关闭](https://github.com/IoTSharp/SonnetDB/issues/185) |
| [#186](https://github.com/IoTSharp/SonnetDB/issues/186) | **open/reopened** | VECTOR INSERT、读取和 KNN 参数已通；measurement VECTOR UPDATE 尚无持久原子替换与 raw/KNN 一致语义。 | [VECTOR 审计](github-issue-186-frame-vector-20260925.md) |
| [#187](https://github.com/IoTSharp/SonnetDB/issues/187) | closed/completed | 无主键空表的安全演进、主键/自增/ROWVERSION 的有界路径。 | [DDL 审计](github-issue-187-ddl-evolution-20260925.md) |
| [#188](https://github.com/IoTSharp/SonnetDB/issues/188) | closed/completed | CREATE TABLE 命名外键约束的线上验收范围。 | [线上关闭](https://github.com/IoTSharp/SonnetDB/issues/188) |
| [#189](https://github.com/IoTSharp/SonnetDB/issues/189) | closed/completed | 有界递归 CTE、拒绝超预算、REST/Frame 读取；严格 CLR 堆峰值不是关闭声明。 | [递归审计](github-issue-189-recursive-cte-20260925.md) |
| [#190](https://github.com/IoTSharp/SonnetDB/issues/190) | closed/completed | 用 ROWVERSION 乐观并发替代行锁，锁定读返回稳定错误码。 | [锁边界审计](github-issue-190-locking-read-boundary-20260925.md) |
| [#191](https://github.com/IoTSharp/SonnetDB/issues/191) | closed/completed | UPDATE JOIN/FROM、首匹配、受影响行、事务与远程协议。 | [专项验收](issue-191-update-join-20260925.md) |
| [#192](https://github.com/IoTSharp/SonnetDB/issues/192) | closed/completed | ceil、floor、exp、power 的有界数值合同。 | [线上关闭](https://github.com/IoTSharp/SonnetDB/issues/192) |
| [#193](https://github.com/IoTSharp/SonnetDB/issues/193) | closed/completed | 选择并验证关系表 VECTOR/GEOPOINT 的产品边界方案。 | [SQL Provider 关闭记录](sql-provider-closure-20260923.md) |
| [#194](https://github.com/IoTSharp/SonnetDB/issues/194) | closed/completed | UPDATE/DELETE RETURNING 的结果、计数和元数据。 | [专项验收](issue-194-update-delete-returning-20260925.md) |
| [#195](https://github.com/IoTSharp/SonnetDB/issues/195) | closed/completed | INSERT SELECT、事务/RETURNING 与预算拒绝；内部所有临时分配并非硬上限。 | [INSERT SELECT 审计](insert-select-195-20260925.md) |
| [#196](https://github.com/IoTSharp/SonnetDB/issues/196) | closed/completed | 时序与关系 JOIN 参数绑定和排序，Core 212/212、Server/SDK 182/182。 | [关闭证据](measurement-join-196-closure-20260923.md) |
| [#197](https://github.com/IoTSharp/SonnetDB/issues/197) | **open** | INSERT RETURNING 主线跨协议合同已测；首次正式发布版本、包安装与兼容门禁未完成。 | [结果合同](github-issue-197-insert-returning-20260925.md)、[发布草稿](issue-184-197-release-readiness-20260925.md) |
| [#198](https://github.com/IoTSharp/SonnetDB/issues/198) | closed/completed | 明确 Int64/STRING 边界、溢出拒绝及 ADO/Frame v2 元数据协商。 | [整数边界审计](github-issue-198-int64-boundary-20260925.md) |

本机对 #91 的完整 Studio 测试在恢复 NuGet 资产后为 58/58；早先使用 `--no-restore` 的首次运行因输出目录没有 `System.IO.Hashing` 10.0.12 而为 57/58，不能计作通过。#184/#197 的首次完整正式发布仍未发生，最新公开 Release 为 `v3.1.0`；#186 的 UPDATE 尚未交付。四条开放 Issue 均保持开放，直至其各自剩余验收有真实证据。
