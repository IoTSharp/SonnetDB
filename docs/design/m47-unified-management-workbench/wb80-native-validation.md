# WB80 本机 Native 数据库恢复验证

2026-10-09，从本地 `70b19b21c626d61d8a020ac8d78db42d339ac975` 接续。唯一新 actual 已取得本机 Studio 的普通 A/B 选择、两次桌面启动、被动恢复 B、真实 B 查询和两次正常关闭证据。这是已有功能的受限验证，复用旧 Release 产物，没有新增恢复功能或构建产品，也不证明当前源码与旧二进制等价。本片准备、独审、运行及本地集成合计本会话第4个独立任务，收尾后4/5。

证据根为 `artifacts/wb80-native-validation-20261009`，唯一运行是 `actual/studio-native-real-4fb02b8f-46d4-4927-9e7d-cc67e5a9f749`。`task-contract.json` 冻结14个受管命令、最多2次预检、1次 actual、0产品构建、Native 900秒、清理预留90秒、外层1020秒及根07:30Z截止。actual 已消费1/1，不再重跑。

## 准入与执行绑定

runner 只增加5处 WB80 证据入口/标签，逆投影逐字节恢复90,092字节基线，其它行为未改。执行源90,256字节，SHA256 为 `AC17C2FDDA98C7A63B8D23EB462BDC9644BE90BBD6D5C3C8D4C071563D8A04A7`。沿用 WB79 的真实 C# SaveAsync 共享夹具与严格派生身份合同（C#20/20、Node80/80），本片未重复这批测试。

专属工具代理准备新证据工具；另两位代理分别复核源码和工具。`source-review.json`、`tools-review.json` 绑定合同、37个依赖项（9 runtime manifest项及28 named项）和11个 reviewed source；独立终态复核另存 `actual-evidence-review.json` 与 notes，不由 runner 自报 PASS 代替。旧 WB77 工具/证据保持。

受管微试及源码语法命令exit0。第一次预检通过，`checkedAtUtc=2026-10-09T06:27:51.4934451Z`，CPU1%、可用内存28,998,240KiB、进程行879，四端口18371/18372/55371/9371通过。actual 于06:28:23.7197590Z预占，Node保留句柄启动06:28:23.7626258Z，退出收据06:32:03.3424849Z、exit0。runner记录199.645秒；包装器stdout的201秒在finally之前，不能当作完整外层时长。

六个必需终态文件全部保存：`normal-exit.json`、`cleanup.json`、`bridge-responses.json`、`database-recovery.json`、`process-events.json`、`result.json`。最终 `passed`、`normalExit`、`cleanupProven`、`requiredResultWriter` 均为true。

## 已完成的真实旅程

普通 A/B 选择分别获得新的 bridge PUT acknowledgement（A sequence8/request66，B sequence9/request74），与各自磁盘及DOM身份核验配对。独立的普通 B 准备步骤未计作这两次主验收选择。首次 Studio 身份为 `67844:2026-10-09T06:28:27.7698470Z`，第二次为 `59648:2026-10-09T06:29:37.8226600Z`；两个 Managed Server 身份分别为 `57392:2026-10-09T06:28:30.0334280Z` 和 `70768:2026-10-09T06:29:40.0917480Z`。

第二次桌面在路由导航前记录被动恢复至 `WB61_Bravo_e5a9f749` 的DOM，随后取得 launch2 的 GET `/studio-bridge/connections` acknowledgement：sequence12/request117、HTTP200；这是观测到的路由bootstrap GET，不称最早自动GET。连接库文件788字节，重启前后 SHA256 一致，为 `69c0b7c878fd5520a51f45ce4e3d2f7e3a96c7f869ba2c4b6634b5d6d2e8a3c4`。五次安全字段观察均记录已知 `activeIdentity` / `identity` 类别，unknownFieldCount=0；不由此推断 WB77 已被清理的实际未知字段。

恢复后向真实 Server POST `/v1/db/WB61_Bravo_e5a9f749/sql`，执行 `SELECT "Marker" FROM "WB61Probe"`，HTTP200、1行 `WB61_B`、complete=true，界面显示同一值。磁盘内容没有替代 native acknowledgement，查询也没有替代持久化/被动恢复检查。

两次 `CloseMainWindow` 均accepted、Studio exitCode0/signal null，分别确认自有身份退出及四端口释放，fallbackUsed=false。Studio的正常关闭可能在有界等待后终止其Managed Server，因此本片不证明Server优雅关停、任意崩溃恢复或服务器实例恢复。

## 运行时、清理与证据限制

实际CDP报告 `Edg/154.0.4258.62`、protocol1.3；两次WebView根PID71452/70388的记录路径均为 `C:\Program Files (x86)\Microsoft\EdgeWebView\Application\154.0.4258.62\msedgewebview2.exe`，父身份分别为上述两个Studio，命令含自有profile及CDP9371。文件前置、实际运行时观察与源码构建等价是不同证据，不以版本字符串推导整个进程生命周期。

独立终态复核通过跨文件核验，覆盖37个有序依赖项、11个先前源码签名、49个记录内完整身份、252个父链链接及26个helper退出/生命周期；37项包含固定重叠，不称37个唯一文件。复核两次工具输出截断、首次单块超过12K的偏差保留，随后通过完整JSON解析及明确投影补核验收字段；不声称全部process-events原文语义审计、再次散列全部11源或fresh CIM。原始连接库文件已由harness清理，独审核其记录而未重新读取原文件。

原cleanup记录18个身份均 `exitedBeforeFallback=true`，fallbackActions/helperReclaims/errors均为空；三个本任务目录由harness清理，profile305、data163、server-content4，共472项。外层包装器51条进程记录、cleanupFailures为空。根post-actual新鲜审计核58个完整已记录身份及4个保留句柄退出收据，存活0、复用0、unknown0、额外stop/delete0。最终门禁后的新鲜审计另记，不能把未观察后代或短命shell缺口写成全会话孤儿自由证明。

根曾在独审尚未写完时读取不存在的tools-review文件，该次在调用包装器前失败，无受管子进程、预算仍0；原检查点保留。WB77原FAIL、被拒字段原因unknown及耗尽的actual、WB79的C#包装器FAIL和首轮Node中止均不被本次成功改写。WB61建表HTTP400与修后0actual也是历史事实。

首次文档准备假定CHANGELOG为CRLF，实际为LF，在门禁/提交前失败；三个已插入的自有摘要经逆投影恢复并逐SHA匹配接收原文，未动HANDOFF或外来文件。修正为保留各文件换行并通过CRLF/LF微试，原失败存于 `integration-preparation-failure.json`。这不改变已冻结的Native工具/源码或actual结论。

## 本地集成与后续

仅集成runner、本报告、HANDOFF、ROADMAP、CHANGELOG、work-queue与validation-report七路径。HANDOFF候选由HEAD blob加本片自有段生成，完整工作前缀及其它会话追加保留，不whole-add，不提交博客/CSDN/OSChina/微博内容。完整最终树 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 必须实际通过后才能提交；首轮完整门禁均exit0，format保留工作区加载警告；首次staged diff因仅自有HANDOFF新增CRLF段失败，commit0。修正该段为LF并逐字保持外来前缀/尾部后，以新独审integration-only副本重跑最终树；最终stdout、retained-exit与wrapper-cleanup收据使用restore-final/format-final前缀。首轮fresh审计本身PASS（65身份/6retained），外层误查PowerShell脚本未设置的native LASTEXITCODE而exit1另存，不重写原结果。最终源/私有HANDOFF冻结、精确index/tree、提交与4/5计数、新鲜退出审计见 `integration-freeze.json`、`staged-tree.json`、`commit-checkpoint.json`、`post-commit-verification.json`，不得预填未取得的结果。

本机这条数据库选择恢复旅程已补证，三宿主整体仍未闭环。OS文件对话框四phase尚未取得完整证据；再次运行前须先证明窗口可见/可激活，不能盲重跑WB41。VS Code向导UI、Webview分页、Notebook、LSP等按最新队列分别核实；安装、Extension Host、NativeAOT、固定硬件、长稳和发行物继续独立验收。M47导航/九模型合同、Graph Beta、MQ database身份及instance `.system/mq`边界不变。

本片结束后仍使用当前聊天及同一个workbench自动化（ACTIVE、每30分钟）；未到5/5，不迁移或重复创建。固定PS7、有界数量/墙钟/取消/进度、完整身份父链及仅自有树清理要求保持。保护旧拒删Temp、WB40保留runtime、其它会话、共享缓存/交付物和 `origin/parity-results=0061d6d78591fb08493f473d3231ca42303faae1`；无push、发布、部署、安装或外部发送。
