# #271：公开包 BLOB 参数与实际 ABP Passkey 验收

日期：2026-10-16（Asia/Shanghai）。原 Issue 为 IoTSharp/SonnetDB #271，线上快照为 open、无评论，更新时间 2026-10-11T02:12:11Z。本次只补真实消费者证据及可复现工具，没有重写已存在的生产实现。Issue 的回复、关闭、提交和批次推送由监督主任务完成；本文不预写线上操作成功。

## 结论与固定来源

公开 **4.5.0-preview.ef266.5** 已通过原四进程复现、SQLite 对照、嵌入式扩展 BLOB 合同、独立进程重开，以及真实 ABP Passkey Store 与账户 HTTP 旅程。公开客户端连接独立 Server 的 REST/h2c 同合同及进程终止重开也通过。`.5` 是本次实证固定基线，不推断更早版本的最低修复点。

| 层 | 本次使用的来源 |
| --- | --- |
| SDK / runtime | 实际 SDK 10.0.400，目标 net10.0；与原 Issue 的 SDK 10.0.401 不混写 |
| Core / ADO / EF 客户端 | NuGet.org，全部 `4.5.0-preview.ef266.5`，nuspec commit `1af23d8f299febfdf66b040ab3419980396acf46` |
| SQLite / EF Core | 10.0.12 |
| 真实 Server | 冻结源码构建 `285186b868a59d4b22e47279ea51deb38a3a1f45`，541 个文件逐项 SHA256 复制核对；不是公开同版本 Server 包验收 |
| 实际 ABP 应用 | IoTSharp/SonnetDB.Abp 固定 `841567fd963621a8656a904121462234c6c80c0e`，ABP 10.7.0，固定公开 SonnetDB `.5` |

| nupkg | SHA256 |
| --- | --- |
| SonnetDB.Core | `10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268` |
| SonnetDB | `0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4` |
| SonnetDB.EntityFrameworkCore | `9688BA682E160639681AF3E4882DE6A58F70CF289B42552987FA7CD85D87E125` |

上述 SHA 与 #273 公开包审计相同。包 metadata.source、nuspec commit、私有恢复目录、assets 与实际加载的三份 DLL 均另有收据；NuGet contentHash 与原始文件 SHA 是不同字段，不宣称等同。没有跨仓库引擎 ProjectReference、DLL 覆盖、fallback 或全局缓存修改。未进行本轮漏洞审计或 NuGet 签名认证复验。

## 原复现与嵌入式扩展

`scripts/validation/issue271-public-package/Original/Program.cs` 来自线上正文，内容保持；只将项目包版本固定到上述公开基线。四个独立进程分别执行 SQLite/SonnetDB 的 binary-key、passkey-any，全部 exit 0，均记录 CLEANED。两个 Release consumer build 为 0 warning / 0 error。最终 assets 中只存在 NuGet.org 和全新私有 packages 目录。

| 实际执行 | 结果 |
| --- | --- |
| SQLite 原 binary-key / passkey-any | 2/2 通过 |
| SonnetDB 原 binary-key / passkey-any | 2/2 通过 |
| SQLite 扩展 exercise | exit 0，119 条断言 |
| SQLite 独立进程 reopen | exit 0，38 条断言 |
| SonnetDB 扩展 exercise | exit 0，124 条断言 |
| SonnetDB 独立进程 reopen | exit 0，38 条断言 |

断言数量不是独立测试数量；前期 C 盘同范围执行与最终 D 盘执行有重叠，不累加。

- 三个用户、六个初始凭据，多个凭据归同一用户；同时检查目标、第二凭据、另一用户、无凭据用户、不存在值、短/长前缀和高位字节差异。
- `CredentialId == lookup` 与 `Users.Where(u => u.Passkeys.Any(p => p.CredentialId.SequenceEqual(lookup)))` 均实际执行。拦截器检查每次只有一个参数化 reader 命令，相关查询含 EXISTS，实际参数为 `DbType.Binary` 且 byte[] 全内容一致；不是仅凭 ToQueryString 的文本推断类型。
- 空 BLOB、`0x00`、高位字节及声明的 1024 字节凭据均正确；可空 payload 的 EF null 比较为 null 语义，显式 ADO Binary/DBNull 的 `= @value` 不命中，NULL 主键插入被拒且不增行。
- BLOB 次级索引普通相等和相关 Any 均正确；`EXPLAIN ANALYZE` 实证主键/次级索引路径且 actual_examined_rows=1、actual_rows=1。BLOB 父主键/子外键相同内容不同数组可关联，不存在父键被拒且不增孤儿。
- 无次级索引的独立 BLOB 主键接受 65,530 / 65,531 字节，65,532 字节被拒且保留两条合法边界行。默认 65,536 字节物理 key 上限包括 1 字节行前缀和 4 字节 BLOB 长度；不同 key/index 形状有不同额外开销。SQLite 接受 65,532 字节，作为实现边界对照，不强令其失败。该边界不约束普通 BLOB payload；65,536 字节 payload 已更新、比较并完整重读。
- 事务内更新/删除/插入及 read-own-write 后回滚，逐项验证恢复；提交删除空 key 后，独立新进程确认 3 用户/5 凭据、完整更新 payload、回滚前内容、已提交删除、BLOB FK 与最大 key 保留。

## 真实远程与进程重开

公共客户端与 Server 在不同 dotnet 进程，通过真实环回 Kestrel 请求通信。每个协议 exercise 完成后终止自己启动的 Server，再启动新 Server 进程执行 reopen；并非仅重建 DbContext。最终数据库在短路径 `D:/sndb-t/i271-final/remote/data/`，临时认证令牌只保存在进程环境中。

| 协议 | exercise / reopen | 实际数据请求证据 |
| --- | --- | --- |
| REST | 124 / 38 条断言，均 exit 0 | 85 / 18 次数据请求均 HTTP/1.1 |
| Frame HTTP/2（h2c） | 124 / 38 条断言，均 exit 0 | 85 / 18 次数据请求均 HTTP/2，其中 `/v1/frame` 46 / 17 次 |

Frame 路径仍有 DDL/事务管理的 JSON SQL 请求，不能把 Protocol 选项解释为所有请求都使用 Frame。协议依据实际 Kestrel日志，覆盖 `/v1/db/...` 及 `/v1/frame`；重启后数据、BLOB 类型、事务回滚结果与完整内容沿用同一扩展矩阵。不是正式 Server 发布包、跨机网络、TLS、任意崩溃点或远程 ABP 旅程的证据。

## 真实 ABP 应用

独立 clone 固定提交，以全新专属 NuGet packages/http-cache 恢复。SonnetDB.Abp 内部 ProjectReference 保留真实集成层/官方 BookStore 项目，引用的 Core/ADO/EF 引擎全部仍是公开包。唯一未跟踪附加文件是阻断 SDK/父目录额外 restore 源的 `Directory.Build.targets`；最终 `git -c core.longpaths=true diff --exit-code <fixed SHA> --` 通过，业务、模块、迁移、授权与测试源码没有修改。Release build 0 warning / 0 error。

| 原有测试 | 本次结果与范围 |
| --- | --- |
| `BookStoreSonnetPasskeyTests.PasskeyStore_WithPublicSonnetDB_PreservesValuesAcrossApplicationReconstruction` | 1/1 通过，0 skip。标准迁移、实际 `IUserStore` / `IUserPasskeyStore`，五次应用初始化/关闭；新增用户与 Passkey、FindByPasskeyId、FindPasskey、集合读取、更新、删除及再次缺失；完整二进制/数组/Unicode/DateTimeOffset/uint.MaxValue JSON Data 都断言 |
| `BookStoreSonnetHttpJourneyTests.SonnetDB_OfficialApplication_MigratesLogsInPerformsCrudAndReadsAfterApplicationRestart` | 1/1 通过，0 skip。标准迁移与重复迁移，真实账户登录/认证 cookie、Antiforgery、Authors/Books 页面与 HTTP CRUD/分页，应用重建后重新登录读取关联内容及删除 |

账户 HTTP 使用真实官方 ASP.NET Core 应用的 WebApplicationFactory/TestServer，保留实际 ABP 授权与持久数据库；它不是浏览器端 WebAuthn 仪式或外部 Kestrel 账户验收。Passkey Store 多次应用重建在同一测试宿主进程，独立 Server 进程恢复已在上一节单独验证，不能混称。

## 保留的失败与修正

1. 前期 standalone restore 的 NU1008 来自父目录中央版本配置，随后使用局部 props 隔离；SDK 的隐式 library-packs 源也已清除，最终两份 assets 仅 NuGet.org。原失败日志保留。
2. 第一次 ABP C 盘 restore 明确磁盘空间不足，未到产品测试。随后源码、包缓存、产物与 TEMP 转入独占 D 盘目录，不修改全局设置。第一次 final-diff 未加 `core.longpaths=true` 产生路径误判；固定提交的最终检查通过。
3. ABP 账户首轮 GET `/Account/Login` 为 500。应用日志明确 `wwwroot/libs` 不存在或为空。按该仓库原 CI 执行锁定 `npm ci --no-audit --no-fund` 与 `node eng/Copy-BookStoreAssets.cjs` 后，原测试通过。没有关闭资源检查、修改原测试、替换账户或授权。失败 TRX/日志保持。
4. 远程初轮 readiness 的 1 秒 HttpClient 超时未被探针捕获，修正为在固定最多 40 次范围内处理该启动阶段超时。第二轮 h2c 查询成功，但探针只筛 `/v1/db/` 漏掉 `/v1/frame`，于是正确拒绝提供“已观察 Frame”结论；修正筛选范围后，最终 remote03 四阶段及协议断言均通过。上述是探针故障，不更改数据库代码，不删除旧记录。

## 证据位置、复现与交付边界

- 可提交工具：`scripts/validation/issue271-public-package/`，含原复现、扩展矩阵、锁文件、独立恢复配置及有界 PowerShell runner；使用方法见该目录 README。
- 最终可复现消费者：`D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue271-work/final-public/`；最终 `Run.ps1` 从新目录完整执行 exit 0，日志、原/扩展进程收据和包来源均保留。
- 远程最终：同根 `final-public/remote/`；包括逐文件 Server 快照、请求协议、四个消费者和四个 Server 的进程退出收据。前一通过的 `remote03/` 与此重复，不累计断言。
- 实际 ABP 最终：同根 `final-abp/`；最终 `Run-Abp.ps1` 从新 clone/缓存完整执行 exit 0，两个原测试 TRX 都为 1/1。第一次执行的 `abp01/` 仍保留初次失败 `BookStoreSonnetHttpJourneyTests.trx`、补资源后 `account-with-assets.trx` 和应用日志，不覆盖失败。
- 前期/失败材料：独立工作树 `artifacts/issue-271-public-20261016/`、`artifacts/a271/` 及 D 盘 `public01/remote/`、`remote02/`。此前通过的 `public01/`、`abp01/` 与最终完整脚本执行结果分列，不累计。旧 `D:/t` 或 `online-closure-20261012` 缺失回执未被本次引用为新 PASS。
- 源码/构建产物/关键日志的 SHA256 和最终收据见同名 `issue-271-public-package-20261016.json`。本次没有新增生产 JSON、二进制格式或依赖，没有进行 Native AOT 发布、正式包发布或浏览器 WebAuthn 验收；#266、完整 A06 与其它 EF 问题仍按各自合同跟踪。

## 主监督者复审

根重新核对原Issue验收条目，审查普通BLOB相等、相关Any、实际Binary参数拦截、索引计划、长度/NULL边界、事务与跨进程重开代码；逐一验证冻结收据及16份源文件、240份证据文件、12份运行二进制SHA256，并直接解析最终ABP两份TRX，均1/1、零失败/跳过。17份交付按SHA集成，本段为根追加审查说明；清单位于 `artifacts/issue-supervision-20261016/issue271/integration-manifest.json`。

原要求的公开修复客户端、嵌入式/SQLite原复现、独立远程合同与实际ABP Passkey/账户消费均已有证据，可在完整提交门禁、推送和相关CI回读后结项。没有将源码Server记为公开同版本Server，也未把Store或TestServer测试提升为浏览器WebAuthn或完整A06验收。GitHub实际回复与关闭回执另由监督记录更新。

提交前按仓库LF约定规范化机器收据与两份packages.lock.json的换行，JSON内容未变；收据中的sourceFiles仍指向原冻结运行源码哈希。原始与集成SHA对应保留在本地line-ending-normalization.json，不能将换行变化误记为重新运行证据。随后重新执行完整restore和规定Format Check。
