# GH-Issue #264：公开预览包 Decimal 约束与 Chloe 适配候选验收

2026-10-16（Asia/Shanghai）。固定公开 `SonnetDB.Core/SonnetDB 4.5.0-preview.ef266.5` 已通过原始 `DECIMAL(5,2)` 复现、数值边界、失败原子性和跨进程重开。Chloe PR #435 的固定源码加本地 DDL 适配候选，也已通过嵌入式、REST、HTTP/2 消费验收。

**#264 仍应保持 OPEN**：本次没有正式稳定包或对应正式 Server 发行，也没有把候选提交至 Chloe 上游、合并或发布。已补齐的运行证据与这些交付条件分别记录；没有重写已实现的 SonnetDB 生产代码。

## 固定输入与来源

- SonnetDB 工作树基线：`285186b868a59d4b22e47279ea51deb38a3a1f45`。
- 公开客户端：两个 SonnetDB 包精确固定 `[4.5.0-preview.ef266.5]`；nuspec repository commit 和运行时程序集 InformationalVersion 均为 `1af23d8f299febfdf66b040ab3419980396acf46`。SDK 实测 `10.0.400`。
- 原始 Issue 快照为 OPEN、最后更新 `2026-10-15T15:50:46Z`、1 条评论；原文和该评论均已核对。
- Chloe PR #435：OPEN / Draft，来源 `maikebing/Chloe` 的 `feat/sonnetdb-provider`，固定提交 `bb7ec8735bd4511c35453eb009f70cc606795703`。运行脚本重新克隆并验证 HEAD，记录 561 份 Chloe 核心/Provider 原始源文件 SHA256；候选仅修改 `src/Chloe.SonnetDB/DDL/SonnetDBTableGenerator.cs`。
- 远程宿主使用独立冻结的 SonnetDB 源码构建副本，生产源码绑定 `285186b8...`。541 个文件逐一比对 SHA256 后启动；清单 SHA256 为 `F3C8003EE04C621A4FBF975E29263414199D8DD521D0071D0048859F14ED7A1D`。该宿主**不是公开 `.5` Server 安装包或正式发行**。

| 公开包 | nupkg SHA256 |
|---|---|
| SonnetDB.Core | `10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268` |
| SonnetDB（ADO.NET） | `0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4` |

原生消费者有 **0 个 ProjectReference**。Chloe 消费者仅引用固定 Chloe 自身的两个源码项目，SonnetDB 仍全部来自公开包；原始 Chloe guard 构建和候选构建中的 SonnetDB DLL 都与公开包解包 DLL 完全一致，没有用引擎源码 DLL 替代。Chloe 自身是本地源码产物，不能称为公开 Chloe 包。

每次最终运行都使用新的私有 packages、HTTP cache、plugins cache、CLI home；NuGet 源只有 `https://api.nuget.org/v3/index.json`，没有 fallback。显式禁用 SDK 隐式 library-packs 源，空 Directory.Build/Packages 文件阻断祖先仓库配置；全局 NuGet 缓存没有修改。NuGetAudit 关闭，本报告不提供依赖漏洞审计结论。

## 原生公开包实际结果

最终原生运行 `native-04` 于 `2026-10-15T23:10:08.4689755Z` 完成，即 **2026-10-16 07:10:08 Asia/Shanghai**。Release 构建 **0 warning / 0 error**。嵌入式 initial/reopen、独立 Core 事务检查、REST initial/reopen、frame-http2 initial/reopen，合计 **7/7 消费者进程 exit 0**，没有超时。

| 验收合同 | 实际结果 |
|---|---|
| 原 Issue SQL | 原样执行 `INSERT INTO amounts(id,amount) VALUES(1,'1.234'),(2,'123456.789')` 被明确拒绝；实际行集为空 |
| 正负、边界、NULL | `0`、`±0.01`、`±999.99`、`1.2300` 和 NULL 正确读回；多余尾零可无损接受 |
| 原始精确参数与字符串 | `1.234`、`-1.234`、`±1000`、`±123456.789`、`1.0000000000000000000000000001` 分别通过 Decimal 参数和字符串参数写入，均拒绝；没有先转 double 或舍入后接受 |
| 批量原子性 | 第一行合法、后行 scale 或 precision 越界时整条 INSERT 不写入任何行 |
| UPDATE 原子性 | CASE 表达式使前行合法、后行越界时，原精确行集保持 |
| 显式事务 | 合法 UPDATE 后的非法 INSERT/UPDATE 不留下本语句部分结果；事务内保留先前合法改动，ROLLBACK 恢复旧值；后续新事务可正常提交 |
| 核心跨表提交 | 通过公开 Core API 提交两表 mutation，第二表越界时两表均为空；随后两表首个生成主键均为 1 |
| 元数据 | `GetSchema("Columns")` 精确保持 `(5,2)`、`NUMERIC(3,0)` 的零 scale，以及显式 `(38,28)` |
| 裸 DECIMAL | 原始 Decimal 参数保存 `decimal.MaxValue`、`decimal.MinValue`、`±1e-28`、`123456789.1234567890123456789`、0 和 NULL，reader 原生类型与值精确一致 |
| 裸类型与显式声明区分 | 裸 DECIMAL 接受 MaxValue；显式 `DECIMAL(38,28)` 拒绝 MaxValue，接受合法 28 位小数 |
| 持久化 | 独立消费者进程重开后，精确数据、约束和元数据保持；远程宿主在 initial 与 reopen 之间实际终止并重新启动 |

本次拒绝合同是不进行隐式舍入，不以列元数据存在代替实际写约束。原 `4.0.0` 的缺陷来自原 Issue，本轮未重新执行旧包；没有把修复 #264 等同于完整关闭独立的精确参数 Issue #235。

## Chloe 基线与候选实际结果

最终 Chloe 运行 `chloe-05` 于 `2026-10-15T23:15:52.4344504Z` 完成，即 **2026-10-16 07:15:52 Asia/Shanghai**。

1. 原始 PR HEAD 先独立构建并运行 guard：成对 Precision/Scale、单独 Precision、单独 Scale、自定义 DECIMAL、自定义 NUMERIC 五种映射均重现现有拒绝，**1 个进程 exit 0**。这证明保护存在，不算功能支持。
2. 对同一固定源码应用 [候选补丁](issue-264-public-probe/ChloeDecimal.patch)，重新构建并在全新数据库运行嵌入式、REST、frame-http2 各 initial/reopen，**6/6 消费者进程 exit 0**。每个 reopen 是独立进程；远程宿主也在两个阶段之间终止再启动。

候选把 Decimal/可空 Decimal 的成对 `Column.Precision/Scale` 生成为 `DECIMAL(p,s)`，允许既有 DataType 入口的明确 `DECIMAL(5,2)`/`NUMERIC(5,2)` 声明，默认 decimal 仍输出裸 `DECIMAL`。数据值的验证与拒绝全部由真实数据库执行，适配层没有模拟金额约束或舍入。

实测包括：生成 SQL 与实际 GetSchema 一致；nullable、正负边界和原始 DbParam；普通 Insert 与异步 Insert；单条批量和 `batchSize=1` 分批 InsertRange 的晚到失败回滚；表达式 UPDATE 整句原子性；显式事务内晚到失败、回滚后旧值与后续提交；自定义 DECIMAL/NUMERIC 的实际拒绝；裸 Decimal 完整 CLR 值域与精确大值；重开后精确行集与继续拒绝。

候选有意保留明确边界：只有 Decimal 属性支持成对元数据，precision 为 1..38，scale 为 0..min(precision,28)；单独 p 或 s、非 Decimal 属性、越界声明、同时提供 p/s 与 DataType 的组合仍明确拒绝，不猜测缺失参数默认值。自定义 DECIMAL/NUMERIC(p,s) 用于非 Decimal CLR 属性也继续明确拒绝，另有实际断言。这是本地可复核候选，尚未改变线上 PR #435 的现有行为或依赖版本。

Chloe 固定核心源码首次 Release 构建有 **38 条既有 CS8632 warning、0 error**；候选增量构建为 **0 warning / 0 error**。原有核心项目未重新编译的警告不能写成已修复；本轮没有压制或修改这些无关警告，独立消费者自身仍启用 Nullable、ImplicitUsings 与 TreatWarningsAsErrors。

## 远程证据边界

原生矩阵使用独占端口 `43864/43865`，Chloe 使用 `43866/43867`；各自只访问其专属数据库。实际宿主 JSON 请求日志逐项验证：

| 运行 | REST 数据请求 HTTP/1.1 | frame-http2 端口数据请求 HTTP/2 | 其中 `/v1/frame` | 其中 HTTP/2 REST |
|---|---:|---:|---:|---:|
| 原生 initial + reopen | 84 | 84 | 18 | 66 |
| Chloe initial + reopen | 71 | 71 | 15 | 56 |

公开 `.5` 客户端在显式 frame-http2 模式使用 Frame 查询和 HTTP/2 REST 写入/事务/元数据；不能把这些请求称为完整原生 Frame 写协议。第二个宿主进程确实重新加载同一数据目录，没有自动降级到 HTTP/1.1。宿主进程被探针主动终止，收据中的宿主 exit `-1` 对应已记录的终止操作；消费者进程均为 exit 0。

## catalog 与发行边界

本切片的实际 Decimal 目录文件为 **v11**，原生 Core 流程直接读取 header 验证，最终所有专属数据库目录另存 SHA256 清单。新增显式来源沿用既有 catalog flags bit4；没有修改 row/key/index 或 FileHeader 布局。主线其它较新功能可以使用更高目录版本，本切片不能代表整个主线只有 v11。

沿用已有兼容合同：旧 v9/v10 中非 `(38,28)` 声明约束后续写入；来源不明的旧 `(38,28)` 保持兼容，旧不合规行不在读取时自动改写。旧 reader 拒绝 v11；回退需要升级前的 **catalog 和数据副本一起恢复**，不能只换旧二进制。本轮没有重新运行旧目录迁移或旧二进制回退实验，上述兼容合同与本轮新库实测分列。

2026-10-16 另行回读公开 NuGet 版本索引，`SonnetDB` 与 `SonnetDB.Core` 的最高稳定版本仍为 `4.0.0`；GitHub release 列表中的最新非预览数据库发行仍为 `v4.0.0`（2026-09-26 发布），Workbench preview 单列。原始 JSON 和 SHA 保存在 discovery 证据中。公开预览已可安装，不代表原 Issue 要求的正式稳定发行已经完成。

剩余顺序：确认包含修复的正式 Core/ADO 与对应 Server 发行及兼容说明；在 Chloe 上游审查本候选、更新正式依赖/支持清单和版本；基于正式交付物重复对应消费者验收，再评估关闭 #264。没有把本地候选或源码宿主当作正式交付。

## 复现与证据

[原生项目](issue-264-public-probe/Consumer.csproj)、[原生程序](issue-264-public-probe/Program.cs)、[Chloe 项目](issue-264-public-probe/ChloeConsumer.csproj)、[Chloe 程序](issue-264-public-probe/ChloeProgram.cs.txt)、两个运行脚本、隔离配置和候选补丁均随报告保留。仅运行嵌入式路径可执行：

```powershell
pwsh -NoLogo -NoProfile -File docs/audits/issue-264-public-probe/Run.ps1 -OutputDirectory D:/sndb-evidence/i264-native-new -RuntimeDirectory D:/sndb-t/i264-native-new -UseLocalProxy
pwsh -NoLogo -NoProfile -File docs/audits/issue-264-public-probe/Run-Chloe.ps1 -OutputDirectory D:/sndb-evidence/i264-chloe-new -RuntimeDirectory D:/sndb-t/i264-chloe-new -UseLocalProxy
```

每次目录必须不存在。`-UseLocalProxy` 只供本机已有 `127.0.0.1:7890` 代理使用，可在直连环境省略。远程矩阵额外提供 `-ServerSnapshot <冻结宿主目录> -ServerManifest <冻结清单>`；脚本核源码身份和每个宿主文件 hash 后，使用自己的 content root、数据目录和端口。所有子进程的 NuGet/CLI 缓存均在 evidence 目录，TEMP/TMP 与数据库在短 RuntimeDirectory，未改全局环境。

最终证据根为 `D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue264-work/`：

- `native-04/receipt.json`，SHA256 `5506A944B15B8320787256609E37DB8633F01E83EED2C0A9A88995092B2907A6`。
- `chloe-05/receipt.json`，SHA256 `543C10E647A3571E09CB5C7994EC2CE62B069D6E045CAE9893AF2B0597623283`。
- 两目录均包含源码副本、实际编译产物、公开包、全部命令/PID/退出结果、日志与文件 SHA；另有最终 `database-manifest.json`。数据库分别保留于 `D:/sndb-t/i264-native-04/`、`D:/sndb-t/i264-chloe-05/`，包含归属记录。
- [永久验收摘要](issue-264-public-package-20261016.json) 保存来源、命令结果、实际协议统计、catalog 身份、源码副本一致性及日志哈希。

没有 TRX。最终原生 7 个进程、Chloe 旧保护 1 个进程、候选 6 个进程分别计数，不把内部 PASS 行或旧运行累加成新测试数量。未运行完整 Chloe suite、NativeAOT 发布或远端 CI。

保留的前置记录：`native-01`/`native-02` 为探针缺少 namespace、nullable 判定导致的编译错误；`native-03` 是较早完整成功，最终按共享脚本配置重跑为 `native-04`，不累计。`chloe-01` 被依赖检查捕获 PowerShell 参数数组拼接错误；`chloe-02` 为探针遗漏 InsertRange 第三个参数的编译错误；`chloe-03` 为探针未显式 NonAutoIncrement 导致身份假设错误，修正后使用全新库通过 `chloe-04`。复审进一步限制自定义 Decimal 声明与非 Decimal CLR 属性的未知组合，最终在新库完整通过 `chloe-05`；前次成功不累加。全部原日志保留，没有改写为产品失败或最终成功。

执行智能体没有修改 SonnetDB 生产源码、共享 HANDOFF/CHANGELOG，没有 commit、push、发布、部署或线上 Issue 操作；主监督者负责整合、完整 restore/Format Check、独立提交、批次推送和线上进展回读。
## 主监督者复审

根按冻结清单逐一核验13份交付文件，审查原生精度/原子性/事务与Chloe真实DDL/原始DbParam候选，重新核验原生65份和Chloe1362份运行证据文件、26条命令的实际成功退出与日志SHA。原生7个消费进程、Chloe原保护1个进程与候选6个进程分别计数。候选补丁仅改外部Provider DDL，保留未支持声明的拒绝，未发布或修改外部PR；SonnetDB生产实现没有重复改写。

最终完整restore与规定Format Check通过后独立提交，再随下一批正常推送。已推送的上一批270～272当前CI待完成；本项仍保持OPEN，正式发行、对应正式Server及Chloe上游合入/发布和正式消费者验收不能以本地候选替代。

提交前git diff --check已核：除候选.patch三条必须保留的统一diff空上下文前缀（单个空格）外均通过；该补丁保持原始SHA且已在最终Chloe运行中实际应用。完整solution Format Check的命令与级别不变。
