# GH-Issue #347 锁定读扩展复验（2026-10-24）

## 范围与基线

- 基线：`origin/main` `86a82480333d842baf754172fc89afdfa993bb15`（包含 #345 合并以及 #358 的远程锁定读合同修正）。
- `FOR UPDATE`、`FOR UPDATE NOWAIT` 与 `FOR UPDATE SKIP LOCKED` 的 parser/AST、逐行锁竞争、序列复验和租约清理已经在基线存在；本切片不重复实现这些能力。
- 发现的实际缺口是 SKIP LOCKED 在带 `OFFSET`/`LIMIT` 时先分页再尝试加锁，竞争第一页会返回短页且可能错误地把可用后续行排除；同时 SQL/ADO 文档仍把已支持的锁定读写成全部拒绝。

## 本切片变更

- `TableSqlExecutor.ExecuteLockingSelect` 在 SKIP LOCKED 路径先取得稳定排序的完整候选集，逐行使用 no-wait lease 跳过竞争行，再对可用行应用 `OFFSET`/`LIMIT`；仅返回页保留行锁，分页之外的探测 lease 释放。复合主键、空结果、全部竞争和事务内写入身份仍由原有序列复验覆盖。
- parser/执行器提示更新为 `FOR UPDATE [NOWAIT|SKIP LOCKED]`，明确 `FOR SHARE` 仍不支持。
- `SqlLockingReadContractTests` 新增竞争行填充 `LIMIT`、返回页外行不被错误持锁，以及复合主键排序/分页回归；`RemoteLockingReadContractTests` 新增嵌入式和远程 ADO 相同结果、冲突错误码与释放边界回归。
- `docs/sql-reference.md`、`docs/ado-net.md` 更新行锁、NOWAIT、SKIP LOCKED、FOR SHARE 的适用范围及错误合同。

## 语义边界

- 仅显式事务内单个关系基表的非聚合锁定读受支持；JOIN、聚合、DISTINCT、CTE、派生表、子查询、间隙锁、幻读保护以及 `FOR SHARE` 仍明确拒绝。
- 普通 `FOR UPDATE` 继续等待并受取消/超时约束；NOWAIT 返回 `sql_lock_not_available`；SKIP LOCKED 不等待并跳过竞争行。锁仅保留到事务提交、回滚、连接关闭或远程会话过期。
- 该切片不宣称正式 FreeSql 包、Server 发布包或真实物理掉电证据；HTTP/2 Frame 宿主受环境支持能力约束，沿用既有远程验收边界。

## 验证状态

- `git diff --check`：PASS。
- 本地 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`、Core/Server 测试：当前执行环境没有 .NET SDK（`dotnet: command not found`），**NOT_RUN locally**。
- PR #363 CI run `40132710533` 的 Ubuntu、Windows core、Windows REST、三项 Native AOT 与 Format Check：**PASS**；CodeQL `40132710542`：**PASS**；Connectors Release `40132710532`：**PASS**。PR #363 已于 2026-10-24 合并到 GitHub `main`；本文件只记录其合并后的证据，不把这组 CI 结果扩展为固定硬件、生产或现场验收。
