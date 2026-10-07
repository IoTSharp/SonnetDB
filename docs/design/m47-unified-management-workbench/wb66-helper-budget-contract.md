# WB66 Native helper 协议与预算合同

2026-10-08（Asia/Shanghai）。本片仅盘点 WB64 实际 helper 调用链及收集、缓存、回收预算，形成下一独立诊断任务的合同；0 actual、0 build、0 test、0 产品/runner/helper 改动。WB64 的实际恢复、正常退出和原回收均仍为失败，WB65 新严格退出审计失败独立保留。

## 源与实际执行绑定

以下是一基源位置；SHA-256 为本片明确路径的新鲜读取，不代表重新构建 runtime。

| 代号 | 路径 | SHA-256 | 本片引用入口 |
|---|---|---|---|
| R | `web/e2e/run-studio-native-real.mjs` | `C696CE1E866B038E78FC05A2A2C14D57B2E2D0BECEFB018AB33CADCEA222D599` | 第 43、59、130、135、194、197、203、697、724、782、1084、1138 行 |
| H | `web/e2e/studio-native-process.ps1` | `E0C15C2BD8D812B9D1ACEACFB358B90EEFCB850C20B4AD214C1AD94E228FD198` | 第 11、18、24、42、76、101、116、133、174、197、232、236 行 |
| E | `web/e2e/studio-native-evidence.mjs` | `CFC16E8E29B50F0B8606B2A2FC36C416DD38A552544296DFA24DD495874D723F` | 第 14、42、98、110、115、146 行 |
| X | `artifacts/wb64-studio-database-observation-20261008/runner-executed.mjs` | `82012CD05152AF55334D1ED9A2D92BADDF370EEDE67689A775DA8D29A7D0A9C1` | 与实际 `run.json.hashes` 一致 |

WB64 实际执行的 runner 为 X，不把最终 R 的整文件 SHA 冒称为实际执行 SHA。新鲜逐行字符串对拍证明 X 与 R 的第 59～66 行 deadline、第 135～221 行 helper/snapshot/captureOwned，以及第 1084～1158 行 cleanup/terminal 入口完全相同。实际 `run.json` 同时绑定 H、E 的上述 SHA；本片没有把 WB65 修改后的 scenario 称为 WB64 实际源。`runtime-reuse.json` 的九项旧产品/runtime 记录只用于定位已有证据，本片未重新读取九项产物或宣称其新构建/新验收。

## 实际调用链与协议

R 第 43 行直接选择 H。第 135 行 `processAction` 只启动固定 PowerShell 7，参数只含固定 helper 路径和 `snapshot|close|kill` action，payload 作为结构化 stdin 发送；H 第 1 行限定 action，第 10 行检查 PowerShell 7。H 首先读取自身完整身份和父链，然后在第 120 行输出、flush `kind=helper` 握手，再读取 payload、执行 action、输出 `kind=result`，异常由第 236 行输出 `kind=error`，退出 1。没有 shell 命令解释、按进程名停止或工具发现入口。

R 第 160、186～193 行要求握手身份匹配已启动 child PID、runner 父 PID 和 7.x 版本，记录退出/close事件，最多接受三行 JSON。Node payload 必须小于 524288 **字节**，stdout 累计拒绝超过 524288 **字节**的chunk。第155行只在追加前检查握手缓冲小于262144个JS字符，随后追加整chunk，没有追加后截断/拒绝；这不是握手缓冲的硬上限，末chunk可越过该阈值。H 第 125～132 行的 stdin 上限是 128 次、每次最多 4096 **字符**，累计字符数达到 524288 拒绝；两端的字节/字符限不能互换。stderr 只累计字节数，不保存内容；这条计数本身没有独立拒绝上限。

H 成功 envelope 第 232 行已有 `cimQueries/cachedPids/elapsedSeconds`；R 第 194 行只返回 `result.result`，第 142～144 行的 helper record 没有保存这三项统计。E 第 98 行的 compact helper 只保留 action、启动/退出、身份引用、exitCode、stderrBytes、timedOut 等既有字段。H error envelope 只有 `kind/message`，WB64 墙钟拒绝的三项数字目前仅存在脱敏错误字符串中；本片不解析字符串并伪造结构化遥测。

## 预算归属、起止与调用点

| 预算 | 归属及起点 | 结束/检查位置 | 合同与实际限制 |
|---|---|---|---|
| database-recovery 总窗口 900 秒 | R 第 59～62 行，模块初始化 `startedAt` | main=总截止前90秒；cleanup=总截止前15秒；terminal=总截止 | R 第 101 行 `check` 在调用/循环边界检查，SIGINT/SIGTERM 与 main timer 请求取消；final 清理不继承已取消的 main signal。不会证明正在运行的 native/CIM 被取消。 |
| helper 次数 main64/final96 | R 第 137～138 行，同一个累计 counters；入口先 check | 每次 `processAction` 入口拒绝超额 | final96 为累计上限，非再额外96次；count 在 payload 检查/spawn之前递增，因此不能把 count 自动等同成功启动数。 |
| 单 helper 最多25秒等待 | R 第 168 行，在 spawn/stdin发送后设置 timer | child close 或 timer reject；第181行finally清timer | 等待取25秒与剩余main/cleanup时间最小值。timer只记 `timedOut` 并拒绝等待，未直接kill；无握手身份时保留交根审阅。 |
| 单 H 调用20秒、CIM160次 | H 第 11～12 行，版本检查后启动 Stopwatch/清零计数 | 第18～20行 `Assert-TaskBudget`，各项操作前及cache写入前调用 | `elapsed>=20` 或 `queryCount>=160`拒绝。并非硬取消；最后输出result前没有额外最终Assert。外层helper进程耗时含PS启动/握手/传输，不能与内部Stopwatch直接等同。 |
| 单次 CIM operation20秒 | H 第47～49、157～159行，查询前计数+1 | `Get-CimInstance` 返回后进入cache写入/后续guard | 固定 `OperationTimeoutSec 20`，未按内部剩余预算缩短。前后guard不取消进行中的CIM；旧证据没有每次CIM耗时，不能归因到特定查询。 |
| 每调用缓存最多160 PID | H 第16行创建；第24～39行写入 | 新键遇160拒绝，调用进程退出结束作用域 | 非跨helper、跨run、跨desktop共享缓存；null亦缓存。非Fresh读取可命中；descendants枚举直接写full CIM行，因此cache PID数不等于query次数。 |
| ancestry最多12层 | H 第76～98行 parent chain；第101～112行mutation ancestry | 每层wall/count guard；cycle、超深、替换身份拒绝 | handshake与snapshot父链读取可用本调用缓存；mutation对每条已录父身份必须Fresh。不允许拿旧cache代替fresh停止权限。 |
| snapshot seeds8/items64/descendant depth12 | R 第197～200行；H 第133～173行 | BFS队列及items合计/子数/深度守卫，余队列拒绝 | 一次snapshot返回最多64身份；每子枚举本身计一次CIM，完整行入cache后dequeue不再重查PID。缓存上限160并非snapshot输出上限64。 |
| runner owned32/events24 | R 第95、203～220行 | root/descendant exact身份及创建时序核验，owned/events超额拒绝 | database-recovery32为不同完整身份数上限，不是当前活PID数；其他scenario上限16不变。 |
| helper reclaim20秒 | R 第1088～1095行，finally先筛未证明exit的helper | 逐项前检查reclaim截止，最终受cleanupDeadline约束 | pending上限96；每次实际action仍受单helper25秒/剩余cleanup限制。20秒reclaim的前置check并不硬取消一次已派helper，故不能称整个reclaim严格最多20秒。 |
| owned fallback/absence | R 第1100～1124行 | discovery→分批snapshot8→逐identity kill→再次snapshot→四port→helper exit→目录 | 最多32 target；具体kill helper内再次fresh target/ancestry；任一步失败进入外层catch，后续不获得PASS。不得用root补救升级原normalExit/cleanupProven。 |
| native close10次/2秒、sleep100ms | H 第197～218行 | 原窗口发现/refresh后fresh target再CloseMainWindow | 只在已匹配target并fresh祖先后执行；找不到窗口为未动作，不据此诊断产品故障。 |
| verified单PID kill与1500ms退出wait | H 第174～229行 | targetFresh、exact五字段、fresh已录祖先→Kill(false)→WaitForExit(1500) | 无隐式tree kill；runner逐后代记录、复验。dispose有finally；timeout不等于退出，仍需后续absence。 |
| owner目录扫描8秒/4096entries/512dirs/depth16 | R 第724～756行 | 路径/realpath/no-link/marker核验后先完整扫描，再逐已核文件unlink与目录rmdir | 仅profile/data/server-content；删除阶段受cleanupDeadline，8秒是扫描预算，不是整个删除硬界。不能清共享缓存/历史交付物。 |
| terminal独立写入≤2秒/项、encode≤1秒 | E 第110～159行；R 第1138行 | result预留3秒，detail失败不会跳过essential result，整体受totalDeadline | 本scenario六terminal；512KiB/credential拒写保留。所有writer与lifecycle/cleanup门禁同时满足才可passed；独立写齐不等于实际旅程成功。 |

### 身份与缓存的不可放宽边界

R 第 130 行的身份比较包含 PID、父 PID、创建时间、完整commandLine和executablePath。报告只引用匹配结果、位置和SHA，不复制raw tuple、command或credential。H 第 24～39 行拒绝从已缓存null变为live身份或已缓存live身份发生创建/父/command/path变化；live变null可记录absence。mutation第178行强制Fresh target，第186/107行强制Fresh每条已录父身份；已退出祖先允许作为先前记录孤儿的边界，live复用必须exact匹配。close在窗口发现后第215行再Fresh target。kill只作用于单个经准入身份，不能以数值父PID关联、缓存命中、snapshot缺项或统计字段授予停止权限。

## WB64 实证、缺口与可成立结论

唯一实际run为 `studio-native-real-38583042-a9a4-4af9-aaf2-dba5696ccd8c`。`process-events.json` 的 `segments[0].evidence.helpers` 恰有5次snapshot，均已录完整身份引用、stderrBytes=0、timedOut=false；前4次exit0，末次exit1。下表只有已留脱敏字段，外层耗时由已留启动/退出时间相减，不是helper内部Stopwatch。

| helper序号 | 外层start/exit（UTC） | 外层耗时秒 | exitCode/Node timedOut |
|---|---|---:|---|
| 1 | 17:11:32.039 / 17:11:41.676 | 9.637 | 0 / false |
| 2 | 17:11:41.713 / 17:11:54.135 | 12.422 | 0 / false |
| 3 | 17:11:54.402 / 17:12:15.828 | 21.426 | 0 / false |
| 4 | 17:12:17.021 / 17:12:38.182 | 21.161 | 0 / false |
| 5 | 17:12:46.761 / 17:13:07.586 | 20.825 | 1 / false |

`cleanup.json.errors[0]` 为 `Native process helper budget exceeded (CIM queries=15, elapsedSeconds=20.19, cachedPIDs=16).`，与H第20行模板一致：20.19是两位小数墙钟观察，15小于160、16小于160，因此这次拒绝符合墙钟分支，**不是查询数或cache容量耗尽，也不是Node25秒timer已触发**。cache16可能来自子枚举直接缓存或先前PID读取，现有证据不足以证明具体组成。第3/4次helper外层耗时超过20秒且exit0，不足以证明其内部Stopwatch超限。

调用链只能定位原cleanup第1100行 `cleanup-discovery` 的snapshot拒绝；同一个try在第1126行catch，故原fallbackActions、helperReclaims、identityChecks、directories均空、allFourPortsReleased=false。原normal-exit未attempt/accepted，原result `passed/normalExit/cleanupProven=false`，launches=1；database selections空，firstClose/secondLaunch/restored/query/secondClose全null。native总耗时95.85秒，actual wrapper完整收据122.1665353秒/exit1，二者分别记录。

已有：六terminal、helper action/外层start/exit/exitCode/timedOut/身份引用、错误汇总、实际执行source hashes、原失败检查点与后续root补救。缺失：成功helper内部三项汇总的terminal保留、失败error结构化汇总、每个CIM的种类/耗时/阶段/剩余预算与cache hit/miss统计、明确失败guard位置。因此不能推断慢CIM是哪一项、系统性能瓶颈、权限缺口、原窗口故障或历史因果；也不能把snapshot改成批量全机CIM、缩掉祖先核验或延长预算作为已证修复。

root `root-recovery-result.json` 明确单列 `rootRecovery=true/removedObjects=454/stops=0/terminalEvidenceUnchanged=true`、四port独占bind成功，且 `actualPassed/originalNormalExit/originalCleanupProven=false`。此补救证明不能回填原cleanup。A→B、两次正常desktop关闭、被动恢复B、B查询仍待独立真实验收，下一次actual还必须有可成立的异库选择前置。

## 下一独立任务：保留现有helper安全统计

建议下一片冻结“helper envelope统计保留”一个独立任务，先补可复核信息再决定是否需要逐查询诊断。具体最小合同：H success envelope的既有 `cimQueries/cachedPids/elapsedSeconds` 原样保留；H error envelope以相同内部计数器/Stopwatch附加这三项有限number，不解析错误字符串，不调用新CIM；R `processAction` 将三字段安全投影到当前helper record，失败时在抛原primary错误前也能保留；E compact helper以最多三个标量字段纳入原terminal。诊断只进入既有result/error envelope和既有writer预算，保留至多三行协议，不新增第四行progress输出。`result.result` 返回形状、错误文字与authority、旧payload/身份/terminal判定不变。未知/缺失/非法统计标记unknown，不coerce或伪造0，不使原失败转为PASS；观察字段不能决定stop、排除后代或继续cleanup。

验收须同时满足：

1. 独立冻结精确路径、parent、源SHA、总截止及原count/wall预算；此诊断0新CIM、0新process/扫描/重试、0actual、0新产品build。观察投影为固定字段/O(1)，不新增列表或动态getter读取；如需要更多phase/query耗时字段，另冻结新范围，不把本合同视为默认授权。
2. 保留20秒/160query/cache160、Node25秒/64至96helper、8seeds/64snapshot/12层、owned32、fresh target/ancestry/close复核、单PIDkill及原回收/终态门禁。原成功业务result逐字段一致；原失败/timeout/无握手/身份替换仍拒绝，统计迟返不得赋予动作权限。
3. meaningful本地合同测试覆盖success、error、missing/非法数字、oversize/credential、Node timeout后迟返及compact终态保留；缺诊断不能遮盖primary错误。测试需要专属预算与进程/Temp finally归属，不借本片0test预算补跑。源和独立review均绑定最终SHA，后续集成由根执行完整restore与原Format Check。
4. 故障数据证明三字段确实来自对应同一helper envelope，失败不把未知填0，不复制raw CIM/command/payload/token；旧六terminal和所有旧FAIL只读保留。只有这些本地合同证据可记诊断PASS，不能记真实恢复/正常退出/回收或性能修复PASS。

## WB65严格退出的独立边界

新继承收据 `artifacts/wb65-inherited-recovery-20261008-01/ending-exit-audit.json`（SHA见下表）为 `passed=false/strictGateUnchanged=true/stops=0/exclusions=[]`，保留一个数值父PID关联的外部图片预览潜在后代；本片只读取该脱敏边界，未重新CIM、kill或排除。数值父PID碰撞不能代替完整身份/创建时间/父链归属；该独立root退出失败不作为WB64 helper墙钟原因，也不形成“忽略后代”建议。

## 证据索引

T目录：`artifacts/wb64-studio-database-observation-20261008/studio-native-real-38583042-a9a4-4af9-aaf2-dba5696ccd8c/`。所有旧文件只读。

| 路径（T为上述目录） | 新鲜SHA-256 | 使用字段/范围 |
|---|---|---|
| T `normal-exit.json` | `E80D9965E0C06B5FEE265A410C3D55D7908A8FB418ACBB295C1D160C52CCAA0D` | attempted/accepted/normalExit/ports |
| T `cleanup.json` | `AFC752D547E25F86B7ACE4591D8546FB835A1C2EF81BF8CD936732ABFA2473C5` | errors与原空actions/checks/dirs及false状态 |
| T `bridge-responses.json` | `F92BB029B99DC2F05F492656B5B8AD6B1C2272AA578DB7048C20AA18FC01877F` | 6条method/path/status/sequence/requestSequence，不复制body |
| T `database-recovery.json` | `42C34604763D0565AEF86CB931B6923B1E872067F42FB36F2835B42CB75F57CE` | selections空/后续阶段null/passedfalse |
| T `process-events.json` | `BCAD569A06197FACC420727645F785E2ADBD166D18BB30F6A919339DB07E57D4` | helperCount5/eventCount2/complete及5个脱敏helper时间/状态 |
| T `result.json` | `0020E5307EE52903144551F77893902DD7E3E9D54C9EBF8EA7082665DF06D611` | 原false状态、launch1、counters、95.85s/fatal |
| T `run.json` | `DA6C4CF0DAEDE4EE7A1AFDCA25A78AEFAD268EEA84F744336A957263389DA449` | actual源SHA、scenario与900s预算 |
| WB64 `actual-receipt.json` | `993612291ECB9C4B54FDD76A1451B61986A83015EFC4FE920A8A101164B5954E` | start/end/exit1，122.1665353s |
| WB64 `root-recovery-result.json` | `CE3FDC1DAC529C6083A2A84FB03DEDBD8A757395F8F37B796DF26E333182F02B` | 独立root补救454/0stops/四ports及原false |
| WB65继承 `ending-exit-audit.json` | `A65FF91D0CB671A5773B1A135014224E28A3E123280047A19C5AF647B204F878` | strict失败/0stop/0exclusion，独立边界 |

本片交付只含此报告与专属implementation evidence，未改共享五docs、git/index、旧证据或任何runtime/source/test。实施使用固定PowerShell7.6.6的短读与SHA读取，未启动长process、建立Temp/下载/服务或删除对象；短shell工具收据不代表完整CIM生命周期捕获。独立review、根最终六路径门禁/提交及当前退出审计由根另记；本报告不是这些门禁的PASS替代。三宿主/真实Server/OS文件/安装/ExtensionHost/AOT/固定硬件/长期/发布分别验收，Graph Beta。
