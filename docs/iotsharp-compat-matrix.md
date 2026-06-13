# IoTSharp 兼容矩阵与基线套件

日期：2026-06-12

范围：梳理 `external/IoTSharp` 当前数据底座能力，并为 SonnetDB 作为关系库、时序库、缓存、对象桶、向量搜索与全文搜索的新增可选后端建立 #109 基线验收清单。

## 状态图例

| 状态 | 含义 |
| --- | --- |
| 已接入 | IoTSharp 当前代码已有配置枚举、依赖注入路径或实现类。 |
| 需验证 | 有插件或配置入口，但生产选择 SonnetDB 前必须补迁移、方言、事务或长稳验证。 |
| 规划接入 | IoTSharp 当前未以该形态消费 SonnetDB，需要后续 PR 增加 provider / API / profile。 |
| 不适用 | 该后端不是当前能力域的直接对照对象。 |

## 关系数据库矩阵

IoTSharp 当前通过 `DataBaseType` 和 `ApplicationDbContext` 选择主数据存储，承载 Identity、租户、客户、设备、资产、规则、告警等开源主平台数据。

| 后端 | IoTSharp 当前入口 | 主要能力 | 风险点 | SonnetDB 基线要求 |
| --- | --- | --- | --- | --- |
| PostgreSQL | `DataBase=PostgreSql`，`ConfigureNpgsql` | EF Core、迁移、健康检查、分片遥测默认路径 | 生产常用基线，查询/事务/迁移覆盖面最大 | EF Core provider 必须先对齐 PostgreSQL 常用 DDL、Identity、分页、事务和迁移回滚。 |
| MySQL | `DataBase=MySql`，`ConfigureMySql` | EF Core、迁移、健康检查 | 字符集、大小写、日期精度、分页方言 | provider 需验证字符串、时间、唯一索引、分页和迁移差异。 |
| SQLServer | `DataBase=SqlServer`，`ConfigureSqlServer` | EF Core、迁移、健康检查 | Identity schema、datetime/rowversion、事务语义 | provider 需覆盖 Identity 登录、并发列、事务提交/回滚。 |
| SQLite | `DataBase=Sqlite`，`ConfigureSqlite` | 本地体验、安装向导、轻量部署 | 并发写、大小写搜索、迁移切换 | SonnetDB Profile 应至少达到 SQLite 本地体验易用性。 |
| Oracle | `DataBase=Oracle`，`ConfigureOracle` | EF Core、迁移、健康检查 | 标识符长度、序列、分页、大小写 | provider 需记录不支持清单，不以 Oracle 完整兼容作为首批门槛。 |
| Cassandra | `DataBase=Cassandra`，`ConfigureCassandra` | 有 EF 插件和健康检查入口 | 非关系模型，迁移/事务/查询语义差异大 | 仅作为兼容风险对照；SonnetDB 不按 Cassandra 语义建模。 |
| ClickHouse | `DataBase=ClickHouse`，`ConfigureClickHouse` | 有 EF 插件和健康检查入口 | 分析型数据库，事务和更新语义差异大 | 仅作为读多写少/分析场景对照；不作为 OLTP provider 首批等价目标。 |

关系验收用例：

- `ApplicationDbContext` schema 创建、迁移升级、迁移回滚和空库初始化。
- Identity 用户、角色、登录、JWT 相关查询。
- 租户、客户、设备、资产、规则、告警 CRUD。
- 常用 `Include`、分页、排序、条件过滤、唯一索引冲突和事务回滚。
- 迁移前后行数、主键、外键、索引和关键业务查询结果一致。

## 时序数据库矩阵

IoTSharp 当前通过 `TelemetryStorage` 和 `IStorage` 承载遥测写入、最新值、历史查询与聚合。

| 后端 | IoTSharp 当前入口 | 主要能力 | 风险点 | SonnetDB 基线要求 |
| --- | --- | --- | --- | --- |
| SingleTable | `TelemetryStorage=SingleTable` | EF 单表遥测存储 | 大规模性能有限 | SonnetDB 应覆盖同等写入、最新值和历史查询语义。 |
| Sharding | `TelemetryStorage=Sharding` | EF 分表，支持 PostgreSQL/MySQL/SQLServer/SQLite/Oracle；SonnetDB 已有最小 ShardingCore 分表 CRUD 基线 | 路由、分片周期、跨分片聚合、生产迁移 | 继续验证按时间范围查询、聚合、分片迁移和长稳一致性。 |
| InfluxDB | `TelemetryStorage=InfluxDB` | 原生时序写入、查询、健康检查 | token/bucket 配置、Flux/聚合差异 | SonnetDB 需覆盖写入、latest、range、聚合和 Influx 迁移校验。 |
| TimescaleDB | `TelemetryStorage=TimescaleDB` | hypertable、time_bucket 聚合 | PostgreSQL 扩展依赖、聚合 SQL 方言 | SonnetDB 需覆盖 `Mean/Max/Min/Sum/First/Last/Median` 对应能力或不支持清单。 |
| Taos / TDengine | `TelemetryStorage=Taos` | 超级表、tag、last_row、范围查询 | SQL 拼接、类型映射、聚合差异 | SonnetDB 需验证 tag、latest、批量写和中文/特殊 key 迁移。 |
| IoTDB | `TelemetryStorage=IoTDB` | storage group、设备路径、聚合查询 | path 编码、类型映射、时间格式 | SonnetDB 需验证设备维度映射、点位类型和聚合结果。 |
| SonnetDB | `TelemetryStorage=SonnetDB`，`SonnetDBStorage` | 已有遥测适配器，支持 auto-create measurement、write、latest、range、聚合走 IoTSharp 本地聚合 | 当前仅覆盖时序遥测，不覆盖关系/缓存/S3 | #109 后续测试应把该路径纳入真实回归基线。 |

时序验收用例：

- Boolean/String/Long/Double/Json/XML/Binary/DateTime 输入映射。
- 单设备多 key、多设备同 key、空 key、重复 key、保留列名冲突。
- 最新值查询、指定 key 最新值查询、时间范围查询。
- `None/Mean/Median/Last/First/Max/Min/Sum` 聚合语义。
- 批量写入、断线恢复、重复写入、时间边界和 UTC 转换。

## 缓存与 KV 矩阵

IoTSharp 当前通过 `CachingUseIn` 和 EasyCaching provider 选择缓存后端。

| 后端 | IoTSharp 当前入口 | 主要能力 | 风险点 | SonnetDB 基线要求 |
| --- | --- | --- | --- | --- |
| InMemory | `CachingUseIn=InMemory` | 进程内缓存，默认轻量路径 | 重启丢失，多实例不共享 | SonnetDB provider 需提供同等 API，并明确持久化/共享语义。 |
| Redis | `CachingUseIn=Redis` | 分布式缓存、健康检查 | TTL、连接池、网络故障、集群差异 | SonnetDB 需补 TTL、惰性过期、后台清理、批量操作和故障语义。 |
| LiteDB | `CachingUseIn=LiteDB` | 本地持久化缓存 | 文件锁、TTL 行为、并发 | SonnetDB 需验证本地单文件缓存体验和重启恢复。 |
| SQLite | `CachingUseIn=SQlite` 枚举存在 | 当前启动逻辑未显式注册 SQLite provider | 入口不完整 | 作为不支持项记录，不纳入首批 SonnetDB 缓存选项目标。 |
| SonnetDB | 当前未有 `CachingUseIn=SonnetDB` | 规划 EasyCaching / `IDistributedCache` provider | TTL 与并发语义未落地 | #116 前不得宣称已达到 Redis/LiteDB/InMemory 的兼容语义。 |

缓存验收用例：

- `Set/Get/Remove/Exists`、批量读写、前缀隔离。
- 绝对过期、滑动过期、过期后不可读、重启后过期仍生效。
- 并发写同 key、并发读写、删除后读一致性。
- provider 名称、配置错误、健康检查和降级行为。

## 对象桶矩阵

IoTSharp 当前通过 `StorageFactory.Blobs.FromConnectionString(ConnectionStrings:BlobStorage)` 使用 Storage.Net `IBlobStorage`，默认回退到用户目录下的 `disk://`。

| 后端 | IoTSharp 当前入口 | 主要能力 | 风险点 | SonnetDB 基线要求 |
| --- | --- | --- | --- | --- |
| BlobStorage / disk | `ConnectionStrings:BlobStorage` 或默认 `disk://.../IoTSharp/` | list、upload、download、modify、delete | 本地磁盘容量、备份、权限 | SonnetDB S3 需覆盖现有 BlobStorageController 行为。 |
| S3-compatible | Storage.Net 连接串可承载 S3 类后端 | 对象上传下载、外部对象存储 | multipart、etag、range、presigned URL、权限 | #117 起提供 S3-compatible 常用子集，不只做 metadata。 |
| SonnetDB bucket | 当前未接入 | 规划 bucket/object metadata、content、审计和生命周期 | 大对象写入、range、multipart、一致性 | #117/#118 后再进入 IoTSharp profile。 |

对象桶验收用例：

- bucket 创建/列举、对象上传、覆盖写、下载、删除。
- content-type、etag、sha256、大小、最后修改时间。
- range read、multipart upload、copy object、presigned URL。
- 对象 metadata 与 content 迁移一致性，删除/回滚后引用不悬挂。

## 向量搜索矩阵

IoTSharp 当前开源主平台未发现独立向量搜索后端入口；SonnetDB 已具备 `VECTOR(N)`、`knn(...)`、HNSW/IVF/Vamana 等向量索引规划与实现基础。该能力应作为 SonnetDB 对 IoTSharp 的增强能力，而不是把 IoTSharp 现有功能误标为已支持。

| 能力 | IoTSharp 当前状态 | SonnetDB 当前状态 | 基线要求 |
| --- | --- | --- | --- |
| 向量字段 | 未作为主平台通用字段使用 | `VECTOR(N)` measurement field | 支持设备/资产/文档 embedding 存储，不污染遥测业务数据边界。 |
| KNN 查询 | 未接入 | `knn(measurement, field, vector, k, metric)` | 支持 cosine、L2、inner product，返回稳定 topK 与 distance。 |
| 向量索引 | 未接入 | HNSW 等索引能力 | 验证索引创建、重建、备份恢复和召回率基线。 |
| 混合检索 | 未接入 | `hybrid_search(...)` | 可将设备/资产/知识文档语义搜索与全文 BM25 融合。 |

向量搜索验收用例：

- 向量维度校验、非法向量拒绝、空集合查询。
- topK 顺序、distance 单调性、metric 差异。
- 索引创建、重建、备份恢复后查询结果一致。
- 与时间/tag/关系维表过滤组合时结果不越权、不串租户。

## 全文搜索矩阵

IoTSharp 当前主要是普通字段过滤和 SQLite 大小写搜索配置，未发现独立全文索引服务入口；SonnetDB 已通过 DotSearch 支持 document collection 全文索引、`match(...)`、`bm25_score()` 与 explain 访问路径。

| 能力 | IoTSharp 当前状态 | SonnetDB 当前状态 | 基线要求 |
| --- | --- | --- | --- |
| 普通搜索 | 控制器/EF 查询中的字段过滤 | 关系表/文档集合查询 | 保持现有名称、标签、属性筛选能力。 |
| 全文索引 | 未作为独立后端接入 | `CREATE FULLTEXT INDEX`、`match(...)` | 支持设备/资产/规则/知识文档搜索，索引可重建。 |
| 中文分词 | 未统一抽象 | DotSearch CJK/Jieba adapter | 验证中文、英文、混合 token 和大小写。 |
| BM25 排序 | 未接入 | `bm25_score()` | 结果排序稳定，支持分页和 explain。 |

全文搜索验收用例：

- 创建/删除/展示全文索引。
- 中文、英文、数字、混合符号查询。
- `match(...)` 命中、`bm25_score()` 排序、分页稳定性。
- 索引重建、备份恢复、删除文档后索引同步。

## 迁移、双写与回滚清单

关系迁移：

- 从 PostgreSQL/MySQL/SQLServer/SQLite 导出 schema、索引、约束和数据。
- 导入 SonnetDB 后执行行数、主键、唯一索引、外键候选关系和核心查询校验。
- 迁移失败时保留原库只读可用，SonnetDB 目标库可丢弃重建。
- 回滚时关闭 SonnetDB 写入，恢复原连接串并验证 Identity 登录和核心 CRUD。

时序迁移：

- 按设备、key、时间窗口分批迁移，保留原始时间戳和数据类型。
- 支持双写窗口，比较 latest、range、聚合和抽样原始点。
- 回滚时以原时序库为准，SonnetDB 写入可停止并保留校验报告。

缓存迁移：

- 缓存不作为强一致主数据迁移；仅迁移需要持久化的命名空间。
- 切换前清理易失缓存，切换后验证 TTL 和热点 key。
- 回滚时允许缓存冷启动，但不得影响关系/时序主数据。

对象桶迁移：

- 先迁 metadata，再迁 content，校验 size、etag/sha256、content-type。
- multipart 未完成会话不得迁为完成对象。
- 回滚时保留原 bucket 只读，切回原 BlobStorage/S3 连接串并抽样下载校验。

向量/全文迁移：

- embedding 原始向量、索引定义和全文索引定义分开迁移。
- 索引可重建，不作为唯一事实来源；重建后执行 topK/BM25 抽样校验。
- 回滚时保留原文档/向量主数据，删除或停用 SonnetDB 派生索引。

## #109 后续出口

- `tests/SonnetDB.IoTSharpCompat.Tests` 先固定能力域、后端清单、验收用例和迁移/回滚清单。
- #110 已完成 ADO.NET 轻事务、异步 API、取消令牌与远程 `/sql/batch` 事务基线；#113 已将关系表轻事务扩展到同一数据库内多表 DML 原子提交/回滚，并补外键、ROWVERSION 与稳定约束错误码。
- #111 已完成关系表 DDL 与 schema metadata 核心基线：`ALTER TABLE ADD/DROP/RENAME COLUMN`、`ALTER TABLE RENAME TO`、`INFORMATION_SCHEMA.tables/columns/indexes`、`DbDataReader.GetSchemaTable()` 与 `DbConnection.GetSchema()` provider metadata；首版仍明确拒绝主键列变更和被索引列删除。
- #115 已完成 SonnetDB EF provider 的 migrations history 支持（`__EFMigrationsHistory` 与可配置历史表），并以 `Database.Migrate()`、迁移升级、回滚、重复执行幂等检查、空库初始化、IoTSharp `ApplicationDbContext` schema 创建、Identity 登录、主数据 CRUD、`Include`、分页、常用查询、`LIKE` 字符串模式翻译和 `SaveChanges` 事务作为入口验收。
- #110 ~ #121 逐步把占位清单替换为真实 adapter、provider、容器化端到端和长稳测试。
- 在 #116/#117/#119 完成前，不宣称 SonnetDB 已具备 IoTSharp Redis、S3 或关系数据库路径的完整兼容语义；始终作为新增可选后端推进。
