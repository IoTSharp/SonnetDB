# 下一阶段：共同 Provider 候选的监督与并行验收

2026-11-07（Asia/Shanghai）。用户要求提交推送本轮交付，并创建新会话监督多个智能体按根 ROADMAP 的下一执行顺序开工。产品整合基线为 `e1ea61d27efa49ecacedb131f751b59e2549b195`（PR #413）；本说明随新的文档收口提交，实际启动基线由新会话重新读取 Git/远端并冻结。

## 本阶段目标

先完成根路线第一顺序：#392 / #395 / #394 的共同 Provider 候选准备、真实包应用与集合生命周期验收。不得重做已合入源码，也不得把旧 `0d687bd9` 的候选结果转移到最终整合源码。本阶段产出准确 source、版本、包/Server/DLL 指纹、三线原始证据、支持/拒绝矩阵和可供用户最终审查的剩余门禁。

正式发布沿用已记录的延期决定。本阶段可完成源码、测试、文档、隔离候选构建与验收、必要修复的提交推送；候选就绪后给出具体发行评审材料。新正式 tag、NuGet/正式 Release、生产部署、重启和迁移须遵守对应明确授权，不因候选测试通过而自动执行。

## 新会话的监督职责

新会话主智能体必须实际启动三位独立子智能体，统一接口、候选身份、重负载队列、集成和最终状态。先并行读取合同及准备各自验证工作，随后共享同一冻结候选，重负载验证按独占锁串行排队。不要仅返回计划或把一位智能体的工作改称多智能体。

| 子智能体 | 独立归属和交付 | 必须证明的行为 |
| --- | --- | --- |
| A：#392 迁移/恢复 | `eng/probes/issue392-migration-app/` 的最小必要修改、独立 `artifacts/provider-next-phase-20261107/migrations/` 及对应专题审计增量。 | 真实 PackageReference 库存应用、迁移历史失败原子性、同连接重试、Down/Up、嵌入式/REST/原生 Frame、独立进程重开与适用强杀/恢复；旧5.0部分提交边界保留。 |
| B：#395 JSON 更新 | `eng/probes/issue395-json-app/` 的最小必要修改、独立 `artifacts/provider-next-phase-20261107/json-update/` 及对应专题审计增量。 | 真实补货应用、参数化路径/类型/子树/固定集合目标、更新前行右值、显式并发影响行数、拒绝/回滚/重开及三协议；不以源码引用代替包消费。 |
| C：#394 集合生命周期 | 独立集合消费者/专题审计路径及 `artifacts/provider-next-phase-20261107/owns-many/`；先由监督者冻结确切文件白名单。 | 实际应用模型集合插入/读取/增删/元素变更、null/空/转换器、嵌套父重排→保存→再次修改，明确 EF10 ordinal 后 Clear/重载的支持边界，事务/并发与重开。 |

共享 `HANDOFF.md`、`ROADMAP.md`、`CHANGELOG.md`、发行清单及产品公共代码由监督者串行维护。子智能体发现产品缺陷时先给具体复现、源码归属和最小修复建议，由监督者解决共享文件冲突并决定修改。新增修复须绑定新提交和重新冻结的候选，不复用旧版本/摘要冒充同一制品。

## 候选与验证

1. 先读根 AGENTS/HANDOFF、根路线、PR #413 审计和本说明，核对 main/远端/现有候选及本地共享锁。旧 #395 已有单一候选/发行协调职责，新监督会话接续这份协调，先只读对账，避免同时开启第二条发行链。
2. 监督者统一冻结最终已提交 source、实际候选版本、四个相互匹配的 Provider/ADO/Core/Protocol 包及 Server/RID/原始制品指纹。按现有发行规范选择版本，不预占或覆盖5.0.0。
3. 各消费者复制到仓库外，使用 PackageReference、独立 NuGet.Config/packages/http-cache，核对 nuspec source 与实际运行 DLL；本地候选 feed、CI 候选和 NuGet.org-only 公开消费分别记录。Server 只使用同源匹配候选，各测试 Server 的生命周期有明确唯一归属。
4. 使用原始命令、完整 TRX、请求协议、目录和重开结果报告 PASS/FAIL/UNKNOWN/NOT_RUN，保留失败和未知写入，禁止删断言、跳过或放宽生产合同换绿。
5. 若本阶段修复源码，提交前对最终树执行完整 restore 与规定 Format，并完成适用测试和精确新 head 的远端检查。监督者负责集成、提交推送、候选再验和 issue 状态判断；公开包或实机验收未完成时不关闭母 issue。

## 共享工作树与资源边界

canonical `D:/source/SonnetDB` 含其它会话文档、发布账本和素材 dirty；不得 reset/clean/全量 stash/宽泛 stage 或复制覆盖。`parity-results` 本地/GitHub/Gitee分别保留，禁止合入或互相强推。旧工作树有未提交文档和证据，先核对归属；可为 SonnetDB 选择干净的隔离开发树，不能丢弃旧树独有工作。

所有 build/test/restore/format/pack 先以 `FileShare.None` 持有 `D:/source/SonnetDB/artifacts/sonnetdb-heavy-20261106.lock`，全程 CPU2、MSBuild `-m:1`、禁持久构建服务。主智能体统一队列；代码/合同分析可并行，重负载不得叠加。锁等待、轮询、搜索和批处理均设置项目/迭代上限与墙钟超时，支持取消并合理退避，启动长命令记录 PID/创建时间/完整命令/父链，结束回收确认归属的完整子树和临时对象。

委派提示必须重复上述有界执行、工具发现范围、进程归属和临时文件清理要求。使用 PowerShell 7 和 Cordonix typed argv；禁止 Graphify、递归扫描大目录找工具、按进程名批量杀进程或清空共享缓存。Internet 失败按机器规则采用进程级本地代理；SSH遵循既有stdin包装规则。

## 证据入口与后续顺序

- 已整合源码/三项review/两平台完整测试：[PR #413 审计](../audits/pr413-integration-20261107.md)。
- 本机原始收据：`artifacts/pr413-integration-20261107/completion.json`、`issue-reply-receipts.json`、27份远端TRX及Git bundle。
- 当前共同候选合同：`docs/releases/5.1.0-provider-candidate-20261106.md`，其中旧来源和状态须与 issue 最新内容对账。
- 第一顺序交付后，监督者按根路线依次推进 #397 SDK、#230/#404 上游 Provider、#396/#402 AOT/发行恢复、#398/#399/#403 环境验收及 #401 M50；每次只冻结一个明确工作包，不把所有未完项并发改进同一候选。
