# SonnetDB 4.5 — 聚合与持续计算深化

规划基线：2026-10-03，本地提交 `4b004946`。本文对应根 [ROADMAP](../../ROADMAP.md) 的 **M45-C01~C10**；C01~C09 为 4.5 必选，C10 为 P2 条件项。本文是设计与验收计划，新增合同、SQL 支持和性能改进均未据此宣布实现或发布。

目标是在现有九模型底座上形成一致的批量分析、增量聚合、可恢复持续计算和 AI 特征计算能力。设备分析是重要负载，同时覆盖业务指标、文档分类、状态统计及应用事件；参考其它产品的机制后，按 SonnetDB 的类型、事务、存储和嵌入式边界独立实现。TsFile 不纳入本版适配或格式方案。

## 1. 当前事实与复用边界

| 已有范围 | 源码或合同 | 本版增量与仍需保留的边界 |
|---|---|---|
| 关系 GROUP BY、HAVING、标准聚合、单字段 DISTINCT、精确 Int64/Decimal 及关系窗口 | [关系执行器](../../src/SonnetDB.Core/Sql/Execution/RelationalSelectExecutor.cs)、[关系窗口](../../src/SonnetDB.Core/Sql/Execution/RelationalWindowExecutor.cs)、[SQL 合同](../sql-reference.md) | 不重新建设基础分组。关系窗口已有默认 RANGE/peer 语义，但仍有物化与前缀复算成本；JOIN 与聚合/窗口同层组合存在限制。 |
| measurement 标准、分类、扩展聚合和 `GROUP BY time(duration)` | [measurement 执行器](../../src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs)、[函数注册](../../src/SonnetDB.Core/Query/Functions/FunctionRegistry.cs) | TAG/普通列分组尚未成为当前合同。C01 优先补 TAG＋时间分组，复用原名、类型和稀疏行规则。 |
| 可合并累加器、Welford、TDigest、HLL、段聚合 sketch 与下推 | [累加器接口](../../src/SonnetDB.Core/Query/Functions/IAggregateAccumulator.cs)、[扩展聚合](../../src/SonnetDB.Core/Query/Functions/Aggregates/ExtendedAggregateFunctions.cs)、[QueryEngine](../../src/SonnetDB.Core/Query/QueryEngine.cs) | 当前接口有 Add/Merge/Finalize，没有统一 retract。接口 XML 将 `A+B == B+A` 称为结合律，并要求 merge 幂等；COUNT/SUM 的累加合并不能按这一要求解释。C02 分开数学合同和输入去重。 |
| 差分、increase/rate/irate、积分、累计、平滑、LOCF/interpolate/fill、状态/轨迹函数 | [窗口函数注册](../../src/SonnetDB.Core/Query/Functions/FunctionRegistry.cs) | 已有函数不再包装为新增；C07 补时间边界、批流 state、counter reset、缺失跨度及对拍。 |
| 逻辑视图、显式全量刷新物化视图、代际发布与中断恢复 | [物化视图管理器](../../src/SonnetDB.Core/Views/MaterializedViewManager.cs)、[物化合同](../sql-reference.md) | 当前 REFRESH 是全量路线；C04 在有限 SQL 子集上增加增量维护与 rollup，不自动把任意 SELECT 改成增量执行。 |
| GROUP BY/排序等阻塞算子 spill、查询准入和本地资源合同 | [阻塞算子回归](../../tests/SonnetDB.Core.Tests/Sql/SqlBlockingOperatorSpillTests.cs)、[关系执行器](../../src/SonnetDB.Core/Sql/Execution/RelationalSelectExecutor.cs) | M41/M42 已有机制继续复用；C08 为持久聚合状态和恢复收费，接 V45-X02，总 heap/进程工作集仍需独立验证。 |
| 持久滚动/滑动 COUNT、decimal 数值与字符串分组、会话 COUNT、水位、drop/reject | [窗口合同](../../src/SonnetDB.Core/Streaming/StreamingWindowContracts.cs)、[文件窗口](../../src/SonnetDB.Core/Streaming/FileStreamingWindowAggregator.cs)、[会话合同](../../src/SonnetDB.Core/Streaming/StreamingSessionWindowContracts.cs) | 不是空壳。当前数值属性缺失/null/非法值拒绝整批，与 SQL 忽略 NULL 不同；会话窗口也不等于已有全部数值/撤回能力。 |
| 持久订阅、checkpoint、任务目录、暂停/恢复、条件重试、DLQ、outbox、有界多分区桥接 | [任务](../../src/SonnetDB.Core/Streaming/FileStreamingTaskRunner.cs)、[CDC 桥接](../../src/SonnetDB.Core/Cdc/CdcStreamingBridge.cs)、[多分区拓扑](../../src/SonnetDB.Core/Cdc/CdcStreamingBridgeTopology.cs) | M43 管运输、位点和任务生命周期；多分区各自提交，当前不表示跨分区原子快照、远程接管或业务副作用 exactly-once。 |
| 文档原生 pipeline 与 SQL `json_value` 分组 | [SQL 文档聚合合同](../sql-reference.md) | 基础聚合已存在；进入增量计算需要固定 schema/转换、稳定实体身份及真实 before/after 合同。 |

本轮仅进行文档整理和源码复核，未执行构建、目标机性能测试、真实服务或长期运行。既有证据仍按对应报告范围使用；实现存在不升级为生产验收。

## 2. 代表产品研究与采纳判断

以下按产品家族覆盖代表产品，避免把“流行产品”理解为穷尽全部数据库或承诺全方言兼容。官方资料反映产品设计，未构成与 SonnetDB 同机器、同持久性、同语料的性能胜负。

| 家族 / 产品 | 值得学习的机制 | 对 SonnetDB 的采纳与取舍 |
|---|---|---|
| PostgreSQL / MySQL | 聚合返回类型、NULL/空集、DISTINCT、有序集合及窗口语义 | C01 用标准语义复核现有合同；保留 SonnetDB Int64/Decimal 类型和明确错误。MySQL 本轮官方正文受 robots/403 限制，其特定细节保持待核，不当作设计结论。 |
| DuckDB | 批执行、丰富聚合、顺序敏感性及近似统计 | C02/C06 将 state 能力写清，C08 衡量嵌入式工作集；函数数量不作为完成条件。 |
| ClickHouse | AggregateFunction state、State/Merge 组合与预聚合 | C02/C04 使用可合并中间状态和多级 rollup，避免“均值的均值”；不直接复制函数后缀或存储类型。 |
| TimescaleDB | continuous aggregate、时间桶、回填/刷新、时间加权与 counter 分析 | C04/C07 借鉴失效范围与边界 state。真实数据和已物化数据如何组合必须声明，不将查询补偿与持久结果混淆。 |
| TDengine | 超级表/设备维度、持续聚合、时间/状态窗口、WATERMARK/IGNORE_DISORDER/EXPIRED_TIME/DELETE_RECALC/FILL_HISTORY 等策略 | C01 扩展 TAG 分组为通用维度分析；C05 先复用已交付时间窗口。学习删除重算/历史衔接时保留通知重启可能重复的合同，其它 window 家族逐项按 C10 条件评审。 |
| IoTDB | 设备 Tree/Table 建模、时间统计、跨设备分析与窗口；Table 写入的部分列 NULL 与 attribute-only UPDATE 边界 | V45-X08 使用已有 measurement 与关系实体定义模板/静态属性；M45 负责类型一致的跨实体分组。摄取更新不能泛化为任意行 before/after，不增加第十模型或改造持久格式。 |
| InfluxDB | 按任务下采样、聚合/窗口与补算 | 借鉴调度和数据生命周期；v2 Flux task 与不同 InfluxDB 版本的计算路径分别核查，不混称同一能力。 |
| QuestDB | SAMPLE BY、时间对齐/填补、ASOF JOIN | C07 明确桶边界、时间加权和不规则采样；ASOF 为 C10 条件项，不能先承诺低成本任意时间 JOIN。 |
| VictoriaMetrics | 流聚合、降采样及内存/持久性取舍 | 其流聚合默认使用摄取时间，内存 state 重启丢失；可学习低成本状态管理，不能据此推导持久 event-time/retract 合同。 |
| DolphinDB | 流计算引擎、窗口、历史与实时分析协作 | 学习运算层与调度分层；商业产品的完整能力不等于开源引擎已具备或本版承诺。 |
| MongoDB / Redis TimeSeries | 文档 pipeline、预计算视图；时序 compaction rule/聚合 | 文档输入先归一为固定类型；KV/缓存服务热状态，MQ 服务增量触发。明确刷新及持久性范围，不声称跨模型事务。 |
| Flink | 动态表、changelog、retract、event-time/水位、checkpoint | C03/C05 从追加事件升级为可证明等价的表变更；M43 继续运输，M45 定义计算与检查点一致性。 |
| Materialize / RisingWave | durable 增量物化、变化驱动、状态与恢复 | C04 选择有限可维护 AST。Materialize durable materialized view 与内存 view/index 有区别；其 v26.10 preview replacement 不纳入稳定采纳方案。 |

来源索引见第 10 节。具体版本、商业部署及许可在实现选型时再锁定，设计参考不构成引入运行时依赖或源码授权；TDengine AGPL 核心不可直接搬入后继续只标 MIT。

## 3. 首批支持矩阵：先证明等价，再扩展

下表描述 **拟冻结的首批边界**，不是当前产品能力声明。C01/C03/C04 开工前，应将每格绑定能力发现、计划阶段错误和定向测试。当前批查询已有更广范围，不因为增量子集缩小而撤销旧能力。

| 源 / 操作 | 首批批查询 | 首批增量维护目标 | 准入要求与拒绝边界 |
|---|---|---|---|
| 单关系表 | 复用现有 WHERE、投影、GROUP BY、HAVING | INSERT，以及有完整 before/after 的 UPDATE/DELETE | 稳定主键、已提交变更、schema 版本、事务边界；不能把未提交行投入 state。缺图像或日志缺口进入失效/重算或拒绝。 |
| 单 measurement | time 桶、TAG，多 TAG＋time；普通 FIELD 键逐类型冻结 | 首批明确追加子集；覆盖写/删除只有完成身份与修正合同才开放 | `count(*)` 保持 series×时间行身份及稀疏字段并集；range tombstone 不伪造逐点 before-image，必须有界重算失效桶或拒绝增量模式。 |
| 单文档集合适配 | 复用已有 pipeline/SQL 分组 | 固定投影 schema 且有完整变更图像的子集 | JSON 属性名保持 Ordinal，与 SQL 名称合同分开；缺失/NULL/转换失败各自处理。TTL 作为明确删除事件或使派生结果失效。 |
| 本地持久 MQ/订阅 | 将 payload 显式映射成已验证类型 | 首批追加、稳定身份去重；更新删除要求 typed changelog | 复用现有订阅、任务、checkpoint；至少一次投递，身份保留期有限且公开。普通 JSON 数值窗口旧合同不隐式改变。 |
| 图/向量/全文/对象/KV 派生输入 | 复用已有有界查询/导出入口 | 暂以版本化快照或应用适配输入，逐源评审 | 不把任意遍历、ANN 结果、对象变化变成强一致 CDC；所有输入附出处、版本、权限和恢复边界。 |

首批 AST 支持：单源、确定性已支持标量投影/谓词、明确列或 TAG 分组键、固定 UTC 时间桶、标准 COUNT/SUM/AVG/MIN/MAX，以及只依赖分组键和已支持聚合的 HAVING。CAST 和表达式先验证输入类型、NULL、溢出、版本及确定性；未声明支持的函数在计划阶段拒绝。

初始增量模式拒绝 JOIN、递归 CTE、任意子查询、外部/UDF、非确定性 now/random/模型调用、无界排序分页和跨数据库事务。逻辑视图展开、物化视图链、DISTINCT、FIRST/LAST、sketch 与复杂窗口只能在各工作包明确开放后使用，不能因为批 SELECT 接受它们而自动进入增量模式。GROUPING SETS/ROLLUP/CUBE、计数/状态/变化窗口及 ASOF 不默认进入必选。

示意目标查询，当前 measurement 执行器尚不支持这一分组组合：

```sql
SELECT "Plant", "DeviceType", time AS bucket,
       count(*) AS samples, count("Power") AS valid_power,
       avg("Power") AS mean_power
FROM "Telemetry"
WHERE time >= @from AND time < @to
GROUP BY "Plant", "DeviceType", time(1m)
HAVING count("Power") > 0
```

名称按 GH-Issue #211 绑定一次后使用原名；未加引号按 OrdinalIgnoreCase、双引号按 Ordinal、保留 `""` 转义。schema 命名空间禁仅大小写不同的冲突；分组数据值的比较仍遵守自身类型/collation，不因 SQL 名称大小写规则折叠。

## 4. C01/C02：类型、数学合同与版本化 state

| ID | 优先级 | 交付 | 依赖与退出 |
|---|---|---|---|
| M45-C01 | P0 必选 | 类型/NULL/空集/overflow/名称统一矩阵；measurement TAG＋时间分组、投影/HAVING 验证 | 复用 M31/M33 名称绑定与类型。所有入口结果元数据一致；未支持键/表达式在执行前拒绝，保持现有关系兼容。 |
| M45-C02 | P0 必选 | 分离 state 能力、Add/Merge/Finalize、版本/身份与有界编码 | C01；每种状态说明是否可结合/交换/有序/撤回及误差，未知版本拒绝或显式迁移。投递去重不伪装成 Merge 幂等。 |

需要冻结的合同：

- COUNT(*) 统计行，COUNT(expr) 忽略 NULL；SUM/AVG/MIN/MAX 的空集与全 NULL 使用现有 SQL 合同逐入口对拍。缺失 FIELD/JSON 属性不可未经声明折算为 `0`。空输入有分组返回零组，无分组的聚合结果按合同返回 COUNT=0/其它空值。
- `SUM(INT)` 继续保留 Int64，溢出明确报错，不转 double；Decimal 保持现有 `System.Decimal` 范围和舍入，schema 声明不代表新增任意精度实现。Boolean 数值转换、NaN/Infinity、字符串比较、FIRST/LAST 同时间戳选择分别记录，禁止通过统一 double state 丢失大整数或十进制精度。
- 结合律是 `(a⊕b)⊕c = a⊕(b⊕c)`，交换律是 `a⊕b = b⊕a`。SUM/COUNT 合并通常不幂等；合并重复输入会重复计算。只读 Finalize 可重复调用，与 Merge 幂等不是一回事。
- 浮点算术有限精度，合并顺序可能改变最后若干位。声明绝对/相对误差或确定顺序，不宣称所有 batch/stream/parallel 结果逐 bit 相同。
- 有限范围 checked 累加也不是无条件可结合的运算：不同分区/顺序可能在中间步骤溢出。C01/C02 明确有效域、顺序和报错点；无法证明保持旧行为时回退顺序执行。若另选扩大内部 state，必须作为明确兼容性决策验证，不能以优化名义静默改变既有错误合同。

state 元数据至少区分算法/版本、参数、输入/输出类型、比较规则、时间单位与边界、输入 schema 版本、来源绑定和计划指纹；有效载荷限制字节、元素和解码时间。SUM/COUNT、AVG 的 sum+valid-count、Welford、TDigest/HLL、FIRST/LAST 边界状态使用各自能力描述，避免所有聚合硬套一个接口。

state 持久化遵守 Safe-only、Core 零第三方运行时依赖、source-generated JSON 或显式二进制 codec、Native AOT。持久格式先版本化/CRC或校验和并确定旧 reader/writer 策略；不能偷用现有标志承载新语义。新增 public API 保持兼容并有中文 XML 文档。

## 5. C03/C04：变更修正与增量物化

| ID | 优先级 | 交付 | 依赖与退出 |
|---|---|---|---|
| M45-C03 | P0 必选 | typed before/after、retract、UPDATE/DELETE/TTL 与失效范围 | C01/C02、M43 源变更合同；按支持源证明离线重算等价，不完整变更拒绝或显式失效，修正失败不发布半份结果。 |
| M45-C04 | P0 必选 | 有限 SQL 的增量物化、rollup、历史回填与原子发布 | C03、M37 generation、M43 位点/任务；快照增量无缺口、重投无重复、日志缺口/依赖变更可恢复或重算。 |

变更输入包含稳定 source/entity/key、partition/offset、操作种类、before/after、schema/plan 版本和提交/批次身份。将 UPDATE 表达为旧值撤回＋新值加入；若 WHERE、分组键、时间桶或 HAVING 变化，必须撤回旧归属并增添新归属。日志顺序/身份与状态发布绑在同一已声明恢复边界，不能按 payload 相等丢弃本来不同的两笔业务事件。

| 聚合 | 撤回策略候选 | 必须付出的成本 / 拒绝边界 |
|---|---|---|
| COUNT、SUM、AVG | 维护合法行/非空数与精确和，按 typed delta 修正 | 计数不得变负；溢出、值域与重复撤回校验。AVG 不能只保存平均值或合并平均值。 |
| MIN/MAX、FIRST/LAST | 有界有序候选/多重集，或标记组/桶失效后有界重算 | 单个极值 state 不可逆；同值多次出现需 multiplicity，同时间戳需稳定身份/顺序。无保留数据不能假定恢复。 |
| DISTINCT / MODE | 有界值→出现次数 state，零次数移除 | 内存随基数增长；超过预算 spill 或拒绝，不换 HLL 后继续声称精确。 |
| Welford 等统计 | 校验删除算法数值稳定性，或有界重算 | 小样本、接近常数、大偏移与多次加删循环验证误差；不从可 Merge 推断可逆。 |
| TDigest/HLL 等 sketch | append-only 状态；删除时失效并重算，或拒绝增量模式 | 常规 state 不支持任意精确撤回；不通过“负权重”伪装通用删除。 |
| 积分/rate/有序函数/会话 | 修正相邻边界点或受影响区间，无法局部修正则有界重算 | 乱序插入、删除桥接点可能改两个区间甚至拆分会话，需明确最大重算跨度与成本。 |

增量物化的第一版采用有限定义与明确一致性点：先取得与源日志关联的快照边界，分页回填到候选 generation，再连续重放边界之后的变更，验证追平与完整性后发布。快照与 CDC 无法建立同一边界的源先拒绝此模式，不能边扫边订阅后声称无缺口。

回填可暂停/取消/续传，每批限制行、字节、时间和资源；checkpoint 包含 generation、源位点、回填游标和计划版本。源日志过期、schema/模板/函数参数改变、依赖对象替换、state 损坏时进入可观察失效/重建状态。旧活动代际在候选成功前继续保持有效；失败不能把半个刷新结果暴露为最新。物化依赖图必须有环检测、深度/任务数量限制与有界调度。

rollup 使用 state 而非只用最终值：平均值保存和/有效数；方差合并有效统计量；时间加权保留首尾点及覆盖时长；近似分位保持算法/参数一致。只有 bucket 对齐、源谓词和类型/NULL/时间边界等价的计划才可下推或复用已有 rollup；查询重写先作为独立有证据的优化，不随 C04 自动承诺。

## 6. C05/C06/C07：批流时间与统计深化

| ID | 优先级 | 交付 | 依赖与退出 |
|---|---|---|---|
| M45-C05 | P1 必选 | 统一 SQL 批流窗口、分区 event-time 进展、迟到/idle 与关闭策略 | C02/C03、既有 M43 窗口；同事件日志及相同策略下批流等价，重开不回退水位、不重复关闭。 |
| M45-C06 | P1 必选 | 精确/近似分位、distinct/sketch 的资源与误差合同 | C02/C08；现有 TDigest/HLL 复用，精确模式可排序/spill；偏态、merge、种子/哈希及误差可复算。 |
| M45-C07 | P1 必选 | 时间加权、率、积分、采样/填补的边界与 state | C01/C02/C05；核对已有函数后补切片，不把已注册 rate/integral/LOCF 重做。 |

时间合同以 UTC Unix 毫秒和 `[start,end)` 作为拟统一基础；固定长度桶沿用 epoch 对齐，负时间及加减溢出单独验证。现有入口若有 inclusive 上界或不同精度，转换须明确且兼容。日历/月桶、时区/DST、不同精度不自动加入基础子集。查询输出不会凭空创造无观测的桶，填补模式必须显式。

C05 的目标是把 SQL 绑定结果映射到既有窗口 state，允许明确选择 SQL NULL 策略，而不更改旧 JSON NumericField API 的整批拒绝语义。分区水位从有效 event time 与允许乱序策略派生，全局推进按活跃分区的安全边界处理；idle、恢复活跃、分区增加/重置及异常未来时间戳有独立规则。不能把分区位点追平当作事件时间关闭，也不能把墙钟作为未声明的代替。

已有 drop/reject 继续支持；4.5 对需要迟到修正的应用增加显式修订/失效重算路径，关闭结果携带 revision/finality，消费者能撤回旧结果。会话由迟到桥接事件合并，删除桥接事件可能拆分；C03/C08 负责保留或重算边界，unsupported 组合拒绝。滑动窗口重叠量继续有限，不能通过缩小 slide 制造无界 fan-out。

C06 区分精确 percentile 和近似 percentile/distinct 的语义、名称/能力及结果元数据。现有 percentile 家族经 TDigest 的路径必须保持其实际近似属性，不能因名字相同宣称精确。选择新算法先用偏态、重复值、离群值、小样本、排序/反序和多级 merge 证明收益；误差包括 rank/value 两种解释及尾部分位，HLL 则衡量基数误差。压缩参数、哈希/种子和 state 版本进入可复现记录。

C07 重点冻结：

- 时间加权采用 LOCF 或线性插值时的积分定义、覆盖时长、首尾边界点、无点/单点和最大插值跨度。缺失很长时间不能继续沿用末值冒称有观测。
- rate/increase 区分 gauge 与 counter、单位、counter reset/wrap、重复时间戳、零时间差及非单调值；不把业务金额误判为单调计数器。
- 积分使用的边界外点来自明确的有界取样，不能忽略窗口左/右端后仍声称连续覆盖。删除或乱序点需修正相邻贡献。
- fill/LOCF/interpolate 返回值可标识观测与推算来源；填补只作用于查询或明确派生结果，不修改原始缺失事实。参与 AI 特征时保持 point-in-time 边界，禁止未来样本泄漏。

## 7. C08/C09/C10：资源、验证与条件扩展

| ID | 优先级 | 交付 | 依赖与退出 |
|---|---|---|---|
| M45-C08 | P1 必选 | 聚合 state 预算、spill/checkpoint、恢复与公平准入 | M41/M42、V45-X02；state、窗口数/重叠、group cardinality、候选和回填都收费；取消/磁盘满/坏状态不损已提交 generation。 |
| M45-C09 | P0 必选 | 批流、内存/spill、跨端对拍、原始报告与观测 | 从 C01/C02 开始建设并贯穿；相同日志覆盖重投、late、update/delete、kill/reopen，精确与浮点/近似分开。 |
| M45-C10 | P2 条件 | 按真实查询选择 GROUPING SETS/ROLLUP、扩展 window 或 ASOF | C01~C09 满足适用前置后，单项设计/预算/差分收益决定。CUBE、count/state/change windows、任意 ASOF 不全部默认纳入。 |

资源计划复用现有治理器。登记每组 state 字节、哈希键、DISTINCT multiplicity、候选排序、sketch、窗口重叠、checkpoint 编码缓冲、回填页和结果队列；区分已保留 state 与编码时临时峰值。不能只限制序列化文件大小后宣称 heap 已有硬上限。高基数在加入前收费，退化路径/拒绝理由可观测；spill 并发、打开文件、磁盘占用、写放大及恢复读取也有额度。

所有推进/回填/重算/恢复均有项目数或步骤数上限、墙钟 deadline、取消和定期进度，失败保留可恢复已提交点。校验版本、长度、CRC/校验和、顺序、负数/非法计数和来源绑定；读取旧或坏状态不能降级为空 state 后继续输出。临时 spill 与未发布 generation 按所有权回收，不删除用户源数据或仍有效的快照。

## 8. 验收方法与性能成本

统一事件日志是正确性基准：含稳定身份、已提交顺序、event time、分区、typed before/after、NULL/缺失和 schema 变更。离线以同一源快照重新计算，增量以同一日志推进，比较每个一致性点的分组键、窗口、结果、revision 和有效位点。没有外部服务时只报告本地合同 PASS，真实对拍保持 NOT_RUN/NOT_READY。

| 验证层 | 必测内容 | 判定 |
|---|---|---|
| 类型与 SQL | 空输入/全 NULL、稀疏 measurement、Int64 极值与 `2^53` 邻域、Decimal、小数舍入、Boolean/String、引号/别名/大小写冲突 | 值、错误、原名及结果类型一致；不以转 double 消除失败。 |
| state 数学 | 单序列与合法切分/merge；不同 merge 树、Finalize 重复调用、重复 state/input、顺序敏感函数 | 精确域一致；浮点/近似误差按冻结合同；重复输入由身份层控制。 |
| 变更 | UPDATE 更改值/键/桶/谓词、DELETE/TTL、DISTINCT 重复值、MIN/MAX 唯一极值、FIRST/LAST 同时间、sketch 删除、事务回滚 | 不完整 before-image 明确拒绝/失效；每次发布等价于离线重算，未提交数据不泄漏。 |
| 时间 | 顺序/乱序、桶端点、负 epoch、多分区/idle、未来时间异常、迟到 drop/reject/修订、会话合并/拆分 | policy 与 revision 可复算；重开水位/关闭单调，拒绝批次不前移有效位点。 |
| 恢复 | 回填取消/续传、publish 前后 kill、checkpoint/ACK/outbox 各边界、位点缺口、坏 state、schema 变化、磁盘满 | 旧 generation 保持有效，恢复无静默缺失/重复；未知结果进入声明的恢复状态。 |
| 执行与跨端 | 内存/spill、embedded/真实 Server、REST/Frame/ADO/SDK、取消/断连与权限 | SQL 类型/名称、结果及错误对拍；首行和真实工作集独立记录，不以切帧证明惰性执行。 |
| AI 特征 | 指标/特征版本、时间切分、观测/填补标签、来源血缘、TTL/删除修正及模型评测重跑 | point-in-time 一致，无未来泄漏；M44 真实模型质量验收独立。 |

负载应覆盖低/高基数、热点/均匀键、稀疏字段、长字符串键、不同选择率、乱序比例、迟到/删除比例、窄/宽时间范围、滑动重叠和大分区。对照 PostgreSQL/DuckDB/ClickHouse、TDengine/IoTDB/TimescaleDB 的同语义子集，锁定产品版本、配置、机器、持久性、冷热状态和客户端路径；语义不同先标不可比，不拼接厂商基准。

报告 batch 吞吐、增量事件吞吐、查询/更新可见性 P50/P95/P99、首行时间、backlog/追平时间、state/group/window 数量和字节、allocated bytes/GC/working set、CPU、逻辑/物理 I/O、spill/checkpoint 字节与写放大、回填/重算时长、恢复 RPO/RTO。COUNT 精确、浮点容差、sketch 误差、数据完整性和性能分别判定。

实现前冻结各 workload 的现有基线和适用回归门槛；未知项保持 NOT_READY，不预填提升倍数。局部优化需要同时说明代价：state 加速读取会增加写入/存储；精确 DISTINCT 随基数增长；可撤回 MIN/MAX 增加候选保留；迟到修正增加读写；checkpoint 降低恢复成本也增加 fsync/放大。发布需在前台查询、后台回填、AI 推理混合竞争下验证公平性和尾延迟。

## 9. 实施顺序及与九模型/AI 的关系

1. C01/C02/C09 先冻结矩阵、误差、版本和日志基准；measurement TAG＋time 与 state 描述可拆为单一职责 PR。修正文档数学合同同时核对实现，不能只改 XML 后宣布 state 完成。
2. C03 与 C08 的最小预算合同先行，随后 C04 构建单源、有限聚合、可恢复回填/增量发布；缺源变更边界时先不开放自动增量。
3. C05 接已有 M43 窗口和多分区运输；C06/C07 在同日志框架补统计/时间能力及真实数据成本。首批 SQL 支持扩展逐项更新能力发现与拒绝矩阵。
4. C09 完成适用跨端/目标机/恢复报告，条件 C10 单项评审。实现完成、目标机性能、长期与远程证据分开，既有 M19/M20/M40/M42 门禁不由本专题关闭。

九模型职责保持清晰：时序提供观测和特征；关系提供实体、业务维度和定义；文档提供事件/知识元数据；KV 保存热点与幂等身份；MQ 运输计算输入/结果；对象保存原始资产和报告；全文/向量/图消费经过权限与版本治理的派生指标/证据。计算对单库/单源的原子范围逐项说明，跨模型派生发布按版本、一致性点和恢复对账，不宣称九模型原子事务。

M44 的预测/异常与业务分析依赖可重算、无未来泄漏的 C01/C04/C07 特征；模型/profile、推理质量与预算仍由 M44 负责。M45 不内置模型训练平台，也不将外部模型响应作为确定性 SQL 聚合。M43 提供至少一次运输，M45 在有限身份/事务/state 范围证明计算修正与恢复；邮件、Webhook、外部告警和其它未知副作用不由本方案承诺 exactly-once。

## 10. 官方来源与核查边界

资料日期：2026-10-03。以下为本轮规划研究采用的官方入口，源码事实以第 1 节及本地基线为准；可变的 latest/current 文档在实际实现、外部对拍前需冻结具体版本与采纳段落。本文没有新增产品安装或实际外部基准。

- PostgreSQL：[Aggregate Functions](https://www.postgresql.org/docs/current/functions-aggregate.html)。用于类型、空集、顺序与 partial aggregation 语义参考。
- MySQL：[Aggregate Functions](https://dev.mysql.com/doc/refman/8.4/en/aggregate-functions.html)。官方正文访问受限，细节待核；未将未读正文作为已验证能力。
- DuckDB：[Aggregate Functions](https://duckdb.org/docs/stable/sql/functions/aggregates.html)。用于类型/NULL、顺序敏感和近似统计参考。
- ClickHouse：[Aggregate function combinators](https://clickhouse.com/docs/sql-reference/aggregate-functions/combinators)。用于中间 state/merge 与参数一致性设计。
- TimescaleDB：[Continuous aggregates](https://docs.timescale.com/use-timescale/latest/continuous-aggregates/about-continuous-aggregates/)。具体 real-time/default、刷新和 toolkit 行为按版本核对。
- TDengine：[官方源码仓库产品资料](https://github.com/taosdata/TDengine/blob/main/docs/en/02-product-intro/index.md)、[流处理语法](https://github.com/taosdata/TDengine/blob/main/docs/en/07-stream-processing/01-syntax.md)、[TMQ](https://github.com/taosdata/TDengine/blob/main/docs/en/06-data-subscription/02-native.md)。开源核心与企业增强分别看，许可按所取组件核对。
- IoTDB：[Table 数据模型](https://iotdb.apache.org/UserGuide/latest-Table/Background-knowledge/Data-Model-and-Terminology_apache.html)、[写入/更新边界](https://iotdb.apache.org/UserGuide/latest-Table/Basic-Concept/Write-Updata-Data_apache.html)。已核实部分列 NULL/attribute-only UPDATE；未取得具体 query-feature 正文的函数细节保持待核，Tree/Table 与不同版本不混称同一合同。
- InfluxDB：[v2 processing tasks](https://docs.influxdata.com/influxdb/v2/process-data/)。本处仅参考该版本任务模型，不外推所有 InfluxDB 产品线。
- QuestDB：[SAMPLE BY](https://questdb.com/docs/reference/sql/sample-by/)、[ASOF JOIN](https://questdb.com/docs/reference/sql/asof-join/)。用于时间对齐/填补及条件 ASOF 设计参考。
- VictoriaMetrics：[Stream aggregation](https://docs.victoriametrics.com/stream-aggregation/)。其默认时间与内存 state 恢复限制单独记录，不能冒称持久 event-time。
- DolphinDB：[Streaming engines](https://docs.dolphindb.com/en/Streaming/streaming_engines.html)。商业/版本功能在选型时冻结，不作开源现状声明。
- MongoDB：[Aggregation](https://www.mongodb.com/docs/manual/aggregation/)、[On-Demand Materialized Views](https://www.mongodb.com/docs/manual/core/materialized-views/)。刷新/merge 行为与持续维护区分。
- Redis：[Time series](https://redis.io/docs/latest/develop/data-types/timeseries/)。模块/版本及持久性配置分别锁定，不从 compaction rule 推导通用撤回。
- Flink：[Dynamic tables](https://nightlies.apache.org/flink/flink-docs-stable/docs/dev/table/concepts/dynamic_tables/)。用于批流表变更、append/retract/upsert 区分。
- Materialize：[CREATE MATERIALIZED VIEW](https://materialize.com/docs/sql/create-materialized-view/)。durable materialized view 与内存 view/index 分别理解；v26.10 preview replacement 不纳入稳定方案。
- RisingWave：[官方文档](https://docs.risingwave.com/)。增量物化、变更与恢复以所选版本条目冻结；不以首页描述替代执行语义证明。

完成判定以根 ROADMAP 必选范围为准。计划、研究表和示意 SQL 不能进入 CHANGELOG 的已实现功能分类；实际代码交付、验证结果和保留边界分别归档。
