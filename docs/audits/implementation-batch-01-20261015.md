# 首批十任务：2026-10-15

本批由主智能体负责派工、审查、串行集成和最终验证，三个执行智能体分别负责 measurement 分组、多结果 ADO 和聚合状态。任务按可独立验收的产品能力计数，同一 Issue 可以包含多个任务；不将一次构建、格式检查或文档更新另计任务。

## 接收与分支

- 工作树最初干净，位于 `codex/cap-integration`；读取根 HANDOFF 后拉取 origin，主分支从 `5424b666` 快进到 `c7b307f6`，接收 193 个提交。
- `codex/cap-integration`（`8c299f40`）与 `codex/m42-m43-local-slices-20260930`（`311078e0`）均为新 main 的祖先，使用 `git branch -d` 删除；没有待分析的未合并本地开发分支。
- 清理 31 条目录已不存在的旧 worktree 登记；没有删除实际工作目录。保留 `origin/main`、`origin/parity-results` 和所有 tag；没有合并 parity 结果分支。
- 首次 GitHub API 返回 46 条 open Issue、0 条 open PR；第二次复核为 48 条（新增 #312/#313）；最终回读仍为 48 条（又纳入 #314，并已关闭 #273）。新增请求未混入本批。完整标题、创建顺序与快照时点见 [结构化清单](github-open-issues-20261015.json)。旧 2026-09-25 状态仅保留为历史。

## 固定本批范围

| 任务 | 归属 | 具体交付 | 当前验收 |
|---|---|---|---|
| B01-01 | GH-Issue #224 / M45-C01 | measurement TAG/time 分组后的 HAVING，包括隐藏聚合、参数和 NULL 三值筛选 | 本地 Core 与真实 REST/h2c 通过 |
| B01-02 | GH-Issue #224 / M45-C01 | 聚合结果上的算术、标量函数、CASE 与 CAST 复合投影 | 本地 Core 与真实 REST/h2c 通过 |
| B01-03 | GH-Issue #224 / M45-C01 | 分组键/投影别名/多键升降序排序，再执行分页 | 本地 Core 与真实 REST/h2c 通过 |
| B01-04 | GH-Issue #229 / M43 | 单个 ADO CommandText 的多条 SELECT，NextResult/Async、元数据切换和资源生命周期 | 本地 Core、DataSet/DataTable 与真实 REST/h2c 通过 |
| B01-05 | GH-Issue #257 / M43 | 关系表省略 WHERE 的全表 UPDATE，保留原子性、RETURNING、事务、预算和取消 | 本地 Core 与真实 REST/h2c 通过 |
| B01-06 | M45-C01/C02 | Int64 众数按原整数计数/比较，保留超过 2^53 的相邻值与合并精度 | Core SQL 与聚合回归通过 |
| B01-07 | M45-C06 / M46-S06 | Int64 近似去重使用精确整数身份；拒用旧 double 来源摘要，回退原块扫描 | Core SQL、持久化摘要回退与聚合回归通过 |
| B01-08 | M45-C08 | TDigest 拒绝未排序均值、非法权重和计数溢出的持久状态 | Core 状态校验与既有聚合回归通过 |
| B01-09 | M45-C06/C08 | HLL 拒绝超过算法合法上界的寄存器状态 | Core 状态校验与既有聚合回归通过 |
| B01-10 | M45-C01/C08 | Histogram 验证极值桶索引/边界，失败不污染既有聚合状态 | Core 极值、普通小数与失败后状态回归通过 |

## 优先级与下一批

最早的 #219～#223、#225 和 #228 在接收基线已有实现与对应测试，继续跟踪正式发布和下游 ORM 消费，不重复实施或自动关闭 Issue。#230 本身是发布工作，也不计本批新功能。#224 是最早尚缺实现的 P1 请求，本批先补它。

#226 主键迁移和 #227 DDL 事务需要共同设计 schema/索引/外键/数据交换及失败恢复；不能仅删除拒绝检查。#236 的 DATETIME 精度涉及现有毫秒落盘合同，不能仅改参数绑定。本批先完成可以沿用既有格式和事务机制的 #229/#257，并处理路线图中已证实会产生错误结果的聚合缺口。

下一批先复核当前线上状态，再按 #226/#227 的迁移依赖、#231 序列、#232 schema、#236 时间精度、#237 注释、#240 字符串长度、#242 原子替换等逐项选择十项，冻结各自验收和兼容方案。新增格式必须单独设计版本与迁移；不将本列表等同十项已经完成或已授权发布。

## 验证记录

审查修正前 Release Core 与 Server 测试项目构建均为 0 warning / 0 error，Core SQL 2662 与 ADO/非 SQL 聚合 269 共 2931 项、真实 Kestrel REST/h2c 161 项通过。独立审查又发现多结果查询中嵌入式缺名参数的回退序号在各 SELECT 重置，已修为整个批次递增并保留远程严格命名合同，补 Core 7 项和真实传输 6 项。修正后两测试项目重建均为 0 warning / 0 error，SQL/ADO/聚合集合合并重跑 **2938/2938**、真实 REST/h2c **167/167** 通过，无 skip。Server 最终新增 34 项与相关既有 ADO/Serializable 回归同跑。格式门禁和实际提交以根 HANDOFF 为准；未声称完整 Core/Server suite 或远端 CI 通过。

本地证据保存在忽略目录 `artifacts/batch-20261015/`：最终审查后的 `core-build-reviewed.log`、`core-reviewed.trx`、`server-build-reviewed.log`、`server-transport-reviewed.trx`；最终源文件、DLL/PDB 与结果绑定见 `validation-assets-final.json`。较早带 `final` 名称的日志/TRX 属于审查修正前运行，重叠结果不累加；最初取消异常测试期待错误、DataSet 未声明字符串长度失败和中间日志保留，最终分别按既有取消包装合同、经典 schema 的 `ColumnSize=-1` 适配修正后通过。强类型列 schema 未声明长度仍为 null。

聚合校验未更改文件布局或版本：旧 Int64 double 来源 HLL 摘要回退 typed 扫描；TDigest 保留原有有序正负无穷均值边界；Histogram 保留 `Math.Floor` 浮点分桶语义。多结果仅支持预检后的多 SELECT，不新增写批次协议或跨语句快照。正式 NuGet/Server、FreeSql/Chloe/XCode 消费、固定硬件、长期运行、干净安装与完整 CAP NativeAOT 继续各自验收。

完整 solution restore、原 CI Format Check、实际提交和 Issue 回复/关闭回读结果见根 [HANDOFF](../../HANDOFF.md) 的本批交接段；只按实际远端结果更新，用户授权的“已完成回复并关闭”不以本地代码存在替代原 Issue 验收条件。

[逐项 Issue 审计](issue-closure-audit-20261015.json)与[远端操作回执](issue-actions-20261015.json)保留实际回复、关闭和剩余项；[GH-Issue #273 公开包报告](issue-273-public-package-20261015.md)记录独立消费者的两种脚本选项、迁移升级、重复迁移与新进程重开。它兑现原 Issue 允许的生成阶段明确拒绝方案，已回复并关闭为 completed，不表示新增幂等脚本支持。
