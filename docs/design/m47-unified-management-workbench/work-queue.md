# Workbench 持续推进队列

状态：2026-10-06 三宿主阶段实施中。用户授权持续推进、子智能体独立实施、无冲突并行、任务闭环后本地提交，并确认本轮收尾后新建会话、转移同一每30分钟 heartbeat。任务状态以当前文件、验证记录和提交为准，本文的待办不表示已完成。

### WB-27 合同与文件冻结（2026-10-06；本机 Relation Web 旅程已验证）

- 起点 `main / b3b2d3cd`，WB-25/WB-26提交已核验，原三代理均结束；三博客文件及HANDOFF末尾博客发布hunk属于其它会话，保留且不暂存。本轮只补Relation新Web UI到真实本机Kestrel的分页/当前结果导出、审批批次完整终态及真实撤权清载荷，依赖WB-17、WB-16与WB-26隔离runner，不重复页面迁移。
- `/root/wb27_runner`独占新增 `web/e2e/run-workbench-real.mjs`、`web/e2e/run-relation-real.mjs` 及既有 `web/e2e/run-document-real.mjs` 的薄入口抽取；真实证据发现兼容缺口时，独占候选 `web/src/components/RelationalTableWorkbench.vue` 与 `web/tests/relational-workbench-migration.test.mjs`，先回报并冻结最小修复。`/root/wb27_ui`独占新增 `web/e2e/relational-real-permission.spec.ts`；`/root/wb27_review`独立只读复核。根独占六共享文档、验证runner/集成/审计、最终restore/format与git。最多三个活动子代理，各25命名文件/35分钟，不自行长验证或另派代理。
- 复用真实Release Server的隔离contentRoot/DataRoot与loopback HTTP，关闭非HTTP协议和外联，缺配置直接失败。runner最多10分钟、readiness120次/60秒、请求10秒，浏览器三测试各120秒、retries=0；新Relation与既有Document真实回归串行。普通用户实际API登录产生token，装入浏览器会话，不称登录UI/宿主readonly props验收；无API mock、prop harness或Server改动。
- 本任务最多251条初始关系行、两条批准插入、一条重复主键失败尝试与一条被撤权拒绝插入；有效MixedCase表/列与原名深链接保留。真实分页沿LIMIT/OFFSET50、200取得实际窗口/尾页，导出只含当前已加载结果，不称全表快照或Server扫描/物化/字节/总堆预算。批准两条插入须对应BEGIN/两语句/COMMIT四个完整终态，并由管理员独立读查和原身份历史佐证；真实执行冲突可能HTTP200加NDJSON error，须记error且清审批，不将HTTP200计为写成功，管理员查证数据未新增。
- 实际UI静态复核发现暂存立即打开modal，而返回编辑清空全部暂存，无法通过正常操作积累两项批次。追加同一实施者独占Relation组件/Node最小修复：返回编辑/Escape/遮罩仅关闭预览保留当前上下文草稿，工具栏提供显式重新预览和丢弃暂存；重新预览仍核验身份/权限，确认前一次消费，撤权/身份/卸载清除隐藏预览与草稿。共享WriteApprovalPanel不改；既有fixture由UI代理追加必要兼容断言（冻结 `web/e2e/relational-workbench-migration.spec.ts`）。不通过强制穿透modal或私有状态注入伪造两项真实旅程。
- 真实Server源合同复核确认两insert终态affected为[0,1,1,2]，COMMIT已包含实际提交2；旧Relation求和会误报4。追加同一组件/Node的最小统计修复：完整无错误批次使用末尾COMMIT权威影响数，缺失/错误COMMIT不得把事务内暂存数计作已确认持久写入；历史仍保留error/unknown及不完整性，不改变Server帧合同。真实spec与必要fixture/Node相应终态按此对齐。
- 旧审批暂存后真实REVOKE，确认获得实际403（HTTP或NDJSON权限终态按现有Handler核验），旧表行/结果/草稿/审批与DDL隐藏，管理员查证未写入；重新授READ及同身份Schema刷新不能解除本地锁存、不能重放写。Relation未提供显式安全恢复，本片不新增恢复入口或把重授/刷新计为解锁证据。unknown/fixture回归仍独立记录。
- PowerShell7；禁止Graphify、广域工具扫描与未授权安装。所有循环/搜索/等待/重试有限次数/项目及墙钟，先小输入；长进程记录PID/创建时间/完整命令/父链，finally只回收已核验自有树。临时路径绝对核验，两处策略保留Temp不删除、不重试或绕过。保留origin/parity-results，不push/发布/部署/外部沟通；三宿主、安装/Extension Host/AOT/固定硬件/长稳/发行物分别待验，heartbeat保持ACTIVE。
- 三代理已冻结停止写入，独立复核PASS。最终Relation真实 `relation-real-final2` 3/3、Document共享runner真实3/3、Node20/20、全Web300/300、Chrome fixture12/12、TypeScript/Vite与Server Release0警告/错误通过。真实两insert四end [0,1,1,2]，history权威2；重复PK在COMMIT为HTTP200+table_unique_violation、error/partial/0，管理员253行未变；撤权403后regrant READ/schema刷新仍锁存。首两run断言分别误要求合法名双引号、泛sql_error，未启动项与失败trace保留，最终只按真实合同修spec。证据 `artifacts/wb27-validation-20261006`，门禁前134身份0存活、4隔离数据根和Chrome profiles清理；完整restore/原级别format/staged检查为13任务文件本地提交前置，实际退出/提交见final-gates/commit-checkpoint。

### WB-26 合同与文件冻结（2026-10-06；本机 Document Web 旅程已验证）

- 干净起点 `8a4c64c8b96536f67f0b124471790e05ef441bdb`；WB-25三代理已结束且提交已核验，不重复Object。仅补Document新Web UI连接真实本机Kestrel的权限/显式恢复与高级读取输出预算旅程，依赖WB-12/WB-18和既有Server控制面/Document API；组件只补真实Distinct兼容，不改生产Server、路由或其它宿主。
- `/root/wb20_vector_impl`独占新增 `web/e2e/run-document-real.mjs`，真实合同复核后追加 `web/src/components/DocumentCollectionWorkbench.vue` 与 `web/tests/document-workbench-migration.test.mjs` 的Distinct满窗完整性修复；`/root/wb20_vector_ui`独占新增 `web/e2e/document-real-permission.spec.ts` 与既有 `web/e2e/document-recovery-budget.spec.ts` 的必要请求上限断言；`/root/wb19_fulltext_impl`独立只读复核。根独占六共享文档、Server build、验证/runner/审计、集成、完整restore/format、stage/commit。每代理最多25命名文件、35分钟；必要修复另开15文件/15分钟有界turn，不自行长验证/git/另派代理。
- Runner仅启动本任务隔离contentRoot/DataRoot的真实Release Server，loopback HTTP、关闭MQTT/CoAP/UDP/Modbus/语义外联，复用现有Vite/Playwright入口与真实代理。必需配置缺失直接失败，不skip；最多三浏览器测试、各120秒、总10分钟，readiness最多120次/60秒、HTTP请求10秒，不安装工具。API响应不得mock，不能用prop harness替代真实路由。
- 动态普通用户先获写权限：浏览/暂存插入审批，真实撤权后确认获得403并清旧载荷/草稿/审批；恢复尝试仍403保持锁存；重新授READ后普通Refresh不解锁，显式恢复仅一次空条件Find100/skip0/无旧cursor，同token/原名目标并校验实际返回，恢复不重放旧写。管理员独立查证被拒绝文档未落库。真实只读Server拒写与routed host的readonly props证据分开。
- Aggregate保留用户pipeline并追加limit1001，真实返回哨兵后预览/导出最多1000；Distinct真实Server把limit封顶1000，故请求min(cap+1,1000)，仍保留用户1～1000预览。cap<1000的哨兵证明truncated；cap=1000且实际返回恰1000时，页面/历史明确完整性unknown，不写complete或虚构下一页；返回不足cap为complete，意外超返仍先截断。真实Distinct场景覆盖500/501哨兵及1000满窗，最多1001个本任务种子文档。不称扫描/中间物化/字节/总堆预算、进程重启恢复或全部九模型验收；登录API产生真实token并装入浏览器会话，不称登录UI验收。
- PowerShell7；禁止Graphify、广域工具扫描与未授权安装；循环/搜索/等待/重试有次数/项目数及墙钟，先小输入；长进程记录PID/创建/完整命令/父链，finally仅回收已核验自有树。临时路径绝对核验，隔离数据结束后只清本任务目录；两处策略保留Temp不删除不绕过。fixture、真实新UI、三宿主/安装/Extension Host/AOT/固定硬件/长稳/发行物分别记录。保留origin/parity-results，不push/发布/部署/外部沟通；三宿主仍未闭环，heartbeat保持ACTIVE。
- 真实证据发现空IDs发送[]会选择空目标集，追加同一组件/Node最小修复：空输入省略ids，显式IDs仍保留原值；路径$.site及501/1000断言不变。三代理已冻结停止写入，独立复核PASS。最终专属Node21/21、全Web295/295、TypeScript/Vite、Chrome fixture11/11、真实Web→本机Kestrel `document-real-final4` 3/3（无skip/retry）通过；Server Release0警告/错误。
- 首四run依次为非法集合名setup400、编辑区定位、2/3真实空IDs兼容缺口、并行验证首次Find连接中断；日志/trace保留，不计整体PASS。相同冻结源码单独第五run3/3；第四runVite中断根因未确认，后续同仓库Vite/Playwright验证串行。门禁前合并162条进程记录0存活，五个隔离contentRoot均清理；证据 `artifacts/wb26-validation-20261006`。完整restore/原级别format/staged diff为本地提交前置，实际门禁与11任务文件提交绑定见final-gates/commit-checkpoint；三宿主与服务端资源预算继续另验。

### WB-25 合同与文件冻结（2026-10-06；本地切片已验证）

- 干净起点 `a2bc3f1a6af69df5e4716f0f41c80013ca7c64e3`；WB-24 三代理已结束，不重复派单。本轮仅 Object 现有写审批执行器终态兼容切片，依赖 WB-24 的身份/权限门禁，复用 22 个暂存入口，不改 Server、路由、其它模型或宿主代码。
- `/root/wb20_vector_impl` 独占 `web/src/components/ObjectBucketWorkbench.vue`、`web/tests/object-workbench-migration.test.mjs`，复核后追加 `web/src/api/objectStorage.ts` 的必要 DELETE HTTP终态验证；`/root/wb20_vector_ui` 独占新增 `web/e2e/object-write-terminal.spec.ts`；`/root/wb19_fulltext_impl` 独立只读复核真实 Handler/DTO 与最终 diff。根独占六共享文档、runner、验证、集成、完整 restore/format、stage/commit。最多三个活动子代理；初轮25命名文件/35分钟，必要修复另开15文件/15分钟有界 turn，代理不自跑长验证或 git。
- 审批在派发前一次消费；冻结原身份/API/输入，最多1000个操作，60秒客户端新操作启动窗口与现有30秒单请求超时。只有实际响应的目标、必要完成字段和批准参数相符才记 success；批删逐项匹配批准 key，缺/重复/外来终态为 unknown，完整明确拒绝为 error。已开始操作的缺失/损坏/断连/408/5xx/身份变化为 unknown，历史保留原目标、已确认影响和不完整性，不重放审批；401/403继续锁存并清载荷。started计数为客户端执行入口，不冒称实际网络发送计量。
- Void DELETE 与 Copy/Part 响应沿用真实 HTTP/DTO 能力，不能伪造 Server 未回显的目标。图片处理/backfill 的入队接受不称异步处理完成；完整 Multipart/语义/字节/Server预算、新UI真实权限与三宿主另验。验收为专属/全Web Node、TypeScript/Vite、Chrome fixture、既有Object/语义浏览器和真实Kestrel兼容分别记录，再做独立复核及最终代码门禁。
- PowerShell7；禁止Graphify、广域工具扫描与未授权安装；循环/搜索/重试同时有次数/项目数及墙钟，长进程记录PID/创建时间/完整命令/父链并finally仅清自有树，临时绝对路径先核验。两处策略保留Temp不删除、不重试或绕过；保留origin/parity-results，不push/发布/部署/外部沟通。三宿主未闭环，heartbeat继续ACTIVE。
- 最终专属Node32/32、全Web292/292、TypeScript/Vite、新Chrome5/5、既有Object/语义浏览器4/4、既有真实Kestrel Object4/4+Multipart2/2和独立复核PASS。DELETE校验204/对象marker+version+ETag，nullable setter匹配实际Server省略null合同；Multipart写后刷新拒绝不推翻已证明终态。证据 `artifacts/wb25-validation-20261006`，门禁前24条身份0存活；完整restore/原级别format/staged检查退出0才本地提交，最终门禁与实际提交绑定见final-gates/commit-checkpoint，提交说明 `feat(m47): validate Object write terminal outcomes`，仅本任务10文件。

### WB-24 合同与文件冻结（2026-10-06；本地切片已验证）

- 干净起点 `cb32050b54e5f5d3b767f09b7593460ce18152d1`，WB-23 三代理均结束；本轮只推进 Object 桶浏览/选中对象/Range 的兼容隔离切片，复用现有六页签、v2 continuation、版本/下载、native dialogs、Multipart 与图片语义。不改 Server、路由、其它模型或宿主代码。
- `/root/wb20_vector_impl` 独占 `web/src/components/ObjectBucketWorkbench.vue`、`web/src/api/objectStorage.ts` 的读取 optional signal、必要 `web/src/api/semanticSearch.ts` 读取 optional signal 和新增 `web/tests/object-workbench-migration.test.mjs`。`/root/wb20_vector_ui` 独占新增 `web/e2e/object-workbench-migration.spec.ts`；`/root/wb19_fulltext_impl` 独立只读复核。根独占六共享文档、runner、兼容证据、集成、完整 restore/format、stage/commit；最多三个活动子代理，每代理25个命名文件/35分钟，不另派代理或自行长验证。
- 保留 database/Bucket/key/version 原名与旧 `bucket:` key。同步实际 API/endpoint/Authorization/token/profile/database/Bucket 代际，列表 prefix/continuation 和选中 key/version 各自请求快照；全部自动伴随读取及其错误/finally/URL/历史隔离迟返、ABA、新读取与卸载。401/403 清载荷、派生 URL、结果、写草稿和审批并锁存；同身份刷新/空身份往返不解锁，显式恢复另验。readonly 保留浏览/读取/下载，全部写暂存/确认程序与按钮入口门禁；六态与固定脱敏错误。
- 列表每页1～1000、累计预览1000，先截断再映射；校验响应 bucket/prefix、条目目标和 token 严格推进，不复用超返导致跳项的游标。结果/历史保留实际数量与不完整性，不称全桶快照。Range start/length/end 为安全整数，长度最多4096，冻结格式模式与版本，响应 Blob 先 slice 再 arrayBuffer/格式化；明确 Range/客户端截断，不称传输、扫描或总堆预算。
- Server 兼容复核：prefix 按现有 `TrimStart('/')` 规范化，首 continuation null/空串等价，opaque token 只验证非空/变化/不重复，不要求字面 v2。`getObjectBlob` 可兼容增 optional status/contentRange，验证真实206/Content-Range与冻结版本；head.bucket/key 是客户端回填，sizeBytes 是 Range Content-Length，不用这些值虚构 Server 目标回显或全对象长度。
- Multipart/图片语义仅补读取归属和权限隔离，不声称完整分页/语义预算；写执行器终态、未知结果和批次预算另开切片，当前补审批身份失效与门禁且禁止旧上下文回写。fixture、既有真实 Kestrel 兼容、新 UI 真实权限、三宿主/安装/Extension Host/AOT/硬件/长稳/发布分别记录。
- PowerShell7；禁止 Graphify、广域工具扫描与未授权安装；循环/搜索/重试同时有项目/迭代上限及墙钟，长进程记录 PID/创建时间/完整命令/父链并 finally 仅清自有树，临时路径先核验。两处策略保留 Temp 不删除、不重试或绕过。最终树完整 restore、原级别 format 与 staged diff check 通过才本地提交；保留 origin/parity-results，不 push/发布/部署/外部沟通。
- 最终第三生产树专属Node20/20、全Web280/280、TypeScript/Vite、Chrome15/15、既有Object/语义浏览器4/4、既有真实Kestrel兼容4/4及独立复核PASS。补安全Range差值/声明长度与两Web picker ABA；URL诊断只排除已证明的MapLibre全局worker，Object图片/未知URL回收断言保持。53条进程身份0存活；证据目录 `artifacts/wb24-validation-20261006`。提交说明 `feat(m47): isolate Object reads and bounded previews`，实际哈希见git log；根最终完整门禁通过才提交。本轮不启动下一片，优先后续Object写终态/一次消费/unknown与批次预算，整体三宿主继续ACTIVE。

### WB-23 合同与文件冻结（2026-10-06；本地切片已验证）

- 干净起点 `759f36912fdb6a82edc008db6c02d181b66ebb9e`，WB-22 三代理均结束；本轮只推进 Graph 权限/有界画布兼容切片，复用原有 Canvas、Schema、元素编辑、JSON transfer 与维护审批，不重做 M40 引擎或 Object 复杂子页。Graph 保持 Beta、database/Graph 原名与旧 key/入口。
- 实施代理 `/root/wb20_vector_impl` 独占 `web/src/components/GraphWorkbench.vue`、`web/src/api/graphs.ts` optional signal、`web/tests/graph-workflow.test.mjs` 必要兼容断言及新增 `web/tests/graph-workbench-migration.test.mjs`。UI代理 `/root/wb20_vector_ui` 独占新增 `web/e2e/graph-workbench-migration.spec.ts`；`/root/wb19_fulltext_impl` 独立只读复核。根独占六共享文档、runner、兼容证据、集成、restore/format、stage/commit；最多三个活动子代理，每代理25个命名文件/35分钟，不自跑长验证、不另派子代理。
- 补 normal/empty/error/permission/readonly/longContent 六态，401/403 来自 overview/visualization/element/audit/export/write 均清旧画布/metadata/元素/导入草稿/维护载荷/审批并锁存。readonly 浏览/元素读取/导出保留，全部暂存/确认/导入文件/维护批准拒绝的按钮与程序入口禁写；同身份刷新和空身份往返不解锁，显式读取恢复另验。错误正文固定脱敏，不入历史。
- 固定实际 API/endpoint/Authorization/token/profile/database/Graph 与同步 epoch，隔离迟返、跨库同名、ABA、新读和卸载；API optional signal 支持分派前取消，已派写取消不冒称 Server 未执行。消费 overview 的 boundedVisualization 能力，缺失或 false 不请求/不显示画布。画布按所选 10～1000 总元素上限先截断再映射/渲染，边必须指向保留顶点；保留 Server truncated 且客户端超限明确不完整，不虚构分页/全图。属性检查器预览有界，不改完整已加载编辑/JSON round-trip 合同，不称传输/扫描/字节/总堆预算。
- 审批 dispatch 前一次消费并绑定发起身份/冻结输入；元素写需完整 mutation 终态，维护需匹配 database/Graph/审批身份和实际 state，不把 staged/paused/applying 称执行完成。缺失/错目标/传输异常为 unknown 且不重放，历史保留原目标。完整长期维护、服务端预算、新UI真实权限与三宿主另验。
- 复核追加最小安全门禁：现有 JSON number 的 unsafe ID/elementVersion/edge endpoint 不可用于元素读取/编辑/写审批，阻止四舍五入后的错误目标；不实施全 Graph Int64 字符串合同。画布容器重建时重建 ECharts 并更新 ResizeObserver 归属，真实 DOM 实例须由浏览器证据验证。
- 验收为专属及全 Web Node、TypeScript/Vite、真实 Chrome fixture、既有 Graph 浏览器和既有真实 Kestrel Graph 合同分开记录，另由只读代理复核。Server/路由/其它模型/宿主代码不改；旧策略保留 Temp 不删除、不重试或绕过。所有循环/搜索/重试同时限制项目数与墙钟，长进程记录身份/父链并 finally 仅清自有树，临时路径先解析核验。最终树完整 restore 与原级别 format、staged diff check 通过才本地提交，不 push/发布/部署/外部沟通。
- 最终本地验证：专属 Node `27/27`、全 Web `260/260`、Graph Chrome `16/16`、既有 Graph 浏览器 `3/3`、TypeScript/Vite、真实 Kestrel Graph 兼容 `4/4`，独立只读复核 PASS；证据目录 `artifacts/wb23-validation-20261006`，最后进程审计 `43` 条且 `liveOwned=[]`。完整 Graph Int64 字符串、真实新 UI 权限/恢复、三宿主与发行证据仍分别待验；提交说明 `feat(m47): isolate Graph canvas and approval outcomes`，实际哈希以git log为准；最终树完整restore/原级别format与staged diff check均退出0才提交。

## 每个任务的闭环

1. 读取 HANDOFF、AGENTS、当前 git 状态与本队列，选择有证据的最小切片；记录任务 ID、负责人、文件归属、依赖和验收条件。
2. 为独立任务分配子智能体。主智能体最多并行三个子任务；只在文件范围和依赖不重叠时并行。共享队列、ROADMAP、CHANGELOG、HANDOFF 与 git 操作由主智能体串行维护，子智能体不得自行提交。
3. 设计切片先补可评审原型与状态规范；已有行为的兼容修复和合同可自主实施。最终视觉基线仍待确认的页面继续做设计，不把整个初稿当作用户已验收。
4. 实施后用适合变更的窄测试、必要构建和 UI 验证证明验收条件；由另一智能体独立复核关键合同及 diff。静态/mock、真实服务、三宿主和发布证据分开。
5. 完成最终待提交树的 `dotnet restore SonnetDB.slnx` 和 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`。失败先修正，再重新验证；通过后再改代码须重跑。未通过不得 commit。
6. 主智能体仅 stage 本任务改动，更新 CHANGELOG/HANDOFF/任务验收，执行 Conventional Commit，记录实际哈希并复核提交内容。不得 stage 其它会话文件、构建产物或凭据。
7. 有依赖的下一项在前项闭环后启动；独立项可已在进行。失败保留证据并派修复，不重复创建同一任务。没有可执行事项时等待定时唤醒，不能写成已全部完成。

## 初始任务

| ID | 任务与交付 | 文件归属与依赖 | 验收 / 状态 |
|---|---|---|---|
| WB-00 | 接收设计包、核对现状与提交设计交付基线 | 主智能体串行维护根文档与 design 包；不动生产代码 | 现有 7/30/9/166/60 目录、MQ 逻辑/物理范围、链接、证据可追溯；restore、Format Check、脚本语法与差异检查通过；已提交 `3484aafd` |
| WB-01 | 原型工作区页签、草稿与键盘交互闭环 | prototype/app.js、styles.css；先完成 WB-00。实施者独占这两文件 | 已完成并提交 `88fc7914`：active 页签滚入可见、内存草稿标记/关闭后保留、只读/规划/错误动作门禁、对话框焦点恢复、箭头与 Home/End、桌面/390 Playwright 回归；未接真实服务。 |
| WB-02 | 页面能力、字段与状态逐页定稿 | catalog.js、task-details.js、screen-specs.md；可与 WB-01 并行，各自不改对方文件 | 首批五页已提交 `11de7131`（SQL、KV、MQ、对象 Bucket、Graph Beta）；WB-02B 再完成五个模型页，详见下一行。其余页面和真实宿主接线留待后续批次。 |
| WB-02B | 下一批五模型状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md`；依赖 WB-02，主会话串行维护测试与共享文档 | 已提交 `c3bfc2f1`：measurement、table、document、vector、fulltext 各补 capabilities 与 normal/empty/error/permission/readonly/longContent 六态，并注入对应任务页；Node 合同测试 2/2、VM 5 页×6 状态、动作/边界 30/30、语法与 diff 检查通过。仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、权限、质量、三宿主或生产迁移。 |
| WB-02C | 五个全局页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md`；依赖 WB-02B，主会话串行维护测试与共享文档 | 已提交 `2ebc6978`：`database-catalog`、`connections`、`notebook`、`history`、`metrics` 各补 capabilities、六态和规范字段并注入任务页；Node 合同测试 3/3、与 WB-02B 联合 5/5、VM 7/9/30/39 guard、语法与 diff 检查通过。仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、权限、三宿主或生产迁移。 |
| WB-02D | 观测与数据流状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md`、`web/tests/m47-observe-flow-state-contract.test.mjs`；依赖 WB-02C，主会话串行维护共享文档与提交 | 已提交 `07506a99`：`events`、`slow-queries`、`alerts`、`runtime`、`modbus` 各补 capabilities、六态和专用字段，并注入全部对应任务页；Node 合同测试 3/3、两个 JS `node --check`、独立复核 PASS、`git diff --check`，提交前 `dotnet restore` 与 `dotnet format` 通过。保持视图暂停不暂停服务器、慢查询恢复不重跑、告警/诊断不发送或伪造健康、Modbus Runtime/Pending/Audit 与审批边界。仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、权限、现场写入、三宿主或生产迁移。 |
| WB-02E | 五个工作台/数据流页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md` 由子智能体 `/root/wb02e_global_flow_contracts` 独占；主会话串行维护测试与共享文档；依赖 WB-02D | 已提交 `5cbd759a`：固定页面 `summary`、`recent`、`imports`、`transfers`、`jobs`，补 capabilities、六态、专用字段和任务页注入；新增合同测试 3/3，与 WB-02B/C/D 联合 11/11，两个 JS 语法检查与 `git diff --check` 通过，提交前 `dotnet restore` 与 `dotnet format` 通过；复核确认 MQ、恢复、导入、传输及异构位点边界。静态原型仍保持 REVIEW_DRAFT 与真实服务边界。 |
| WB-02F | AI 与 MCP 页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md` 由子智能体 `/root/wb02f_ai_mcp_contracts` 独占；主会话串行维护测试与共享文档；依赖 WB-02E | 已提交 `4f9720e6`：固定页面 `ai-connect`、`copilot-settings`、`rag`、`tool-permissions`，补 capabilities、六态、专用字段和任务页注入；新增合同测试 3/3，与 WB-02B/C/D/E 联合 14/14，两个 JS 语法检查与 `git diff --check` 通过，提交前 `dotnet restore` 与 `dotnet format` 通过；复核确认 typed HTTP/stdio bridge、Provider 质量/成本、RAG profile/generation/revision、权限交集、默认只读和数据外发边界。生产迁移继续冻结。 |
| WB-02G | 治理页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md` 由子智能体 `/root/wb02g_governance_contracts` 独占；主会话串行维护测试与共享文档；依赖 WB-02F | 已提交 `c6781b4d`：固定页面 `users`、`grants`、`tokens`、`approvals`、`backup`，补 capabilities、六态、专用字段和任务页注入；新增合同测试 3/3，与 WB-02B/C/D/E/F 联合 17/17，两个 JS 语法检查与 `git diff --check` 通过，提交前 `dotnet restore` 与 `dotnet format` 通过；复核确认控制平面权限、MQ database grant/实例 Store、一次性 Token、审批终态与错误不重试、单库备份不覆盖共享 `.system/mq` 的边界。生产迁移继续冻结。 |
| WB-02H | 设置与发布页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md` 由子智能体独占；主会话串行维护测试与共享文档；依赖 WB-02G | 已完成并提交 `175cf674`：固定页面 `preferences`、`server-settings`、`studio-host`、`capability-matrix`、`about`，补 capabilities、六态、专用字段和任务页注入；合同测试 3/3，WB-02B–H 联合回归 20/20，两个 JS `node --check`、`git diff --check` 和独立只读复核 PASS。保持宿主范围/服务器配置只读边界、Studio 安装证据独立、版本 manifest/签名未就绪不显示 PASS、真实发行版本与敏感诊断边界；仍为 REVIEW_DRAFT 原型设计，生产迁移继续冻结。 |
| WB-03 | 统一资源身份与能力合同的兼容切片 | `web/src/management-core/resourceDescriptor.ts`、`capabilityRegistry.ts`、`index.ts` 与 `web/tests/management-core-contract.test.mjs` | 已完成并纳入本地提交：MQ identity 含 database + topic，通用工厂强制显式 topic 且 `name === topic`，逻辑 `scope` 与 `persistenceScope` 分开，保留原始拼写和现有 key/路由；实例 MQ 明确 `.system/mq`、共享边界与单库备份排除；Graph 始终 Beta；未知能力安全返回 `unavailable`；Node 6/6、TypeScript、`git diff --check` 通过。仅为静态/内存合同，不代表真实权限、Server、存储迁移、AOT 或三宿主证据。 |
| WB-03C | Explorer 兼容基线证据 | web/tests 下专属 contract 测试与自有 fixtures；只读消费当前 managementExplorer.ts，可与 WB-01/WB-03 并行 | 已完成并提交 `828b7638`：`web/tests/management-explorer-compat.test.mjs`，`node --experimental-vm-modules --test ...` 5/5；覆盖 keys、大小写/冒号、index/backup、MQ 旧 key 与外层 database 选择上下文。发现并记录 index-only fallback 与跨库 MQ key 兼容边界，未改源码。 |
| WB-04 | 按已确认基线迁移全局壳和一级/二级导航 | 预检仅新增 `web/tests/navigation-compat.test.mjs`；生产壳/路由文件仍冻结 | 迁移前兼容预检已完成并通过本地提交门禁：5/5 静态断言覆盖 `/admin`、`/admin/app`、Studio/databases/trajectory-map legacy redirects、现有路由与管理员 meta、7 项 baseNavigation、5 项 adminNavigation、secondaryNavigation 管理员条件、设置/关于入口、setup/auth/admin guards 及 trajectory query→SQL。用户已确认设计基线；本项生产壳迁移仍待独立切片，不把预检写成运行时或三宿主 PASS。 |
| WB-04B | Explorer → SQL 深链接兼容预检 | 仅新增 `web/tests/explorer-routing-compat.test.mjs`；只读消费 `useSqlExplorerRouting.ts`，不改生产源码 | 已完成并通过本地提交门禁：5/5 静态断言覆盖九模型 `tool/model/node` 深链接、database selection、index/backup `{model,node}` fallback、Open-in-SQL allowlist 与 KV/MQ/Bucket 排除、route-only 不自动执行。仅证明源码兼容基线，不代表 Vue 运行时、真实路由、Server 或迁移完成。 |
| WB-05 | 结果、草稿、历史和审批工作流迁移 | 主会话冻结共享文档与集成；WB-05A 独占 `sqlConsole.ts`、`workbenchHistory.ts`、结果/历史组件和自有测试；WB-05B 独占 `WriteApprovalPanel.vue`、`writeApproval.ts` 和自有测试；根会话接线 `useSqlExecution.ts`、`SqlQueryWorkspace.vue`、`SqlConsoleView.vue` 与自有集成测试；依赖 WB-04 兼容预检 | 已完成并提交 `e5fc4668`：结果最多 10,000 行且保留截断标记；关闭草稿可恢复/丢弃且不自动执行；历史保留 unknown/completeness 并对文本、JSON/JSONL 敏感值脱敏；SQL 取消、传输中断、缺少终态写入 unknown；审批绑定连接/端点/数据库/草稿指纹并在失效时拒绝确认；共享 SQL 工作区已接恢复入口。WB-05 窄测 9/9、全 Web Node 回归 94/94、SQL 回归 11/11、TypeScript、Playwright 3/3 通过；仍不代表真实 Server、三宿主、AOT、安装、发布或全量页面迁移验收。 |
| WB-06 | 按确认基线迁移生产外壳与一级/二级导航 | `/web/src/views/AppShell.vue`、`web/src/router/index.ts`、`web/tests/workbench-shell-migration.test.mjs` 与更新后的 `web/tests/navigation-compat.test.mjs`；依赖 WB-03 资源合同、WB-04/WB-04B 兼容预检；子智能体独占生产壳/路由/迁移测试，根会话串行维护共享记录与提交 | 已提交代码 `f5c5cf53`，交接记录 `503cb6fe`：一级 rail 固定七模块，设置置于 footer；旧查询/数据/Studio 同义按钮不再重复；新增模块 aliases 并保留 legacy redirects、trajectory query、setup/auth/admin guards；active 映射覆盖现有二级/管理员路由；flows/govern 按现有 admin 边界隐藏，flows→Modbus、settings→About、ai→RAG 仅兼容落点；Explorer 九模型与 MQ database+Topic / instance `.system/mq` 语义未改。迁移/兼容/Explorer 路由 13/13、管理 Explorer 5/5、TypeScript、`git diff --check` 与独立复核通过；不代表真实 Vue runtime、Server、三宿主、AOT、安装、发布或全量页面迁移。 |

后续模型与三宿主切片从 M47-U01~U09 的实际差距继续选取，复用现有交付，不重做 M29/M32/M34，也不自动改 MQ 存储为 KV/关系表。

## 有界执行与定时检查

定时检查建议每 30 分钟唤醒同一新会话。持续执行不等于单次运行无限长：每次选择一个有界集成切片；子任务一般 20–45 分钟并写清最大项目数、重试次数及墙钟超时。未完成时保存检查点，下一次唤醒接续。任务运行期间不得再次派同一文件；主智能体保持有意义的进度更新。

每次委派必须重复：PowerShell 7；禁止 Graphify、广域编译器扫描和未经授权安装；循环/搜索/重试同时有迭代或项目上限与墙钟超时，先小输入试运行；长进程记录 PID/创建时间/完整命令/父链，成功/失败/取消/超时后仅清理任务自有树；临时文件/下载/日志/目录有归属并在核验绝对路径后回收，保留交付物。禁止按进程名批量终止。

提交、分支切换与统一文档串行。保留 origin/parity-results，禁止合并；不可 reset、清除或覆盖不明来源的改动。当前用户未授权自动推送、发布、部署或外部沟通。机器资源异常时停止新增并行工作，清理归属明确的失控进程，报告检查点。

定时检查在未变化或无可执行事项时保持安静；仅有实质完成（含提交哈希）、失败、阻断或用户需要处理的事项才通知。全部已授权任务确实完成且队列没有下一项后，记录完成并暂停该 heartbeat；不删除工作成果或自动归档会话。

## WB-07 当前切片（2026-10-05）

- 状态：已完成并提交 `3ad20c6a feat(m47): project canonical resource identity into workspace tabs`；负责人：根会话集成，子智能体 `/root/wb07_resource_identity` 独占生产 Explorer/页签文件与自有测试。
- 范围：Explorer item 投影统一 `ResourceDescriptor`，页签携带 database/resource/legacy key；保留旧 `tool/model/node` 深链接和 route-only 语义。
- 边界：MQ 仍为 database + Topic，`scope=database`、`persistenceScope=instance`、`.system/mq`、单库备份不覆盖；Graph 保持 Beta；不接入 CapabilityRegistry 权限判定，不改博客改动或共享文档。
- 验收：WB-07 自有测试 5/5、Explorer/路由/壳联合回归 18/18，独立复核全 Web Node 回归 102/102，TypeScript/Vite build、`git diff --check`、最终树 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均通过。直接浏览器旧 `tool/model/node` URL 的 `node` 选择仍是后续切片边界。

## WB-08 当前切片（2026-10-05）

- 状态：已完成并提交 `418c21d3 fix(m47): restore legacy explorer route selection`；负责人：根会话集成，子智能体 `/root/wb08_route_selection` 独占实现，`/root/wb06_shell_reviewer` 独立复核。依赖 WB-07 `3ad20c6a`。
- 范围：修复旧 `tool/model/node` 直达 URL 的 `node→activeExplorerKey` 回选；保持旧 query 形状、当前 active/default database、Explorer 点击路径和 route-only 不自动执行。
- 独占候选文件：`web/src/utils/managementExplorer.ts`（route node→legacy key helper）、`web/src/views/SqlConsoleView.vue`（route/db/schema/management 就绪后一次性选择 watcher）、`web/tests/explorer-route-selection.test.mjs` 与必要兼容测试。不得接入 CapabilityRegistry 或改变 MQ/Graph 资源合同。
- 验收：覆盖 measurement/table/document/kv/mq/vector/fulltext/bucket/graph/index/backup、大小写/冒号、缺失/未知 node 回退、tool-only、MQ database+Topic、Graph Beta、metadata 等待、watcher 顺序和 route-only；自有 Node 测试 5/5，Explorer/路由/壳/导航/页签定向回归 20/20，独立复核全 Web Node 107/107，TypeScript/Vite、`git diff --check`、最终树 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均通过。URL 无 database 参数的 active/default DB 边界单独记录。

## WB-09 当前切片（2026-10-05）

- 状态：已完成并提交 `09f5711b feat(m47): add database context to explorer deep links`；负责人：根会话集成，子智能体 `/root/wb09_database_route` 独占实现，`/root/wb06_shell_reviewer` 独立复核。依赖 WB-08 `418c21d3` / `9c3ace1f`。
- 范围：为新的 Explorer 深链接补可选 `database` 上下文，同时保留旧无 database 的 `tool/model/node` query、旧 redirects、当前 active/default DB 回退和 route-only 不自动执行。
- 独占候选文件：`web/src/composables/useSqlExplorerRouting.ts`（生成链接携 database，保留旧字段）、`web/src/views/SqlConsoleView.vue`（校验 route.query.database，数据库列表加载后选择指定库，再按现有 helper 回选）、必要时 `web/src/composables/useSqlExplorer.ts`、新增 `web/tests/explorer-database-route-selection.test.mjs` 与兼容测试。不得接入 CapabilityRegistry 或改变 MQ/Graph 资源合同。
- 验收：九模型、index、backup、大小写/冒号、MQ database+Topic、Graph Beta；有效 database 等待列表后选择，缺失/未知 database 安全回退 active/default，database-only 与 A→未知→恢复路径有 token 保护，用户手动切库不被同一旧 URL 强制切回；旧 query 与 route-only 全回归。自有 Node 4/4，Explorer/路由兼容定向 10/10，独立复核全 Web Node 111/111，TypeScript/Vite、`git diff --check` 和最终 restore/format 通过。手动切库后只改变同 database URL 的 model/node query 由 WB-10 补齐；真实 Server、权限、三宿主和发布证据分开。

## WB-10 当前切片（2026-10-05）

- 状态：已完成并提交 `ee44d7bb34b0b9a95ae3e7914b82a65021bf97f3`；负责人：根会话集成，独占实现与独立复核已完成。依赖 WB-09 `09f5711b` / `674b675a`。
- 范围：补齐用户手动切换数据库后，URL 保留旧 `database` 但只改变 `model/node` 时的投影策略；同一旧 URL 不强制切回，新的 model/node 在当前手动数据库解析或安全回退，不误选其它数据库。新 database query 仍优先切库。
- 独占候选文件：`web/src/views/SqlConsoleView.vue`、`web/tests/explorer-database-route-selection.test.mjs`（必要时新增小型手动切库回归）；不改资源 descriptor、CapabilityRegistry 或 MQ 存储。
- 验收：旧/新 tool/model/node/database query、metadata/token 等待、A→未知→恢复、MQ database+Topic、Graph Beta、route-only 全回归；定向 Node 5/5、Explorer/路由/管理兼容 15/15、全 Web Node 112/112，TypeScript、Vite build、`git diff --check`、最终 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均通过。策略和未覆盖的真实 Server/权限/三宿主边界写入记录。

## WB-11 当前切片（2026-10-05）

- 状态：已完成并提交 `390ff526`；用户已确认 Measurement Workbench 页面基线，根会话负责共享文档、集成、门禁与提交，子智能体 `/root/wb10_manual_switch` 独占页面组件与专属迁移测试。依赖 WB-10 `ee44d7bb` / `d6aa81d8`。
- 范围：沿用已确认五区外壳与现有 measurement 路由/旧深链接；中心区域呈现数据点、导入、监控、Schema，右侧 Inspector 继续由工作台外壳承载，底部保留结果/状态；查询、刷新、导出可用，写入/删除继续进入 WriteApprovalPanel。
- 身份与边界：保留 database、measurement 原名/大小写和旧 key；不改 MQ 存储、Graph 语义、Server API 或三宿主发布，不把本地 UI/定向测试写成真实服务验收。
- 验收：normal、empty、error、permission、readonly、longContent 六态有可验证生产组件合同；旧路由/深链接、查询/刷新/导出、写审批、跨库同名资源和权限错误载荷清理已覆盖。专属 Node 5/5、全 Web Node 117/117、TypeScript、Vite build、Measurement Playwright 8/8、`git diff --check`、最终 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均通过。代码提交为 `390ff526 feat(m47): migrate measurement workbench`。真实 Server、三宿主、安装、发布和全量九模型验收仍待补。

## 新三宿主阶段（2026-10-06）

用户已授权继续实现独立 SonnetDB Studio、VS Code Workbench 与 Web Admin。三项并行任务先按确认基线做有界合同/页面切片；共享文档、集成、stage、restore/format 和 commit 由根会话串行维护。

| ID | 任务与文件归属 | 依赖与验收 / 状态 |
|---|---|---|
| WB-12 | Web Admin Document Workbench；`web/src/components/DocumentCollectionWorkbench.vue`、`web/tests/document-workbench-migration.test.mjs` | 依赖 WB-11。补齐 database + collection 原名身份、六态、旧路由/深链接与 WriteApprovalPanel 边界；专属Node10/10、全Web127/127、TypeScript/Vite、Document浏览器7/7与独立复核通过；已提交 `54c787551186f74036a1845e25a937d1a268ddc1`，最终restore/format通过。Find预览1000；403显式恢复与Aggregate/Distinct预算另验。 |
| WB-13 | SonnetDB Studio 宿主合同；三个 Studio 源文件、`StudioConnectionLibraryTests.cs`、`StudioHostContractTests.cs` 与现有 `StudioManagedServerHostTests.cs` 的生命周期断言 | 已完成实现并冻结；Release定向34/34（真实bridge与ManagedLocal/external）、独立复核与diff check通过。宿主/端点/profile/数据库原名身份、URL校验、source-generated JSON与canStop已验证；已提交 `7483584771ba7164b30047eff59f2c6f2e97e96f`，最终restore/format通过；客户端展示、安装和发布另验。 |
| WB-14 | VS Code Workbench 资源/深链接合同；扩展 `src/core/types.ts`、`workbenchResource.ts`、`src/extension.ts`、`package.json`、`src/test/host/index.ts` 与专属测试 | 依赖现有 Remote-first 扩展；补齐 database/resource 原名、旧 key 兼容与九模型 Workbench 入口，不扩大治理权限；TypeScript/Node20/20、本机Code.exe真实Extension Host注册smoke与独立复核通过；已提交 `e7cf2fe512974a1cd9bdbac3f767529f2b065e1a`，最终restore/format通过；浏览器最终回选/认证和VSIX另验。 |

## 三宿主后续队列（本地切片通过不等于整体完成）

| ID | 有界下一项 | 依赖 / 验收 / 剩余边界 |
|---|---|---|
| WB-15 | Studio Web 客户端消费真实 bridge 身份与生命周期合同，复用现有 native bridge、连接库和 Managed Local UI | 已提交 `e1f93a6995e6e1e24e1badfaefb2eb918f31393a`；五客户端文件与专属 Node 测试由 `/root/wb15_client_inventory` 实施，`web/e2e/studio-host-client.spec.ts` 由专属夹具代理实施，另一代理独立只读复核PASS。依赖WB-13；原名身份、external/owned/stopped/failed/canStop与未知合同保守门禁、bootstrap/status/save迟返、同ID endpoint认证同步和目录ABA均已收口；保存串行、宿主确认且无回授循环。专属Node10/10、全Web137/137、Studio三类定向40/40、TypeScript/Vite和StudioNative浏览器夹具8/8通过；代码最终完整restore/Format Check与staged diff check退出0。真实Server、干净Windows/WebView2、安装升级卸载、未使用旧Header及完整Explorer异步组合仍独立验收。 |
| WB-16 | VS Code → Web Workbench 的最终资源回选与认证边界证据 | 已提交 `0bb628adde45ad160f05a67057f098fa982c4f85`，依赖WB-14与WB-08～WB-10。九模型/index/backup、原名/冒号、同名MQ、登录返回、显式base/SSE及index分组收口；Node9/9、全Web146/146、扩展20/20、根/代理构建、两部署浏览器各17/17、真实Host13节点命令调用、独立复核和代码最终完整门禁通过。真实Server权限、真实代理部署/远程SSE/同origin存储隔离与VSIX发布单列。 |
| WB-17 | Relation Table Workbench 的身份、六态与预览/审批 | 已提交 `f19782638789279e8099d7cbdf6f82b40cac12c0`，依赖 WB-11/WB-12/WB-16；复用分页/设计器/审批。原名、200 行预览、会话/Schema/迟返隔离、403 锁存、一次审批及 unknown 终态收口；Node15/15、全Web161/161、TypeScript/Vite、Chrome12/12、既有设计器2/2、独立复核及代码最终完整门禁通过。readonly 仅安全 DDL/浏览/结果导出；真实Server权限、物化/字节预算、三宿主和发行物另验。 |
| WB-18 | Document 权限修复后的显式安全恢复入口与剩余高级读取/预算证据 | 已完成并提交 `1d9465fb`；干净起点 `5dec4282`，依赖 WB-12。恢复与读取预算合同、专属文件归属和验收条件见下方检查点。 |
| WB-19 | FullText Workbench 上下文、权限载荷与预览预算 | 已提交 `2f9a5477`，从干净 `1d9465fb` 接续；专属Node15/15、全Web184/184、TypeScript/Vite、Chrome8/8、既有真实Kestrel兼容4/4、独立复核与最终完整门禁通过。真实权限/写终态、读取恢复与三宿主/发行物另验。 |
| WB-20 | Vector Workbench 原始向量检索上下文、权限载荷与Top-K预览 | 从干净 `2f9a5477` 接续，依赖WB-11/WB-16与既有Vector API；原名/六态、请求快照、401/403及空Schema锁存、Top-K100与子页门禁完成本地切片。专属Node17/17、全Web201/201、TypeScript/Vite、Chrome10/10、既有导入回归1/1、真实Kestrel兼容3/3与独立复核通过；本轮提交说明 `feat(m47): isolate Vector preview context and permission payloads`，实际哈希以git log为准，最终完整门禁为提交前置。索引Profile、真实权限/恢复/预算与三宿主/发行物另验。 |
| WB-21 | KV 权限锁存、六态与有界预览 | 本地切片已验证；专属Node17/17、既有KV12/12、全Web218/218、TypeScript/Vite、Chrome12/12、既有浏览器5/5、真实Kestrel兼容6/6与独立复核通过。合同/归属见下方；最终完整restore/format及staged检查为提交放行条件，实际哈希见git log。真实新客户端权限/恢复、完整atomic响应、字节/堆预算与三宿主另验。 |
| WB-22 | MQ 权限锁存、实际Topic/请求隔离与有界预览 | 本地切片已验证，合同/归属见下方；专属Node22/22、全Web240/240、TypeScript/Vite、Chrome16/16、既有MQ浏览器3/3、真实Kestrel兼容2/2及独立复核PASS。最终完整restore/format和staged检查为提交前置，实际哈希见git log。新UI真实权限/恢复、完整metadata/解码/总预算、三宿主另验。 |
| WB-23 | Graph 权限/请求隔离与有界画布 | 已提交 `cb32050b`，专属Node27/27、全Web260/260、Chrome16/16、既有浏览器3/3、真实Kestrel兼容4/4、build及独立复核通过。Graph Beta、完整Int64字符串/新UI真实权限/三宿主边界见顶部冻结。 |
| WB-24 | Object 读取隔离、权限门禁与有界列表/Range | 本地切片已验证，顶部冻结；Node20/20、全Web280/280、Chrome15/15、既有浏览器4/4、真实Kestrel兼容4/4、build与独立复核PASS。最终完整门禁为提交前置，实际哈希见git log。写终态/unknown/一次消费、完整语义/Multipart/Server预算、新UI真实权限/OS/三宿主另验。 |
| WB-25 | Object 写终态、一次审批消费与批次预算 | 本地切片已验证，顶部冻结；Node32/32、全Web292/292、Chrome5/5、既有浏览器4/4、真实Kestrel Object4/4+Multipart2/2、build与独立复核PASS。最终完整restore/format/staged检查为本地提交放行条件，实际哈希见git log；完整语义/Multipart/Server预算、新UI真实权限/OS/三宿主另验。 |
| WB-26 | Document 新Web UI真实权限、显式恢复与输出预算 | 本机Web→Kestrel 3/3、Node21/21、全Web295/295、Chrome fixture11/11、build与独立复核PASS；空IDs省略，Distinct满1000完整性unknown。最终完整门禁与实际11文件提交见证据目录；API登录、READ拒写与真实登录UI/readonly props分开，Advanced/Server资源预算及三宿主另验。 |
| WB-27 | Relation 新Web UI真实分页/导出、事务终态与撤权锁存 | 本机Relation→Kestrel3/3、共享runner Document真实3/3、Node20/20、全Web300/300、Chrome fixture12/12、build和独立复核PASS；返回编辑保留暂存并显式丢弃，COMMIT权威影响数避免重复，真实冲突error/partial/0与撤权403保持锁存。13文件最终完整门禁/实际提交见证据目录；显式恢复、Server预算/完整SQL矩阵、其它宿主及发行物另验。 |

WB-15～WB-27已按各自本地范围推进；M47-U01～U09 仍需完整九模型适配器、三宿主真实旅程、AI/MCP 入驻和版本/安装/发布矩阵；没有发布授权时保留可复核的 NOT_READY 边界，不因本机切片 PASS 暂停整体研发。下一次优先盘点Measurement/FullText/KV等新UI真实权限/恢复与Studio/VS Code剩余合同，再按一个有界切片推进；不重复WB-25～WB-27，本轮不启动下一片。

### WB-20 合同与文件冻结（2026-10-06；本地切片已验证）

- 实施代理独占 `web/src/components/VectorSearchWorkbench.vue` 和新增 `web/tests/vector-workbench-migration.test.mjs`；UI代理独占新增 `web/e2e/vector-workbench-migration.spec.ts`；独立复核只读。根独占六个共享文档、验证runner、完整restore/format、stage和commit；最多三个活动子代理，每代理最多25个命名文件、35分钟、无自跑长验证，根统一执行有界验证。
- 保留database、measurement/column原名和现有内部`${measurement}:${column}`选择键；外层Explorer的`vector:measurement:column` key不改。六态、只读/无权限、发起profile/实际endpoint/auth/Schema/epoch/API和参数快照隔离迟返、同名跨库、ABA、新查询和卸载；401/403清命中/metadata/生成向量/结果，保留用户raw/text/filter输入，同身份刷新不解锁，显式安全恢复另验。
- 原始向量须有限且维度匹配；Top-K沿用Server1～100，先截断再格式化/显示/导出，历史绑定发起身份及实际preview count/完整性，不新增哨兵或continuation。服务端扫描/中间物化/字节/总堆、Recall和真实模型质量独立验收。
- 既有`embed-preview`只返回vector/dimension且未给出所选索引的显式Profile绑定。本轮不伪造Profile，不用图片语义搜索status代替，不调用未验证的隐式文本embedding；文本入口明确Profile未就绪并保留raw路径，Profile合同留后续，不改变Server或Provider实现。
- 数据编辑/导入继续复用MeasurementWorkbench；按资源/身份/Schema代际key重建子页，使旧草稿/审批不能跟随新上下文；传递readonly/permission门禁，不重新迁移子页写执行器或声称其完整写终态/权限旅程已验收。Server、路由、其它模型、三宿主代码与策略保留Temp目录不改。
- 两名实际专属代理分别独占组件/Node与UI spec；UI代理在自身交付冻结后独立只读复核组件/Node，根集中取得最终运行证据。空Schema→同资源恢复不解除deny；最终Node17/17、全Web201/201、Chrome10/10、既有Vector导入1/1、build与独立复核通过。初轮UI9/10、下一轮8/10均为抽屉定位/Chart模式夹具缺口，修后真实打开Raw、断言旧载荷可见及deny后目标panel0，未降低断言。真实Kestrel兼容3/3单列，证据与门禁/清理见validation-report。

### WB-19 合同与文件冻结（2026-10-06；本地切片已验证）

- 实施代理独占 `web/src/components/FullTextSearchWorkbench.vue` 与新增 `web/tests/fulltext-workbench-migration.test.mjs`；浏览器证据代理独占新增 `web/e2e/fulltext-workbench-migration.spec.ts`；第三代理独立只读复核。根独占共享文档、验证 runner、集成、完整 restore/format、stage 与 commit，最多三个活动子代理，25 个命名文件、45 分钟、代理测试最多两次；根每条长命令另设超时。
- 保留 database、collection/index 原名、现有 `fulltext:collection:index` key 与 route-only 不执行；补六态与只读/无权限门禁，搜索、Analyzer 和 Find 按发起时 profile/endpoint/auth/epoch/API 与参数快照隔离迟返、ABA 和卸载。
- HTTP 401/403 清命中、文档、Token、结果、写草稿和审批，保留检索输入；同身份 Schema/认证刷新不得解锁。安全读取恢复入口留后续独立切片，不凭刷新或本地按钮宣称权限恢复。错误正文固定脱敏。
- 沿用既有 search-preview API，Top-K 限 1～100；返回先截断到发起 Top-K 再 Find、格式化、分页和导出。超返明确 truncated，历史写实际 preview count/完整性；本地分页只覆盖该结果，不代表所有匹配文档、不新增哨兵请求或虚构 continuation。服务端扫描/物化/字节/总堆预算仍待验收。
- 重建/导入仍复用一次审批；冻结原上下文、API、模式和项目，批次前后校验 epoch，已派请求不声称未执行，缺终态/传输断连记 unknown 且不重放旧审批。不改 DocumentAdvanced、Server API、路由和其它宿主；fixture、真实 Server 兼容、三宿主/安装/AOT/发布证据分别记录。
- 重建终态匹配真实 Handler 的 `rebuild_index/ok/success`、completedUtc、index check 与 document/原 owner/name/fulltext/sync_touch/planned=false；planned、缺失或错目标不得记 success。导入最多1000文档，文件10MiB、文本10MiB字符，批次窗口60秒并使用既有30秒请求超时。首次已派批次后ABA记原身份unknown，后续批次为0；真实Axios分派前同步切身份adapter为0属于独立本地证据。
- 最终专属Node15/15、全Web184/184、TypeScript/Vite与Chrome8/8通过；真实Kestrel既有FullText与Maintenance兼容4/4，未连接新UI执行真实权限/写旅程。根证据目录 `artifacts/wb19-validation-20261006` 保留最终规格/日志与自有进程核验；初轮14/15夹具复制问题已修正，reviewer早期无完整身份记录的阶段运行不计最终证据。提交以最终完整restore/format与diff门禁为前提，真实宿主/AOT/安装/硬件/长稳/发布继续分开。

### WB-18 合同与文件冻结（2026-10-06；已完成）

- 恢复合同：本地 401/403 锁存后仅显式“重新验证读取权限”可发起一次全新 Find，默认空过滤、100 行、无旧游标。运行期间保持 permission 且隐藏载荷；仅当前 database/原名集合/profile/endpoint/auth/epoch 的有效成功响应解除本地锁存。失败、迟返、卸载和身份 ABA 不解除；外部 permissionDenied 不能被本地动作覆盖。恢复不还原旧写草稿/审批、不自动执行写入，不恢复旧高级查询或 Change Feed。
- 高级读取合同：Aggregate 保留用户 pipeline 并在末尾追加 `$limit: 1001`；Distinct 将用户预览上限限制为 1～1000，请求多一个哨兵值。两条路径最多格式化/展示/导出 1000 项（Distinct 为所选上限），超限明确 truncated，历史记录实际预览数与不完整性。不得把末尾 limit 称为服务端扫描/中间物化/字节/总堆预算；不提供虚构分页。
- 实施代理独占 `web/src/components/DocumentCollectionWorkbench.vue`、`web/tests/document-workbench-migration.test.mjs`；UI 证据代理独占新增 `web/e2e/document-recovery-budget.spec.ts`；独立复核只读。`DocumentAdvancedWorkbench.vue` 的更新/索引/Change Feed、Server API、路由与其它宿主保持独立验收，不重复包装 WB-12。
- 根独占共享文档、兼容证据盘点、验证 runner、完整 restore/format、stage 和 commit。最多三个活动子代理；每代理最多 25 个命名文件、25 分钟、测试最多两次，根整体验证各命令另设超时。静态/fixture、真实 Server、三宿主/安装/AOT/发布证据分别记录；旧策略保留目录只读复用 runner，不触碰删除。

### WB-21 合同与文件冻结（2026-10-06；本地切片已验证）

- 干净起点 `08bd329f`，只推进 KV 权限与有界预览兼容切片，依赖既有 KV API、WB-05/WB-16 共享结果与原名导航。复用游标浏览、JSONL round-trip、NX/XX、交换/删除、精确版本字符串与一次审批，不重做整页。
- 实施代理 `/root/wb20_vector_impl` 独占 `web/src/components/KvKeyspaceWorkbench.vue`、新增 `web/tests/kv-workbench-migration.test.mjs` 和既有 `web/tests/kv-workflow.test.mjs` 的必要合同断言；必要 API 响应校验仅在先回报并追加冻结后进行。UI代理 `/root/wb20_vector_ui` 独占新增 `web/e2e/kv-workbench-migration.spec.ts`，`/root/wb19_fulltext_impl` 独立只读复核。根独占六个共享文档、runner、完整restore/format与git。最多三个活动子代理，每代理最多25个命名文件、35分钟，不自跑长验证。
- 保留 database/keyspace 原名、旧 `kv:keyspace` key、真实 prefix/cursor；补 normal/empty/error/permission/readonly/longContent 六态、外部readonly/permission门禁，身份/实际endpoint/auth/API与同步epoch隔离迟返、ABA和卸载。401/403来自Scan/Stats/Get/Write均锁存、清值/统计/游标/结果/编辑与导入草稿/审批，固定脱敏提示；同身份刷新及空资源往返不解锁，安全恢复另验。
- 扫描每页1～1000，累计与批量Get预览1000；先截断再映射/格式化/显示，达到上限停止Load more，超返不可复用会跳过未保留项的cursor。历史/结果记录实际预览数与完整性，不把加载页或客户端截断称为全keyspace、快照或服务端资源预算。
- Inspector按原始字节数提示，Text/JSON/Hex/Base64最多格式化4096字节，明确截断；完整原始值仅用于既有round-trip导出，不把截断预览自动填入写草稿。readonly浏览/Get/统计/导出保留，TTL/写入/删除/导入禁用；所有程序入口亦检查门禁。
- 审批仍一次消费并绑定发起身份；缺失/传输异常的既有unknown路径在历史保持unknown，不重放。全量atomic响应形状、导入/写字节预算、CAS及真实新客户端权限/写旅程留独立切片。Server、路由、其它模型与宿主不改。静态/fixture、真实Kestrel兼容、三宿主/安装/AOT/硬件/长稳/发布分别记录，旧策略保留Temp不删除、不重试或绕过。
- 三代理已停止写入；独立复核首轮发现空数据库占位解锁及缺真实Axios证据，修复并补测试后第二轮PASS。最终专属Node17/17、既有KV12/12、全Web218/218、TypeScript/Vite与Chrome12/12通过；既有真实Kestrel兼容6/6单列。完整Base64仍解码，不能称字节/总堆预算；raw markRaw defaults无信号的独立ABA不在证据内。根证据目录 `artifacts/wb21-validation-20261006`，本轮提交说明 `feat(m47): isolate KV permission state and bounded previews`，实际哈希以git log为准；最终完整restore/原级别format与staged diff check为提交放行条件，日志/退出值见验证记录。

## 会话与自动检查（2026-10-06已转移）

### WB-16 文件与依赖冻结（2026-10-06；已完成的本轮记录）

- 干净起点 `941550a5`，WB-15三个代理已结束。只推进WB-16，不并行启动WB-17/18。发现Web router、API默认地址与Vite固定根路径；登录守卫的已认证分支丢失redirect，需同一兼容切片验证修复。
- 实施代理独占 `web/src/router/index.ts`、`web/src/views/LoginView.vue`、`web/src/api/client.ts`、`web/src/stores/connections.ts`、`web/vite.config.ts`，新增 `web/src/utils/workbenchNavigation.ts` 和 `web/tests/workbench-navigation-auth.test.mjs`。复核发现AppShell自动SSE订阅仍请求根路径，追加冻结 `web/src/api/events.ts` 的最小部署base修复及实际构造URL证据；不扩大remote profile SSE语义。未改旧导航测试。保留默认根部署，代理子路径使用显式 `SONNETDB_WEB_BASE_PATH` 构建配置，不从不可信URL猜测部署基址。
- 浏览器/Host证据代理独占新增 `web/e2e/vscode-workbench-navigation.spec.ts` 与扩展 `extensions/sonnetdb-vscode/src/test/host/index.ts`；真实消费WB-14链接生成器，验证最终页面/资源与登录返回、同名MQ、仅导航字段及不执行SQL。Host的外部浏览器开启边界可拦截，仅作为真实命令调用合同证据。
- 浏览器首轮14/17发现索引id的 `table:` 前缀使Sidebar错误展开Tables，追加实施代理独占 `web/src/components/ManagementExplorerSidebar.vue` 的精确资源group匹配；不改原名、key、路由或选中索引语义。另两项登录失败为等价URL编码断言误差，仅修fixture完整decoded字段比较。复验和独立复核按最终树重跑。
- 独立复核代理全程只读并PASS；根独占共享文档、验证runner、集成、完整restore/format、stage与commit。最终代码提交12文件（八Web生产文件、专属Node测试、浏览器spec、Host测试与CHANGELOG），不修改Server/资源身份/存储/发布合同。初轮14/17不计完成证据；修复后根与代理浏览器各17/17，无skip。Measurement/Relation允许既有只读预览，保存草稿不执行；index/backup保留SQL工作区落点。fixture认证、SSE构造和真实Host捕获分别记录，不能计作真实Server/外部OS浏览器/VSIX验收。
- 根临时目录 `C:\Users\mysti\AppData\Local\Temp\sonnetdb-wb16-73be578e6aa94e93aee31d340ef55950` 删除被自动审查拒绝，仅返回blocked by policy；58个PID身份核验无任务存活，runner/日志和cleanup-status.txt保留，不重试删除或绕过。原交接保留目录亦不触碰；研发从WB-17继续，不因临时文件保留阻断。

当前会话：**SonnetDB Workbench 三宿主研发与验收**，ID `01a10d27-d964-7550-9b8e-066447122527`，host `local`，SonnetDB本地项目。已从`d773a62e`接管唯一写入并提交WB-15～WB-17；同一个heartbeat `workbench`（Workbench 三宿主持续研发与闭环）保持ACTIVE、每30分钟，没有创建重复自动化。旧thread `01a10862-bcd5-7d82-ab22-c916c00221a3` 在最后交接提交后停止仓库写入；下一次从WB-18接续，无需重新确认已有授权。

### WB-17 文件与依赖冻结（2026-10-06；已完成的本轮记录）

- 干净起点 `fefcc72e`，WB-16 三代理均结束；本轮只推进 Relation Table Workbench，依赖已提交 WB-11/WB-12/WB-16。
- 实施代理独占 `web/src/components/RelationalTableWorkbench.vue` 与新增 `web/tests/relational-workbench-migration.test.mjs`。复用已有 SELECT 分页、行编辑、设计器、索引、导入导出、ER/DDL 和 WriteApprovalPanel；补 database/原名身份、六态、读请求迟返隔离和写审批上下文门禁。子工作台权限合同不足时保守隐藏，不能把本页门禁计作其独立迁移。
- UI 证据代理独占新增 `web/e2e/relational-workbench-migration.spec.ts`，覆盖实际组件状态、跨库同名、权限载荷清理、分页与审批；另一个代理只读复核。根独占共享文档、验证 runner、完整门禁、stage 和 commit；不改路由、Server、MQ 或其它宿主代码。
- 自动只读 SELECT 预览仍允许；打开历史/SQL 草稿不执行。静态、fixture 与真实 Server、三宿主、安装/AOT/发布证据分开。两处已被策略保留的临时目录不触碰。
- 最小差距已盘点：旧行浏览没有上下文/卸载迟返校验，审批没有只读/权限/连接门禁，batch 结果不足也可能记 success。迁移合同为读取快照与 epoch 隔离；403 清理载荷/草稿/审批；只有请求语句数与完整终态全部匹配才记写成功，断连/不完整为 unknown 且旧审批不可再确认；历史始终保留发起时上下文。只读保留本页 SELECT 与结果导出，未具备只读合同的子工作台暂隐藏。
- 最终纯 DDL 经只读复核进入 readonly allowlist；403 仍隐藏全部子页。Node15/15（含真实 Axios/SQL API 分派 adapter）、全Web161/161、Chrome12/12和既有设计器2/2；初轮UI10/12的实际 FAB 遮挡及 selector 歧义已修复并复验。代码提交 `f1978263` 前完整 restore/Format Check/staged diff check 通过，根维护实际哈希、门禁和共享记录，不把 fixture 写成真实 Server/三宿主完成。

### WB-22 合同与文件冻结（2026-10-06；本地切片已验证）

- 干净起点 `92c73a5baa3bfd9aff7a4553eba24382e6420ba5`；只推进 MQ 权限、请求隔离与有界预览兼容切片，依赖 WB-16、既有 Browse/Publish/Ack API 与共享结果/审批。复用 Overview/Messages/Consumers/Configuration、JSONL、真实 offset 窗口和趋势，不重做页面。
- 实施代理 `/root/wb20_vector_impl` 独占 `web/src/components/SonnetMqWorkbench.vue`、`web/src/api/mq.ts` 六 helper 的 optional signal、`web/src/api/management.ts` 仅 `fetchMqTopics` 的 optional signal、新增 `web/tests/mq-workbench-migration.test.mjs`；UI代理 `/root/wb20_vector_ui` 独占新增 `web/e2e/mq-workbench-migration.spec.ts`；`/root/wb19_fulltext_impl` 独立只读复核。根独占六共享文档、runner、集成、restore/format、stage/commit；最多三个活动子代理，每代理最多25个命名文件、35分钟，不自跑长验证。
- 保留 database/Topic 原名、旧 `mq:Topic` key、scope=database、identity=database+Topic、persistenceScope=instance 与 `.system/mq`。补六态、readonly/permission 程序与按钮门禁；冻结实际 API/endpoint/auth/profile/database/Topic 和同步 epoch，隔离迟返、ABA、同名跨库、新读与卸载。Topics/Browse/Stats/Offsets/Retention/Write 的401/403清旧 topics/消息/header/metadata/trend/结果/写草稿/审批并锁存；同身份刷新、空身份往返不解锁，显式安全恢复另验。错误正文固定脱敏。
- Browse请求1～1000，返回先截断再map/分类/显示/导出，历史写实际 preview count 与窗口/截断完整性；下一页沿最后保留消息真实offset前进，不使用超返尾项、不虚构cursor或全Topic快照。Inspector payload最多格式化4096原始字节，header有界预览；完整已加载payload仅为既有JSONL round-trip。完整Base64解码/传输/服务端扫描/字节/总堆预算单列。unsafe JSON整数offset不得用于Ack/分页/Seek，不伪造字符串精度。
- Seek保持最多25窗口，每窗最多1000消息，补60秒墙钟、严格前进、请求快照/取消；自动采样最多12轮/60秒且单飞，用户可显式重启。审批绑定原API/身份/项目，dispatch前一次消费；批次最多1000项/60秒，缺失/错目标/传输异常终态记unknown且不重放，401/403仍锁存。Publish真实终态topic+安全非负offset，Ack为topic+原consumerGroup+安全nextOffset；客户端abort不能声称Server未执行。
- Server、路由、其它模型和三宿主代码不改。Fixture/真实Kestrel既有兼容、新UI真实Server权限旅程、三宿主/安装/Extension Host/AOT/硬件/长稳/发行物分别记录；旧策略保留Temp不删除、不重试或绕过，禁止Graphify、广域工具扫描及未授权安装。
- 三代理已停止写入，第三冻结独立复核PASS；high-water自身safe门禁、Seek最终Browse/selection与总deadline、auto请求归属及fallback实际Topic同步epoch已补行为证据。最终专属Node22/22、全Web240/240、Chrome16/16、既有MQ浏览器3/3、TypeScript/Vite通过；既有真实Kestrel兼容2/2单列。初轮Chrome13/16发现结果抽屉内共享Panel缺inline的实际组合问题，修复后未降低Raw/deny断言。根证据目录`artifacts/wb22-validation-20261006`，门禁/清理见validation-report；完整metadata/解码/传输/总堆预算与真实新UI权限/三宿主另验。
