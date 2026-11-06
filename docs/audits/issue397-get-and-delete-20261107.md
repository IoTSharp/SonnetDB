# #397：轻量 .NET Client 的 KV GetAndDelete 切片

2026-11-07（Asia/Shanghai）。独立工作树 `sdk-kv-get-and-delete-20261107/SonnetDB`，分支 `codex/sdk-kv-get-and-delete-20261107`，起点 `eb5c0e5f938372109b473db2194670761dc26a96`，当前已快进整合至基线 `f09b4375c1010e7f7699b2bc62ee90e769fc7d6c`。源码切片已提交为S1=`336f9aa84d32951f2fbaf90bc41caedf9b313431`，以下本地候选消费绑定该精确source。本批只补轻量 Client 的一个既有 KV 操作，不并入 Provider 候选。

冻结 API 为 `Task<KvDataFrameExchangeResult> GetAndDeleteKvAsync(db, keyspace, ReadOnlyMemory<byte> key, uint streamId=1, CancellationToken cancellationToken=default)`；结果由引擎无关的 `PreviousValue` 与 `MutationVersion` 组成，旧值带值字节、旧版本及完整 UTC ticks。缺失记录两项都为空；存在空二进制值时 PreviousValue 非空、Value 长度0。

产品 Server 已有 op6/GetAndDelete，直接调用持久 keyspace 的 GetAndDelete 并使用既有交换响应布局。本切片不改变 Server、存储或协议字节。codec与Client由另外两位worker负责，主监督集成、统一验证和精确源码/包身份。

## 真实产品 Server 验收入口

`tests/SonnetDB.Tests/KvGetAndDeleteFrameClientTests.cs` 复用 TestServerHost 启动真正的 Program/Kestrel，已有测试工程已引用 SonnetDB.Client，无新增依赖。每个测试拥有独立目录和认证 token；中间件只观察/复位 Frame 请求头，随后交给真实产品端点，不生成伪响应。

| 合同 | 对应真实检查 | 本轮执行状态 |
| --- | --- | --- |
| 精确旧记录与删除版本 | 二进制键/值、旧版本、UTC offset=0及完整ticks；MutationVersion=旧版+1，自定义streamId=37。 | PASS：最终Server复跑7/7。 |
| 缺失与空值 | 空值有PreviousValue；重复删除缺失返回两项null，不消耗版本；删除后Get/前缀Scan均缺失。 | PASS：最终Server复跑7/7。 |
| 两个独立客户端并发 | 两个独立HttpClient通过一次开始信号提交，观察中间件须等待两条实际op6都抵达后才放行真实产品next，恰好一方返回完整旧值，另一方无MutationVersion。 | PASS：最终Server复跑7/7。 |
| 提交后响应预算不足 | 大旧值超过小客户端响应预算，要求frame_budget与OutcomeUnknown；删除前只一条实际请求，独立客户端Get/Scan对账已删，不自动重放。 | PASS：最终Server复跑7/7。 |
| 持久重开 | 同一数据目录真实停启两次后键仍缺失；预算不足用例也停启后独立核对。 | PASS：最终Server复跑7/7。 |
| 权限/预取消 | 只读与未认证拒绝删除且原值/版本不变；预取消不发送请求。 | PASS：readonly、匿名和预取消最终均通过；匿名首失败另保留。 |
| 真实h2c与协议身份 | 观察全部请求为HTTP/2、/v1/frame、application/x-sonnetdb-frame；记录service/op/stream到测试输出。 | PASS：最终7项均核实际h2c业务请求，匿名观察边界单列。 |

主监督真实Server首轮为 **6 PASS / 1 FAIL / 0 SKIP**，原始 `D:/source/SonnetDB/artifacts/issue397-get-and-delete-20261107/test-server/stdout.log` 与 `server.trx` 保留。失败是匿名权限用例的夹具观察位置：已经收到准确的 `http_error`，但错误要求后认证观察队列恰有一条，实际为空，后续原记录核对尚未执行，不能记为该用例PASS。

源码顺序已核实：Program.BuildApp先注册SonnetDbRequestPipeline.Configure，后由测试追加观察中间件；产品Bearer认证对匿名请求返回HTTP401并return，因此匿名不会抵达该观察点。夹具已按确切合同分支：匿名必须 `http_error` 且消息含HTTP401、观察队列严格0条；有效readonly身份必须 `forbidden` 且恰好一条真实op6。两者随后均保留独立admin读取原value=42及原版本不变的断言。没有删除拒绝/数据断言、接受任意错误或改变产品认证；修订后的最终树已独立复跑 **7 PASS / 0 FAIL / 0 SKIP**；原首轮6/7及其失败收据继续保留。

最终复跑收据为 `D:/source/SonnetDB/artifacts/issue397-get-and-delete-20261107/test-server-r2/{result.json,stdout.log,server.trx}`。Worker C独立读取result、stdout及TRX关键结果；TRX计数total/executed/passed均为7，failed/error/timeout/aborted/notExecuted均为0，七个UnitTestResult全部Passed。进程PID47524、parentPID53592，创建时间2026-11-06T19:18:59.9315959Z；实际命令退出0，耗时18.474秒，rootExited=true。完整所属树回收仍按主监督外层原始收据判断。

关键成功证据如下，均来自原始server.trx而非只引用摘要：

- 两条真实op6分别stream10/11；第76～78行记录到达count=1、count=2，随后明确requests=2、previous-record winners=1、missing-record results=1。该用例还保持精确旧值/版本、只一次删除版本、Get/Scan缺失断言。
- 响应预算用例在TRX第35～49行Passed：实际请求序列为Put→单次GetAndDelete→独立Get/Scan→版本对账Put；原始代码中的frame_budget、OutcomeUnknown、独立读回已删断言全部执行通过。之后同数据目录再次启动并核Get/Scan与缺失GetAndDelete。
- 两次重开用例在第16～34行Passed：相同目录后缀f7291b2d5735486ead3b6e07b238466f，Server地址54431→54435→54437，三次启动及两轮重开读回均在stdout中可核，完整UTC ticks/旧版本/删除版本断言通过。
- readonly用例第50～59行Passed，日志明确一次真实op6到产品端点并返回forbidden，随后admin Get；匿名用例第60～68行Passed，日志明确HTTP401、后认证观察点0条，随后admin Get。两者的原value42与原版本保留断言通过，未将匿名0条泛化为所有权限拒绝的观察行为。

主监督另记录同工作包Core新codec44/44、完整Client272/272通过；这些独立套件不累加成Server测试数。本地新包PackageReference消费者的独立验收见后文；公开包、TLS/ALPN与AOT门禁保持独立。

主监督持共享重负载锁后实际执行：

```text
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false --filter FullyQualifiedName~KvGetAndDeleteFrameClientTests --logger "trx;LogFileName=server.trx" --results-directory D:/source/SonnetDB/artifacts/issue397-get-and-delete-20261107/test-server-r2
```

每个业务测试有30或45秒取消预算，客户端10秒总期限，宿主每次启停10秒；并发固定两个请求，重开次数显式两次，夹具请求最多64个。不创建轮询或无限重试；清理先关闭所属Server，再核对独占临时目录父路径和名称后删除。外层还须记录测试进程身份、墙钟期限和所属完整子树回收。

## 独立PackageReference消费者与S1候选

源码切片已提交为 **S1=`336f9aa84d32951f2fbaf90bc41caedf9b313431`**。隔离SDK版本为 `0.0.0-issue397.local.20261107.1`，状态 `LOCAL_CANDIDATE_NOT_PUBLIC`；本段绑定S1原始制品，后续审计文档提交不将同一版本的包身份改为其他source。

共同manifest为 `D:/source/SonnetDB/artifacts/issue397-get-and-delete-20261107/candidate-manifest.json`，构建前gitHead=S1、dirtyCount=0，source/版本与两包repository commit一致。Worker C重新核manifest SHA256为 `5deb0a0618d99d3863e56b47b8461e448feb57dd463b03388a4953e4ef92bf9f`，与消费总回执引用一致。

仓库外消费者根为 `C:/Users/mysti/.codex/issue397-consumer-20261107-local-1/`。其result.json记录restore/build/seed/exchange/verify-deleted五步均PASS、各退出0，业务三进程PID59672/28228/52388；Worker C逐读三份evidence phase JSON及stdout，分别为23/35/10项业务检查，均status=PASS。固定db为issue397_sdk，keyspace为get_and_delete。

恢复图只有下列两个type=package依赖，无ProjectReference、Core、Data或Native依赖；唯一packages根为消费者自己的packages，使用独立NuGet.Config。两份.nupkg.metadata实际source均为本地 `D:/source/SonnetDB/artifacts/issue397-get-and-delete-20261107/feed`。三份phase JSON的实际loaded程序集指纹均与下表及manifest一致；AssemblyVersion均为0.0.0.0，准确NuGet版本/source由恢复图与制品清单确定。

| S1包 | 原始nupkg SHA256 | 实际loaded DLL SHA256 |
| --- | --- | --- |
| SonnetDB.Protocol | `bb79e370a774935681f34128ab3da80eb34e997d61fb7ad8420ce9e41cc7b102` | `d01892eea988ae1fb4fd172bf8128769cf8ccd4ed34cdd05062cdf8e206f58a2` |
| SonnetDB.Client | `cec5f9e8ed066a4f61569f92b9e8a3c0d195c1fc56c37a8e88f8e189ac9eaa03` | `71cdf321ef6109b5fe1b77a1cb0f3663b6ae907bf556dbceb7eaceef34ec2616` |

phase原始路径为 `evidence/seed.json`、`evidence/exchange.json`、`evidence/verify-deleted.json`，对应stdout亦独立保留。exchange实际回执包括：主记录旧值00017f80ff/旧版1/完整UTC ticks642135781237654300，删除变更版5；空旧值是非null记录、旧版2、变更版6；缺失记录旧值/变更版均null；两个独立Client竞争结果[1,0]，唯一旧记录旧版3/字节ff003937/ticks完整，变更版7，输方变更版null。种子包含四个key，因此变更版本按keyspace递增。三个已删key经Get均缺失，单页Scan只保留survivor，并核survivor值和ticks。包应用没有替代前述Server单测的响应预算未知结果用例；两层证据分别保留范围。

匹配Server是同S1的win-x64 framework-dependent JIT，原始归档为 `D:/source/SonnetDB/artifacts/issue397-get-and-delete-20261107/server-win-x64-0.0.0-issue397.local.20261107.1.zip`，72,384,324字节，manifest记录SHA256 `2a006168fff211759dbb830f887f1408f1849d35254e451ab288fb368030a48e`。Server主DLL摘要为 `6aa2d32be3ef650dddcafb3da0eeed9bc1ed5f4bee3c9ebd76278ff91e7a5a7f`，其Protocol DLL与上表一致；原归档与摘要保留，不将源码测试输出目录冒充该匹配Server。

两代Server均实际运行上述目录的SonnetDB.dll，PID60240→48864，共用消费者根下server-data；parentPID60188及其上级重负载执行器身份记在总回执。两代readiness PASS，均按回执强制停止所属树、rootExited=true；清理范围记录为owned roots plus outer Cordonix Job descendants。第二代启动后再以独立进程verify-deleted，确认删除持久化且survivor仍正确，属于受控进程强停/重开证据，不是物理掉电验收。

真实业务协议已独立读线日志：第一代server-1.stdout.log第24/27行为19:34:12.422Z开始HTTP/2 POST /v1/frame、application/x-sonnetdb-frame，并以200结束；第二代server-2.stdout.log第18/21行为19:34:14.527Z开始同一协议/端点，再以200结束。时间为2026-11-06 UTC，对应本地2026-11-07。未将连接串或app自述当作协议证明。

结论：**S1本地两包、独占缓存、三业务进程及匹配JIT Server的真实h2c消费PASS**。PR #415精确source CI由主监督跟踪，本专题未提前记录成功；正式NuGet、TLS/ALPN、NativeAOT和其他模型范围仍独立。

## TLS/ALPN、包与关闭边界

精确S1的[PR #415](https://github.com/IoTSharp/SonnetDB/pull/415)已独立读回12 SUCCESS、1项按PR条件SKIPPED，当前无未解决review线程。CI42280330037的27份原始TRX已下载解析：Ubuntu/Windows Core各8175、Client各272、EF各438、Server各1610，另行迁移强杀各10，均0失败/未执行；Windows Studio90/90通过。三RID NativeAOT检查覆盖Server/CLI及图像codec smoke，不把它写成typed Client的NativeAOT包消费者。完整机器身份、包/DLL/原始Server归档、消费步骤和CI收据见[机器清单](issue397-get-and-delete-20261107.json)。本地及最终提交前完整restore、规定Format、diff检查分别保留原始结果；后续纯文档提交不改变冻结S1或候选字节。

对现有 Server/Client 测试目录的有界搜索未找到可信TLS/ALPN夹具。Client可注入HttpClient的构造函数是internal，当前friend只有SonnetDB.Client.Tests；本真实Server测试项目无法直接配置专用CustomRootTrust。此批不增加友元/项目依赖、不修改机器信任或全局关闭证书校验，TLS/ALPN明确 **NOT_RUN**。需要后续由主监督冻结可信证书与客户端传输配置入口后独立验证。

上述精确本地候选PackageReference消费为 **PASS**；PR #415精确source CI运行中、结果尚未在本专题确认。公开包、NativeAOT、其他模型/语言/平台、固定硬件与生产均 **NOT_RUN**。本文件不把源码单测、h2c或一个KV操作视为M49/#397整体完成。最终版本、命令退出码、TRX、CI及包消费回执由主监督按最终source回填；#397关闭仍需其完整接受条件满足。

本worker只拥有上述新Server测试文件与本专题，不修改公共产品代码、共享文档或Provider树；没有运行build/test/restore/format/pack/Server，没有提交推送或对外消息。
