# SqlSugar Fastest 原生 TEMP/CTAS 消费者

固定 SqlSugar 源码 `65e8226ce25a8e1cb9cca2590683f1fd6778cab3`，原 ORM 核心 DLL 保持相同。补丁只修改该源码 `Src/Asp.NetCore2/SqlSugar.SonnetDBCore` 内的 `SonnetDB/SqlBuilder/SonnetDBFastBuilder.cs`，将现有 Fastest 的普通工作表改为 `CREATE TEMP TABLE ... AS SELECT ... WHERE 1=0`；批量导入、集合更新/插入与调用方事务继续使用现有接口。

在固定 provider 项目根使用 `git apply --check --unidiff-zero sqlsugar-native-temp-ctas.patch` 检查后，再使用相同选项应用。不要把 SonnetDB 仓库的中央包管理与构建配置意外施加到外部固定源码；独占构建目录可以用空的 Directory.Build.props/targets/Packages.props 隔离外部工程，然后仅重编 provider，保留 ORM 核心原 DLL 和原有编译告警。

本目录同样通过三个本地空配置隔离非AOT的外部ORM测试宿主，保持与实际验收环境一致；不表示SqlSugar支持AOT，也不修改任何生产项目的AOT/trim要求。探针需要物理DLL位置记录哈希，不能按生产库的AOT/单文件分析结果解释它。

准备独占运行目录，放入对应 SonnetDB Core/Data/Protocol、SqlSugar、provider、System.IO.Hashing、System.Numerics.Tensors、Newtonsoft.Json 的实际运行 DLL。后者是固定 ORM 的依赖，不进入 SonnetDB Core。使用 `dotnet build FastestProbe.csproj -c Release -p:RuntimeDirectory=<绝对运行目录>` 编译探针。`ISSUE245_CONNECTION` 提供连接字符串，`ISSUE245_ROWS` 为2至10000，默认10000；运行参数为标签与 `original` 或 `candidate`。先以2行有界试跑，再使用10000执行正式规模。输出的 `ROWS` 是实际规模。

候选10个场景覆盖一万行BulkCopy/BulkUpdate、原生空集CTAS、所有者/观察者隔离、同步更新、调用方已有事务的一万行提交/回滚、BulkMerge更新与插入、真实复制前失败后的清理及全部工作表生命周期。Fastest自身会关闭逐SQL日志；探针只在已有 `Begin Bulk` 通知中重新启用日志，以观察真实SQL并触发明确的复制失败，不修改ORM核心、SQL或结果。

远程宿主使用真实产品Server DLL，`FastestConsumerHost.csproj` 的 `ServerRuntimeDirectory` 指向匹配且完整的服务端运行目录。编译后，其参数是包含 `run-original/FastestProbe.dll` 和 `run-candidate/FastestProbe.dll` 的独占目录；各子目录包含探针及对应运行依赖。宿主建立实际HTTP/1与HTTP/2端口，运行原版/候选并保留请求协议、路径与Frame opcode。Frame模式中的事务、写入和metadata仍可能走HTTP/2 REST；这不宣称全部操作都为二进制Frame。

2026-10-19本机候选三协议各10/10；原版三协议各2项普通批量操作通过、2项原生TEMP/隔离合同失败。固定包发行、上游补丁提交、最终整合SHA的CI另行验收，详见 `docs/audits/issue245-fastest-consumer-20261019.md`。
