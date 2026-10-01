# 2026-10-01 第三批并行任务闭环

本轮在 `codex/roadmap-four-closures-20261001` 的已有工作树上继续实现三个互不重叠的切片，保留第二批 CDC、COUNT 窗口、KNN 和组合样例。三个智能体分别负责 measurement SQL、数值窗口、订阅运维，主智能体负责组合恢复、交叉审查、公共文档和统一验证。启动时基底为 `cf8c70e0032e2015590eef32300584b077831396`；验证时已有第二批交付记录在 `419aca7218e8cf7f1189df9bc9f9d8d3c89e22f5`。验证后按用户要求归档为三个功能提交：measurement `c61bb9b2`、数值窗口 `4cee048c`、订阅运维 `3dbd7897`；组合恢复测试、公共索引与本报告由后续提交归档。证据中的 HEAD 和文件哈希记录提交前验证快照，不表示已发布。

| 任务 | 选择依据与交付边界 |
| --- | --- |
| M42 measurement 物化准入 | 关系 SELECT/DML 已有预算，measurement raw 路径仍缺执行期准入。跨字段前沿合并后，过滤和分页后的完整行在进入结果列表前计入根调用累计行/估算字节预算；排序、聚合、JOIN 等未覆盖路径在扫描前拒绝 opt-in。 |
| M43 精确数值窗口 | 已有持久 COUNT 与重投去重，数值聚合仍未实施。显式选择顶层 JSON 属性，精确 decimal SUM/MIN/MAX，并明确 AVG 舍入；坏值与中间总和溢出整批拒绝，不 ACK。 |
| M43 订阅运维 | 已有状态与投递次数上限，故障批次缺少运维恢复入口。持久暂停/恢复及条件重试使用状态 revision、delivery ID 和 attempt 校验，保留原事件和候选检查点。 |

独立合同：[measurement](../benchmarks/m42-measurement-result-bounds.md)、[数值窗口](../m43-numeric-windows.md)、[订阅运维](../m43-subscription-operations.md)。公开记录保留已有主构造及 Deconstruct；订阅状态迁移到 v3，旧实现不能重开 v3；COUNT 状态继续保持原版本 1 字段与哈希形状，数值状态显式使用版本 2。

主智能体新增 `FileStreamingOperationsWindowJourneyTests`：真实文件订阅和数值窗口先提交、后中断 ACK，持久暂停后重开，解除暂停并确认投递上限，条件 reset 后再次暂停/重开，再重投、ACK、消费尾批次并关闭窗口。最终核对 COUNT=3、SUM=5.25、MIN=-1.25、MAX=4、AVG=1.75、pending=0，以及两端的 sequence/revision。

## 验证记录

所有构建测试使用 PowerShell 7.6.6、.NET SDK 10.0.401。验证输出采用任务专属 `--artifacts-path`，避免与 VS Code 后台构建争用 `obj/bin`；编译并发为 2，关闭共享编译器和节点复用。长命令分别设置墙钟上限，记录 PID、创建时间、完整命令行与父进程，结束后只回收身份核对通过的任务进程和临时文件。

| 检查 | 最终结果与范围 |
| --- | --- |
| solution restore | 标准和隔离输出 restore 均通过。 |
| 全方案 Release build | 通过，0 warnings / 0 errors；生产程序集开启 AOT/trim 分析。未执行 NativeAOT publish。 |
| CI Format Check | `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 通过，exit 0。 |
| Core 全套 | 5607 / 5607 通过，0 failed / skipped / notExecuted。 |
| 本轮新增 | measurement 31、数值窗口 28、订阅运维 18、组合恢复 1，共 78 项全部通过。 |
| 相关七类测试 | 最终全套 TRX 的过滤视图 175 / 175 通过；早期独立定向运行是 171 / 171，后补 4 项由最终全套覆盖。 |
| 真实 REST / Frame / ADO SQL 回归 | 43 / 43 通过。 |
| 持久订阅进程硬杀恢复 | 1 / 1 通过；核对运行的 CrashTests 子进程与最新 Core DLL SHA256 一致。 |
| 十四能力索引 | validator 通过，14 capabilities、14 journeys、2 composedJourneys。 |
| 差异空白检查 | `git diff --check` 通过。 |

首轮完整 Core 使用长任务 TEMP，结果为 5520 passed / 87 failed。79 项直接报告 `DirectoryFsync` 目录打开或路径找不到，异常路径长 260～276 字符；其余 8 项为 RAG/CLI 断言或锁错误，不能仅凭首轮日志归为同因。切换任务专属短 TEMP 后，14 项失败子集通过，随后完整 5607 项全部通过，期间没有修改源码。保留首轮日志及逐项复验结果；本轮没有扩展修改 Win32 长路径支持。

Format 首次检查发现 `StreamingWindowContracts.cs` 一处换行，修正后重新构建及复验通过。最终 formatter stderr 仍含“加载工作区时遇到警告”提示，没有格式错误。CrashTests 首次命令虽 exit 0，却没有执行测试，明确记为 NOT_EXECUTED；该项目不在 solution 的隔离 restore/build 范围内，随后独立构建，并将九个本任务新构建的子进程文件放入隔离测试目录，才取得 1 项实际通过证据。

证据：[validation.json](roadmap-three-task-evidence-20261001/validation.json) 包含命令状态、TRX counters、78 项新增结果、175 项相关结果、首轮 87 项失败及最终结果、源文件 SHA256；同目录保存原始命令配置、进程身份和 stdout/stderr。完整 TRX 和编译输出在摘要与哈希留存后回收。[cleanup.json](roadmap-three-task-evidence-20261001/cleanup.json) 记录任务进程和两个专属临时目录的最终清理情况。

## 审查及未覆盖边界

三个智能体交叉审查数值精度、旧格式严格校验、重投/CAS、measurement 名称绑定和稀疏字段合并；主智能体统一审查 public API、source-generated JSON 与跨模块恢复。数值窗口状态加载补充根 JSON 对象检查，防止畸形状态泄漏非存储格式异常。交叉审查还修正事件浅快照：有界复制 payload 和 headers，哈希与聚合读取同一份独占副本，并补充调用方突变和累计复制容量两项确定性回归。

measurement 累计准入只覆盖 SQL 保留行。匹配 series 列表、MemTable 快照、段解码、已有缓存及标量求值的瞬时分配仍有独立工作集，不能宣称 CLR heap 硬上限或首行性能门禁完成。预算路径的 LIMIT 早停不检查未选中行的投影错误；默认路径保持原行为。

窗口聚合和运维仍是本地文件 API。分组、滑动/会话窗口、任务目录、DLQ、远程协议、分布式租约、ACK fencing、持续事务化 CDC 到流桥接、掉电、固定目标硬件及长稳均未由本轮完成。AVG 为 decimal 除法结果，除不尽时舍入。管理员身份和运维授权由宿主控制。M20 七天 scheduled、容量、安装和十四能力完整验收保持原证据状态。
