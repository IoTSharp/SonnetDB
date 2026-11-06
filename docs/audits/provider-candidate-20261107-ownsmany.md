# #394 共同 Provider 候选：实际包应用的集合生命周期

2026-11-07（Asia/Shanghai）。**隔离本地与CI候选的 OwnsMany 应用验收 PASS；#394 继续 OPEN，公开新包验收尚未完成。** 本轮版本为 `5.1.0-rc.20261107.1`，最终 source 为 `eb5c0e5f938372109b473db2194670761dc26a96`。结论绑定下列实际制品及消费者，不外推到旧候选、公开 NuGet、生产或 EF NativeAOT。

主监督统一构建、重负载队列、候选清单与 Server 生命周期。Worker C 独立读取总回执、11份业务 stdout、恢复依赖图、四包来源元数据及两代 Server 日志后回填本专题。应用执行运单装箱批次/零件的 EF SaveChanges 跟踪生命周期，未用 ExecuteUpdate 替代。

## 精确候选与包来源

- 候选清单：`D:/source/SonnetDB/artifacts/provider-next-phase-20261107/candidate-manifest.json`，status=PASS，source/版本如上，构建前 dirtyCount=0。
- 仓库外消费者：`C:/Users/mysti/.codex/provider-consumers-20261107-01a1b1e2-r2/owns-many/`；总回执为父目录 `result.json`，边界明确为 `LOCAL_CANDIDATE_NOT_PUBLIC`。
- Provider/ADO/Core/Protocol四包均为上述版本，repository commit均为上述source。独立NuGet.Config、packages与恢复图已核；四个 `.nupkg.metadata` 的source均为 `D:/source/SonnetDB/artifacts/provider-next-phase-20261107/feed`。
- `obj/project.assets.json` 中四项SonnetDB library均为type=package，projectReferences为空，唯一packageFolder是该消费者自己的packages。恢复源还含SDK library-packs和NuGet.org，不能写成NuGet.org-only。
- 复制输入的csproj SHA256为 `30f0eac48e3299f4a40a8c2677fae5799bd039463d95145ce4e1772dadb93494`，Program.cs为 `969e93fcecd85661d9598b4d7ec9dba7efb2e2544e476dcbebad75194c1b26e4`。
- Server为同source的win-x64 framework-dependent JIT，原始ZIP SHA256为 `69fe3cc886ff9df23b92cf4a5a8c0f798ba77d206259181ec8619fb995ebef12`。CI/NativeAOT制品独立记账。

11份业务回执中四个**实际已加载**DLL的指纹均匹配候选包。程序集自身版本报告0.0.0.0，因此准确NuGet身份由恢复图、nuspec/source和指纹共同确定。

| 程序集 | SHA256 |
| --- | --- |
| SonnetDB.Protocol.dll | `57404c753cb96a1dd71d250977d831e159a9df53086f8f559cdc8f069234e2cf` |
| SonnetDB.Core.dll | `4a3a5bd871ec39796f0ea6adbf88d8eb1899607946446ed912060db4e0b2a914` |
| SonnetDB.Data.dll | `b1e93fce3cee0ec7ea317e5d3e086a11e26b63da5ef56f1cd1d2a179d45213bb` |
| SonnetDB.EntityFrameworkCore.dll | `15037c532bc2d0f5319df07adb3834999e1e7cc8d92b1b0756b01efe2e761c16` |

## 实际业务与支持边界

| 业务合同 | 本轮实际检查与结果 |
| --- | --- |
| 插入、读取与元素编码 | PASS：六个owner、重复标签的不同元素、可空值、Guid；converter模型读值和大写存储值分别核对。三个seed各12项断言。 |
| 根与嵌套OwnsMany | PASS：嵌套零件、多字段批次、父数组删除/追加、清空/从空追加，根数组元素修改/追加，均用SaveChanges实际执行。 |
| null、空、缺失与SQL NULL | PASS：四行分别核JSON表示及EF物化，不折叠为同一种集合。 |
| 父ordinal保存后的下一次修改 | PASS：删除首元素并追加父项后顺序已持久；同跟踪器立即清空仍identifying-key拒绝，零下一条SQL、已提交JSON原文不变。此PASS证明拒绝边界。 |
| Clear/重载后的准确ordinal | PASS：保存第一父项零件数量7后立即读原始JSON，核 `batches.'[root] 😃[0].Parts[0].Quantity=7`，同时第二父项数量仍为1，再清空集合。三个mutate均有该断言。 |
| 事务隔离与回滚 | PASS：外部连接看不到事务内嵌套修改，回滚恢复完整持久JSON原文。 |
| 显式乐观并发 | PASS：胜出者更新嵌套值与Stamp；stale SaveChanges并发异常，实际零行。三个mutate各11条完成UPDATE，最后一条均含Stamp谓词且Affected=0。 |
| 目标外JSON与重开 | PASS：大整数及1.2300e+04原文、最终数量8和Stamp=accepted均在独立进程及Server重开后核对。 |

EF10父ordinal限制继续保留：父数组结构变化后，应用须显式Clear/重新查询再继续操作。该旅程不证明任意重排、跨owner转移或无需重载的连续跟踪受支持；没有修改EF内部身份快照或自动重试。#395批量更新的PASS不代替本表跟踪验收。

## 三协议、独立进程与Server重开

原始stdout/stderr位于上述消费者父目录。总回执逐步记录PID、创建时间、父PID、命令、起止时间、退出码与日志路径。#394有13项执行步骤：restore/build两项，以及下列11项业务进程；总矩阵69项还包含另两应用，不写成69项OwnsMany测试。

| 协议/阶段 | 回执前缀 | 结果与独立进程 |
| --- | --- | --- |
| embedded seed/mutate/verify | owns-many-embedded-0-seed / 1-mutate / 2-verify | PASS，PID5756 / 42664 / 27572。 |
| REST seed/mutate/verify | owns-many-rest-0-seed / 1-mutate / 2-verify | PASS，PID21964 / 51656 / 1880。 |
| 原生Frame seed/mutate/verify | owns-many-frame-http2-0-seed / 1-mutate / 2-verify | PASS，PID48940 / 43784 / 51548。 |
| REST Server重开verify | owns-many-rest-server-reopen | PASS，PID49492。 |
| Frame Server重开verify | owns-many-frame-http2-server-reopen | PASS，PID9088。 |

每协议seed/mutate/verify分别为12/24/15项业务断言；两个额外Server重开verify各15项，重复运行不累加为新的单元测试数。全部11份业务JSON均status=PASS，restore/build退出0。

实际协议证据：`server-1.stdout.log:1053` 在REST mutate窗口内的17:53:29.888Z记录HTTP/1.1 POST `/v1/db/ownsmany_rest/sql`，ContentType为JSON；`server-1.stdout.log:2929` 在Frame mutate窗口内的17:54:04.105Z记录HTTP/2 POST `/v1/frame`、`application/x-sonnetdb-frame`。上述UTC时间均为2026-11-06，对应本地2026-11-07，未以连接串代替协议验收。

总回执记录第一代Server PID2640在ACK后强制停止所属树；第二代PID14680从同一候选与数据目录重开，两代readiness PASS、最终rootExited=true。第二代日志26～31行实际预热ownsmany_rest及ownsmany_framehttp2的两张业务表。两个后续verify均PASS；Frame重开verify时间窗内 `server-2.stdout.log:240`（17:54:29.436Z）再次记录真实HTTP/2 Frame。此证据覆盖受控进程强制停止后重开；物理掉电、现场及完整子树回收分别按对应原始收据判断。

## CI四包与NativeAOT Server复验

本段独立于前述本机JIT候选。CI清单为 `D:/source/SonnetDB/artifacts/provider-next-phase-20261107/ci-candidate-manifest.json`，绑定同一source与版本，边界为 `CI_CANDIDATE_NOT_PUBLISHED`；[Publish run42265079795](https://github.com/IoTSharp/SonnetDB/actions/runs/42265079795) attempt1在清单中记录completed/success、workflow_dispatch。该成功不代表正式NuGet发布。

实际消费者在 `C:/Users/mysti/.codex/provider-consumers-20261107-01a1b1e2-ci/owns-many/`，父目录result.json完整69步均PASS，其中#394仍是13步：restore/build及三协议11个业务进程。Worker C本次逐读三个mutate和两个Native Server重开verify共5份关键stdout；三个mutate各24项断言、11条完成UPDATE，准确父ordinal数量7及邻项数量1、identifying-key拒绝后零下一SQL/原文不变、Clear重载后继续修改均有PASS标记，最后一条更新均Affected=0。两个重开verify各15项断言PASS。

CI版csproj/Program.cs输入指纹与本机消费者相同，source不变。独占packages恢复图的四项SonnetDB依赖均为type=package、projectReferences为空；四份.nupkg.metadata实际source均为 `D:/source/SonnetDB/artifacts/provider-next-phase-20261107/ci-packages`。所读5份stdout的实际loaded程序集指纹全部匹配CI清单，而不是沿用本机构建的DLL摘要：

| CI实际加载程序集 | SHA256 |
| --- | --- |
| SonnetDB.Protocol.dll | `619ba549afc977e8f0cd6c8a9b94bec7f69f13c1e9f8f00d6ae9456b6fd25f58` |
| SonnetDB.Core.dll | `bd26ac8e18cf5b1978f1e197c240d08b9e21a4f634ca0dc1698ed3621edb7024` |
| SonnetDB.Data.dll | `8ae0871cb80e3b06108119eb21e644cbe1b9a589ca217f1f3d7221d17eaf8a6d` |
| SonnetDB.EntityFrameworkCore.dll | `a92697c9086917e0c5c9f0a55c646db96343f8425f0313ff1535e09d408a543d` |

服务实际启动的是 `D:/source/SonnetDB/artifacts/provider-next-phase-20261107/ci-server-r2/SonnetDB.exe`，命令不是dotnet DLL；清单记录win-x64、nativeAot=true、ProductVersion=`5.1.0-rc.20261107.1+eb5c0e5f938372109b473db2194670761dc26a96`。本worker重新流式核验实际EXE的91,418,112字节，SHA256为 `363a9795a57eb574334bbebb7e50f8c309fb8985a1716648490c0e23e192364a`，与CI清单一致；原始完整ZIP摘要为 `041b4b36a6ce01c87457cf1249b2572fe55ccb7c314cc0d90dca65def5875e1a`。

第一代Native Server PID49952和第二代PID57788都由parentPID18304启动，readiness PASS、最终rootExited=true。两代间有ACK后的所属树强停；第二代对同一数据重开后，REST verify PID27564、Frame verify PID58812均PASS。总回执用创建时间区分PID复用：embedded mutate的PID44556创建于18:19:51.880Z，REST mutate的同号PID创建于18:20:25.084Z，不能按PID数值认作同一进程。

CI实际请求独立核到：`server-1.stdout.log:1053` 在REST mutate窗口18:20:26.121Z记录ownsmany_rest的HTTP/1.1 JSON SQL；第2929及2937行在Frame mutate窗口18:20:52.983Z/18:20:53.216Z记录HTTP/2 POST `/v1/frame`、`application/x-sonnetdb-frame`；`server-2.stdout.log:240` 在重开verify窗口18:21:12.530Z再次记录原生Frame。上述均为2026-11-06 UTC，对应本地2026-11-07。

结论为**CI四包+实际NativeAOT Server的受支持应用旅程PASS**。EF消费者本身仍是托管应用，EF NativeAOT应用、正式NuGet.org-only消费、实机及物理掉电均NOT_RUN。公开关闭条件与EF10支持限制保持如下。

## 完整接受条件与关闭判断

本轮只读核对[#394正文](https://github.com/IoTSharp/SonnetDB/issues/394)及全部3条评论，当前state仍OPEN。正文最后一项明确要求“使用隔离公开NuGet消费者及应用模型复验，明确实际支持最低版本”。最新[关闭条件评论6690530270](https://github.com/IoTSharp/SonnetDB/issues/394#issuecomment-6690530270)要求最终新公开Provider、匹配Server与独占空缓存。本轮两种实际来源分别为本地feed与CI候选feed，尚未完成公开验收。

**#394当前不可关闭。** 本地与CI候选已完成实际应用模型、受限生命周期、事务/并发、三协议、独立进程及Server重开。正式发行获准后，还须公开新Provider/匹配依赖及Server，以NuGet.org-only独占空缓存复验并记录准确最低公开版本，在正文/最终回复保留EF10 Clear/重载边界。若要声明无需重载的连续父重排，则另须解决并验证该限制，不能删除当前拒绝断言换绿。

关联[#266](https://github.com/IoTSharp/SonnetDB/issues/266)实时为CLOSED，其单对象JSON/ABP交付不替代#394；关联[#230](https://github.com/IoTSharp/SonnetDB/issues/230)实时为OPEN，上游FreeSql发行仍由其自身门禁判断。本worker没有外部回复或关闭issue。

| 独立门禁 | 状态 |
| --- | --- |
| 本文件精确本地候选与JIT Server的实际包消费 | PASS。 |
| CI四包与实际NativeAOT Server的托管EF消费者 | PASS，独立来源/指纹及实际旅程见CI段；EF应用NativeAOT不在此范围。 |
| 新公开NuGet-only消费及最低公开版本 | NOT_RUN。 |
| 实机/生产、物理掉电、EF NativeAOT应用 | NOT_RUN。 |

命令仍是 `dotnet Issue394.OwnsManyApp.dll seed|mutate|verify <absolute-database-directory>`；远程用进程级SONNETDB_APP_CONNECTION，每phase120秒取消预算，外层有墙钟与资源归属边界。历史合同及原始拒绝见[#394源码审计](issue-394-20261105.md)。本worker仅更新本专题，未改app/公共代码/共享文档、未运行重负载、未提交推送。正式发行继续遵守延期与授权决定。
