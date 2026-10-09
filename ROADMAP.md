# ROADMAP — SonnetDB 4.5

WB75（2026-10-08）：仅完成已有Native CIM观测的精确本地集成后暂停，修隐藏.git读取并先走真实本机不提交预演，再对新owned树完整restore/原format放行。继承38/14/41、不重跑、不新增actual；旧WB74失败保留。仅原15路径，结果见[WB74/WB75集成报告](docs/design/m47-unified-management-workbench/wb74-owned-handoff-integration.md)与独立收据。heartbeat PAUSED，无push或新切片。

WB74（2026-10-08）：修复本地共享 HANDOFF 的 owned 内容守卫，固定完整工作前缀、精确 owned 段、private blob/index tree 与其它提交路径，仅允许未暂存尾追加。继承 WB73 PS14/Node41，不重测、不改六源，0actual/产品build；原 WB73 集成失败保留。新微试、完整门禁与本地提交以 [WB74 报告](docs/design/m47-unified-management-workbench/wb74-owned-handoff-integration.md) 和独立收据为准；不预填完成。

本文件是 **4.5 版本的主执行路线**：在九种原生模型上强化 AI 应用、通用聚合与持续计算、存储编码和执行成本，并把 Web Admin、Studio 桌面和 VS Code 收敛到一套统一的数据库管理工作台核心，补齐现有能力的远程、恢复、容量、真实质量及三面发布边界。4.5 是规划目标，本文不宣布版本已发布，也不修改当前包版本。

规划基线：2026-10-04，本地提交 `4b004946`；M47 设计基线已于 2026-10-05 获用户确认，生产实现按 [Workbench 队列](docs/design/m47-unified-management-workbench/work-queue.md) 的有界切片推进。当前 WB-00～WB-34 已按各自本地范围验证，WB-34补MQ新Web真实100/51当前JSONL/history、正常Publish/Ack审批和撤权锁存3/3；Node22/22、全Web327/327、MQ fixture16/16与build通过，冒号Topic400/实例恢复等边界单列；最终独立证据复核、完整门禁及本地提交以队列/证据和git log为准。WB-12 Document 为 `54c78755`、WB-13 Studio合同为 `74835847`、WB-14 VS Code资源导航为 `e7cf2fe5`、WB-15 Studio客户端为 `e1f93a69`、WB-16导航/认证为 `0bb628ad`、WB-17 Relation为 `f1978263`；这些局部状态不等同 M47/U01~U09 全量完成。已完成范围归入 [CHANGELOG 本轮归档](CHANGELOG.md#roadmap-completed-archive-2026-10-03-45)，历史背景见[原归档](docs/roadmap-history.md)。本轮研究、证据复核与文档验证见[规划核查记录](docs/audits/sonnetdb-45-roadmap-planning-20261003.md)及 [M47 统一管理工作台专题](docs/design/m47-unified-management-workbench.md)。

现有能力事实继续沿用[综合审计](docs/audits/2026-09-05_project-SonnetDB-report.md)、[九模型证据](docs/audits/nine-model-capability-evidence-20260905.md)、[gap catalog](docs/audits/nine-model-gap-catalog-20260905.json)和[十四能力索引](docs/audits/fourteen-capability-evidence-index.json)，结合后续已核实切片判断。已撤回的系统性能原始报告不作为验收依据。

## 完成判定

1. 代码必须存在且由真实产品入口调用；原型、未调用类型和文档计划不算功能交付。
2. 实现合同、本地回归、真实服务、固定硬件、真实模型、长期运行及远程发布分别记录；一个层级的 PASS 不升级其它层级。
3. 新增工作统一为 `📋 planned`。`M44-Axx`、`M45-Cxx`、`M46-Sxx`、`V45-Xxx` 是规划 ID，不是已创建的 GitHub issue 或 PR。既有内部 `#N` 与外部 `GH-Issue #N` 保持区别。
4. P0 为基础合同与优先交付，P1 为本版深化与验收，P2 为条件候选；只有明确必选范围参与 4.5 完成判定，条件项未经独立评审不成为版本承诺。
5. 质量、性能与资源阈值在实现前绑定语料、版本、机器及基线冻结；未校准保持 `NOT_READY`，不以自造数据或缩规模 PASS 代替真实效果。
6. Core 保持 Safe-only、零第三方运行时依赖、source-generated JSON、Native AOT/trim 零相关警告及 public API 中文 XML 文档。API/帧保持兼容；新落盘格式必须版本化、拒绝不兼容旧 writer，提供迁移或明确拒绝及恢复方案。SQL 名称遵守 GH-Issue #211。

状态：`🟢` 当前范围已完成 / `📋` 规划或尚未启动 / `🚧` 有剩余实现或验收 / `🟡` 指定本地切片完成、外部证据待补 / `⏳` 未执行 / `❌` 已执行失败。历史完成范围在 CHANGELOG，主路线只列待办。

## 当前完成度一览（2026-10-06）

这张表放在路线图前部，直接区分“本地切片已经完成”和“整个里程碑仍未完成”。`🟢` 只表示表中列出的切片已通过其记录的本地验收；它不会把真实 Server、固定硬件、长期运行、三宿主安装或发布证据一并标记为完成。

| 范围 | 当前状态 | 已完成 / 当前证据 | 尚未完成 |
|---|---|---|---|
| M47 WB-00～WB-10 | 🟢 | 设计基线、原型交互/状态合同、资源身份、导航/Explorer 兼容、结果/审批工作流、外壳迁移与深链接切片均已提交；提交哈希和测试见 [Workbench 队列](docs/design/m47-unified-management-workbench/work-queue.md)。 | 全量九模型页面、真实 Server 旅程、Studio/VS Code/安装/发布证据仍待补。 |
| M47 WB-11/WB-30/WB-31 Measurement Workbench | 🟡 | 原迁移`390ff526`；WB-30补点/monitor/write精确权限锁存、readonly、连接/Schema代际与旧finally隔离、一次审批/原身份unknown，500行先截断、auto12轮/60秒和导入1000语句/10批/60秒。Node16/16、全Web311/311、Chrome fixture20/20、旧Measurement/Vector子页8/8、build与独立复核通过；既有Kestrel SQL预览端点兼容3/3单列；WB-31新UI真实4/4、当前100/500/61与导出、WRITE两点逐INSERT终态、撤权锁存，Document回归3/3、审计Node10/10/全Web321/321及完整成功证据独立复核通过；最终完整门禁为本地提交放行条件，实际结果见验证记录。 | 显式恢复、完整COMMIT/权威影响数/Int64、raw defaults无信号ABA、文件/Server预算、三宿主/安装/AOT/发行物。 |
| M47 WB-12/WB-18/WB-26 Document Workbench | 🟡 | WB-12身份/六态/隔离与WB-18显式Find100恢复/输出预算已有实现；WB-26补本机新Web→真实Kestrel权限/恢复及Aggregate1001、Distinct501/1000旅程3/3，修空IDs省略与满1000完整性unknown。Node21/21、全Web295/295、Chrome fixture11/11、TypeScript/Vite与独立复核通过。 | 登录UI/routed readonly props、扫描/中间物化/字节/总堆预算、Advanced完整读写、其它九模型真实旅程、三宿主与发行物。 |
| M47 WB-13 Studio宿主合同 | 🟡 | 真实bridge身份/URL与Managed Local生命周期合同已提交 `74835847`，Release定向34/34；客户端消费由WB-15另验。 | 干净Windows/WebView2、安装与发行物。 |
| M47 WB-38 Studio原生宿主诊断 | 🚧 / 完整烟测❌ | 新增真实Studio/WebView2诊断runner；宽窗CSS1266×663/DPR1.5下native bootstrap/manifest、正常Health/Stop/Start与old/new Studio-owned Server身份已核验。Web build、Server/Studio Release通过，根89记录身份回收审计无自有存活；生产不改，详细原失败/局部证据及最终门禁见M47验证记录。 | 三run额度耗尽，CloseMainWindow前helper未发现窗口、process-events超512KiB导致终态缺失；完整正常退出未通过。默认/窄窗Health隐藏、OS文件对话框、安装、完整三宿主/NativeAOT/硬件/长稳/发行物另验。 |
| M47 WB-39 本机宽窗Studio生命周期 | 🟡 | 修诊断普通可见启动/有界主窗发现与独立终态、schema2完整身份去重；16内存故障测试/语法通过，第二真实run普通CloseMainWindow退出0/signalnull、Health/Stop/Start、四端口/旧新Server释放、零fallback/五terminal保存及根63身份回收核验通过。生产不改、旧产物hash复用；首轮root monitor超时/缺文件与WB-38失败保留，最终完整门禁/独立复核/提交见队列。 | 只证本机宽窗，不推断旧windowsHide因果或Server优雅恢复；默认/窄窗Health本机局部证据由WB-40补齐，原生文件对话框、安装/升级卸载、完整Studio/VS Code/Extension Host、NativeAOT/硬件/长稳/发行物及三宿主整体仍待验。 |
| M47 WB-40 默认/窄窗Studio Health | 🟡 | 修native响应式身份/状态/Health与现有生命周期入口隐藏；最终fixture12/12零skip、Web/Server/Studio构建通过，两真实native default不传尺寸/CSS946×556与1000×800/CSS652×476（DPR1.5）六geometry/fiveDOM、正常Health/Stop/Start/CloseMainWindow exit0、四端口/零fallback和独立终态通过。Explorer实测已收起；原skip/9-3/首native wrapper失败保留，首run runtime策略拒删而保留，root164身份无自有业务存活与独立复核通过；最终九文件完整门禁/本地提交见队列。 | 展开Explorer、native库database同步/恢复、登录UI/OS文件对话框、安装/升级卸载、完整Studio/VS Code/Extension Host、NativeAOT/硬件/长稳/发行物与整体三宿主未闭环；保留runtime不计完整cleanup。 |
| M47 WB-41 SQL原生文件诊断 | 🚧 | 显式四phase诊断入口、真实DTO/普通DOM/磁盘与OS独立ack门禁已实现，syntax通过；首actual Open POST观察到，桌面工具无可见picker/激活失败、60秒无ack超时，后三phase NOT_RUN。六失败终态、9核验fallback/四ports/三runtime回收与root失败checkpoint、独立复核保留；生产及旧default不改，最终八文件门禁/提交见队列。 | 四phase真实OS/响应/结果仍未通过；native窗口可观测/正常激活条件须先确认，不盲重跑。不计普通退出、安装/AOT或三宿主整体完成，其它未完成VS Code有界队列仍继续。 |
| M47 WB-42 真实VS Code只读Query诊断 | 🚧 / 两条局部证据 | 新增独立Host/真实Server runner，syntax/TS与既有Node20/20通过；current两行/selection一行、原列及生成SQL/database与独立真实reference相符，HTTP200。首helper parent2秒超时有据修为一次有界snapshot lookup；两失败原证据保持。根两单PIDfallback、两个端口/282对象runtime回收另验，最终八文件完整门禁/本地提交见队列。 | EXPLAIN第三POST200但断言失败/无phase3载荷，history未运行，Code正常退出/runner cleanup未证；reference preview32与生产{sql}差异、partial discovery29/24与finally回收须先修，不第三跑本片。向导UI/Webview分页/Notebook/LSP/安装/AOT/整体三宿主仍另验。 |
| M47 WB-43 查询诊断恢复 | 🚧 / 三条局部数据证据 | sql-only参考/安全观察/身份ledger与独立终态已修；TS/语法、纯内存9/9和Node20/20通过。第二真实Host current2/selection1/EXPLAIN45行与独立真实reference逐值一致、POST200；完整实跑仍FAIL/process_audit，原两失败/false保持，根fresh PID与281对象runtime回收另证，最终十文件门禁/本地提交见M47验证记录。 | ledger近256KiB准入与固定合并reason需有界诊断；history/API恢复/Code正常退出未验证，不第三跑本片。生产maxRows、向导UI/DOM分页/Notebook/LSP、安装/AOT/整体三宿主另验。 |
| M47 WB-44 有界身份ledger/公开历史 | 🚧 / 历史与API局部实证 | 保原门禁压缩external ancestor正文，完整own/连续anchor链与event exact引用/hash保留，missing/changed父拒绝stop；TS/PS7/内存17/17和既有20/20通过。新Host current/selection/EXPLAIN与真实参考同，publichistory3/apiRestored真/清理0已核；完整runner仍FAIL/process_audit，原false保持，root273对象清理另证，最终十文件门禁/提交见验证记录。 | 三个完整identity先验未知拒绝须安全细分，Code正常退出仍未证；不能重复盲跑。生产maxRows、向导/DOM分页/Notebook/LSP、WB41 OS、安装/AOT与三宿主另验。 |
| M47 WB73 Native CIM 本地验收 | 🟡 / 本地夹具通过、0actual | 新窗口 micro 各1/1、PS14/14与Node41/41，原full空结果mock失败保留，夹具bare-return窄修独审；最终owned树完整门禁/本地提交以WB73收据为准。 | 不升级WB72过期或旧strict/actual失败；下一窗口冻结真实Native准入，恢复/正常退出/三宿主/安装/AOT/硬件/长期仍另验。 |
| M47 WB72 Native CIM 阶段观测 | 🟡 / 已实现、验收未全 | 四阶段/160槽/结果数/单调时间及100ms记账停止，原查询/cache/预算/权限保持；静态独审与修复PS micro1/1通过，初始micro语义失败保留，0actual。 | 窗口过期失败关闭，本会话1/5；WB73新本地验收窗口完成Node micro/PS14/Node/full restore-format/owned集成，不升级历史失败或真实恢复。 |
| M47 WB71 Native运行时/snapshot合同 | 🟡 / 静态报告、0actual | 原.62 CDP HTTP成功与.53 prereq文件分列；启动无明确选择绑定，coarseCDP fatal含snapshot，20s总/单CIM20s及四helper统计已核，逐query原因unknown。七路径集成见收据。 | 下一独立WB72冻结既有query阶段耗时观察/夹具，NOT_IMPLEMENTED；不重跑WB70，三宿主/安装/AOT/硬件/长期及旧FAIL保持。 |
| M47 WB70 Native真实窗口 | ❌ / 唯一actual失败检查点 | CR/LF与五metadata inverse/source syntax、fresh资源准入通过；唯一actual CDP helper13/17/21.537s失败，原恢复/normalExit/cleanupfalse。根18identity/四ports门禁后回收324自有对象另证，六路径集成见收据。 | 下一独立片有界查actual runtime .53/.62选择与helper snapshot合同；WB70不重跑，overall/processIntegrityfalse及旧失败保持，三宿主/安装/AOT/硬件/长期分列。 |
| M47 WB69 Native资源准入 | 🟡 / 0actual失败检查点 | 五metadata inverse/source syntax、23依赖/九runtime SHA、.NET10三framework/Playwright CDP与四exclusiveports已核；独立source/tools局部通过但180s有效期晚5.894s，actual NOT_RUN。六路径本地集成见收据。 | 下一独立资源窗口先review再fresh gate/单次actual；过程完整性false与旧WB68/67/66/65/64失败保持，三宿主/安装/AOT/硬件/长期分列。 |
| M47 WB68 普通异库准备 | 🟡 / 本地合同、0actual | runner显式opt-in；首次A已active时最多一次独立B控件准备、完整ack/DOM/disk并重读A守卫，默认及原A→B旅程/关闭/预算保持；Node60/60（新增17）、micro1和源码29/工具27独立检查通过，最终8路径集成见收据。 | 下片先冻结独立native metadata/runtime资源窗口，再验真实恢复/正常退出；旧WB67 numeric退出FAIL、三宿主/安装/AOT/硬件/长期分列。 |
| M47 WB67 helper三统计保留 | 🟡 / 本地合同、0actual | H→R→E固定安全三标量，unknown不造0；Node31/31（新增15）与两个纯PS输出夹具、源码22/工具v2 26独立检查通过，最终九文件门禁/本地集成见收据。 | 具体慢query/phase仍unknown，不升级旧真实失败；可成立的异库前置与完整恢复/正常退出、三宿主/安装/AOT/硬件/长期另验。 |
| M47 WB66 helper预算合同 | 🟡 / 本地报告、0actual | 实际协议/关键source range/SHA核验，独立28/28 PASS；20.19s墙钟拒绝，CIM15/cache16非数量耗尽，success统计丢弃已证。六docs完整门禁/本地集成见收据。 | 下一片仅保留existing三安全统计，逐查询原因unknown；旧actual/退出/cleanup与WB65strict失败不升级，真实恢复及三宿主另验。 |
| M47 WB65 Studio选库前置 | 🟡 / 本地合同、0actual | 普通DOM同值/unknown拒绝与异库身份一致前置；原fresh七谓词/成功链保持。micro1/1、最终43/43（32+11新），旧fixture精确适配新读取；继承WB64终审v2新收据PASS，原FAIL不升级。 | helper采集/缓存/回收预算独立后续；真实A→B/两desktop/B查询/正常关闭未验，超时不证明底层evaluate取消，三宿主/安装/AOT另验。 |
| M47 WB64 Studio真实选库观察 | 🟡 / 真实FAIL、失败检查点 | 独立runtime SHA与唯一actual：A PUT200 seq6/request43对pre-click barrier6/57，两fresh谓词false、其余五项true；DOM匹配A，30call/7658ms。六终态与执行源码保持，根exact owner资源回收另证；0新build/0定向测试，最终六路径门禁与提交见收据。 | 原normalExit/cleanupProven false，helper预算CIM15/20.19s/cache16失败；B/关闭/第二desktop/恢复/查询未运行。下一片先冻结同值选择fresh前置合同，helper回收另片；WB62历史原因与三宿主/安装/AOT不升级。 |
| M47 WB63 Studio选库失败观察 | 🟡 / 本地32合同，0actual | 同步pre-click双barrier、至多2次选择记录；至多128既有候选的原七find谓词及有界普通DOM计数/布尔，原error对象保持。实际轮询次数/耗时单列；原30次/250ms/20秒上限与ack/DOM/disk/close不变。最终七路径门禁/本地提交见收据。 | WB62实际A PUT200的拒绝原因仍unknown；不得把20秒上限当耗时。后续一次有界真实运行须绑定新源/runtime/失败检查点；A→B、两desktop、B查询及正常退出仍待验，不盲重跑旧证据。 |
| M47 WB62 Studio恢复接续 | 🟡 / 本地23合同，真实FAIL | seed有界脱敏观察/原HTTP失败保持、固定证据目录与源码独立PASS；9产品SHA复用，0新build。唯一actual已越过STRING/普通PK双库seed，但A选库fresh PUT验收失败；实际A PUT200存在，失败barrier缺证。9fallback/四ports/481对象回收另证，最终8路径门禁/本地提交见收据。 | WB63已补选择双barrier与候选/DOM观察合同，0新actual，旧原因未证；selections0/launch1、第二desktop/恢复/B查询NOT_RUN，normalExit=false。三宿主/文件/安装/AOT/长期/发布另验。 |
| M47 WB-61 Studio 数据库恢复验收 | 🟡 / 本地合同，真实旅程FAIL | 独立两desktop场景、双ack barrier与候选身份准入，最终14/14本地合同和Web/Server/Studio构建通过。唯一actual native bootstrap有效，夹具建表HTTP400，9归属fallback/四port/481对象回收另证。最终完整门禁/本地提交以M47收据为准。 | 原TEXT不合原生关系SQL，已修正STRING/普通主键但0新actual；选库、库持久化、第二desktop和B查询均NOT_RUN。须接续真实恢复旅程，登录/对话框/安装及三宿主整体仍未闭环。 |
| M47 WB-59 VS Code SQL耗时字段兼容 | 🟡 / 本地兼容，真实另验 | 唯一parser补native/legacy→optional elapsedMs；finite非负原值保持，缺失/非法/冲突省略alias，其他metadata保持。唯一TS、新Node5/5及最终九文件完整门禁/本地提交见M47收据，0actual/.NETbuild。 | parsed Raw可能增加alias；WB57原FAIL不升级。真实history/ack、stop-verification及三宿主/安装/AOT继续另验。 |
| M47 WB-58 公开history/错误通知安全观察 | 🟡 / 本地合同，真实另验 | 原完整await/串行Store及catch fulfilled保持；3phase固定通知/公开history snapshot，原总20命令及final3断言不放宽。必要synthetic ack/deferred与保密测试、唯一TS及九文件完整门禁/本地提交见M47收据，0actual/.NETbuild。 | 不推WB57 history2原因，不授持久ack；elapsed DTO、真实history/stop-verification及三宿主/安装/AOT继续另验。 |
| M47 WB-57 真实终态三检查 | 🟡 / 三检查到达实证，原旅程/cleanupFAIL | 唯一真实finalChecks refused/passed/refused，原首失败与authority保持；三payload2/1/45对真实reference同。49ledger/64refs/27helper/16原文件冻结，根62PID absent/0新stop/282对象回收及八文件完整门禁/本地提交另列。 | history20/2失败、Host0byte/API恢复unknown、Code/Hostnull/normalfalse、原FAIL/cleanupfalse保持。下一片先冻结history/ack及stop-verification合同，三宿主/安装/AOT另验。 |
| M47 WB-56 终态三安全检查 | 🟡 / 本地75通过，真实cleanup另验 | 三原同步检查独立执行、首失败保持、三passed且无terminal才proof；fixed finalChecks单读/异常隔离/幂等，syntax与75/75通过，0actual。最终八文件完整门禁/本地提交见M47收据。 | 原WB55 FAIL/false/null及remaining1/blocking7不升级；不新增stop/采样或authority，真实cleanup及三宿主/安装/AOT另验。 |
| M47 WB-55 真实Query/history/Code | 🟡 / 局部生命周期通过，原严格cleanupFAIL | 唯一actual三query2/1/45与真实reference逐值同，公开history3/HostPASS/API恢复/Code0-signalnull正常退出；3 existing-fresh安全观察、37ledger/43refs/17helper与16原文件完整，根44PID absent/0kill/280对象回收另列。最终八文件完整门禁/本地提交见M47收据。 | 原runner FAIL/unknown reason-type null/cleanupfalse保持；lookup变化不推权限/短命/复用或stop。下一片先冻结blocking/refusal生命周期，DOM/分页/Notebook/LSP/安装/AOT/三宿主另验。 |
| M47 WB-54 既有fresh安全观察 | 🟡 / 本地67合同通过，真实Host另验 | 仅复用同batch lookup追加有界transition投影；原deadline双guard、failure/authority/ledger/events/stop不变，syntax/source绑定与67/67通过，0actual；原工具与fixture FAIL保留。最终八文件完整门禁/精确提交/post见接续收据。 | 关系不证明权限/短命/复用/stop；WB53 history/Host终态/Code正常退出与原cleanup仍未证，三宿主和发布矩阵另验。 |
| M47 WB-53 真实Query与snapshot再验证 | 🟡 / 三payload局部通过，原FAIL保留 | 单次current2/selection1/EXPLAIN45逐值对真实reference与POST200一致；actual initial缺parent command与fresh lookup缺项已区分。51ledger/68events/28helper和12项manifest342719B完整；根fresh54PID absent/0新增kill/新runtime292对象回收另列，最终八文件完整门禁/失败检查点提交见收据。 | history/Host终态缺席、API恢复unknown，Code/Host null、原cleanupfalse；缺失原因、正常退出及UI/分页/Notebook/LSP/三宿主另验，不放松原stop。 |
| M47 WB-52 父链snapshot诊断 | 🟡 / 本地安全合同通过，真实Host待验 | fixed来源/角色/count/presence，未知role/null及同PID单读；最终55/55、syntax/双PS7 AST通过，初53保留。只已有lookup，不改准入/stop/预算或runner；503后旧closed4/14，新窗口续八文件完整门禁/本地提交，0actual/TS/新build。 | WB50采集原因unknown，原FAIL保持；真实三phase/history3/Code退出/cleanup与三宿主另验。 |
| M47 WB-51 调用时SQL上下文 | 🟡 / 本地合同通过，真实Host待验 | execute首await前捕获SQL字符串，保selection/statement/EXPLAIN/错误/数据库顺序与history ack；最终调用21/21、必要history6/6，同一TS项目初版/v2编译通过，failure cleanup先drain再恢复stub。14wrapper/0actual/0新.NETbuild、最终八文件完整门禁/提交见收据。 | 已证异步漂移风险不等于WB50因果；真实三phase/history3/Code退出、parent snapshot/cleanup与三宿主另验。 |
| M47 WB-50 真实Query/history窗口 | 🟡 / 实跑失败检查点 | 两源metadata逆投影与syntax/TS通过，唯一actual current2/selection1对reference通过，Host FAIL explain/source、history缺席，runner process_audit/Code与hostOutcome null。新cleanup诊断6identity拒绝/终态残余1；原false、根45PID absent/0kill/280对象回收与五terminal/15原证据绑定分列，最终八文件门禁/提交见M47收据。 | 先诊断前一payload观察与parent snapshot缺失合同，不追加actual或升级旧FAIL；UI/Webview分页/Notebook/LSP/安装/AOT/硬件/长稳/发布及三宿主整体另验。 |
| M47 WB-49 Cleanup子检查诊断 | 🟡 / 本地合同通过，真实Host待验 | 为既有owned-processes补固定安全subcheck与有界数值，恢复性/终态失败分列；原身份/stop/roots/audit/预算门禁保持。最终46/46、Node syntax/双PS7 AST通过；首44/45夹具失败保留。0actual/0TS/0新.NETbuild，最终九文件门禁/验收/提交见M47收据。 | 旧具体cleanup失败与history20/2因果、Code正常退出未知；真实新窗口/三宿主与安装/AOT/硬件/长稳/发布另验。 |
| M47 WB-48 Query history完成 | 🟡 / 本地合同验证，真实Host待验 | immutable FIFO/写ack与读屏障、50 pending/持久50条cap、当前失败可观察/队列恢复；真实production command/panel的确定性Memento夹具与唯一TS编译通过，0actual。旧冻结树完整门禁已过；foreign追加后旧窗口未提交，现以新窗口重新冻结/完整门禁与WB48集成，实际见M47记录与git log。 | 尚非旧20/2根因确证；下一片独立cleanup固定安全subcheck后新冻结真实history/Code退出窗口。安装/AOT/硬件/长稳/三宿主另验。 |
| M47 WB-47 新真实Host窗口 | 🟡 / history失败检查点 | metadata-only、TS/Node/双PS7 AST通过；唯一actual三查询2/1/45行与真实参考同、Host API restored；history20/2失败，Code1/null、runner hostOutcome null。原FAIL与process/runtime false保留；根283对象回收、current tuple/event审计分列，八文件完整门禁/本地提交见M47记录。 | actual1/1已关闭；下一片定位history与独立cleanup失败，不推断旧候选全部恢复或正常退出。向导/DOM分页/Notebook/LSP、OS文件窗口/安装/AOT/硬件/长稳与完整三宿主另验。 |
| M47 WB-46 有界候选重采 | 🟡 / 合成合同完成，真实Host待验 | 一次fresh批次仅补同PID/creation/parent缺命令，原/fresh完整父链至exact Node anchor不变；12hop/1秒/stop保持，async串行。最终37/37、Node/双PS7 AST与源码复核通过，0actual/0新build；最终九文件门禁/提交见M47记录。 | WB45原FAIL/false/null不改，不承诺旧五candidate全部恢复；新metadata/冻结窗口补history/API与正常Code退出。生产maxRows、向导/DOM分页/Notebook/LSP、WB41 OS与完整三宿主另验。 |
| M47 WB-45 身份先验安全诊断 | 🟡 / 诊断切片完成，完整实跑❌ | fixed安全subreason/字段/数值与单读getter，不减原identity/ledger/event/stop合同；最终26/26、既有20/20、TS/PS7与源码复核通过。新run五candidate缺commandLine首guard已证；current2/selection1/EXPLAIN45与真实reference相同，原FAIL/false及根279对象回收保持分列，最终十文件门禁/提交见M47记录。 | history/host-result缺、API恢复与正常Code退出未证；两次调用已用尽，不第三跑。缺命令需先设计有界fresh重采/完整anchor合同；旧WB44 unknown不能猜因果。生产maxRows、向导/DOM分页/Notebook/LSP、WB41 OS、安装/AOT与整体三宿主另验。 |
| M47 WB-14 VS Code资源导航 | 🟡 | 九模型/index/backup资源与Web导航入口已提交 `e7cf2fe5`，Node20/20与本机Extension Host注册检查；浏览器回选/认证夹具由WB-16补证据。 | 真实Server权限、外部OS浏览器交接、VSIX与发布。 |
| M47 WB-15 Studio客户端 | 🟡 | 宿主身份/lifecycle、保守操作门禁与迟返隔离已提交 `e1f93a69`；Node10/10、全Web137/137、Studio定向40/40、TypeScript/Vite与浏览器夹具8/8。 | 真实Server、WebView2/干净Windows、安装与三宿主全旅程；旧Header与完整Explorer异步组合另验。 |
| M47 WB-16 导航/认证与部署base | 🟡 | 已提交 `0bb628ad`；登录返回、base/SSE和index分组修复，Node9/9、全Web146/146、两部署浏览器各17/17、根/代理构建、本机真实Host13节点命令调用及独立复核/完整门禁通过。 | 真实Server权限/代理部署、远程SSE、同origin存储隔离、VSIX与三宿主全旅程。 |
| M47 WB-17/WB-27 Relation Workbench | 🟡 | WB-17迁移 `f1978263`；WB-27新Web→本机Kestrel分页50/200/尾51与当前导出、两insert四终态、COMMIT冲突及撤权403/regrant锁存3/3。返回编辑保留暂存，影响数取COMMIT避免重复；Node20/20、全Web300/300、Chrome fixture12/12、共享runner Document真实3/3、build与独立复核通过。 | 显式安全恢复、完整权限/SQL名称矩阵、物化/字节/总堆预算、完整九模型/三宿主、登录UI/readonly props、安装/AOT/发布。 |
| M47 WB-19/WB-28 FullText Workbench | 🟡 | WB-19迁移已有；WB-28新Web→本机Kestrel Top-K20/100/当前导出、数据库Admin同步重建151权威终态、撤权403/regrant READ锁存真实3/3，成功证据独立落盘及SHA256核验。Node15/15、全Web300/300、Chrome fixture8/8、TypeScript/Vite与独立复核通过；生产组件/Server不改。 | 显式读取恢复、完整权限矩阵/typed全文分页/facet/highlight、服务端扫描/物化/字节/堆预算、登录UI/readonly props、完整三宿主和发行物。 |
| M47 WB-20/WB-32/WB-33 Vector Workbench | 🟡 | 原名/六态、请求快照/迟返隔离、Top-K100与Profile缺失门禁已有切片。WB-32补真实READ L2 Top-K20/100、当前JSON/CSV/history与Search403；WB-33修当前child拒绝typed上行、原目标/render generation/authority校验及父载荷清理，Schema/auth/重挂/空身份ABA保持锁，旧拒绝不锁新上下文。Node39/39、全Web327/327、Vector/Measurement fixture13/13与20/20、build及新UI真实首轮3/3通过；三JSON/manifest核验，最终独立复核/完整门禁及提交见证据。 | 索引Profile、显式恢复、Server预算、子页完整写终态、Recall/模型质量、三宿主与发行物。 |
| M47 WB-21/WB-29 KV Workbench | 🟡 | WB-21原名/六态/锁存/readonly与1000项/4096字节预览已有；WB-29新Web→本机Kestrel首轮3/3，真实Scan100/opaque cursor尾51、Get/当前JSONL，普通WRITE三审批NX成功/未应用影响0/交换版本history与管理员Get对拍，撤权403/regrant READ锁存。Node17/17、全Web300/300、Chrome fixture12/12与build通过，三成功JSON/manifest独立落盘，生产组件/Server不改。 | 显式恢复、完整权限/atomic/Int64/TTL/CAS、瞬时原子结果表展示、Base64解码/传输/字节/堆预算、三宿主和发行物。 |
| M47 WB-22/WB-34 MQ Workbench | 🟡 | WB-22原名/六态/权限/一次审批/有界预览与Seek/auto已有；WB-34新增普通READ100/51当前JSONL/history、正常WRITE Publish201 offset151/Ack200 nextOffset1与管理员对拍、旧审批REVOKE403清载荷/READ重授及同tokenSchema200锁存真实首轮3/3。Node22/22、全Web327/327、MQ fixture16/16/build和源码复核通过，三JSON195806字节/manifest核验；成功独立复核、最终完整门禁与提交见证据。 | 冒号Topic真实400不支持、真实文件导入/显式恢复、Int64/Nack全矩阵、metadata/解码/传输/堆/Server预算、实例恢复、三宿主和发行物。 |
| M47 WB-23 Graph Workbench | 🟡 / Graph Beta | 权限锁存、只读浏览/元素读取/导出、请求代际隔离、客户端 10～1000 总元素画布预算、32项/4096字符 Inspector、safe-number ID 门禁及维护终态校验完成本地切片；Node27/27、Graph Chrome16/16、既有浏览器3/3、真实Kestrel兼容4/4及独立复核通过。 | 完整 Graph Int64 字符串身份、真实新 UI 权限/恢复、服务端长期预算、三宿主、AOT、固定硬件和发行物。 |
| M47 WB-24/WB-25/WB-37 Object Workbench | 🟡 | WB-24读取锁存/代际/readonly与1000列表/4096 Range，WB-25写终态已有；WB-37接续首run新Web真实3/3：151原DTO/100+51 continuation/当前51截断结果、206 Range4096/Download8192与history，普通text一次审批PUT200/69字节完整DTO及管理员GET/list/history1，旧审批REVOKE403/admin404/先前值保持和READ重授同tokenSchema200六tabs锁存。三JSON335381字节/manifest及独立源码复核通过，完整成功复核/最终门禁/本地提交见证据。 | 初始三失败与未知reset保留；Blob body unavailable/null不计raw上传字节观测。完整语义/Multipart/Server预算/显式恢复/OS对话框/三宿主/安装/发行物另验。 |
| M47 U01～U05 | 🚧 | 设计、首批共享合同、结果/审批语义及 Web Admin 页面切片已有局部实现。 | 完整九模型适配器、分页/取消/离线组合、真实权限与全量生产旅程。 |
| M47 U06～U08、U10 | 📋 | 已记录规划边界和退出条件。 | Studio、VS Code、WorkBuddy/stdio bridge、manifest/签名/插件安全尚未启动完整验收。 |
| M45-C01 首批实现 | 🟡 | TAG/time 分组首批代码、SQL/EXPLAIN 合同和定向回归已完成。 | C02～C09、更新/删除修正、增量物化、恢复预算及真实性能证据。 |
| M44、M46 及其余 4.5 必选包 | 📋 | 已有设计专题、现存底座和验收边界记录。 | 计划中的 AI 应用、编码/成本优化及对应真实质量、容量、恢复和发布验收尚未启动或未闭环。 |

历史上已经完成且不再作为当前待办的范围，请看 [CHANGELOG 完成归档](CHANGELOG.md#roadmap-completed-archive-2026-10-03-45)；路线图下方的“里程碑总览”保留每个里程碑的剩余交付。

## 4.5 目标与范围

**把九模型从可组合的数据能力推进到有真实效果、有资源边界、可恢复的 AI 与分析应用底座。** 应用覆盖工业诊断、业务经营分析、文档知识与资产检索；工业是重点场景，产品保持通用多模型引擎定位。

本版必选：M44-A01~A07、M45-C01~C09、M46-S01~S09、M47-U01~U09，以及 V45-X01~X06、X08 中注明的有界合同和适用验收。M44-A08/A09、M45-C10、M47-U10、V45-X07 是条件项；M44-A10 是后续候选。每包拆成单一职责 PR，不将完整研究集合塞入一个实现。

本版不纳入 TsFile 适配、导入导出或替换持久格式。参考其它产品机制后按 SonnetDB 当前存储、类型、事务和嵌入式边界独立改进，不以复制语法、算法数量或商业功能清单作为完成条件。完整分片集群、九模型分布式事务、任意外部副作用 exactly-once、完整 SQL/GQL/S3 兼容均不由本路线自动承诺。

### 代表产品研究与采纳原则

按产品家族覆盖主流关系、分析、时序、文档、缓存、搜索、向量、图和持续计算产品。官方来源、版本/商业边界及未核实项写在专题中；不声称穷尽所有产品或已完成同条件性能对照。

| 产品家族与代表 | 学习机制 | SonnetDB 采纳方向 |
|---|---|---|
| PostgreSQL / MySQL / SQLite | 聚合类型/NULL、事务/WAL、快照、统计及代价可见性 | 精确语义、资源指标与恢复；不复制全部方言，未取得正文的项保持待核。 |
| DuckDB / ClickHouse | 批执行、聚合 state、并行合并、统计跳读与列式成本 | 在 M33/M41 深化，兼顾嵌入式及多模型并发。 |
| TDengine / IoTDB / TimescaleDB | 设备/维度分析、时间函数、rollup、迟到修正与持续聚合 | TAG 分组扩展为通用聚合/增量计算，区分静态属性与观测。 |
| InfluxDB / QuestDB / VictoriaMetrics / DolphinDB | 窗口、ASOF、采样/填补与流聚合资源取舍 | 逐项核对已有函数；内存态流聚合不等同持久 event-time 计算。 |
| Flink / Materialize / RisingWave | merge/retract、event time、检查点与增量物化 | 复用 M43 任务/投递，首批限定可证明等价的 SQL 子集。 |
| MongoDB / Redis | 管道聚合、索引、持久性取舍与状态 | 文档/KV/MQ 参与应用，不伪称跨模型原子事务。 |
| Elasticsearch / Qdrant / Weaviate / pgvector | 混合召回、过滤、重排、相关性与向量工作集 | 延续 M35/M36 的索引与 RRF，强化真实语料、ACL、删除同步。 |
| Neo4j GraphRAG / TDgpt / IoTDB AINode / pgai | 图证据、时序推理、模型调用与版本 | 已有预测/RAG/GraphRAG/provider 推进到受治理应用。 |
| Snowflake Cortex / Databricks 等 AI 平台 | 质量/成本、批量推理、评测与应用闭环 | 学习治理与旅程；托管商业功能不算开源引擎现状。 |
| dbx / Tabularis / DBeaver / DataGrip / pgAdmin / Compass / RedisInsight / Kafka UI / MinIO Console / Neo4j Browser | 统一 Explorer、SQL/Notebook、结果平面、模型专用工作台、MCP/插件和发布分发 | 由 M47 统一三面工作台；复用交互语义与安全边界，不复制通用多库协议或商业功能清单。 |

来源与选择理由见 [AI 专题](docs/design/sonnetdb-45-ai-applications.md)、[聚合专题](docs/design/sonnetdb-45-aggregation-continuous-compute.md)、[存储专题](docs/design/sonnetdb-45-storage-performance.md) 和 [M47 统一管理工作台专题](docs/design/m47-unified-management-workbench.md)。采纳代码前逐组件核查许可：TDengine AGPL 核心不可直接搬入继续只标 MIT，其他许可也保留相应声明，不从 SDK 许可推断全产品许可。

## 里程碑总览

| Milestone | 主题 | 状态 | 4.5 保留工作 |
|---|---|---|---|
| 14 / 27 | Copilot、AI/Agent 数据访问与治理 | 🚧 | 真实 provider 质量/成本、双网与 Studio 实机；Agent Framework 迁移单独评估，自研 CopilotAgent 不改称已基于该框架。 |
| 19 / 25 | 时序与文档容量 | 🟡 | 四档时序、million/ten-million 文档固定硬件与恢复，复用 runner/verifier。 |
| 20 | 多模型 Parity | 🚧 / 历史窗口未过 | 最新候选 light/full 原始证据及七天 scheduled 观察，不由规划推断线上状态。 |
| 29 | Studio 安装与宿主 | 🟡 | 干净 Windows、升级/卸载、WebView2、端口冲突与生命周期。 |
| 35 | 语义内容与多模态检索 | 🚧 | 真实质量、模型换代/回滚、删除同步、容量与硬件；持久摄取/provider 基础不重做。 |
| 36 | 九模型易用性 | 🚧 | 完整真实旅程、远程 parity、取消/恢复、对象分页与 MQ 实例恢复。 |
| 39 | SQL 触发器生产观察 | ⏳ | 已归档研发之外的混合 DML、deferred/outbox 尾延迟及长期 SLO。 |
| 40 | 原生属性图 | 🟡 / Graph Beta | 外部对拍、容量、AOT、Couplet、hard-kill 及长期 gate；AI 图应用不替代底层门禁。 |
| 41 / 42 | 规划器与九模型性能 | 🚧 | 全链路有界读取、总资源预算、页/I/O 成本、冷启动及固定架构/168 小时。 |
| 43 | 十四能力与生态发布收口 | 🚧 | 远程 CDC/schema/冲突、恢复、业务副作用边界、完整旅程及原始报告。 |
| **44** | **AI 应用与可治理推理** | **📋** | 预测/异常、证据 RAG、模型治理、可恢复推理任务与真实效果门禁。 |
| **45** | **聚合与持续计算深化** | **🚧** | C01 首批 TAG/time 分组已完成局部实现；仍需通用 state、更新删除修正、增量物化/rollup 与批流等价。 |
| **46** | **存储编码与执行成本优化** | **📋** | 编码策略、整数/高熵回退、范围解码、统计精度及存储成本。 |
| **47** | **统一数据库管理工作台与三面发布** | **🚧** | WB-00～WB-16 本地切片已提交；Studio客户端、VS Code→Web回选/认证及显式部署base已有本地证据，真实三宿主旅程、九模型、AI/MCP与发布矩阵仍未闭环。 |

M22 保持上层应用候选；样例验证通用合同，行业规则不直接内置引擎。M0~M13、M15~M18、M21、M23/M24/M26/M28/M30~M34/M37/M38 及其它完成代码范围只在 CHANGELOG 追溯。

## Milestone 44 — AI 应用与可治理推理

复用已有 `forecast(linear/holt_winters)`、`anomaly(zscore/mad/iqr)`、`changepoint(cusum)`、在线 chat/embedding provider、显式 ONNX profile、持久摄取、Hybrid/RRF/重排、typed MCP，以及知识图谱/GraphRAG 上层合同与 typed SDK 投影。增量是应用效果与可治理推理，不把已有实现重新包装为新功能。

### 九模型如何支撑 AI

| 模型 | 应用角色 | 必须验证的边界 |
|---|---|---|
| 时序 | 趋势、预测、异常、变点与时间特征 | 时间切分防泄漏、缺失/乱序、基线、误报及区间校准。 |
| 关系 | 业务实体/指标、标签反馈与特征元数据 | 类型/SQL、权限、血缘；模型结果不隐式执行业务写操作。 |
| KV | 热状态、幂等键、缓存与 feature lookup | TTL/模型/ACL 进入缓存键，原子性范围和恢复明确。 |
| 文档 | 知识、标注、结构化输入与结果 | 更新/删除/TTL 同步、出处和 schema 可追踪。 |
| 全文 | 关键词与精确证据召回 | analyzer、语言、ACL 与相关性，不仅测返回命中。 |
| 向量 | 语义召回与相似样本 | profile/维度/版本、过滤 Recall、重建与回滚。 |
| 对象 | 原文、多模态内容及模型资产 | checksum、大文件/Range、出域与删除引用。 |
| MQ | 推理/重建触发、结果与告警 | 至少一次、稳定身份、背压、offset 与实例恢复。 |
| 图 | 关系、路径证据与影响传播 | 有界遍历、出处/权限/时间、无证据拒答，Graph Beta 继续公开。 |

### 工作包

| ID | 优先级 / 范围 | 增量交付 | 依赖与退出条件 |
|---|---|---|---|
| M44-A01 | P0 必选 | 模型/profile、版本和输入输出增量合同 | 延续 M27/M35；维度/版本/能力不匹配拒绝，切换可回读/回滚。 |
| M44-A02 | P0 必选 | 权限、出域、时限、成本/并发预算及追踪 | 接 V45-X02；未授权数据不入检索/prompt，缓存不跨 ACL/模型。 |
| M44-A03 | P0 必选 | 时序预测、异常及诊断应用 | 复用算法，接 M45 分组；独立测试集对照 naive/seasonal 基线，验证误报/不规则采样。 |
| M44-A04 | P0 必选 | 有出处的混合检索与证据回答 | 复用 RRF/重排；真实召回、引用支持率、无证据回答及删除同步。 |
| M44-A05 | P0 必选 | 可恢复的有界批量推理任务 | 复用摄取/M43；checkpoint、结果幂等、取消/恢复、积压及外部未知结果。 |
| M44-A06 | P0 必选 | 真实模型/应用评测与发布 profile | 质量/延迟/成本/版本可复算；hash fallback/tiny fixture 仅作合同证据。 |
| M44-A07 | P0 必选 | 工业诊断、业务分析、知识/资产助手三条旅程 | 嵌入式和真实 Server/SDK/MCP，覆盖九模型职责、权限/删除/失败/重开及批准写回。 |
| M44-A08 | P1 条件 | provider 原生流式及中断/预算 | 部分现有 provider 为完整响应；先对拍能力发现、首 token、取消、成本及续流。 |
| M44-A09 | P1 条件 | 有界 GraphRAG 证据扩展 | M40 适用 gate；真实对照收益，不足时降级、不编造路径。 |
| M44-A10 | P2 后续 | 高级离线/多模态/自适应模型候选 | 有任务、资产许可及质量/内存证明再单项纳入，不承诺训练平台。 |

逐项设计与评测见 [M44 专题](docs/design/sonnetdb-45-ai-applications.md)。应用评测不关闭 M27 双网或 M35/M40 底层容量门禁。

## Milestone 45 — 聚合与持续计算深化

覆盖关系、measurement、文档可适配数据及增量，不限设备分析。复用 M31 类型、DISTINCT 子集、时间函数、M33 下推/sketch、M37 全量物化、M41 GROUP BY spill 和 M43 持久滚动/滑动/会话窗口。measurement 仍缺 TAG/普通列分组；有 `Merge` 不代表可撤回，持久数值窗口拒绝坏值/NULL 也不自动等价 SQL 忽略 NULL。

| ID | 优先级 / 范围 | 增量交付 | 依赖与退出条件 |
|---|---|---|---|
| M45-C01 | P0 必选 | 类型/NULL/空集/overflow/名称合同，measurement TAG＋时间分组 | 原名/引号及关系兼容；SELECT/HAVING/分组键支持矩阵在执行前校验。 |
| M45-C02 | P0 必选 | 版本化聚合 state 与 Add/Merge 复用 | 分别声明结合/交换/顺序敏感性；COUNT/SUM merge 不冒称幂等，投递去重在输入身份层。 |
| M45-C03 | P0 必选 | 支持源的 before/after、retract、更新/删除修正 | 非可逆 MIN/MAX/sketch 有界重算或拒绝；缺 before-image 不静默按追加处理。 |
| M45-C04 | P0 必选 | 有限 SQL 的增量物化/rollup 与有界回填 | 复用 generation/位点/调度；快照增量无缺口、失效重算、原子发布与依赖变更。 |
| M45-C05 | P1 必选 | SQL 批流窗口及分区 event-time 推进 | 复用窗口；乱序/迟到/idle、水位回退/关闭及回填可对拍。 |
| M45-C06 | P1 必选 | 精确/近似分位、distinct/sketch 准确性和资源 | 复用 TDigest/HLL；误差/merge/偏态/种子公开，不冒称精确结果。 |
| M45-C07 | P1 必选 | 时间加权、率、积分、采样/填补边界和 state | 核对已有函数；边界点、重复时间、counter reset、缺失及最大跨度。 |
| M45-C08 | P1 必选 | 聚合 state 预算、落盘及恢复 | 复用 spill/V45-X02；高基数准入，取消/磁盘满/坏状态不损已提交结果。 |
| M45-C09 | P0 必选 | 批流、内存/落盘、跨端对拍和观测 | 同事件日志离线/增量等价；重投/更新/删除/重开，计数精确、浮点误差有声明。 |
| M45-C10 | P2 条件 | ROLLUP/GROUPING SETS、扩展窗口与 ASOF | 真实查询驱动单项选择；CUBE/count/state/change windows/ASOF 不全部默认纳入。 |

冻结首批源、AST、聚合及变更种类；不支持的 JOIN/递归/UDF/事务在计划阶段拒绝。M45 负责计算语义与状态，M43 负责运输、位点、订阅及任务生命周期。详见 [M45 专题](docs/design/sonnetdb-45-aggregation-continuous-compute.md)。

## Milestone 46 — 存储编码与执行成本优化

编码器已有，但 `SegmentWriterOptions` 时间戳/值编码默认均为 `None`，V2 Int64 仍 raw 8B。v6 已内嵌 HNSW/sketch。mmap 仍有复制；解码缓存按 reader 设置，共享 HNSW 缓存及 KV I/O 预算另有实现。不能把可选编码、mmap 或局部额度写成默认压缩、zero-copy 或进程总内存上限。

| ID | 优先级 / 范围 | 增量交付 | 依赖与退出条件 |
|---|---|---|---|
| M46-S01 | P0 必选 | 基线、版本、默认/显式编码策略及收益门槛 | raw/现有可选编码都测，默认是否切 auto 由语料与回归决定。 |
| M46-S02 | P0 必选 | 整数 delta/RLE/bit packing 与 raw 回退 | 全值域/溢出、稀疏/乱序 round-trip；新语义不偷复用 v6 标志。 |
| M46-S03 | P0 必选 | 无损浮点/Boolean 与高熵回退 | NaN 位模式、±0、Infinity；无默认量化/有损压缩。 |
| M46-S04 | P1 必选 | 字典高基数与稀疏 presence 成本 | 字典扩张、Unicode、大值及缺失/NULL 分开，超预算不截断。 |
| M46-S05 | P1 必选 | 分块自适应与范围解码 | 块/检查点结合窄读测 I/O/CPU/分配，防压缩导致读放大。 |
| M46-S06 | P0 必选 | 统计跳读精度 guard | Int64→double 等边界；不得依据不可靠元数据剪枝，精确差分及旧段回读。 |
| M46-S07 | P1 必选 | 解码分配、缓存与读成本接线 | 接 V45-X01/X02；租约/取消/淘汰/mmap 成本可观测，不新造资源管理器。 |
| M46-S08 | P1 必选 | small-segment/WAL/compaction 成本 | 复用 manifest/调度；写放大/fsync、合并读尾延迟与恢复共同验收。 |
| M46-S09 | P0 必选 | 统一语料、兼容/损坏及性能验收 | commit/硬件/配置/原始样本；旧格式可读、不兼容写拒绝，收益与代价分开。 |

指标包含磁盘字节、读写放大、解码 CPU、P95/P99、分配/GC 和恢复，不预设倍数。落盘改动先设计版本/CRC，遵守格式升级、CHANGELOG、迁移或显式拒绝规则。详见 [M46 专题](docs/design/sonnetdb-45-storage-performance.md)。

## Milestone 47 — 统一数据库管理工作台与三面发布（UI）

M47 将 Web Admin、Studio 桌面和 VS Code 扩展规划为“一套核心、三个宿主、三个发布物”。共用资源上下文、九模型能力矩阵、设计令牌、Explorer、Workspace、结果平面、Inspector、History、Approval、MCP/AI onboarding 和验证夹具；宿主分别承载完整治理、原生桌面和开发者 Remote-first 子集。M29 的已有工作台与原型是实现基线，M47 不重新包装已完成的页面为新功能。

对照学习范围包括 dbx、Tabularis、DBeaver、DataGrip、pgAdmin、MongoDB Compass、RedisInsight、Kafka UI/RabbitMQ Management、Milvus Attu/Qdrant Console、Kibana/OpenSearch、MinIO Console 和 Neo4j Browser。逐模型、三面边界、发布形态、MCP/插件安全和原型目录见 [M47 统一管理工作台专题](docs/design/m47-unified-management-workbench.md)。

**M47 设计基线已确认，生产迁移按切片执行。** 外壳、七个一级/二级导航、九模型数据库逻辑资源树、共享对话框/状态和 MQ `database + Topic` 身份已确认；当前 [设计评审包](docs/design/m47-unified-management-workbench/README.md) 仍记录逐页像素与三宿主证据边界。规划覆盖七一级（概览/工作台/观测/数据流/AI 与 MCP/治理/设置）、30 个全局页面、九模型和 166 个任务页签（模型任务 60 个），不等同逐页生产完成。MQ 逻辑作用域在数据库资源树内，物理持久化为实例 `.system/mq`；单库备份不覆盖共享 MQ。

| ID | 优先级 / 范围 | 交付与退出条件 |
|---|---|---|
| M47-U01 | P0 | 竞品矩阵、完整菜单/导航、五区外壳、共享对话框/提示、九模型能力矩阵、三面边界和资源合同设计；先取得用户原型确认，再逐页细化与生产实现，不重复声明 M29 已实现内容。 |
| M47-U02 | P0 | `management-core` 规划与首批实现：typed API/MCP client、资源类型、能力/状态/权限/结果/审批合同、设计令牌；三个宿主各消费一个真实共享合同。 |
| M47-U03 | P0 | 统一 Explorer、Workspace、Result Plane、Inspector、History；九模型上下文一致，分页、取消、截断、离线和局部失败语义一致。 |
| M47-U04 | P0 | 九模型 adapter 和模型专用工作台：measurement、relational、document、KV、MQ、vector、full-text、object、Graph Beta；正常/空/错/只读/长内容状态可验证。 |
| M47-U05 | P0 | Web Admin 完整治理工作台：查询、编辑、导入导出、索引/策略维护、监控、审批、审计和九模型真实旅程闭环。 |
| M47-U06 | P0 | Studio 桌面宿主：同一 Web Admin 资源、native bridge、Managed Local、原生文件对话框以及干净 Windows/WebView2/升级卸载/端口/进程回收证据。 |
| M47-U07 | P1 | VS Code 开发者面：连接向导、可复用 Query/Notebook、分页结果、LSP/EXPLAIN、稳定宿主 Chat/MCP 能力或保留自有 Copilot WebView，并可深链接转交治理面。 |
| M47-U08 | P1 | WorkBuddy/Claude/Cursor/Codex AI Connect：现有 HTTP MCP、`sonnetdb mcp` stdio bridge、配置生成/自检、只读工具、凭据隔离、结果预算和数据外发说明。 |
| M47-U09 | P1 | M47 原型目录、完整页面/任务规范与三面发布矩阵：共享外壳、查询结果、审批/AI、九模型、Studio、VS Code 原型及正常/空/加载/错/只读/离线/长内容状态；HTML 评审稿、逐页最终视觉与真实宿主证据分别记录。 |
| M47-U10 | P2 条件 | manifest、签名/哈希、插件/连接器目录、权限撤销、升级/回滚和市场提交流程；先完成核心工作台和安全评审再启动。 |

### M47 实施状态索引（2026-10-06）

状态只反映当前本地证据：`🟢` 已完成本地切片，`🚧` 仍有实现或验收，`🟡` 指定切片完成但外部/宿主证据待补，`📋` 尚未启动。

| 范围 | 状态 | 当前证据与剩余边界 |
|---|---|---|
| WB-00～WB-03C | 🟢 | 设计基线、原型交互/页面合同、资源身份/能力合同与 Explorer 兼容证据已提交；静态/内存证据不等同真实 Server 或三宿主。 |
| WB-04～WB-04B | 🟢 | 导航与 Explorer→SQL 兼容预检已提交；迁移前证据不等同全量运行时验收。 |
| WB-05～WB-06 | 🟡 | 结果/草稿/历史/审批和生产外壳/七模块导航已完成本地切片；全量模型、真实宿主、安装、发布仍待补。 |
| WB-07～WB-10 | 🟢 | Explorer 资源身份、旧深链接回选、database 上下文及手动切库投影已提交并通过 Web 回归。 |
| WB-11 Measurement Workbench | 🟡 | 用户已确认页面基线；状态、动作、旧路由和 UI 证据已通过本地验证，提交为 `390ff526`。真实 Server、三宿主、安装、发布和全量九模型证据仍待补。 |
| WB-12/WB-18/WB-26 Web Admin Document Workbench | 🟡 | WB-12 `54c78755`、WB-18 `1d9465fb`为既有基线；WB-26取得本机新Web→真实Kestrel权限/Find100恢复、Aggregate1001→1000及Distinct501→500/1000unknown真实3/3，Node21/21、全Web295/295、fixture11/11与build通过。空IDs省略且显式IDs保留；登录UI/readonly props、Server预算、Advanced完整读写及三宿主另验，提交以最终完整门禁为前提，实际哈希见git log。 |
| WB-13 SonnetDB Studio 宿主合同 | 🟡 | 宿主身份/URL与Managed Local生命周期合同已提交 `74835847`，定向34/34、独立复核和完整提交门禁通过；Web消费由WB-15另验，干净Windows/WebView2、安装与发行物证据仍待补。 |
| WB-14 VS Code Workbench 合同 | 🟡 | 九模型资源/深链接入口已提交 `e7cf2fe5`，原名与旧key保留，Node20/20、本机Extension Host、独立复核与完整门禁通过；WB-16补浏览器回选/认证夹具，真实Server权限和VSIX仍另验。 |
| WB-15 Studio Web客户端合同消费 | 🟡 | 已提交 `e1f93a69`；宿主原名身份、完整生命周期、保守操作门禁、初始化/状态/保存迟返与目录ABA隔离；Node10/10、全Web137/137、Studio定向40/40、TypeScript/Vite、浏览器夹具8/8、独立复核和完整提交门禁通过；真实Server/桌面安装及三宿主整体仍另验。 |
| WB-16 VS Code→Web导航/认证 | 🟡 | 已提交 `0bb628ad`；九模型/index/backup最终回选、登录返回、显式base/SSE、同名MQ及index分组收口；Node9/9、全Web146/146、根/代理构建、浏览器各17/17、真实Host13节点命令调用、独立复核与完整门禁通过。真实Server/代理部署/远程SSE/同origin存储隔离/VSIX另验。 |
| WB-17 Relation Table Workbench | 🟡 | 已提交 `f1978263`；Node15/15、全Web161/161、TypeScript/Vite、Chrome12/12、既有设计器2/2、独立复核及完整代码门禁通过；readonly浏览/结果导出/纯DDL，HTTP403清载荷锁存，缺终态/断连/408/5xx为unknown。真实Server权限、预算、三宿主与发行物另验。 |
| WB-19 FullText Workbench | 🟡 | 已提交 `2f9a5477`；原名/六态/Top-K100、载荷清理与一次审批终态，Node15/15、全Web184/184、Chrome8/8和既有真实Kestrel兼容4/4。新客户端权限/写旅程、恢复、预算与三宿主另验。 |
| WB-20 Vector Workbench | 🟡 | 原名/六态、参数/身份快照、空Schema权限锁存、严格维度/Top-K100和子页门禁；Node17/17、全Web201/201、Chrome10/10、导入1/1和既有真实Kestrel兼容3/3。Profile、真实权限/恢复、服务端预算、子页写终态与三宿主另验；本轮提交哈希见git log。 |
| WB-21/WB-29 KV Workbench | 🟡 | 原名/权限锁存/readonly与有界预览已有；新Web→本机Kestrel首轮3/3补游标/Get/当前JSONL、NX成功/未应用影响0/交换实际版本+history、撤权403后READ/同token刷新锁存。Node17/17、全Web300/300、Chrome fixture12/12和build通过，成功JSON+manifest独立落盘；完整atomic/恢复/资源预算与三宿主另验，实际提交见git log。 |
| WB-22 MQ Workbench | 🟡 | 原名/六态、锁存/readonly、实际Topic同步代际、一次审批与unknown、Browse1000/Inspector4096、Seek25窗/60秒和自动采样12轮/60秒；Node22/22、全Web240/240、Chrome16/16+既有3/3、真实Kestrel兼容2/2。真实新UI权限/恢复、完整metadata/资源预算和三宿主另验；本轮提交哈希见git log。 |
| WB-23/WB-35/WB-36 Graph Workbench | 🟡 / Graph Beta | 原名/六态、401/403锁存、readonly与请求隔离、有界画布/Inspector及safe-number门禁已有；WB-35真实Canvas250/10/1000、typed vertex、独立snapshot JSON导出/history、单vertex Upsert/version+1与REVOKE锁存取得3/3，Node27/27、全Web327/327、fixture16/16/build为该片证据。WB-36旧DLL正常导出151实际truncatedfalse定向失败后，两行修复精确顶点预算边哨兵；GraphEndpointTests17/17含8例新Kestrel矩阵、Server Release/trim-AOT分析0警告错误、修复后真实Web3/3/四JSON1015208字节manifest通过。完整最终门禁与本地提交见git log；完整Int64/edge/import/维护/显式恢复、通用Server预算、NativeAOT发布及三宿主另验。 |
| M47-U01～U03 | 🟡 | 设计、首批合同和共享结果/审批语义已有局部实现；三个宿主真实消费、完整分页/离线/取消证据仍待补。 |
| M47-U04～U05 | 🚧 | 九模型专用工作台和 Web Admin 仍按页面切片迁移；WB-11 是其中一个页面样板，不代表整包完成。 |
| M47-U06～U08 | 📋 | Studio、VS Code、WorkBuddy/stdio bridge 与配置自检尚未形成完整真实宿主验收。 |
| M47-U09 | 🟡 | 原型目录、页面规范与发布矩阵已提交；逐页最终视觉、三面真实旅程和发布证据仍待补。 |
| M47-U10 | 📋 | 条件项，尚未启动。 |

逐个 WB 的文件归属、测试数量、提交哈希和剩余边界以 [Workbench 队列](docs/design/m47-unified-management-workbench/work-queue.md) 与 [HANDOFF](HANDOFF.md) 为准；本表用于路线图快速查看，不能替代两份交接记录。

M47 的代码边界是“共享合同和组件优先、宿主适配器隔离”：不把 VS Code 变成完整 Web Admin，不改变九模型存储语义、SQL 名称合同、MCP 只读边界或 M29 的写审批规则。三面分别产出 Web 静态资源、Studio 安装包和 VSIX，但使用同一版本、MCP contract version、能力清单和兼容矩阵。任何一个宿主未通过自己的安装、Electron/Extension Host 或真实 Server 旅程，不能把三面整体标为发布完成。

## 4.5 跨模型能力与性能闭环

沿用既有归属，不重复建设核心服务。

| ID / 归属 | 优先级 / 范围 | 交付与验收 |
|---|---|---|
| V45-X01 / M41、M42 | P0 必选 | 执行器→REST/Frame→ADO/SDK 批/惰性读取、首行、取消/断连、行/字节准入及工作集；明确阻塞/spill，不把切帧/预览当全链路有界。 |
| V45-X02 / M42 | P0 必选 | 基于 SQL worker/内存、KV I/O 和缓存，增加数据库/进程总预算、分类归属及公平准入；治理 reader 缓存随段数放大、共享 HNSW 跨库配置、AI/后台与前台竞争，记录 CPU/I/O/heap/排队/释放。 |
| V45-X03 / M43 | P0 必选 | 已有本地 CDC/桥接/任务之外的有界远程合同：schema/version、历史增量、冲突/删除/TTL、续传/积压/offset；首批限定源和拓扑，不把副本称为 HA。 |
| V45-X04 / M36、M43 | P0 必选 | MQ 实例 snapshot/restore 与数据库恢复组合；对账 offset、任务/checkpoint、物化结果、派生 AI 索引/模型引用的一致性点与顺序，公开 RPO/RTO 和非原子边界，单库备份不含 `.system/mq`。 |
| V45-X05 / M35、M36、M29 | P1 必选 | 全文相关性/重建、filtered ANN/Recall、KV continuation/TTL、对象高变更率分页与大文件校验、图有界结果、SDK/CLI/Workbench 对拍；按明确缺口实施，UI 不先于入口。 |
| V45-X06 / 既有证据里程碑 | P0 必选 | 固定语料/机器/版本/持久性/客户端；分开功能、质量、容量、恢复及长期报告，九模型/AI/聚合/编码可复算，失败样本保留，冻结适用门槛。 |
| V45-X07 / M43 架构决策 | P2 条件 | 主备/复制先定义模型边界、fencing、单写者、日志缺口、未知提交、RPO/RTO 和 split-brain 矩阵；真实需求及一致性证据后单独实现，不承诺 4.5 自动 failover/完整分片。 |
| V45-X08 / M36、M44、M45 | P1 必选 | measurement/TAG/FIELD 上的设备/业务实体模板、静态属性映射、稀疏观测及跨实体分析；复用关系实体，不新增原生模型，名称/类型/模板演进与普通 SQL 一致。 |

### 九模型验收与参考负载

| 模型 | 验证重点 | 对照方式 |
|---|---|---|
| 时序 | 高基数、稀疏/乱序、TAG 分组、rollup、编码/恢复 | TDengine/IoTDB/TimescaleDB 等同语义子集，embedded/server 分开。 |
| 关系 | 分组/窗口、精确类型、增量修正、JOIN/排序及首行内存 | PostgreSQL/SQLite/DuckDB/ClickHouse 分 OLTP/OLAP 负载。 |
| KV | 大 keyspace/TTL、MultiGet、热点、游标/冷读 | 复用 Redis parity，恢复/缓存/网络分开。 |
| 文档 | 索引聚合、TTL/更新删除、million/ten-million 与恢复 | MongoDB 语义参考，不承诺 BSON/wire/官方 Driver。 |
| 全文 | 多语言、精确/模糊词、ACL、相关性/重建 | 复用 Meilisearch，Elasticsearch 作评测设计参考。 |
| 向量 | 过滤选择率、Recall@K/nDCG、换代、ANN/scan 内存 | Qdrant/pgvector 同维度/度量/召回档，合成不替代真实语义。 |
| 对象 | continuation、高变更率、大文件/Range/checksum/恢复 | MinIO 已支持合同，不外推完整 S3/SigV4。 |
| MQ | 至少一次、offset、重投、backlog/实例恢复 | NATS JetStream；TMQ/Kafka 概念参考不构成协议兼容。 |
| 图 | 路径/过滤、引用、算法、容量/恢复 | Neo4j/PostgreSQL、LDBC/Graphalytics，Graph gate 独立。 |



1. **基线与合同冻结：** M44-A01/A02/A06、M45-C01/C02/C09、M46-S01/S09、V45-X06；冻结支持矩阵、语料与门槛，不重做归档实现。
2. **基础实现与成本治理：** M45 分组/state、M46 编码/精度 guard、V45-X01/X02；每包有必要定向回归、Release 和适用 AOT。
3. **持续计算与恢复：** M45-C03/C04/C05/C08 接既有 M43，V45-X03/X04/X08；不支持源/操作及未知结果明确拒绝或进入恢复。
4. **AI 应用与九模型旅程：** M44-A03/A04/A05/A07、M45-C06/C07、M46 深化和 V45-X05；条件项独立证明收益后决定。
5. **性能、质量与发布论证：** 执行既有目标机队列和新应用/聚合/编码 gate，绑定同一候选；本机合成数字不升级生产结论。
6. **统一管理工作台与三面发布：** M47 先评审完整导航、五区壳、共享对话框/提示和一级/二级任务，再逐页细化；用户确认原型后推进共享合同、九模型工作台与生产迁移。Web Admin/Studio/VS Code 旅程、AI/MCP 入驻和版本兼容矩阵分别通过对应宿主证据；单端或原型 PASS 不升级为三面整体发布。

独立切片可并行；共享源码、构建输出和资源验证按所有权协调。单 PR 只做一个可审查切片。

## 真机验证待办

记录硬件/架构、commit、配置/版本/命令/时间、真实 P50/P95/P99、working set/heap/分配/GC、逻辑与物理 I/O 区别、正确性/恢复与原始样本。不可用项为 `NOT_RUN`/`NOT_READY`/`DEFERRED`。

| 来源 | 保留验收 | 当前边界 |
|---|---|---|
| M19 #125 | 百万 series、万 segment、20 次 kill/reopen、万 measurement | 固定 Linux x64 四档待证。 |
| M25 #174 | million/ten-million 文档写查/TTL/重建/备份恢复/内存 | 固定目标及 attestation 待证。 |
| M20 #136 | 最新候选 light/full 原始 backend/artifact、七天 scheduled | 保存的 9/25 三成功四失败，9/26 候选复验通过；本轮无新线上结果。 |
| M27 #184/#185/#187/#340 | 真实模型/成本、IdP/broker、双网/续流及 Studio | 功能/本地已归档，现场独立验收。 |
| M29 #258 | 干净 Windows、WebView2、升级/卸载、端口/生命周期 | 安装包/宿主已有，实机待验。 |
| M35 / M36 | 真实语料、模型换代、九模型跨端/权限/取消/删除/恢复 | 本地不替代完整旅程、远程或容量。 |
| M36 #323/#325/#326 | 对象大文件/分页/校验、MQ 实例/offset 恢复 | 不由单库备份推出实例一致性。 |
| M39 | 混合 DML、触发器/deferred/outbox 长期尾延迟 | 研发之外的生产观察。 |
| M40 #352/#367 / Couplet | 外部对拍、LDBC/Graphalytics、1m/10m、AOT、hard-kill/168 小时 | Beta 与联合环境 gate 独立。 |
| M41 / M42 | 固定 x64/ARM64、同语料、冷启动/首行/heap/I/O、恢复/168 小时 | 本地预算/快路径不代替目标机。 |
| M44 | 三条旅程真实质量/成本、防泄漏、删除/回滚/任务恢复 | 新规划，fixture/hash 不计效果。 |
| M45 | batch↔stream、merge/retract、late/update/delete、rollup/spill/reopen | 新规划，冻结首批子集/误差。 |
| M46 | 编码/高熵回退、旧新格式、损坏/极值/放大/冷读 | 新规划，不预填提升倍数。 |

2026-10-08 木垒现场只读核验已完成 5 个明确子项：当前 ARM64 配置/挂载、六个索引定义、真实 DESC/OFFSET 及 48/48 实际读取放大比、REST 默认完整与恰好 N/超 N 截断边界、真实流水三图完整读取/内容 SHA-256。见[完成清单及原始证据](docs/audits/mulei-field-readonly-verification-20261008.md)。这些子项标记为 ✅；证据绑定现场提交 `6a8d82d2`，冻结语料/现网规范化指纹对账、跨架构、全部模型/客户端、恢复/容量与 168 小时仍待验，表中整体边界继续保留。

## 待补验收证据

### M20 — Parity nightly

沿用 [9/25 回读](docs/audits/m20-nightly-readback-20260925.md)与[9/26 候选 light/full](https://github.com/IoTSharp/SonnetDB/actions/runs/36211622630)的范围。候选 verifier 核对同提交/版本十二类 workflow、最新 attempt、真实 backend 与原始 artifact；七天 scheduled 自 9/27 起为独立非阻断观察，不因手动复验或规划标记完成。其它适用质量/恢复/容量发布证据不能由此省略。

### M19 / M25 — 容量与发布

沿用[固定硬件容量合同](docs/benchmarks/m19-capacity-hardware.md)及 Document verifier；缩规模 hosted validation 仅证明对应行为。固定规模、硬件真实性/attestation、恢复及性能分别验收。

### M27 / M35 / M36 / M40 / M41 / M42

已有合同与 gate 不因新里程碑重编号、降门槛或重新声明完成。见[provider profile](docs/benchmarks/m27-provider-model-profile.md)、[M35 质量/预算](docs/m35-filtered-search-budgets.md)、[对象合同](docs/object-client-contract.md)、[Graph gate](docs/m40-graph-367-production-gate.md)、[SQL 内存边界](docs/benchmarks/m42-sql-result-bounds.md)。

## Milestone 43 — 十四套能力与生态发布总收口

#382~#384、本地 CDC/窗口/任务/DLQ/自动投递/桥接及汇总工具移至 CHANGELOG；这里只保留剩余。M44 应用、M45 计算语义、M46 编码各自归属新里程碑。

| 范围 | 状态 | 剩余交付 |
|---|---|---|
| #385~#390 | 🚧 | 远程同步、schema/冲突、离线续传、客户端及现场故障/容量。 |
| #391~#395 | 🚧 | 远程投递/接管、权限、业务副作用边界与现场恢复，已有本地底座不重做。 |
| #396 | 🚧 | 十四能力完整入口/权限/失败/取消/重启/对账及远程组合。 |
| #397 | ⏳ | 真实发布级原始报告，汇总工具不等于证据。 |
| #398~#402 | ⏳ / 草稿已有 | release 绑定、外部提交/反馈、公开案例、最终验收；提交/收录/排名分开。 |

既有细分与退出见[总索引](docs/roadmap-total-milestone.md)，外部 issue 见[路线快照](docs/github-issues-roadmap.md)。

CAP 生态适配新增统一 `SonnetDB.CAP` 项目（2026-10-09），按用户最新要求合并存储与传输，并通过 Data SDK 覆盖嵌入式与远程；Document 存储、MQ 传输、单 collection 业务+Outbox 事务、幂等索引和持久消费组登记已实现。真实本地/Server/CAP 定向验证及 NuGet 候选包见[集成合同](docs/design/cap-integration.md)；正式发布、Linux/强杀/掉电/固定硬件与长稳仍独立待验。EF Core/ADO SQL 存储、MongoDB wire、跨 collection 事务和跨进程消费者租约不在本片交付中。

## 性能观察项

| 编号 | 候选 | 进入条件 |
|---|---|---|
| PF1 | 级联删除按选择率切索引/哈希 | 1/10/50/100 父键矩阵收益及回滚等价。 |
| PF2 | fuzzy 高基数词典结构 | 100k/500k term 证实瓶颈及收益；原至少 2 倍候选门槛保留，不是当前性能声明。 |
| PF3 | ANN tombstone 区间/gate | 墓碑是主要成本且不降召回/精确语义。 |

候选不自动转为必选；有基准后并入 M46/V45-X05 单项评审。

## 4.5 完成与发布判定

1. 必选代码入口、矩阵、回归及适用 AOT/兼容完整；条件项明确交付或延期，不掩盖必选残余。
2. 三条 AI 旅程有真实模型/数据的可复算质量与成本；九模型职责、ACL/删除/取消/恢复和显式写回可追踪。
3. 支持子集的批流/增量/重算等价，迟到/更新/删除和不支持路径明确；至少一次与计算幂等分层，不承诺未知副作用 exactly-once。
4. 编码无损、统计剪枝正确、旧新格式恢复及高熵回退通过；默认由基准决定，优化不牺牲确认写持久性。
5. 适用固定硬件、远程、安装、容量、恢复、跨架构及长期证据绑定候选；保持 scheduled 独立观察与 Graph Beta gate。
6. 主路线、总索引、专题、能力索引及 CHANGELOG 状态一致；研究/规划只记录文档已变更，未来功能实际交付后才进 CHANGELOG。

## 已完成范围索引

见 [2026-10-03 4.5 整理归档](CHANGELOG.md#roadmap-completed-archive-2026-10-03-45)及[原归档](CHANGELOG.md#roadmap-completed-archive-2026-09-21)，不关闭本文保留的远程/真实质量/容量/恢复/长期任务。

## 历史链接兼容锚点

旧链接保持可达，当前计划以本文工作包、真机待办和 CHANGELOG 证据范围为准。

<a id="milestone-12--函数与算子扩展pid--forecast--udf"></a>
<a id="milestone-17--可观测性与运行时可见性-observability--runtime-visibility"></a>
<a id="milestone-18--vs-code-数据库扩展sonnetdb-for-vs-code"></a>
<a id="milestone-19--生态适配底座能力关系--kv缓存--对象桶--大量-measurement"></a>
<a id="milestone-19--生态适配底座能力关系--kvcache--对象桶--大量-measurement"></a>
<a id="milestone-20--多模能力对齐与平移测试-parity"></a>
<a id="milestone-24--sonnetdb-studio-管理体验升级document-管理面"></a>
<a id="milestone-25--document-store-验收文档与发布治理"></a>
<a id="m40-修复与发布执行顺序2026-08-23-复盘"></a>
