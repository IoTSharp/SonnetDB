# 4.0.0 稳定候选发布审查（2026-09-26）

结论：必须先消除实际代码及验证缺陷，再以同一提交的 GitHub Actions 验证候选。首次审查时 **NOT_READY**，不能由既有 issue 关闭状态或其他提交上的绿色工作流推断可以发布。首次核查源码为 `7dffde4945a03c142bc980746b78a464e035f009`；集成保留后续 `387635ee` 的 M27 文档更新。最新公开版本仍是 `v3.1.0`，候选采用已经记录的 `4.0.0` 主版本兼容边界。

## 发布前需要解决的实际问题

| 问题 | 对发布后稳定性、可靠性或部署的影响 | 修复与验证方式 |
| --- | --- | --- |
| Unix CDC spool 的 `.lock` 使用共享租约 | 同进程或跨进程第二写入者可进入，事件帧和确认位点失去单写者保证 | 独占句柄；竞争、构造失败、正常退出和强杀后恢复测试。Linux 未修复负例已失败，修复后两平台 CDC/向量各 46/46、真实进程恢复各 26/26 |
| Unix ServerRelay 全局 journal 锁使用 DeleteOnClose | 先打开旧 inode、后取得锁的进程可与新文件持有者同时写 journal | 保留唯一全局锁文件；实际 RunStore 延迟获取竞争在 Linux 修复前失败，修复后多实例 Windows/Linux 各 19/19。每个 run 的租约获取仍由全局事务串行，结束后清理，避免累积锁文件 |
| Windows Graph 证据文件原子替换与读取冲突 | `completion.state` 读取发生 sharing violation，恢复/进程控制证据不可靠 | 读取允许 delete sharing，确定性句柄回归；保留 I/O 错误可见性 |
| Graph launcher 退出后测试忙轮询 | 子进程尚在退出即误判清理失败 | 使用原有十秒期限等待实际进程组清空；受控启动钩子故障采用精确退出码 91 |
| EF HTTP/2 测试断言旧轻事务路由 | 当前服务端事务会话无法取得可靠 CI 验证 | 核对会话创建、同一会话 SQL、rollback 和独立连接读回持久状态 |
| Studio 单独构建找不到 Server | 管理工作台 workflow 无法验证真实托管宿主 | 显式构建依赖和构建期目标路径；独立 artifacts 目录 58/58 |
| Document soak verifier 假定 Windows 临时目录和 `pwsh.exe` | Ubuntu job 无法完成证据合同验证 | 使用平台临时目录和当前 PowerShell 7 进程路径；在线 Ubuntu 修复复验通过 |
| loopback HTTP 测试宿主关闭与 accept 竞争 | 完整 CI 偶发失败；过宽异常处理也可能隐藏真实处理器错误 | 取消并等待服务循环后释放监听；重复关闭和四类处理器故障传播在 Windows/Linux 各 31/31 |
| ecosystem torn WAL 测试假设单文件 | checkpoint carrier 出现后测试提前退出，没有执行预期恢复检查 | 选择含已确认写入记录的 WAL 段；完整 quick 旅程由失败变为通过 |
| 固定 MinIO 镜像不能匿名拉取 | light/full 都无法启动参考栈 | 使用原版本对应上游 commit、SHA-256 核验源码构建，保留许可与源码；必须远程实跑后判定通过 |
| ClickHouse `/ping` 成功但实际 SQL 无法认证 | full 报告将五个分析对照场景跳过，工作流仍显示成功 | 同版本容器复现 `/ping` 成功、SQL 403；专用测试凭据与认证查询就绪检查修复后五个场景双方均通过。light/full 的必需参考服务不可达或没有执行时必须失败 |
| VictoriaMetrics 将任意非空查询结果当作整批写入就绪 | 异步 remote_write 尚未全部可见时就核对完整计数，验证合同可能提前放行就绪阶段 | 保留精确数量要求及既有十五秒就绪期限，记录实际计数；分别核对受控分批可见、数量错误和真实固定版本服务，不能仅重跑取绿 |
| 完整 CI 缺少测试挂起诊断和作业期限 | Windows 长时间运行时无法判断停在哪项测试，默认最多等待六小时 | 作业限定六十分钟，单测试十分钟无进展收集 mini dump 并失败，保留 TRX、执行序列及转储上传窗口 |
| 七类并发测试把忙循环与写者、取消或续体放在同一线程池 | 段替换原测试在线上耗时 24 分钟，后续运行被十分钟无进展收集器中止；有限 worker 下可能一直无法退出 | 段 Add/Drop/Swap、查询 reader-map、墓碑、内存表和保留策略改为专用线程与有界同步；保留实际并发人数、固定写入工作量，并断言所有参与者、索引发布及旧快照读取。受限进程负例与两平台回归另行记录，最终仍需完整在线 CI |
| 数据库名称正则 `$` 允许末尾换行 | Linux 可创建不符合已声明名称规则的目录；纯文本诊断日志可能被拆行 | 使用严格字符串边界，USearch 回退日志转义 CR/LF。新增回归修复前一例失败，修复后名称、挂载与语义搜索共 25/25 |
| 图片桶回填共用工作与收尾预算 | 慢磁盘入队后内部 250 毫秒预算耗尽，正常分批工作在保存游标或读取结果时变成 HTTP 500 | 按最后成功对象保存已有格式的游标，收尾使用独立五秒上限并响应调用方取消；I/O 异常继续透传。实际 HTTP 及 scheduler 回归先失败，修复后相关组 34/34 |
| 默认 bundle 暴露公开初始凭据 | HTTP、Frame、MQTT 等入口可能被网络访问 | bundle/Studio/安装包默认 loopback，额外协议关闭；开放远程前替换密码及静态 token |
| 默认 Compose 映射对外端口 | 空目录首次初始化可能直接被外部客户端访问 | 包括可选观测栈的八个 host port 均绑定 loopback，透传初始化环境变量并统一初始化文档 |
| 发布与验证之间缺少完整门禁 | `main` 覆盖 Docker latest，连接器抢先创建 Release，dispatch 可能发布 | dispatch 只验证；同一提交全工作流门禁；镜像运行验证后推送同一镜像；连接器等待主发布成功 |
| 产物缺包、版本错误或重跑借用旧 artifact | 已发布资产不完整或与候选验证不一致 | 校验七个 NuGet 包、版本、内部身份、SHA-256、原生入口；门禁检查当前 attempt 的非空未过期产物 |
| 验证报告可误放行 | 旧绿灯掩盖较新失败/排队重跑，旧七天窗口、全 skipped profile 被当成功 | 按最新 attempt 时间取证，检查必要步骤确实执行，拒绝过期窗口和无实际通过场景 |

原始线上失败证据：CI [36208014238](https://github.com/IoTSharp/SonnetDB/actions/runs/36208014238)、Parity [36106844752](https://github.com/IoTSharp/SonnetDB/actions/runs/36106844752)、管理工作台 [36087324461](https://github.com/IoTSharp/SonnetDB/actions/runs/36087324461)、Ecosystem [35499601797](https://github.com/IoTSharp/SonnetDB/actions/runs/35499601797)。失败记录保留在分母中。

## GitHub 托管验证与发布门槛

按本次用户决定使用在线 GitHub 托管 runner。M19 默认路径执行四种有界容量/恢复场景并保留真实硬件、参数、原始报告，标记 `HOSTED_VALIDATION_ONLY`；它验证工作负载与自动化链路可运行，不提供专用固定硬件容量背书。原冻结硬件路径保留为显式选项。

`eng/verify-release-readiness.ps1 -CommitSha <full-sha>` 核查十二个仓库工作流。Publish 与 Connectors Release 的候选版本统一使用 `4.0.0`；三个发布工作流必须先有该提交的 dispatch 预检，正式 tag 路径才允许发布。CI 包含 Windows/Linux 测试和三个架构 NativeAOT；CodeQL 分析失败必须使 workflow 失败。Parity 额外验证本次候选的 light/full 原始 artifact，以及最近连续七个 UTC 日期的 scheduled 双 profile 证据；最新 scheduled 必须在 48 小时内。手动运行七次不能替代七天 scheduled。

首次在线快照只有 CodeQL 在初始提交成功，其余包括排队、取消、未运行或失败；初始 Parity 最近七次为三次成功、四次失败。最终发布判断必须重新运行脚本获取当前提交的在线报告，不能把此历史快照写成最终结果。

## 首轮在线复验（用于定位剩余问题）

以下运行来自候选 `85dd7b7d00da09cfc23827612bf5057db7990d2c`，不替代后续修复提交的统一复验：

| 验证 | 在线证据与结果 |
| --- | --- |
| Parity | [36211622630](https://github.com/IoTSharp/SonnetDB/actions/runs/36211622630) light/full 工作流成功，实际构建并启动固定版本 MinIO 参考栈；后续原始报告核查发现 ClickHouse 跳过，不能据此声称 full 对照完整 |
| 管理工作台 | [36211620061](https://github.com/IoTSharp/SonnetDB/actions/runs/36211620061) 五个 job 成功；Chromium 162 通过、15 跳过、0 失败。跳过的 13 个 Studio native 和 2 个远程 KV 场景不计入已覆盖范围 |
| M19 hosted | [36211630670](https://github.com/IoTSharp/SonnetDB/actions/runs/36211630670) 四种实际缩规模场景成功，原始数据、环境和校验和保留；固定容量结论仍为未验收 |
| M39 | [36211633775](https://github.com/IoTSharp/SonnetDB/actions/runs/36211633775) 非 quick 路径成功；Core 47、Crash 6、Server 29、Benchmarks 65 项均通过 |
| Ecosystem | [36211627679](https://github.com/IoTSharp/SonnetDB/actions/runs/36211627679) `ci` profile 成功 |
| 发布预检 | [Publish](https://github.com/IoTSharp/SonnetDB/actions/runs/36211636198)、[Connectors](https://github.com/IoTSharp/SonnetDB/actions/runs/36211638185)、[Docker](https://github.com/IoTSharp/SonnetDB/actions/runs/36211640292) 成功；未执行正式发布。Windows/Linux bundle 各十项新旧客户端兼容合同通过，NativeAOT 实际启动验证 loopback 默认配置；MSI 的生成与库存验证不等于干净系统安装验收 |
| CodeQL / Docs | [CodeQL](https://github.com/IoTSharp/SonnetDB/actions/runs/36211614309) 分析上传成功；[Docs](https://github.com/IoTSharp/SonnetDB/actions/runs/36211617219) 构建成功 |

首轮 [Document soak](https://github.com/IoTSharp/SonnetDB/actions/runs/36211625116) 失败被保留；便携路径修复后在 `19abf7bc` 的[在线运行 36211921286](https://github.com/IoTSharp/SonnetDB/actions/runs/36211921286) 成功（10,000 文档、11 个实际阶段、备份恢复 10,032 行）。首轮本机完整测试发现一例 KV loopback 测试关闭竞态，已修复并取得两平台定向回归；必须由最终提交的完整 CI 再验收。

后续 `9ec97d52` 的 [Parity 36212832566](https://github.com/IoTSharp/SonnetDB/actions/runs/36212832566) 仍为绿色，但 full 原始报告为 44 通过、7 跳过，其中 5 个来自 ClickHouse 认证失败。新门禁重放该报告正确得到 44 通过、2 个真实能力跳过、5 个失败，旧绿灯不再作为完整对照证据。ClickHouse 和 CI 诊断修复后，全部工作流必须再次在同一新提交上运行。

CodeQL [告警 2](https://github.com/IoTSharp/SonnetDB/security/code-scanning/2) 与[告警 3](https://github.com/IoTSharp/SonnetDB/security/code-scanning/3) 的随机数来源均为 `M41P0DifferentialTests.cs` 中固定种子的普通行数据 `INSERT`，生产解析器接收 SQL 字符串导致上下文无关的数据流匹配；它们不是生产密码生成路径，保留确定性差分测试并记录误报依据。[告警 1](https://github.com/IoTSharp/SonnetDB/security/code-scanning/1) 指向 USearch 日志，已落实数据库名称完整匹配和日志换行编码，`cd95996a` 在线分析后该告警在 2026-09-26 03:28 UTC 自动变为 `fixed`。没有关闭规则、压制诊断或手动删除在线告警。

首轮 Windows 完整 CI 后续返回图片桶首次 backfill 请求 HTTP 500。原断言没有保存响应正文，不能追认当时的异常堆栈；已在同一生产路径用可控预算到期稳定复现 HTTP 500，并验证修复后的部分页续扫、末项完成、重复回填、外部取消和 I/O 透传。原断言现在保留失败正文，最终 Windows 全量回归仍是必要验收。

`cd95996a` 的 [CI 36214014437](https://github.com/IoTSharp/SonnetDB/actions/runs/36214014437) 中，Linux 6629/6629、三个 NativeAOT job 和格式检查通过；Windows Core 在完成 2518 项后被挂起诊断中止，唯一未完成序列项为 `SwapSegments_ConcurrentReadsAndSwap_NoExceptions`，并保留 mini dump。该次 Windows 结果是失败，不将部分通过的 TRX 当作完整通过。同一候选的 [Parity 36214033032](https://github.com/IoTSharp/SonnetDB/actions/runs/36214033032) 已实际执行十种 full 参考服务，full 为 49 通过、2 跳过、0 失败，ClickHouse 五项全部通过；这仍是下一次统一候选复验之前的历史证据。

并发测试修复具有独立负例与有界工作量：原 Add/Drop/Swap 在四个 worker 的子进程中十二秒超时，Add/Drop 均记录四个 worker 全忙、48 项排队、零项完成；原墓碑测试在一个 worker 的子进程中五秒超时、100 项排队。修复后的 Add/Drop/Swap 每项保留五十读者和四十轮修改；查询缓存完成 1,920 次完整旧/新查询；墓碑五十写者与五十读者完成三十二轮、1,600 条记录；内存表保留十六序列共 32,000 点和每读者 2,000 次读取；保留策略完成五轮、1,000 次新鲜写入。失败必须传播，不能吞掉异常或依赖计时器恰好获得线程来结束测试。这些改动仅调整测试协调与断言，没有借此修改生产段管理器的快照合同。

`620f75d8` 的 [Parity 36216620971](https://github.com/IoTSharp/SonnetDB/actions/runs/36216620971) 再次证明旧绿灯不能替代最终提交复验：light 成功，full 为 48 通过、1 失败、2 跳过。失败发生在 VictoriaMetrics 的 10,000 点写入计数自检，另外两个时序后端通过；旧报告只保存预期数量而没有实际数量，不能追认此次线上读到了多少点。代码核查确认就绪判断只要求任意序列非空；固定 v1.106.1 服务在本机初步复验没有重现原始计数失败，须区分线上失败、确定的合同缺口、受控负例与真实服务成功证据。修复后的候选仍须再次在同一提交上运行全部工作流。

该提交的 [CI 36216593232](https://github.com/IoTSharp/SonnetDB/actions/runs/36216593232) 已完整通过：Windows 九套 6692/6692、Linux 八套 6634/6634，零失败、零跳过，所有 TRX 均为 Completed 且实际结果数与执行数一致；格式检查及三个架构 NativeAOT 也全部成功。两平台 Server 各 1125 项通过，包含原图片桶回填及新增预算回归。此结果确认并发和语义修复的完整在线回归，但该提交仍因 Parity full 失败而不能发布，不能把其 CI 成功转用为后续提交的证据。

## 能延期的能力与仍需限制的发布声明

稳定版本不要求完成全部研究与产品路线图。原生 Frame SQL 写入、任意计算生成列、行锁、尚未实现的 Graph/Agent Framework 扩展应保持明确的拒绝或文档边界，不能临时以回退行为冒称支持。4.0.0 的公共 API 破坏要求消费者重编译；不借主版本跳过持久化格式规则。

以下证据不能从 hosted 短测或代码存在推导：干净离线 Windows/WebView2 安装与升级卸载；百万/千万级固定硬件容量；真实模型质量、成本及迁移召回；现场双网和生产 HA；168 小时混合负载。远程事务已有丢失 COMMIT 响应回读及禁止自动重放，终态仍为单实例短期内存缓存，服务重启后的未知提交结果不应当作确定失败自动重试。数据库目录备份也不等于整个 Server 实例 MQ 的备份。

发布说明应只承诺已验收的边界，缺少证据的规模、部署和质量保证继续标记未验证。正式版本号、NuGet 公布、GitHub Release、Docker stable 标签及其发布后复验未全部完成前，仍称候选版本。
