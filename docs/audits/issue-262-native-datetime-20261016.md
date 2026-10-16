# #262 原生 DateTime 结果合同与验证记录

日期：2026-10-16，Asia/Shanghai。实现基线：`285186b868a59d4b22e47279ea51deb38a3a1f45`。此报告记录候选源码实现和真实消费者验证；提交、推送、Issue 回复及关闭由根监督代理按最终主线和原验收要求执行。

## 结果

嵌入式日期表达式现在具有静态 DateTime 声明；双方确认 SQL 结果版本 4 后，REST 与实际 h2c 保留同一个 CLR DateTime 的钟面 ticks 和 Kind。空结果及全 NULL 结果无需读取首行即可得到 DateTime 类型元数据。`DATE_ADD` 不再因 C# switch 分支的共同类型推断而把 DateTime 隐式提升为 DateTimeOffset。

本地最终 Server 组合 **152/152**、Core 组合 **91/91** 通过，零失败/跳过。根监督代理独立复审集成的更大集合为 **Core 164/164、Server 189/189**，三个 Release 构建零警告/错误；这些集合有重叠，不累加。真实 Chloe 原始适配器每协议 5 通过/9 失败，六文件候选补丁后三协议 **42/42** 通过。Server/Data 各一次最终 AOT 分析强制重编译零警告/错误，未进行 NativeAOT 可执行文件发布。

正式可安装的 Server、驱动、Chloe 发布组合仍待发布后验收。本次没有发布包、外部适配 PR、部署或关闭 Issue。

## 先证明原问题

原源码上新增日期合同测试为 **6 失败/3 通过**；筛选同时包含的两个既有参数比较测试另为 **2/2**，因此原始 TRX 总计为 11 项、5 通过/6 失败，不能把 11 项全部称为新增测试。

实际进程时区为 `China Standard Time`，偏移 `+08:00`：

| 原入口 | 原始观察 |
|---|---|
| 嵌入式 | 日期函数值和 Kind 正确，但执行前 `GetFieldType` 为 Object，空行/NULL 声明缺失 |
| REST | 日期函数投影为字符串，声明为 Object，不能与嵌入式原生日期值一致 |
| h2c | Local 午夜 `2024-02-29T00:00+08` 变成 `2024-02-28T16:00Z`；Unspecified 被赋予 Utc |
| 原生参数/持久化重开 | 三入口均符合既有 UTC Unix 毫秒合同，此部分无须改变 |

原始日志和 TRX 保存在 `artifacts/issue-supervision-20261016/issue262-work/artifacts/checks/baseline-tests.*` 与 `baseline-results/baseline.trx`，同名审计 JSON 保存其 SHA256。

## 结果版本 4

协商头为 `X-SonnetDB-Sql-Result-Version: 4`。这是一项 **SQL 结果能力**，不是文件存储版本，也不改变 `FrameHeader.CurrentVersion = 1`。

### Frame

- 新 meta 的声明版本标记为 **3**，列声明字段沿用结果 v3 的完整布局；此标记明确确认 native DateTime 能力。
- 新值类型 tag **10**：`Int64 little-endian wall-clock ticks + UInt8 Kind`。Kind 只能为 `0=Unspecified`、`1=Utc`、`2=Local`。
- 解码器必须已从 meta 确认该能力后才允许 tag 10。ticks 超界、非法 Kind、截断、未知 tag 均拒绝。
- 不带新参数的旧 codec 重载仍产生原编码；旧 Timestamp tag **6** 仍表示 UTC ticks。DateTimeOffset 仍走其原 UTC timestamp 路径，不获得 offset 保真承诺。
- 客户端首先请求 4，只有收到完整可信的单个 SQL Query 错误帧 `unsupported_result_version`、确认在执行前拒绝后才尝试 3，再尝试 2。错误帧的 service/op/stream/flags/version 均核对。
- 已执行的 Frame 响应若缺少约定能力确认则明确失败，不能自动重发 SQL。日期测试也覆盖 SHOW/DESCRIBE TABLE/EXPLAIN，避免非 SELECT 数据面因新增版本失效。

### REST

成功协商后，日期值写成手工 JSON 对象，例如：

```json
{"$type":"datetime","ticks":638447616000000000,"kind":0}
```

Server 在执行及写出结果前确认版本；客户端只在响应头确认 4 时按该对象解码。写出使用 `Utf8JsonWriter`，读取使用已有 JSON 元素访问，不回退到反射序列化。未知请求版本在执行前拒绝，覆盖普通查询、写入及 batch。

旧客户端未请求 4 时仍收到旧日期值的 ISO JSON 编码；旧 Server 未回显 4 时，新客户端保持旧解析，不宣称 v4 保真，也不重发写请求。新增静态日期声明与运行时 `datetime` 类型码会改善旧 REST metadata，因此 **不承诺所有旧 REST 响应逐字节不变**。

事务 session、batch preview 也使用一致的日期解码。旧无 session 的事务预览保留列声明，包括全 NULL 和零行；Frame 协议连接中的事务/写控制面仍按既有路径走 REST，不冒称所有请求都使用 h2c 行帧。

## 日期语义和兼容边界

| 边界 | 本次合同 |
|---|---|
| 内存日期表达式及 v4 行值 | 原 DateTime 的 `Ticks` 与 `Kind` 原样传递，亚毫秒 ticks 不丢失 |
| `CURRENT_DATETIME` / `.Date` / `.AddDays` | 保持产生进程的 Local 钟面与 Kind，不因客户端时区换算 |
| `CURRENT_UTC_DATETIME` | Utc 钟面与 Kind |
| Unspecified | 保持 Unspecified；不暗自解释成本地或 UTC |
| Local 跨时区传输 | 仅保留钟面 ticks 与 Local 标记；不携带源时区、UTC offset 或 DST 歧义标志。接收方再调用 `ToUniversalTime` 时仍受自身时区影响。跨区域瞬时应使用 UTC |
| 原生 DateTime 参数绑定 | 仍为 UTC Unix 毫秒；Local 转为对应 UTC 瞬时，Unspecified 按 UTC，亚毫秒按原逻辑截断。`SELECT @stamp` 仍是 Int64 |
| 持久 DATETIME 列 | 仍按 UTC Unix 毫秒存储和重开，读出的 Kind 为 Utc；不是 ticks+Kind 持久格式 |
| DateTimeOffset | 本合同范围外，不保证 offset 与 CLR 类型跨入口一致 |
| 旧 Frame v1-v3 / 旧 codec | 保持既有值编码及 UTC 语义；兼容证据包括金样本、旧解析路径和真实端点降级，不等于正式旧发行包完整矩阵 |
| 物化视图快照 | 独立的 `MaterializedViewSnapshotCodec` 已有版本 2 的 ticks+Kind 路径，不使用 SqlFrameCodec；本次不改其格式 |
| Vector Frame | 沿用旧默认 codec 重载，未开启 native DateTime 编码；相关回归通过 |

静态日期声明仅用于已知内建函数及合法参数数量：CURRENT_DATETIME、CURRENT_UTC_DATETIME、DATE_ONLY、TO_DATETIME、TO_UTC_DATETIME、TO_LOCAL_DATETIME、DATE_ADD_DATETIME，以及输入静态确定为 DateTime/Int64 的 DATE_ADD。保留可空性与刷新标记，不把用户覆盖的函数错误宣称为内建日期返回。

## Chloe 消费者

从 Chloe 固定 SHA `bb7ec8735bd4511c35453eb009f70cc606795703` 建立干净归档，未包含其他 Issue 的 DECIMAL 补丁。原始消费者实际加载的程序集及所有断言写入日志；归档、原始/候选 DLL 和补丁 SHA 见 JSON。信息版本字符串不替代固定源码身份。

原始 Chloe 在三协议各 **5 PASS / 9 FAIL**：外层 `TO_DATETIME` 抹去 Kind；Local 参数被改标为 Unspecified。原始 Local equality 还暴露了两条本应不同的时刻被写成同一时刻的问题。总计 **15 PASS / 27 FAIL**，不是 engine 基线测试的同一集合。

候选仅改 Chloe 六文件：四个 Now/Today/UtcNow/Date handler、SqlGenerator_Helper 的 DATEADD、Utils 的参数规范化。删除冗余 `TO_DATETIME` 并保留 Local 参数，让既有原生 binder 转换其 UTC 瞬时。以 `+08:00` 的 Local `2024-02-29 00:30` 为例，旧适配器存成 `2024-02-29 00:30Z`，候选存成 `2024-02-28 16:30Z`。这是有意的 **Chloe 参数行为修正**；旧数据不会自动迁移，原有亚毫秒参数保护与 DateTimeOffset/TimeSpan 限制不变。

候选三协议各 **14/14**，覆盖实际 Context 插入、异步插入、存储 UTC/Local/Unspecified、日期投影/闰日跨午夜/嵌套、NULL、空结果、Now/Today/UtcNow、Local 参数比较和原生毫秒参数边界。所有日期值比较同时检查 ticks 与 Kind。HTTP Server 是实际独立进程，REST 和 h2c 使用不同回环端口，退出身份与日志已保留。

`eng/probes/issue262-datetime-consumer/chloe-native-datetime.patch` 已在六份干净源码上执行 `git apply --check` 与实际 apply，结果包括换行符在内逐文件等于编译过的候选；补丁保留上游 CRLF，目录内 `.gitattributes` 将其作为原始补丁字节保存。复现说明及有界 runner 一并交付。补丁尚未发布到 Chloe 仓库。

## 最终验证与未通过尝试

| 验证 | 最终结果 | 证据标签 |
|---|---|---|
| Server Release 构建 | 0 警告/0 错误 | `final-server-build-2` |
| Core tests Release 构建 | 0 警告/0 错误 | `final-core-build` |
| Server/Frame/Vector/HTTP2 组合 | 152/152，0 失败/跳过 | `final-server-152-tests` / `v4-results/final-server-152.trx` |
| Core codec/date/MV/metadata 组合 | 91/91，0 失败/跳过 | `final-core-tests` / `v4-results/final-core.trx` |
| Server AOT/trim 分析强制重编译 | 0 警告/0 错误 | `final-server-aot-recompile` |
| Data AOT/trim 分析强制重编译 | 0 警告/0 错误 | `final-data-aot-recompile` |
| 原始/候选 Chloe 最终构建 | 各 0 警告/0 错误 | `chloe-original-annotations-build` / `chloe-candidate-build` |
| 原始/候选消费者最终构建 | 各 0 警告/0 错误 | `consumer-original-build-3` / `consumer-candidate-build` |
| 根复审三个 Release 构建 | 各 0 警告/0 错误 | `issue262/root-review/build-*.log` |
| 根复审 Core / Server | 164/164 与 189/189，0 失败/跳过 | `issue262/root-review/results/core.trx` / `server.trx` |

各命令实际参数、开始时间、退出码和日志 SHA 见同名 JSON。AOT 最终命令为 `dotnet build <project> -c Release --no-restore --no-dependencies -t:Rebuild -nr:false -p:EnableAotAnalyzer=true -p:EnableTrimAnalyzer=true`，依赖此前已构建。仅据此宣称两个修改边界的 AOT 分析通过，**不是 NativeAOT publish 或运行验证**。

以下探索失败保留，不能改写为通过：

- `v4-build-1-interrupted.txt`：runner 清理时子进程已退出，结果收据丢失；修正仅捕获已退出进程身份竞争，丢失构建不计通过。
- `v4-build-2`：JSON SerializeAsync 调用编译错误，后续改为明确 source-generated 路径。
- `v4-tests-1`：3 个失败发现通用 DATE_ADD 的隐式 DateTimeOffset 提升，随后修复并完整重跑。
- `final-server-tests`：151 通过/1 失败，测试写错 `DESCRIBE name`（measurement 语法）；改成 `DESCRIBE TABLE` 后最终 152/152。
- `final-aot-analysis`：全依赖分析产生 2 个源生成器 netstandard 子模块的 NETSDK1210 警告，无 IL 警告；保留原结果，后用上述精确边界强制重编译取得零警告。
- `chloe-original-build`：外部源码原配置出现 38 个 CS8632；为原始和候选统一设置 `Nullable=annotations`，最终均零警告，无 pragma/NoWarn 压制。
- `chloe-original-embedded` 是首轮探索，预期失败数写成 8、实际为 9；最终三协议原始矩阵 `chloe-baseline-*` 严格记录 9 失败，候选 `chloe-candidate-*` 严格零失败。

修改文件的局部 whitespace format 通过；子代理未执行最终主工作树的完整 solution Format Check。根监督代理须在最终集成树提交前实际完成仓库规定的 `dotnet restore SonnetDB.slnx` 和 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`，不可用本报告代替。

## 交付和后续

生产/测试 13 文件冻结清单为本地 `artifacts/issue-supervision-20261016/issue262-work/production-delivery-manifest.json`；最终交付清单另含探针和本报告。同名审计 JSON 保存冻结源码、TRX/收据/日志、源归档、探针、补丁、原始/候选消费者和 Server DLL 的 SHA256。

根代理负责共享 HANDOFF/CHANGELOG、最终门禁、单任务提交以及批次推送和 Issue 核对。本子代理不提交、不推送、不改共享交接文件。生产/测试 13 文件冻结后由 #261/#260 按根协调继续扩充共享声明文件；本清单对应 #262 独立冻结版本，不能覆盖后续任务的增量。

下一步是根最终集成门禁和独立提交，随后补正式 Server/驱动发行与 Chloe 适配发布验收。正式包矩阵、跨操作系统/时区部署、NativeAOT 发布运行、性能及长稳不在已验证结果中。

## 主监督者最终复审

根已逐SHA核25份冻结交付和185个绝对路径证据资产，直接解析5份TRX核对基线失败及最终计数；复审Frame协商、REST日期信封、事务声明、日期推断、Chloe六文件补丁及其实际最终值。根三个Release构建零警告/错误，Core164/164、Server189/189均零失败/跳过，原始记录在artifacts/issue-supervision-20261016/issue262/root-review/。

集成时仅将审计JSON的CRLF转换为LF以通过暂存差异检查，数据和所引用证据未变；原冻结JSON保留，原/新SHA映射见root-review/line-ending-normalization.json。Chloe统一补丁保留其CRLF及上下文空白，专属.gitattributes只针对.patch字节和空白检查，生产源码及完整solution Format Check要求未改变。最终完整restore/Format实际通过后才执行本Issue独立提交，门禁收据位于issue262/final/；GitHub推送/状态由监督回执记录。
