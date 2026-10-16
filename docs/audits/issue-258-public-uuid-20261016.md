# #258 逐行 UUID：公开包与真实 Chloe 消费验收

2026-10-16（Asia/Shanghai）。公开 `SonnetDB` / `SonnetDB.Core 4.5.0-preview.ef266.5` 的既有 `uuid()` 实现通过本次原生验收；Chloe PR #435 固定源码的单行翻译候选也通过嵌入式、真实 REST、显式 HTTP/2 消费。未发现本项需要新增的引擎实现，未重复修改生产源码。

**#258 保持 OPEN**：当前正式稳定引擎/驱动仍为 4.0.0，正式 Server 最新稳定 Release 仍为 v4.0.0；Chloe PR #435 仍 OPEN / Draft，本地补丁尚未交付上游，`chloe.sonnetdb` 的 NuGet 索引返回 404。本报告不把可安装预览包、源码构建 Server 或本地候选称为正式交付。

## 固定版本与来源

| 项目 | 固定输入 |
| --- | --- |
| 实施树 | `codex/issue258-public-uuid`，基线 `cfa92314b5eeb152f358adc1ea99c177cb6b6a91` |
| 公开包 | SonnetDB、SonnetDB.Core 均为精确版本 `[4.5.0-preview.ef266.5]`，来源仅 NuGet.org |
| 公开包源码 | `1af23d8f299febfdf66b040ab3419980396acf46`，nuspec 与运行 DLL InformationalVersion 均检查 |
| SonnetDB nupkg SHA256 | `0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4` |
| SonnetDB.Core nupkg SHA256 | `10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268` |
| Chloe PR #435 | `shuxinqin/Chloe`，来源分支 `maikebing/Chloe:feat/sonnetdb-provider`，head `bb7ec8735bd4511c35453eb009f70cc606795703` |
| 远程 Server | 只读复用 `issue264-work/server-frozen`，生产源码 `285186b868a59d4b22e47279ea51deb38a3a1f45`；每轮启动前核对 541 文件 SHA |
| Server manifest SHA256 | `F3C8003EE04C621A4FBF975E29263414199D8DD521D0071D0048859F14ED7A1D` |
| 工具链 | Windows x64，.NET SDK 10.0.400，消费者/Chloe 选择原有 net10.0 目标 |

线上 #258 原文和全部评论、PR #435 原文/评论/head、包版本索引和 Release 列表重新获取，快照 `2026-10-16T00:05:00Z`；稳定 Server v4.0.0 发布于 `2026-09-26T18:02:26Z`。原文早期“current main 未实现”的表述已由现有实现及 2026-10-15 进展评论取代，不能再把本项当作待实现空白。

原生消费者零 ProjectReference；Chloe 消费者仅引用固定源码的 Chloe 与 Chloe.SonnetDB 两项目，SonnetDB 零源码引用。每个运行目录使用全新 D 盘 NuGet packages/http/plugins/CLI cache、无 fallback、仅 NuGet.org；实际执行的 Core/Data DLL 与公开包 DLL 逐 SHA 一致。没有把主树新 DLL 放入公开消费者。

## 原生公开包：native-03

Release build **0 警告、0 错误**；八个独立消费者进程均按完整验收退出 0：

| 路径 | 初始进程 | 独立重开进程 |
| --- | --- | --- |
| ADO 嵌入式 | PASS | PASS |
| 直接公开 Core | PASS | PASS |
| 真实 REST | PASS | PASS |
| 显式 frame-http2 | PASS | PASS |

每次原生 `uuid()` 返回非空、非 Guid.Empty、标准小写 D 格式字符串。单行无 FROM 的两次调用、多行 SELECT 的两个表达式位置、JOIN 及相同查询再次执行均互异；结果列声明为 String/nullable=false，LIMIT 0 和 WHERE 空集在 Read 前/EOF 仍正确。

NULL 分支验证 `CASE` 与 `COALESCE`：已有值保持，NULL 行按各表达式位置生成新值。非法 `uuid(NULL)`、一个或多个参数即使空结果也明确拒绝；`DEFAULT uuid()` 仍按已有合同拒绝，不伪造列默认生成能力。零匹配 UPDATE 返回零行。

事务验证分为回滚与提交：对三个 NULL 行执行 `UPDATE ... SET value=uuid() ... RETURNING id,value`，RETURNING 与事务内读取精确一致；回滚后恢复原 NULL。新事务再次生成三个互异值，且不复用已回滚值；提交前、提交后与独立进程重开的持久值一致。对第二列的四行批量 UPDATE 也逐行互异，RETURNING 精确匹配最终存储。已有非 NULL 行保持原值。

直接 Core 另通过真实公共 UDF 注册接口覆盖同名 `uuid` 为计数函数，验证求值频率：四行 × 两位置正好 8 次，WHERE 空集、LIMIT 0、短路 CASE/COALESCE 和零匹配 UPDATE 均零次额外调用。随后注销计数函数，重新用内置 UUID 执行 UPDATE/RETURNING 和独立重开；计数替身的结果没有当作真实 UUID 互异证据。

初始远程验收结束后停止本任务 Server，再以新进程加载原数据库目录；原生与 Chloe 各自使用独立 Server 数据目录。结构化 Kestrel 日志证明 REST SQL 为 HTTP/1.1；显式 frame-http2 的 Frame 请求和 HTTP REST 写入/事务请求均为 HTTP/2。公开驱动使用的混合路径如实记录，**不声称所有写入都走原生 Frame 请求，也没有 HTTP/1.1 回退**。

## 真实 Chloe：chloe-02

原版克隆 head 固定，保存 561 个 Chloe 核心/Provider 原始源文件 SHA。原保护 handler 在表达式内遇到 `Guid.NewGuid()` 时抛 #258 `NotSupportedException`；每种协议先创建真实表并验证表达式外已生成 Guid 的参数插入/读取，再验证 SELECT 和批量 UPDATE 两个保护拒绝。拦截器确认拒绝前后命令数不增加、NULL 行未被更新。

三个原版 guard 进程退出 0 表示**负向复现符合预期**，不计为功能支持。随后本地候选在三协议各 initial/reopen，共六个独立进程全部通过。

候选补丁只改 `src/Chloe.SonnetDB/MethodHandlers/NewGuid_Handler.cs` 一行：

```csharp
generator.SqlBuilder.Append("uuid()");
```

现有 handler 基类仍只接受精确的 `Guid.NewGuid` 方法。没有改变表达式求值、参数绑定、GUID 转换器或其它 ORM API，也没有客户端先生成 GUID 的替代路径。补丁 SHA256 为 `3389D55C300097AEB6E242C29F7BDB024A63B601F963578105C4A9D1B90DE194`，交付于 `issue-258-public-probe/ChloeUuid.patch`。

实际 command interceptor 记录的代表 SQL：

```sql
SELECT "T"."Id",uuid() AS "First",uuid() AS "Second"
FROM "GuidRows" AS "T" ORDER BY "T"."Id" ASC;
UPDATE "GuidRows" SET "Value"=uuid(),"Other"=uuid()
WHERE "GuidRows"."Value" IS NULL;
```

逐条断言 SQL 含预期数量的 `uuid()`，参数中没有 Guid 或 GUID 字符串，从已执行的数据库命令证明没有客户端预求值一次。

候选覆盖：四行双调用投影及重执行、异步投影、空结果、nullable Guid 的 COALESCE、三 NULL 行的双列批量 UPDATE、事务回滚和重新提交、四行异步 UPDATE、真实 GUID materializer 及独立进程重开。初始业务控制值由表达式外 Guid 参数提供并保持原值，与服务端生成证据分开。

Chloe 没有新增 UPDATE RETURNING API；RETURNING 通过它已有的真实 `Session.ExecuteReader` 执行 `UPDATE GuidRows SET Other=uuid() WHERE Id>0 RETURNING Id,Other`，再用 `Query<GuidRow>` 的 Guid 转换器读取并逐主键比较。返回四个互异值与最终存储完全相同。此项没有假称普通 ORM Update API 自身返回 RETURNING 结果。

原版完整 build **38 个上游既有警告、0 错误**；应用单行补丁后的增量 build **0 警告、0 错误**。没有改写上游 Nullable/警告设置，也没有将增量零警告描述成上游完整构建无警告。三个 guard 与六个正向进程的结果在 JSON 分列，不与原生八进程或历史 xUnit 数字累加成一个“测试总数”。

## 证据、失败与边界

原始文件统一保留于 `artifacts/issue-supervision-20261016/issue258-work/`：`native-03/receipt.json`、`chloe-02/receipt.json` 记录实际命令、PID/起止/退出状态、包与 DLL、源文件 SHA 和 Server 身份；`evidence-summary.json` 提取协议路径/请求计数并核查自有 dotnet 残留为 0。当前数据库目录和跨进程 expected-values 文件保留，用于核对重开值。报告同名 JSON 固定关键来源、产物和证据摘要。

早期失败没有覆盖：native-01 是探针 DbTransaction 类型及 nullable 编译问题；native-02 的嵌入式已通过，但直接 Core 探针最后一条 UPDATE 漏写 SonnetDB 所要求的 WHERE，触发预期 SQL 语法边界；chloe-01 错用不存在的泛型 CreateTable API 而编译失败。修正验收工具后分别取得 native-03/chloe-02 完整结果。没有据这些工具错误修改引擎或扩大本任务。

本项保持原有函数合同：零参数，每个实际求值位置生成一次，结果用 STRING 存储；不新增 UUID 存储类型，不承诺额外加密或全局数学无碰撞保证，不新增 DEFAULT/序列语义。本次没有改任何持久格式、二进制版本、Frame 协议或生产 JSON 路径。

## 复跑与后续

可复跑工具在 `docs/audits/issue-258-public-probe/`。使用新的独占输出和短 runtime 路径执行 `Run.ps1` / `Run-Chloe.ps1`；参数分别为 `-OutputDirectory`、`-RuntimeDirectory`、可选 `-UseLocalProxy`、`-ServerSnapshot` 和 `-ServerManifest`。运行器拒绝覆盖旧证据，先核固定公开包与 Server SHA，Chloe head 改变也会拒绝继续。缺少 Server 参数只能完成嵌入式部分，不能把该运行称为完整远程验收。

下一步由根智能体审查并集成本报告、JSON、可复跑消费者与上游补丁，在最终树执行完整 restore/原 Format Check 后独立提交。本子任务未 commit/push、未评论/关闭 Issue、未修改共享 HANDOFF/CHANGELOG；旧 #261 树和证据保持冻结。

后续仍须正式发行匹配的 Core/ADO/Server，记录最低稳定版本；把 Guid.NewGuid 翻译候选交付 Chloe PR #435，并用正式包重跑三协议验收，更新对应支持清单。以上完成前，不能关闭 #258 或把 Chloe Draft 标为完整支持。
