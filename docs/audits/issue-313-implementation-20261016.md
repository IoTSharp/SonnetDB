# GH-Issue #313 measurement 数值 FIELD 有界更新

日期：2026-10-16（Asia/Shanghai）。实施基线 `89674db0`，隔离树 `C:\Users\mysti\.codex\worktrees\issue313-measurement-update\SonnetDB`。本报告只记录源码与本地测试，未提交、推送、发包、回复或关闭 Issue；交由根智能体审查整合。

## 合同

- 已声明的单个 INT/FLOAT FIELD 可赋数值字面量或参数，允许一元负号。INT 只接受 Int64 整数且不经 double；FLOAT 接受整数、浮点、Decimal 并转换为有限 double。赋值不允许 NULL、字符串、向量或依赖原行的表达式，不修改 time/TAG，不自动增加列。
- 必须显式 WHERE；仅支持 AND 连接的 TAG 字符串等值和 time 的 `= / < / <= / > / >=` 范围，复用已有时间表达式解析（Unix 毫秒、duration、受限 now 算术），允许仅 TAG 或仅 time。OR、NOT、FIELD/Geo 谓词、`time !=`、JOIN/FROM、RETURNING 和轻事务保持拒绝。空/矛盾时间窗或矛盾 TAG 保持现有明确诊断，不解释为全表更新。
- 同一 `(series, time)` 是一个逻辑目标；同键旧物理重复点替换后只返回一个替换值，受影响行数记 1。不同 series 的相同 time 分别计数。同值重复 UPDATE 仍返回命中逻辑行数。
- 如果命中逻辑行在其它 FIELD 有样本而目标 FIELD 缺失，整个语句拒绝；不新增稀疏字段。无匹配样本返回 0。其它字段和未命中时刻保留。
- 一次最多 256 个目标，现有全库替换记录总上限 4096、估算内存上限 128 MiB；超限先拒绝再写 WAL。上限针对受影响行和替换存储，不是候选扫描、series 数量、重复物理点或其它稀疏字段扫描的时间预算。
- 替换之后同一 `(series, field, time)` 的 INSERT 明确拒绝；名称规范化后在 schema 规划阶段预检，防止失败 INSERT 先发布扩列/INT→FLOAT 提升，批量同一规划块也先完整预检。其它时刻的正常写入仍允许既有 INT→FLOAT 演进；原 Int64 替换字节与原始点读取保持精确，既有数值聚合仍返回 double。

## 原子性与读路径

`MeasurementVectorUpdateExecutor` 保留既有名称并扩展数值绑定；在相同 schema、maintenance、write 锁内冻结目标、完整验证并执行一次内部 KV 原子批次。先同步原时序 WAL，再写替换 WAL，成功后一次发布不可变 snapshot。提交前预算/类型/谓词错误保留原数据；WAL 同步结果不确定则封禁读取/更新/写入并要求重开，恢复后只能看到整个已落 WAL 批次。

复用 `MeasurementVectorReplacementStore`，无需改动 `QueryEngine`：原始点与 latest 读取已有覆盖层；`ShouldUsePointAggregatePath` 将普通/多聚合和并行聚合导向替换后的点；`TryAddExtendedAggregateSketches` 在该 series/field 有替换时拒绝旧 sketch，回退逻辑点扫描。FIELD 残差过滤也读取逻辑点。数值写入不修改 time、TAG、series 标识或原段索引。Flush、compaction 保留原段和独立替换记录，DELETE/drop 清理既有替换键；数据库备份含内部 KV，因此恢复同样读取覆盖值。带 HNSW 的其它 VECTOR FIELD 保持原索引和查询合同。

## 兼容性

仅内部 `measurement-vector-replacements` KV 的 value envelope 增加 v2：`[version=2][FieldType=Int64/Float64][8-byte little-endian value]`。旧 VECTOR v1 编码保持字节不变，v1/v2 可混存、重开、备份恢复；schema 验证允许与原时序层一致的 Int64 值与已提升 Float64 schema 兼容。不修改 `FileHeader`、BlockHeader、原时序段、时序 WAL 或公共协议。旧引擎既有版本校验会拒绝 v2，不能直接降级读取包含数值替换的目录；回退使用更新前备份。

## 验证

- Core Release 构建 `dotnet build tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release -m:2 --no-restore`：0 warning / 0 error。
- Server Release 构建 `dotnet build tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release -m:2 --no-restore`：0 warning / 0 error。初次隔离树缺少两个 git 子模块，按仓库既有 SHA 初始化后完成；未修改依赖声明。
- Core 过滤 `SqlMeasurementNumericUpdateTests|SqlVectorParameterTests`：43/43、0 skip。覆盖原示例、其它字段/样本保留、256/257 与 4096 边界、稀疏/NULL/谓词/类型失败、重复点、Int64 超 2^53/最小值、最大 time、大小写引用、聚合/sketch、v1/v2 混合备份及 HNSW、DELETE/drop、compaction、同键 INSERT 提前拒绝、schema 演进、WAL 预算失败和同步故障封禁/恢复。
- 真实传输过滤 `RemoteMeasurementNumericUpdateTests|RemoteVectorParameterTests`：9/9、0 skip（新六组合与既有向量三项）。新测试覆盖 embedded / REST / frame-http2 × 同步/异步六组合，检验字面量/参数/Decimal 转 FLOAT、Int64 精度、失败后数据和影响行数；记录服务器实际 HTTP 协议及 frame 请求，不以 mock 代替网络。
- 结果位于忽略目录 `artifacts/issue-313/`：`core-build-reviewed.log`、`server-build-reviewed.log`、`core-reviewed.log/.trx`、`remote-reviewed.log/.trx`。首轮 `core-numeric-update.trx` 保留两项测试预期错误（ORDER BY 缺 time 投影及诊断子串），修正后最终 43 全通过；不累计重叠运行。

## 仍未完成

根已按 SHA256 核验冻结文件后集成到含 #314 的主树；两个 Release build 均零警告/错误，Core 117/117、Server 16/16，均零失败/跳过，覆盖 measurement schema、旧 VECTOR、#314 舍入及真实传输组合。结果与子树43/9有重叠，不累加。根证据位于 `artifacts/issue-supervision-20261016/issue313/`，含源哈希、构建、TRX及最终完整 restore/Format Check 收据；只有规定两命令实际 exit 0 后执行本次独立提交，SHA 以 Git log 为准。本批尚未推送。

尚未运行完整 suite、完整 solution restore/Format Check、新的 NativeAOT publish、远端 CI 或正式发包。没有新增 JSON、反射序列化、第三方运行时依赖或 unsafe。根智能体提交前仍须在最终整合树执行规定 restore 与 Format Check。

Issue 最后一项要求首次可安装版本和 FreeSql Provider 现有更新入口实际回归，目前没有这两项证据，不能以本地源码/传输测试关闭整个 #313。未声称提供跨查询快照、全模型事务、无界批量更新或全实例 MQ 备份。
