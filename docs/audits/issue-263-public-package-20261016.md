# #263：公开包原始小整数绑定与 Chloe 消费验收

核验日期：2026-10-16（Asia/Shanghai）。本次补齐公开预览客户端的真实消费证据，没有重复实现已经交付的绑定修复。**#263 的 sbyte/ushort/uint 原始参数核心矩阵通过；Issue 保持 OPEN**，正式稳定驱动、Server 和 Chloe 正式消费者发行验收仍未完成。

## 版本与证据边界

| 对象 | 实际使用与核验结果 |
| --- | --- |
| SonnetDB.Core / SonnetDB | NuGet.org 的 `4.5.0-preview.ef266.5`，nuspec 源码提交均为 `1af23d8f299febfdf66b040ab3419980396acf46` |
| Chloe 消费者 | `maikebing/Chloe` 的 PR #435 固定提交 `bb7ec8735bd4511c35453eb009f70cc606795703`，原始已跟踪源码无修改 |
| 远程 Server | 源码 `285186b868a59d4b22e47279ea51deb38a3a1f45` 的冻结构建，复制并核验 541 个文件；不是正式发布的 Server |
| 新探针 | 独立控制台消费者，`net10.0`，新增代码启用 Nullable / ImplicitUsings / TreatWarningsAsErrors |
| 构建 | .NET SDK `10.0.400`，Windows x64；restore / Release build 均 exit 0；Chloe 上游源码存在 38 条既有 CS8632 警告，0 错误 |

每次运行使用全新的私有 NuGet 包缓存、HTTP 缓存、CLI 目录和短 TEMP / 数据库目录；唯一恢复源为 NuGet.org，没有 fallback。`project.assets.json` 只包含 Chloe 和 Chloe.SonnetDB 两个源码 ProjectReference，SonnetDB 引擎与 ADO 均来自公开包，运行 DLL 与 nupkg 内 DLL 逐字节哈希一致。Chloe clone 只新增三个未跟踪空 MSBuild 配置，阻断祖先仓库设置；最终 `git diff --exit-code` 为 0。`5.56.0` 是该源码的项目版本，不代表 Chloe.SonnetDB 包已发布。

| 文件 | SHA256 |
| --- | --- |
| SonnetDB.Core nupkg | `10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268` |
| SonnetDB nupkg | `0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4` |
| 运行 SonnetDB.Core.dll | `45D28D5D664AA60F3B6179E6D6181D25E79FEC0ECDC055C138E27A1AFE3236C2` |
| 运行 SonnetDB.Data.dll | `1238EB255D4AB70D2E1528A533BA92BD0FB16CEB96B99F64F8F13D64CDFB5E4A` |

## 原始合同与实际消费

每个入口覆盖 12 个输入：sbyte、ushort、uint 各自最小值/最大值，以及以上三个底层类型的真实枚举各自最小值/最大值。数值范围分别为 `-128..127`、`0..65535`、`0..4294967295`。非空 Nullable 装箱遵循 CLR 规则，另验 null / DBNull。

- ADO 直接传递原始 boxed 值，分别使用默认 `DbType.Object` 与显式 SByte / UInt16 / UInt32；执行同步/异步、多行 INSERT、参数谓词和事务 INSERT / UPDATE / DELETE 回滚。断言 `SndbParameter.Value` 引用及 DbType 保留，reader 元数据和结果精确为 Int64。
- Chloe 实际调用原始 `DbParam`、`Session` 和 `SqlQuery` / `SqlQueryAsync`。命令拦截器检查到达驱动的对象是 `SndbParameter`，其 CLR 类型仍为相应小整数、DbType 保持原类型，没有预先变成 Int64 或 double；执行后再次检查参数引用及 DbType。
- Chloe 普通 DbParam 对枚举使用其原有的底层类型转换；另用 `ExplicitParameter` 将真实 boxed enum 原样送入驱动，并检查其值、类型和结果。
- 每个表提交四个相同整数、两个 NULL；事务中的第七行、替代值更新和删除均回滚。独立消费进程重开后再次验证四个匹配值、两个 NULL、总行数六、替代值不存在。
- 单独运行 Chloe 实体 Insert / InsertRange（batch size 1）/表达式查询/nullable 重开对照。其已有 provider 将数值扩大为 Int64；对照结果不充当原始参数合同的证据。

最终 `final01` 共 **12 个独立消费进程全部 exit 0**。下表是控制台断言数量，不是 xUnit case 数；不同运行中的重叠检查不累计为新增测试。

| 入口/阶段 | 嵌入式 | REST | frame-http2 | 每个进程的断言组成 |
| --- | ---: | ---: | ---: | --- |
| ADO exercise | 346 | 346 | 346 | #263 原始合同 336；额外 UInt64 10 |
| ADO reopen | 168 | 168 | 168 | #263 原始合同 168 |
| Chloe exercise | 612 | 612 | 612 | #263 原始合同 588；ORM 对照 12；额外 UInt64 12 |
| Chloe reopen | 283 | 283 | 283 | #263 原始合同 276；ORM 对照 7 |

远程每个协议在 exercise 完成后终止自有 Server，使用同一数据目录启动新 Server，再运行两个入口的 reopen。四个 Server 进程均有 PID、创建时间和退出收据，均由 harness 主动终止，exit -1；不称为优雅停机，也不是任意故障点注入。

| Kestrel 实际数据请求 | exercise | reopen | 解释 |
| --- | ---: | ---: | --- |
| REST HTTP/1.1 | 393 | 122 | 全部为 REST 数据请求 |
| h2c HTTP/2 Frame | 122 | 122 | 真实 `/v1/frame` 查询 |
| h2c HTTP/2 REST | 271 | 0 | DDL/DML/事务等 REST 请求；不冒称 Frame 写入 |

请求计数来自实际 Kestrel 日志，只统计目标数据库及 Frame 路径，不含健康探测和创建数据库的管理请求。Frame/REST 的 exercise 与 reopen 均独立断言实际 HTTP 版本；h2c 明确要求观察到 Frame 请求。

## 保留的失败与 UInt64 限制

1. `run01` 的首次嵌入式运行成功：ADO 345/168、Chloe 608/283 断言。其 UInt64 控制与最终探针不同，结果保持历史身份，不与 final01 叠加。
2. `remote01` 的 ADO REST exercise **exit 1**：准备阶段未先创建 `integer_bindings`，Server 返回 HTTP 404 `db_not_found`。harness 随后增加认证的创建数据库操作；原失败日志和 Server 退出收据保留。
3. `remote02` 的 ADO REST exercise **exit 1**：12 个输入的 #263 核心已完成 336 条断言，随后额外 `(ulong)long.MaxValue` 控制触发 NotSupportedException。**整个原始进程仍为 FAIL**，没有因核心通过而改写为完整 PASS。
4. 固定公开 `.5` 的 `ParameterBinder` 没有后续源码中的范围内 ulong 分支。源码 `89674db0` 后来的支持不能反推到公开包。final01 将这个已核实的旧包行为作为单独边界记录，不将“预期拒绝通过”表述成 UInt64 能力完成。

| UInt64 原始参数 | 嵌入式公开 `.5` | 远程公开 `.5` |
| --- | --- | --- |
| `(ulong)long.MaxValue` | 精确返回 Int64 最大值 | NotSupportedException，范围内原始 UInt64 仍不支持 |
| `ulong.MaxValue` 及其枚举 | OverflowException | NotSupportedException |

Chloe 包装为 ChloeException 时，探针核验内部异常类型并保留异常链。UInt64 是独立对照，不属于 #263 明确要求的三个小整数类型。中文异常消息在原始子进程日志中存在编码替换；类型、调用栈、HTTP 状态和断言不受影响，未改写原日志。

## 复现与归档

源码入口为 `scripts/validation/issue263-public-package/Run.ps1`；完整调用：

```powershell
pwsh -NoProfile -File scripts/validation/issue263-public-package/Run.ps1 `
  -EvidenceDirectory D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue263-work/final01 `
  -RuntimeDirectory D:/sndb-t/i263-final01 `
  -Proxy http://127.0.0.1:7890 `
  -ServerDirectory D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue271-work/final-public/remote/server `
  -ServerSourceCommit 285186b868a59d4b22e47279ea51deb38a3a1f45
```

这些目录已保存本次证据；复现须改用新目录，脚本拒绝覆盖。所有构建、clone、缓存和运行产物位于 D 盘，不纳入 Git。临时 bearer 只存在进程内存/环境，收据不保存认证值。

对应机器清单为 `docs/audits/issue-263-public-package-20261016.json`，记录九份运行源码、文档、包、运行二进制、assets、命令、日志、进程退出及在线快照的 SHA256。九份运行源码在 final01 前统一为 UTF-8 无 BOM、LF、单尾换行，与实际运行复制件一致；README 和本报告在运行后新增，仅作为文档，不伪称已编译输入。三个 PowerShell 源文件解析无错误。Server 完整 541 文件快照及其哈希保存在本地证据并由清单绑定。

本任务没有修改生产代码、文件格式或依赖，没有运行 Native AOT、完整 solution 回归或重新宣称旧测试通过。最终仓库 restore / Format Check 与独立提交由监督主智能体整合后执行；本切片未自行提交、推送或外部回复。

## Issue 关闭条件

本次保存的在线快照显示：#263 OPEN；NuGet 的 SonnetDB / SonnetDB.Core 最新稳定版仍为 4.0.0；最新稳定 Server release 为 `v4.0.0`（2026-09-26T18:02:26Z）。Workbench 预览不是新的稳定 Server 发行。Chloe PR #435 为 OPEN Draft、未合并，head 为上述固定提交，其原始验收依赖 SonnetDB 4.0.0；NuGet `chloe.sonnetdb` 索引返回 404。

关闭前仍须：正式发行包含 #263 修复的驱动和 Server；发布/合入对应 Chloe 正式消费者；使用正式发行组合重跑原始 DbParam、Session / SqlQuery 的完整极值、NULL、枚举、批量、事务和重开矩阵。本次公开预览客户端与 Chloe 固定源码的成功减少了消费不确定性，但不能替代这些正式发行条件。
## 主监督者复审

根按冻结清单核12份交付文件，审查ADO原始参数引用/DbType保持、Chloe DbParam/ExplicitParameter与拦截器、精确极值/枚举/NULL/批量/事务和独立进程重开；复核136份证据文件、18份运行二进制SHA与12份最终消费者退出收据。断言数量、独立消费进程数量及UInt64额外控制分列，原remote02完整失败未改写。

本轮未重写生产绑定或修改外部Chloe源码。完整solution restore与规定Format Check实际通过后独立提交，随本批推送；正式发行与Chloe正式消费者条件仍未满足，Issue继续OPEN。上一批270～272的远端CI另行跟踪，不以本次候选证据替代。
