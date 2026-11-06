# 共同 Provider 候选：JSON 补货应用验收

2026-11-07（Asia/Shanghai）。本专题属于根路线第一顺序#392/#395/#394的共同候选；历史7e9单片和0d687bd9候选的PASS不转移至本轮。本轮实际本地候选为`5.1.0-rc.20261107.1`，源码`eb5c0e5f938372109b473db2194670761dc26a96`；Provider、ADO、Core、Protocol四包和`win-x64`匹配Server由共同manifest冻结。Server为framework-dependent JIT，NativeAOT另验。

消费者位于`eng/probes/issue395-json-app/`，只使用PackageReference；实际运行副本在仓库外`C:/Users/mysti/.codex/provider-consumers-20261107-01a1b1e2-r2/json-update/`，使用独立NuGet.Config、packages和http-cache。本页已独立读取总收据、全部20份JSON业务stdout及两代Server协议日志，核对以下本地结果。

## 本轮消费者增量

| 阶段 | 可检查合同 | 本轮运行状态 |
| --- | --- | --- |
| seed | 新产品、显式RowVersion=0、第三方未知JSON及精确数字/转义原文。 | PASS，三协议各一个独立进程。 |
| update | 无预读的单条带参数UPDATE；策略子树、标签数组/固定primitive元素、固定OwnsMany对象/叶子、Guid/可空Guid/offset ticks/decimal及string converter。 | PASS，三协议业务UPDATE均25参数，无SELECT预读。 |
| update→verify | 同语句库存12→14，但Minimum右值从旧库存得到15；PriorVendor读取旧集合元素。策略回滚后保持，旧版本命令0行，目标外JSON原文不变；新进程读取。 | PASS，三协议独立verify均通过13项检查。 |
| reject | 动态/负下标及converter逐行右值零SQL拒绝；数组越界使先声明的库存setter一并原子拒绝；完整JSON与RowVersion保持。 | PASS，三协议均取得三项零SQL翻译拒绝及一项精确越界执行拒绝。 |
| compete | 两个独立DbContext/线程，以10秒Barrier起跑；相同RowVersion=1的补货命令影响行数排序后恰好[0,1]，每项只发一条UPDATE。 | PASS，embedded原序[1,0]、REST原序[1,0]、Frame原序[0,1]。 |
| verify-concurrency | 新进程读RowVersion=2、完整赢家策略及保留字段。 | PASS，三协议各4项检查；另有Server强制退出后新进程重开，REST/Frame各4项检查PASS。 |

每个协议单独建立新数据库，以seed→update→reject→verify→compete→verify-concurrency六个进程运行。JSON线restore/build、18个业务phase及2个Server重开读回共22个步骤均退出0；18个业务phase没有复用同一进程。共同总收据69个步骤状态均PASS，其中3个迁移强杀步骤按预期退出-1，不能写成69个步骤全部退出0。

实际SQL将目标路径、右值路径和非null更新载荷放入参数；例如`$['Offers'][0]`、`$['Offers'][0]['Vendor']`及含单引号/点/emoji的模型路径均出现在`stringParameters`，更新正文不含业务Reason或这些路径。SQL NULL为已声明支持形状，数值常量和固定业务谓词可由EF内联，不扩大为任意查询值全部参数化。策略替换值Minimum=99与最终Minimum=15、新库存14彼此可区分；供应商对象被替换后PriorVendor仍是原供应商，直接证明右值来自更新前行。Guid、nullable Guid的null、decimal、converter的模型值`mixed`/JSON值`MIXED`、时间ticks及原+05:30 offset均由独立verify读取。

三协议动态下标、负下标和converter逐行右值均为`System.NotSupportedException`，SQL命令数0。越界为一条实际UPDATE：embedded捕获`System.InvalidOperationException`，REST/Frame捕获`SonnetDB.Data.SndbServerException`且`serverError=sql_error`，准确原因均为`json_set_value: 数组路径必须指向现有数组中未越界的元素。`。每个reject回执保留真实异常类型/原文/ServerMessage；完整JSON和RowVersion未改。未把取消、传输或其他数据库错误当成预期拒绝。

Server第一代PID2640、第二代PID14680，共同父PID6964，均绑定同一源码/RID/程序目录；收据分别记录创建时间、完整argv、after-ACK强制进程树停止及`rootExited=true`。第二代ready之后，REST和Frame各启动新的verify-concurrency进程，赢家、版本及未知JSON仍通过。该结果是本机真实进程强制退出/重开证据，不能扩为物理掉电、现场或长期运行验收。

远程连接由`SONNETDB_APP_CONNECTION`传入匹配Server；两代实际日志均出现REST `HTTP/1.1 POST /v1/db/jsonupdate_rest/sql`与原生`HTTP/2 POST /v1/frame`，没有用连接串代替协议证明。进程内部90秒取消，外部执行器记录墙钟与PID归属。全部20份JSON业务回执均包含实际已加载的四个SonnetDB程序集，路径位于仓库外消费者目录，SHA逐项匹配共同包清单；程序集Version原值为0.0.0.0，精确候选身份依据nuspec版本/source commit和包/DLL指纹核对。

## CI 实际发行物消费者

本页另行复核了Publish工作流[42265079795 attempt1](https://github.com/IoTSharp/SonnetDB/actions/runs/42265079795)的CI候选manifest、消费者总收据、三协议update/reject/verify/compete共12份关键stdout及第二代Server协议日志。manifest记录工作流completed/success，精确head为`eb5c0e5f938372109b473db2194670761dc26a96`，版本`5.1.0-rc.20261107.1`；本轮消费实际CI四包和同源`win-x64` NativeAOT Server，独占缓存及外部消费者根为`C:/Users/mysti/.codex/provider-consumers-20261107-01a1b1e2-ci/`。这一结果与上方本地构建feed分列，边界为`CI_CANDIDATE_NOT_PUBLISHED`。

| 核验层 | CI 实际结果 |
| --- | --- |
| 包来源与已加载DLL | PASS：四包来源均为`artifacts/provider-next-phase-20261107/ci-packages`；nuspec source和版本匹配，12份关键回执中实际加载的四DLL SHA逐项匹配CI清单。未用本地feed的DLL摘要替代。 |
| 单SQL、路径/值及旧行右值 | PASS：三协议主业务UPDATE各25参数、无SELECT预读；特殊模型路径和业务载荷均在参数；独立verify各13项检查通过，库存14、旧库存右值得15、旧供应商、null/converter/offset ticks/目标外原文均保持。 |
| 准确拒绝/原子性 | PASS：三协议动态/负下标及converter RHS均NotSupportedException且SQL数0；越界仅一条UPDATE，embedded为InvalidOperationException，REST/Frame为sql_error，均为准确json_set_value数组越界原因，完整JSON和RowVersion未改。 |
| 实际并发影响行数 | PASS：原序embedded为[1,0]、REST为[0,1]、Frame为[1,0]，排序后均[0,1]；两contender各一条UPDATE，持久策略与唯一赢家一致。 |
| 独立进程与Native Server重开 | PASS：总收据中JSON线restore/build、三协议18个业务phase及2个Server重开读回共22步骤均退出0；两代Native Server均ready后运行，第二代REST/Frame的verify-concurrency退出0。 |
| 实际线协议 | PASS：第二代日志第120行起为`HTTP/1.1 POST /v1/db/jsonupdate_rest/sql`，第160行起为`HTTP/2 POST /v1/frame`；总收据wire=PASS。 |

Native Server实际启动程序为`D:/source/SonnetDB/artifacts/provider-next-phase-20261107/ci-server-r2/SonnetDB.exe`，argv为空，第一代PID49952、第二代PID57788、父PID18304。manifest记录`nativeAot=true`、产品版本`5.1.0-rc.20261107.1+eb5c0e5f938372109b473db2194670761dc26a96`、程序SHA256 `363A9795A57EB574334BBEBB7E50F8C309FB8985A1716648490C0E23E192364A`，原始win-x64 ZIP SHA256 `041B4B36A6CE01C87457CF1249B2572FE55CCB7C314CC0D90DCA65DEF5875E1A`。两代均记录after-ACK强制进程树停止与rootExited=true。该NativeAOT证据适用于匹配Server，EF Provider运行时模型的非NativeAOT边界保持。

CI证据入口为`D:/source/SonnetDB/artifacts/provider-next-phase-20261107/ci-candidate-manifest.json`及外部根的`result.json`，运行窗口2026-11-07 02:19:06至02:21:12（Asia/Shanghai）。关键stdout延续上方命名，`json-update-{embedded|rest|frame-http2}-{1-update|2-reject|3-verify|4-compete}.stdout.log`；总收据记录其余phase、重开进程、包来源/恢复与原始制品指纹。共同69步骤均为PASS，仍保留3个按预期exit=-1的迁移强杀步骤。

CI候选消费已PASS；公开NuGet.org-only新包消费、正式发行、生产及实机门禁仍NOT_RUN，#395继续OPEN。本次独立复核仅更新本专题，没有重新运行负载或进行外部写入。

## 证据与剩余门禁

- 共同候选身份及包/Server原始制品：`D:/source/SonnetDB/artifacts/provider-next-phase-20261107/candidate-manifest.json`。四包为SonnetDB.EntityFrameworkCore、SonnetDB（ADO DLL为SonnetDB.Data）、SonnetDB.Core、SonnetDB.Protocol；nuspec均绑定本轮精确source/版本，实际来源为本地candidate feed。
- 本轮完整执行收据：`C:/Users/mysti/.codex/provider-consumers-20261107-01a1b1e2-r2/result.json`，边界`LOCAL_CANDIDATE_NOT_PUBLIC`，运行窗口2026-11-07 01:51:46至01:54:29（Asia/Shanghai）。JSON原始stdout/stderr以`json-update-{embedded|rest|frame-http2}-{0-seed|1-update|2-reject|3-verify|4-compete|5-verify-concurrency}`命名；另有`json-update-rest-server-reopen`和`json-update-frame-http2-server-reopen`，后缀`.stdout.log`/`.stderr.log`。
- 实际协议入口：同一外部根的`server-1.stdout.log`第861行起为JSON REST，第1919行起为HTTP/2 Frame；`server-2.stdout.log`第120行起为JSON REST，第160行起为HTTP/2 Frame。两代Server生命周期和argv保留在总收据，不依赖进程名认领服务。
- 原计划专题证据归属为`D:/source/SonnetDB/artifacts/provider-next-phase-20261107/json-update/`；本轮实际原始执行产物位于上述仓库外根，本文明确指向实物，不伪造已归档目录。重负载全部由主监督排队，持既有FileShare.None锁、CPU2/单MSBuild节点/禁持久服务；本页独立复核只做有界读取/搜索，没有重跑负载。
- 候选本地feed、CI候选和NuGet.org-only公开消费是独立证据层；本专题不把源码审阅、既有单测或本地feed运行提升为公开包验收。
- CI运行42265079795 attempt1已完成，实际CI四包/Native Server消费PASS，详见上节独立证据；NuGet.org-only新包消费、正式发行及实机门禁仍NOT_RUN，不由本地或CI候选结果外推。
- 本轮未发布NuGet、正式Release或tag，未部署/重启/迁移生产。公开新包/匹配Server/适用实机门禁未完成时，#395继续OPEN。
- Provider现有运行时模型仍处于非NativeAOT边界；拒绝形状不添加客户端读改写或隐式重试。旧5.0.0负控的verify-rejected阶段保持可用。
