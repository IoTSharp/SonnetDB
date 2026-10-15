# GH-Issue #270：公开包字面 Contains 与官方作者路径验收

2026-10-16（Asia/Shanghai）。本轮核对既有生产修复，未重写 Provider/引擎。固定 NuGet.org 公开包 `4.5.0-preview.ef266.5` 后，原 `%`、`_`、单个反斜杠复现以及扩展矩阵均通过；原官方 BookStore 的作者列表、独立 Count、分页也通过。可复现源码在 `eng/probes/issue270-public-consumer/`，机器回执和逐资产 SHA256 见 [验收清单](issue-270-public-package-20261016.json)。本报告不代表已提交、推送或关闭 Issue；这些动作由主监督代理在集成及规定门禁后执行。

## 固定来源

独立消费者使用 .NET SDK **10.0.400**、net10.0，EF Core/Relational/SQLite **10.0.12**。这与原 2026-10-11 复现记录中的 SDK 10.0.401 不同，未冒用其环境。NuGet.Config 只保留 NuGet.org；每轮使用全新私有 packages/http-cache，清空 fallback，无 ProjectReference、无本机源码 DLL 替换。三个 SonnetDB 包的 nuspec 与运行时 InformationalVersion 均指向 `1af23d8f299febfdf66b040ab3419980396acf46`。

| 公开包 | nupkg SHA256 |
|---|---|
| SonnetDB.Core | `10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268` |
| SonnetDB（ADO.NET） | `0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4` |
| SonnetDB.EntityFrameworkCore | `9688BA682E160639681AF3E4882DE6A58F70CF289B42552987FA7CD85D87E125` |

每个消费者进程记录实际加载 DLL 的位置、InformationalVersion、SHA256；随后与本轮私有目录中上述包的 DLL 再比较。BookStore 同样记录并验证实际加载文件：

| 程序集 | 公开包 DLL SHA256 |
|---|---|
| SonnetDB.Core.dll | `45D28D5D664AA60F3B6179E6D6181D25E79FEC0ECDC055C138E27A1AFE3236C2` |
| SonnetDB.Data.dll | `1238EB255D4AB70D2E1528A533BA92BD0FB16CEB96B99F64F8F13D64CDFB5E4A` |
| SonnetDB.EntityFrameworkCore.dll | `70E8B4676D9D5ED99A68E98915F6F0D9AA670BF1ED75A68E92B908B05B0E2A75` |

所有最终 Release build 均为 0 warning / 0 error。还原关闭 NuGet 漏洞审计，不能由此推导依赖安全结论。本轮没有重新发包。

## 实际结果与原复现

固定原始名字为 `100% Real`、`100X Real`、`A_B`、`AXB`、`path\name`。每个原复现场景使用独立数据库与消费者进程。

| 运行时 filter | 嵌入式 SonnetDB | SQLite 对照 | 真实 REST | 真实 h2c |
|---|---|---|---|---|
| `%` | `100% Real` / Count 1 | 同左 | 同左 | 同左 |
| `_` | `A_B` / Count 1 | 同左 | 同左 | 同左 |
| `\` | `path\name` / Count 1 | 同左 | 同左 | 同左 |

每项还执行 `Skip(1).Take(2)`，原复现恰好一个匹配，因此分页为空且独立 Count 保持 1。实际执行的列表 SQL、Count SQL、分页 SQL 和 DbParameter 均由 DbCommandInterceptor 保存；没有把 ToQueryString 当作执行证据。SonnetDB 运行时参数值保持原样，消费者未转义输入。三个字符串方法共用的参数模式按反斜杠、百分号、下划线顺序转义。以单反斜杠为例，执行的 Count 为：

```sql
SELECT COUNT(*)
FROM "A06Authors" AS "a"
WHERE "a"."Name" LIKE concat(concat('%', REPLACE(REPLACE(REPLACE(@pattern, '\', '\\'), '%', '\%'), '_', '\_')), '%')
```

对应 DbParameter 为 `@pattern`、`DbType=String`、值为单个反斜杠。SQLite 自己可使用 `instr` 或转义后的 LIKE 参数，验证其参数化与实际结果，不要求其内部参数值与 SonnetDB 的实现相同。

| 验收层 | 原复现 | 扩展矩阵 | 最终结果 |
|---|---:|---:|---:|
| 公开包 SonnetDB 嵌入式 | 3 | 48 | 51/51 |
| 独立公开包 SQLite 10.0.12 对照 | 3 | 48 | 51/51 |
| 公开包客户端 → 冻结源码 Server REST | 3 | 48 | 51/51 |
| 公开包客户端 → 冻结源码 Server h2c | 3 | 48 | 51/51 |
| 官方 BookStore 应用测试 | 三种过滤 × 两个宿主生命周期 | 列表、Count、分页 | 1/1 xUnit，六组过滤通过 |

Console 两轮分别为 102/102，零失败；不与既有 Core/EF 测试或中间重跑结果相加。每项场景内部的三条查询也不重复计为测试。

扩展矩阵为 `Contains`、`StartsWith`、`EndsWith` × 八种 pattern × 常量/运行时参数，共 48 项。数据包括单字符、前后缀、`\%_` 组合、`泵_%\😀设备`、单引号与特殊字符组合、空字符串、NULL 列。八个 pattern 为 `%`、`_`、`\`、`\%_`、`泵_%\😀设备`、空串、NULL、`'_%\`。按已明确的 EF/SQL 谓词合同，非 NULL 名字使用 ordinal 字面字符串运算；NULL 输入模式或 NULL 名字不命中，空模式命中全部 15 个非 NULL 名字。每项实际列表、Count、稳定排序后的分页与该预期相符。

## 真实 Server 边界

宿主是主监督代理已验证的源码提交 `285186b868a59d4b22e47279ea51deb38a3a1f45` 的 Release 输出，冻结时逐一校验 **541** 个文件；没有使用之后根目录重建的 bin。快照清单 `server-snapshot.json` SHA256 为 `F3C8003EE04C621A4FBF975E29263414199D8DD521D0071D0048859F14ED7A1D`，同级保存根原验证清单 `source-validation-final-root.json`。

真实 Kestrel 分别监听 loopback `43870` HTTP/1.1 和 `43871` HTTP/2 明文端口；独占数据库、测试 token、内容目录，禁用 MQTT。宿主日志观测到 **200** 次 HTTP/1.1 SQL 请求及 **157** 次 HTTP/2 `/v1/frame` 请求。数量包括建表、写入与查询，只用于证明实际传输，不能当成测试数量。客户端固定公开 `.5`，宿主仍是源码构建，不据此宣布 Server 发行包通过。

运行结束后按保存的 PID、创建时间、父 PID、完整命令行验证身份，再终止本任务宿主。该步骤为自有进程清理；本轮未验证优雅停机、崩溃恢复、完整应用通过远程 Server 运行。

## 官方 BookStore 旅程

使用 `IoTSharp/SonnetDB.Abp` 固定提交 `841567fd963621a8656a904121462234c6c80c0e` 的完整归档；ZIP SHA256 为 `8BB22F2FAD35EF12807C959D0A08ECC2A51090B7345AB12C73CD03BD4523AD59`。该样例来自 `abpframework/abp-samples` 提交 `97bc6ddfbc491843ddcd101342ce10e088c1d0d6` 的 `BookStore-Mvc-EfCore`，ABP **10.7.0**。本轮独立取回原上游两个业务文件，并与 `upstream-files.json` 哈希核对：

| 文件 | 官方上游 SHA256 | 应用快照 SHA256 |
|---|---|---|
| EfCoreAuthorRepository.cs | `279998D4D8F2C09F8B304A2149C9424EA5513DC1CD44816DBDC0104BCB11B267` | `900BD5448079A1D8B18B422DBB3177FCE5AE192B92B838BC17A953469280D468` |
| AuthorAppService.cs | `89FE0EA1C6D39A82AB634B07ACB320169A61C33CBCA701A4582E17CCF230DB0A` | `AC31149B35833E7039A4C289CA17669BBA77BD9644D407631D43A25F4772F1E1` |

两者不是字节相同文件。逐项差异只涉及原快照已有的隐式 using、可空返回值/参数以及 `filter!` 标注；规范化这些已列明编译适配后内容精确相同。原仓储 `WhereIf(... author.Name.Contains(filter!))`、OrderBy、Skip/Take 和应用服务独立 `CountAsync(author => author.Name.Contains(input.Filter))` 保留。本轮未改这些业务或授权代码。

快照正常应用组合包含 12 个内部 ProjectReference（应用层、迁移与 ABP 适配器）；资产图逐项确认均在该快照内，**没有跨仓库引擎 ProjectReference**。实际 Core/ADO/EF 仍来自私有 NuGet.org 包。不能把应用组合称为“零 ProjectReference”。原快照会加载 SQLite.Core 10.0.11，但本次配置选择 SonnetDB；它不是独立 SQLite 10.0.12 对照结果的来源。

新增单独的 `BookStoreContainsPublicPackageTests.cs`，从既有官方完整应用测试保留 WebApplicationFactory 与标准登录/antiforgery helpers，尾部字节比较通过，未替换认证或授权。前端 `npm ci` 成功并复制 551 个原映射资源。最终实际执行：

1. 标准完整模型迁移和 seed，再重跑迁移/seed。
2. 原 `/Account/Login` 表单、Cookie、antiforgery 流程登录 admin，核对应用配置中的已认证用户。
3. 通过正式 `/api/app/author` POST 新增五个原始名字，输入未经转义。原 seed 的其他作者保留。
4. 通过正式作者 HTTP 列表接口分别提交 `%`、`_`、`\`，每次 items 恰好预期唯一名字且 totalCount=1；SkipCount=1 后 items 为空且 totalCount 仍为 1。
5. 完整释放 WebApplicationFactory，重建应用宿主并重新执行标准登录；重复三种过滤、Count、分页，均通过。
6. 记录运行程序集与全部六组 HTTP 响应；按 GUID owner 核对本次测试数据库后清理，回执 `databaseDirectoryCleaned=true`。

最终 test TRX：**1 passed / 0 failed / 0 skipped**。这是实际官方应用的 **TestServer** 请求链与嵌入式持久存储；没有声称完成浏览器操作、单独进程 Kestrel 应用或应用崩溃恢复。最终应用/适配器构建显式绑定 `SourceRevisionId` 与 `RepositoryCommit` 为应用固定快照提交，测试增量另有 SHA。

## 可复现资产与原始证据

见 [运行说明](../../eng/probes/issue270-public-consumer/README.md)。独立消费者 runner 和真实 Server runner 均有有限超时、真实进程退出码及 manifest。`record-evidence.ps1` 已实际运行并验证：两轮消费者全部资产哈希、私有包来源与 nuspec、每层实际加载公开 DLL、应用内部引用边界、原 host/auth helpers、最终应用 TRX/回执。

本机原始证据根：

- `C:/Users/mysti/.codex/worktrees/issue270-public-contains/SonnetDB/artifacts/p270/r-15477a61/`：最终嵌入式/SQLite 102 项、源、公开包、日志、每进程 receipt 与 validation-manifest。
- `D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue270-work/remote/r-13364e8c/`：最终 REST/h2c 102 项、冻结宿主启动配置、协议日志、客户端源/包/回执。其短 TEMP/DataRoot 在 `D:/sndb-t/i270-13364e8c/`，保留 owner。
- `D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue270-work/b270/`：固定应用快照、依赖图、构建输出及 `artifacts/checks/` 命令日志与身份回执。
- `D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue270-work/bookstore-bound/`：最终 `bookstore-author-journey.json`、`issue270-bookstore-bound.trx`；最终测试短 TEMP 为 `D:/sndb-t/i270-bookstore-bound/`。
- `C:/Users/mysti/.codex/worktrees/issue270-public-contains/SonnetDB/artifacts/s270/`：冻结 Server、逐文件快照与根验证清单。该目录还被其他任务只读复用，未修改。

机器回执保存绝对证据路径和 SHA，以便本机复核；可复现源码与正文提交到仓库，二进制、NuGet 缓存、数据库和完整输出保留在忽略目录，不提交 build artifacts。

## 失败记录与结项边界

- 首次长路径独立消费者 restore 成功、build 失败（MSB3106/MSB3030）。缩短运行目录后构建通过；原 `consumer-c987b3b62957481db12522b4c0b8b6a3` 日志保留，不归为字符串语义失败。
- 中间 `r-42562e37` 的 SonnetDB 全部通过；SQLite StartsWith/EndsWith 的 12 项实际结果、Count、分页正确，但探针对 SQLite 误加“必须绑定原始未转义模式”约束导致断言失败。修正仅此 SQLite 探针约束，SonnetDB 原值参数约束保留，最终全新 run `r-15477a61` 102/102。中间失败回执保留，不把它们记成 Provider 缺陷。
- BookStore 首次 C 盘还原因磁盘不足失败，原 `issue270-bookstore-restore.*` 日志保留。失败/重复缓存删除被执行策略拒绝，未删除，也未绕过该拒绝。随后应用、私有缓存和临时文件迁至 D 盘，最终还原构建成功；未清理全局 NuGet 或旧工作树。早期首次 SDK 初始化曾输出 ASP.NET 开发证书安装提示，不能宣称本轮完全没有机器状态变化；后续显式关闭证书生成。
- 应用第一次通过后补充运行程序集哈希，再发现归档位于外层 Git 下使应用 InformationalVersion 自动采用外层提交。最终显式指定固定应用 SourceRevisionId/RepositoryCommit 并再次构建验收，采用 `bookstore-bound` 最终结果；前两次 PASS 不累计，原日志保留。
- 独立远程 runner 初稿在执行前的 PowerShell parse 检查发现续行运算符位置错误，修正脚本后运行；没有生产修改或远程失败被隐藏。

原 #270 的公开修复包、三字符列表/Count 与 SQLite 对照、参数/常量/组合和相关方法矩阵、本地执行、真实 Server、原官方作者筛选路径现均有本轮实际证据。技术验收可交主监督代理复核结项；主线完整 Format Check、独立提交、批量推送、GitHub 回复与状态回读由主监督代理完成，本子任务未执行。正式稳定版本、Server 发行包、完整应用远程/浏览器旅程及跨版本崩溃恢复不在本报告的 PASS 声明内。
## 主监督者复审

根已按14份交付清单验证源文件SHA，复审列表/Count/分页的真实参数拦截、原五行数据与48项扩展、官方应用登录/授权沿用及六组HTTP过滤断言；核验47份顶层证据和130份嵌入式/远程manifest文件，直接解析最终BookStore-bound TRX为1/1、零失败/跳过。应用业务只有已列明的既有编译适配，本轮未改业务或授权。公开客户端、源码Server与应用TestServer分别记证，未升级为公开Server或完整浏览器验收。

集成时仅按仓库LF约定规范化交付文本的换行，并去除模板末尾多余空行，原始运行资产保持冻结；机器收据中的文件SHA仍对应原始资产。逐文件源SHA与集成SHA对应保存于 `artifacts/issue-supervision-20261016/issue270/integration-manifest.json`；本文额外追加根复审说明。完整restore和规定Format Check通过后独立提交，本批正常推送并核相关CI后再回复/关闭并回读状态。
