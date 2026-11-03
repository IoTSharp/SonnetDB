# SonnetDB × WorkBuddy 闭环接入与使用指南

## 目标

本资料用于 WorkBuddy Skill、连接器和资料库，覆盖 SonnetDB 用户从部署到日常使用的完整旅程：

> 部署 → 安全连接 → 九种数据模型访问 → 升级/备份/恢复 → 日常维护 → 故障排查

连接器负责把 WorkBuddy 接入用户已经部署的 SonnetDB MCP 服务；Skill 提供九模型的选型和使用指导，并且只调用现场实际发现且已授权的工具；资料库负责存放版本化、可检索、可审计的产品文档和运行手册。

**覆盖指导不等于覆盖可执行工具。** 当前 Server MCP 的九个只读工具不是九种模型的完整 typed API，不能代替 KV、Document、对象或 MQ 等模型的原生入口。真实联调应分别记录原生 HTTP API 成功、MCP 工具成功和 WorkBuddy 宿主连接成功，三者不可相互替代。部署、升级、备份、恢复目前仅有操作指导，尚未形成该连接器可执行的管理工具；其完整闭环仍待实现和验证。

## 一、三件套的职责边界

### 1. 连接器

连接器提供：

- SonnetDB MCP 地址配置：`/mcp/{database}`。
- 用户自填 Bearer Token。
- 最小权限和 HTTPS 使用提示。
- 只读 MCP 工具发现与调用。

连接器不应保存真实 Token，也不应把任意本地命令、数据库目录写入或高风险管理操作直接暴露给 Agent。

### 2. Skill

Skill 提供：

- 查询顺序和 SQL 生成规则。
- 九种模型的选择、schema 探索、读取和结果解释。
- 部署、升级、备份、恢复、维护和故障排查清单。
- 对写入、DDL、重启、恢复、目录操作的人工确认边界。
- SonnetDB 的产品边界：单节点、非 PostgreSQL/MongoDB/Redis/Kafka/S3 集群兼容，Graph 为 Beta。

### 3. 资料库

资料库建议保存以下版本化材料：

- 产品总览和能力矩阵。
- MCP typed contract 与工具输入/输出/错误码。
- SQL 参考和九模型使用手册。
- Server/嵌入式部署指南。
- 升级兼容与迁移指南。
- 备份、恢复和灾难演练手册。
- 运行维护、健康检查、日志和指标手册。
- WorkBuddy 连接器安装、验收和审核清单。
- 版本发布说明、已知限制和审计证据。

资料库内容应以公开、脱敏、可审阅的文档为主，绝不上传 Token、密码、生产连接串或客户数据。

## 二、部署和连接闭环

### 部署前

1. 确认目标版本、操作系统、.NET/运行时和部署形态。
2. 确认数据目录、容量、备份目标、端口和网络边界。
3. 确认 Server 是否启用 HTTPS、Bearer 鉴权、健康检查、日志和指标。
4. 确认数据库名称、模型使用范围和访问角色。

### 连接配置

WorkBuddy 使用具体数据库的 MCP 地址：

```text
https://<host>/mcp/<database>
```

使用最小权限 Token，至少拥有目标数据库 `Read` grant。连接后推荐顺序：

1. `list_databases`
2. 时序：`list_measurements` → `describe_measurement`
3. 关系：`SHOW TABLES` → `DESCRIBE TABLE`
4. 其他模型：读取对应模型的公开 schema/索引/元数据
5. `sample_rows` 或小范围 `SELECT`
6. `explain_sql`
7. 带 `LIMIT/FETCH` 的 `query_sql`

## 三、九种数据模型闭环

| 模型 | 典型用途 | WorkBuddy 使用重点 | 关键边界 |
|---|---|---|---|
| 时序 | 遥测、指标、窗口分析 | measurement、TAG/FIELD、时间范围、聚合 | 不猜列类型，查询必须有时间范围 |
| 关系表 | 业务实体、JOIN、事务 | `SHOW TABLES`、`DESCRIBE TABLE`、参数化 SQL | 不假设完整 PostgreSQL 兼容 |
| KV/缓存 | 状态、会话、TTL、CAS | keyspace、TTL、计数器、CAS 说明 | 写操作必须人工确认 |
| JSON 文档 | collection、过滤、更新、聚合 | 使用 SonnetDB 原生 document 语义 | 不是 MongoDB wire/官方 Driver 兼容 |
| 全文检索 | BM25、中文/多语言、模糊检索 | 先确认索引、字段和分词策略 | 不凭字段名猜索引 |
| 向量检索 | 相似度、ANN、Hybrid Search | 确认维度、距离和 embedding profile | 不猜维度，不把测试模型当质量证据 |
| 对象存储 | bucket、对象、Range、版本、multipart | 区分元数据和内容传输 | 不是分布式 S3 集群 |
| SonnetMQ | topic、consumer group、offset、pull/ack、replay | 区分消息实例与数据库目录 | 单库备份不能覆盖实例 MQ |
| 原生属性图（Graph Beta） | 顶点、边、标签、属性、邻接 | 使用已发布 SQL/Graph 只读入口 | Beta，不是完整 GQL/Cypher 或生产承诺 |

对于每次回答，Agent 应说明：目标数据库、实际模型、schema 来源、时间范围、行数上限、是否截断、查询假设和未验证边界。

## 四、升级、备份与恢复

### 升级前

- 固定当前版本、提交或发布包来源。
- 阅读版本迁移和兼容合同。
- 记录数据库目录、WAL、外部对象、SonnetMQ 实例和配置文件范围。
- 完成可恢复备份，并核对文件清单和 SHA256。
- 准备回滚条件：健康检查失败、授权失败、schema 读取异常、代表性查询失败或恢复不一致。

### 升级后

至少验证：

1. 进程启动和健康检查。
2. HTTPS/Bearer 鉴权和数据库 grant。
3. `list_databases`、schema 发现和代表性只读查询。
4. 当前实际使用的每个数据模型的读取路径。
5. 需要时补做新写入、事务、WAL replay 和重开验证。
6. 日志、指标、备份和恢复路径仍可用。

### 恢复边界

恢复演练必须明确恢复对象：单库目录、Server 实例、SonnetMQ、对象存储和外部资源不能混为一谈。恢复完成后要执行业务代表性读取和一致性核对，不以“文件已复制”作为成功标准。

## 五、日常维护与故障排查

先收集：版本、部署形态、目标数据库、时间范围、健康状态、日志、指标、脱敏错误码和最近变更。然后按以下类别定位：

- 连接/HTTPS/Token/grant。
- SQL 语法、标识符大小写和 schema。
- WAL、目录、恢复或存储空间。
- CPU、内存、文件句柄、网络和超时。
- 特定模型的索引、维度、topic 或对象状态。
- MCP provider 或 embedding 未就绪。

任何修复方案都要列出影响范围、可逆性、人工确认点和验证查询。禁止直接修改数据库目录、绕过权限或在不理解影响范围时重试破坏性操作。

## 六、WorkBuddy 提交审核闭环

提交前应准备：

- `connector-meta.json`。
- `mcp.json`。
- `icon.svg`。
- `skills/sonnetdb-skill/SKILL.md`。
- 连接器 README 和隐私/安全说明。
- 真实部署的 MCP 地址验收记录（地址本身可脱敏）。
- `tools/list`、连接、只读查询、行数上限、错误码和 Token 轮换验证记录。
- 九种模型至少一项代表性访问/使用说明；不能验证的模型必须明确标为未验证。
- 部署、升级、备份、恢复和维护指导的版本对应关系。
- 不包含真实 Token、密码、客户数据和内部敏感日志的清洁包。

审核材料必须区分：

- 本地静态配置验证。
- 真实 MCP 服务联调。
- SonnetDB 产品自身测试证据。
- WorkBuddy 连接器审核结果。

不能把其中一类证据冒称为另一类，也不能把 Skill/资料库中的指导冒称为已执行的运维动作。
