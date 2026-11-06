# GetAndDelete 独立 SDK 包消费者（GH-Issue #397）

本应用只有准确版本的`SonnetDB.Client` PackageReference，Protocol通过包依赖进入；不使用ProjectReference、Core、Data、引擎引用或REST管理入口。将csproj和Program.cs复制到仓库外的新目录，以`SdkVersion`、独立NuGet.Config、packages/http-cache及预期两包feed执行restore/build。主监督核对实际包来源、nuspec source/版本及加载DLL指纹，区分本地候选、CI候选和NuGet.org-only公开包。

主监督须在同源匹配Server上预建数据库`issue397_sdk`，并独占Server生命周期。`get_and_delete` keyspace沿用Frame Put的自动创建合同，无须预建。连接由进程环境变量`SONNETDB_FRAME_ENDPOINT`及可选`SONNETDB_FRAME_TOKEN`传入。endpoint必须是无嵌入凭据的HTTP/HTTPS基地址，token不会写入输出；使用隔离测试Server及新证据目录，不连接生产。实际HTTP/2和`/v1/frame`需由外层请求/Server日志独立确认。

每阶段一个独立进程：

```text
dotnet Issue397.KvApp.dll seed <绝对证据目录>
dotnet Issue397.KvApp.dll exchange <同一绝对证据目录>
# 主监督回收并重开同一数据库目录的匹配Server，等待真实ready。
dotnet Issue397.KvApp.dll verify-deleted <同一绝对证据目录>
```

`seed`先用单页Scan要求固定二进制前缀为空，再写main、empty、race及survivor四条，逐条用Get核二进制值、版本、过期标记和完整UTC ticks。`exchange`原子取得并删除main/empty，区分空旧值和缺失键的两个null字段；两个独立client/工作线程经10秒Barrier同时竞争race，必须恰有一个旧记录及删除变更版本，另一个没有变更。随后Get确认三个目标键消失，单页Scan只能保留survivor，survivor的Get证明服务继续可用。`verify-deleted`在Server重开后以新进程执行同一删除状态与survivor核验。

每阶段90秒总取消预算，单次请求20秒，固定四条种子、三个删除目标、两个竞争者及最多8条的单页Scan；所有输出和检查循环均有固定上限。应用不创建服务、不自动重试任何写入或删除。每阶段手写JSON输出至stdout及证据目录中的`seed.json`、`exchange.json`或`verify-deleted.json`，包含旧值/旧版本/删除版本、UTC ticks、竞争结果和实际加载Client/Protocol程序集的路径/版本/SHA。采用CreateNew保存回执，发现已有阶段回执会在网络操作前拒绝；失败或未知写入必须由监督者先对账，不能盲目重放阶段。

外层还须限制墙钟时间、记录PID/启动时间/argv/父链，并回收仅归属本次任务的完整子树。静态模型/AOT、正式公开包、TLS/ALPN、其它平台和现场验收按各自结果单列；本消费者源码存在不代表这些门禁已通过。
