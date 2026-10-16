# #261 视图声明元数据、XCode 实际消费及 CONCAT 修复

核查日期：2026-10-16（Asia/Shanghai）。任务为 SonnetDB #261；对应 XCode #100 / Draft PR #101。本报告区分公开包、源码构建 Server、本地 XCode 补丁及本次源码候选，不能把其中一项成功替代正式发布或上游交付。

结论：公开预览包已有的普通/物化视图声明能力经真实消费者验证；同时发现并修复内置 `concat` 静态声明缺口，以及 XCode 对未声明长度/精度和精确物理名称 XML 往返的两个消费问题。源码与本地消费者候选通过下述验收，但正式引擎/ADO/Server 发行和 XCode 上游提交尚未完成，**#261 继续 OPEN**。

## 固定输入与证据

| 输入 | 固定值与范围 |
| --- | --- |
| 公开 SonnetDB、SonnetDB.Core | `4.5.0-preview.ef266.5`，NuGet.org，源码 `1af23d8f299febfdf66b040ab3419980396acf46` |
| SonnetDB nupkg SHA256 | `0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4` |
| SonnetDB.Core nupkg SHA256 | `10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268` |
| 原 XCode PR #101 | `757fa2a078906f87783e55fe0e9d5f54a9e916b9`，原依赖 `4.0.0`，原支持目标 `netstandard2.1` |
| 公开包远程验收 Server | 只读 `issue264-work/server-frozen`，生产源码 `285186b868a59d4b22e47279ea51deb38a3a1f45`；541 文件 manifest SHA `F3C8003EE04C621A4FBF975E29263414199D8DD521D0071D0048859F14ED7A1D` |
| 本次源码候选 | `285186b8` 加冻结的 #262 日期合同候选，再加本项六个生产/测试文件；包含共享文件的 #262 增量，不能当作纯 `285186b8` |
| #262 同步 manifest SHA | `1040936A4A260F647C0087FF438D7E0A69208DA2D29375F00E580C33A91B682A` |
| 本项六文件交付 manifest SHA | `CDA36CC23F5B498EA75E43A09A16513C48E6D17D052D7CD2D161CD651A0CF40D` |
| 工具链 | Windows x64、.NET SDK `10.0.400`；发布消费者 `net10.0`，XCode 本体保持 `netstandard2.1` |

原始在线 Issue/PR/评论及 NuGet/Release 快照位于 `artifacts/issue-supervision-20261016/issue261-work/discovery/`，最后捕获 `2026-10-15T23:40:46Z`。该时点公开 stable 仍为 `4.0.0`，不能为本项声明一个已发布的修复 stable 最低版本。

全部运行使用独占 D 盘目录及短数据库/TEMP 路径。公开包验收使用独立 NuGet packages/http/plugins/CLI cache、仅 NuGet.org、无 fallback；nupkg 与运行 DLL 均核对 SHA。原生公开消费者零 ProjectReference；XCode 公开包消费者仅一个 ProjectReference 指向固定 XCode 源码，SonnetDB 零源码引用。本次源码候选 XCode 补验则显式使用冻结 Core/Data 构建 DLL，并单独标记为非公开包验收。

## 公开包原生验收：native-03

八个独立消费者进程全部退出 0：ADO embedded、直接 Core、REST、frame-http2 各自 initial/reopen。消费者 Release build 为零警告、零错误。远程初始阶段后终止自有 Server，再启动新 Server 进程打开原目录；日志记录实际 HTTP/1.1 和 HTTP/2 请求。该 Server 是源码构建产物，**不是公开正式 Server 包**。

16 种形状中，以下 **14 种完整声明形状**通过 LIMIT 0、WHERE 1=0、实际行与 Read 前/逐行/EOF schema 验证：原 Issue `item_view`，普通/嵌套/空/NULL 视图，算术/比较/CASE/COALESCE/CAST，LEFT/RIGHT/FULL/INNER JOIN，聚合、分组，空/非空物化视图。断言包括名称和顺序、CLR/SQL 类型、可空性、可用 precision/scale、未知长度以及精确行值。

另外两种为边界刻画，**不计入完整元数据 PASS**：

- `MixedView` 的 CASE 分支为 Int64 与 Float64；执行器可返回不同 CLR 类型，没有统一提升合同，声明 Object 保守且准确。本项不改变 CASE 数据转换语义。
- `UnknownView` 实际使用内置 `concat('id=', CAST(DeviceID AS STRING))`，公开 `.5` 仍声明 Object、未知 nullable。这是已证缺口，已由下述源码修复；不能因探针预期 Object 而称为完整支持。

`GetSchema("Columns")` 未提供视图列，本次依赖零行 reader，符合 Issue 的“Columns **或** 零行读取器”。公开 `.5` 的 classic `GetSchemaTable().ColumnSize` 未声明值为 `DBNull`；当前源码候选 STRING 无界值为 `-1`，强类型 `GetColumnSchema().ColumnSize` 均为 null。这两个版本的期望不可互换。

## XCode 公开包实际消费：xcode-05

两个原版负向基线与六个候选消费者进程分开记录，均按各自期望退出 0：

| 运行 | 真实行为 |
| --- | --- |
| 原 XCode + stable 4.0.0 | 原 Issue 零行 schema 为 Object，真实 `GetTables()` 抛出 #261 `NotSupportedException`。这是复现，不是支持 PASS。 |
| 原 XCode + preview .5 | 零行类型已为 Int64/String；`ReadViewColumns` 将 DBNull ColumnSize 转 Int32，真实抛 `InvalidCastException`。 |
| 本地 XCode 候选 + preview .5 | embedded / REST / frame-http2 × initial/reopen，共 6/6；每种通过真实 `GetTables()`、查询、schema 和 `DAL.Export`/`DAL.Import` XML 往返。 |

候选覆盖上述 14 个完整声明形状，再加 `case_lower/value`、`case_upper/VALUE`、`中文视图/显示 名`、`Quote"View/值"标签` 四个精确名称形状，共 18 种。没有手工拼造 schema，也没有自行解析视图 SQL。未知 Object/nullable 仍明确拒绝，未通过取首行或猜类型绕过。

交付的 `issue-261-public-probe/XCodeMetadata.patch` 仅包含两个上游文件：

1. `XCode/DataAccessLayer/Database/SonnetDB.cs`：DBNull 长度/precision/scale 转为 XCode 的未声明 sentinel 0；读取 p/s 后才生成 RawType，保留 `DECIMAL(20,6)`；Object 或未知 nullable 明确报 #261。
2. `XCode/DataAccessLayer/Model/ModelHelper.cs`：仅真实 `IDataTable.DbType` 或 `IDataColumn.Table.DbType` 为 SonnetDB 时，XML 保存物理表/列名原始大小写。原来导出会省略大小写不同的 `ColumnName`，导回后 `name` 变为 `Name`；候选修复了此实际消费失败。其它数据库保留原分支逻辑。

原版与候选 XCode 编译均有上游既存 **105 警告、0 错误**；没有把这些警告写成已修复。候选补丁目前仅在隔离本地克隆中，尚未推送至 PR #101。

## 实际生成实体编译：generated-02

使用同一固定 XCode 源码/补丁与公开 `.5` 包，通过真实 Provider 获得五种视图模型：`GeneratedView`、`GeneratedSnapshot`、`LowerModel`、`UpperModel`、`UnicodeModel`。调用实际 `EntityBuilder` 生成各自 data/business 两个 partial，共十个源文件，然后构建 `GeneratedEntities.dll`；生成项目开启 Nullable、ImplicitUsings、TreatWarningsAsErrors，最终 **0 警告、0 错误**。

加载实际生成程序集，逐一校验九个属性的 `BindTable`/`BindColumn`、CLR 类型、`DataObjectField.IsNullable` 和 DECIMAL precision/scale；确认 `value`、`VALUE`、`显示 名` 物理名保留，中文 C# 属性 `显示名` 绑定到 `显示 名`。五模型 XML 由真实 Provider 导出，校验模型没有手工替换。

生成器使用现有公开选项 `MaxCacheCount=0`：首轮 `generated-01` 因视图模板生成未使用的私有缓存字段而触发 CS0414，原失败保留；没有压制警告、修改上游模板或伪造视图主键。双引号物理名称只在上节完成 XML 往返，**未宣称双引号名称的实体源码编译通过**；上游生成器的 C# 字符串转义仍是未覆盖边界。BLOB 声明在 18 形状消费中已验证，生成编译定向检查未包含 BLOB 属性。

## 本项源码修复与组合回归：source-02

新增 `RelationalSelectExecutor.ConcatDeclarations.cs`，在统一声明推断阶段识别内置 scalar `concat`：至少一个参数、非星号、当前没有同名 scalar UDF 时，声明 STRING 且 nullable=false。NULL 参数仍按既有运行语义转换为空文本；不执行参数、不取样、不虚构长度或 precision/scale。同名 UDF 的返回声明保留未知。

接线保留 #262 的全部日期声明推断增量。两个既有 unknown 测试改用真正混合 String/Int64 的 CASE 表达式，继续断言 Object；其可证明的 nullable=false 如实保留，未削弱未知类型检查。

新增 **4 个 Core 用例、2 个真实远程用例**：

- 空源、NULL、文本及 all-NULL CONCAT，普通/嵌套/物化视图，内部 CAST 和嵌套 CONCAT；直接 Core、ADO 重开以及读前/逐行/EOF schema。
- 参数 UDF 在 LIMIT 0 不执行；同名大小写混合 UDF 不冒用内置声明；物化快照在覆盖、注销和 REFRESH 间保存原声明，REFRESH 后才采用新声明。
- 真 REST/HTTP2 Kestrel，空源后插入 NULL/文本、刷新物化视图及关闭/重开 Server。#262 客户端现协商结果版本 4；测试核验实际 HTTP/2 与请求版本，不把它写成公开 `.5` 的版本。

| 最终组合验证 | 结果 |
| --- | --- |
| Core.Tests Release build | 0 警告 / 0 错误 |
| SonnetDB.Tests Release build | 0 警告 / 0 错误 |
| Core：新 CONCAT、既有 SQL metadata/物化视图、#262 Frame 日期/远程日期响应 | **59/59**，失败 0、跳过 0 |
| Server：完整 RemoteAdoHttp2TransportTests 与 RemoteAdoDateTimeContractTests | **114/114**，失败 0、跳过 0 |

这两个组合数包含新增与既有测试，不能再与新增 4+2 或根验收重复累计。源码候选 XCode 补验 `source-xcode-01` 使用已冻结的本次 Core/Data DLL、`generated-02` 候选 XCode 与对应依赖，编译零警告/错误；独立 initial/reopen 两进程均通过。真实 Provider 对 CONCAT 普通/嵌套/物化三视图的反向工程、XML 往返、空/NULL/文本查询及读前/逐行/EOF schema 全部符合 String/nullable=false，不能据此声称公开 `.5` 已含修复。

## 持久化、版本与失败记录

本项不修改任何文件结构布局或已有 binary version。各最终探针留下的 16 个物化快照实读 magic=`SDBMVS01`、version=2，并记录 SHA。旧 v1 快照仍沿用已有 `RequiresRefresh` 处理；已保存未知声明的 v2 快照须 REFRESH 才更新声明，不能由首行推断。公开 `.5` 的完整声明传输使用既有版本 3；本次组合候选依赖 #262 的新增版本 4 日期合同，不是本项新改 Frame 格式。

所有早期失败保留，不覆盖为 PASS：native-01 探针 Core rows 类型转换编译失败，native-02 错套当前源码的 ColumnSize=-1 期望；xcode-01 误改上游目标 net10 编译失败，xcode-02 两负向基线复现，xcode-03 XML 大小写往返实际失败，xcode-04 补丁 CRLF 应用失败；generated-01 为上述未用缓存字段；source-01 为隔离树尚未初始化 MQTT 子模块，已检出锁定 `9e0c8202fc93737b2bbde013ed488d37948ad5c7` 后通过 source-02。CoAP 子模块固定 `797a85992ad33be02c4aa4f4ea9c849ed382c2cc`。旧证据均完整保留。

最终运行均结束，核查无本任务运行中的 dotnet 进程；自有 Server 终止和重开记录见对应 receipt。未操作其它任务服务、原 #264 冻结树或全局包缓存。

## 交付与后续顺序

- `source-delivery.json` 冻结本项六文件和实际 DLL/TRX SHA，两个共享文件明确包含 #262 基线；其余 11 个 #262 同步文件逐 SHA 证明未改变。报告同名 JSON 汇总运行 receipt、来源与范围，原证据位于 `artifacts/issue-supervision-20261016/issue261-work/`。
- 原生、XCode、实体生成、源码候选的可复跑项目/脚本位于 `docs/audits/issue-261-public-probe/`。这些是验收工具与上游补丁，未修改上游远端。
- 实施子树 `codex/issue261-public-metadata` 未 commit/push；不修改共享 HANDOFF/CHANGELOG，由根智能体按实际集成状态记录。`git diff --check` 已通过。完整 solution restore / 原 Format Check 提交门禁由根在最终待提交树执行，本报告不将定向构建冒充完整门禁。
- 下一步先在 #262 集成基线上接收本项六文件、独立提交并完成根复审；然后统一正式发行 Core/ADO/Server，确定最低匹配版本，使用正式包重跑本报告的原生与 XCode 验收，再向 XCode PR #101 提交补丁/依赖更新并更新 #100。正式发行和上游交付尚未满足，当前不可关闭 #261。

## 主树复审补记

根按冻结清单核对27文件、406证据文件、5源码二进制与16个物化快照；独立两个Release构建零警告/错误，Core44/44、真实Server114/114，零失败/跳过。根定向集合与代理集合重叠不累计。审计JSON仅归一为LF，原交付SHA与根SHA映射保存在 artifacts/issue-supervision-20261016/issue261/root-review/line-ending-normalization.json；补丁保留原始上下文空格，专属.gitattributes仅标识补丁文件。完整提交门禁收据在 issue261/final，须实际成功后才能提交。
