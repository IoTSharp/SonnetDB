# SonnetDB × WorkBuddy 闭环接入与使用指南

## 目标

本资料用于 WorkBuddy Skill、连接器和资料库，覆盖 SonnetDB 用户从部署到日常使用的完整旅程：

> 部署 → 安全连接 → 九种数据模型访问 → 升级/备份/恢复 → 日常维护 → 故障排查

连接器负责把 WorkBuddy 接入用户已经部署的 SonnetDB MCP 服务；Skill 提供九模型的选型和使用指导，并且只调用现场实际发现且已授权的工具；资料库负责存放版本化、可检索、可审计的产品文档和运行手册。

当前连接器0.2.0以含九模型43工具的SonnetDB 5.0合同为基线（28只读、15写入），实际能力以现场 `tools/list` 与 `inputSchema` / `outputSchema` 为准，不能仅依据5.0版本字符串判断。默认推荐目标库Read凭据；用户已明确授权写入且目标库具备Write grant时，才调用独立写工具。**工具发现不等于写授权，覆盖数据工具不等于运维授权。** 原生API、MCP、WorkBuddy宿主和HTTPS/平台审核分别验收。

部署、升级、备份、恢复、服务重启不属于这43个数据工具，需要另有授权和执行入口。本地文档修订不代表这些操作已执行。

## 一、三件套的职责边界

### 1. 连接器

连接器提供：

- SonnetDB MCP 地址配置：`/mcp/{database}`。
- 用户自填 Bearer Token。
- 最小权限和 HTTPS 使用提示。
- 发现43工具及现场schema，按Read或已有明确授权的Write范围调用。

连接器不应保存真实 Token，也不应把任意本地命令、数据库目录写入或高风险管理操作直接暴露给 Agent。

### 2. Skill

Skill 提供：

- 查询顺序和 SQL 生成规则。
- 九种模型的选择、schema 探索、读取和结果解释。
- 部署、升级、备份、恢复、维护和故障排查清单。
- 区分用户明确写授权、数据库Write grant和另行授权的重启/恢复/目录操作；已有授权不重复询问。
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

默认使用仅有目标库Read grant的最小权限Token；已有明确写授权时选择该库适当Write grant，不要求管理员或全库凭据，不在提示词/日志中传递Token。连接后先读 `tools/list` 和输入/输出schema，再按顺序：

1. `list_databases`
2. 时序：`list_measurements` → `describe_measurement`
3. 关系：`list_tables` → 受支持的 `DESCRIBE TABLE`
4. KV、文档、对象、MQ和图：使用现场发布的专用发现/读取工具；全文与向量先检查 `describe_collection`
5. `sample_rows` 或小范围 `SELECT`
6. `explain_sql`
7. 带 `LIMIT/FETCH` 的 `query_sql`

## 三、九种数据模型闭环

| 模型 | 典型用途 | WorkBuddy 使用重点 | 关键边界 |
|---|---|---|---|
| 时序 | 遥测、指标、窗口分析 | measurement、TAG/FIELD、时间范围、聚合 | 不猜列类型，查询必须有时间范围 |
| 关系表 | 业务实体、JOIN、事务 | `SHOW TABLES`、`DESCRIBE TABLE`、参数化 SQL | 不假设完整 PostgreSQL 兼容 |
| KV/缓存 | 状态、会话、TTL、CAS | list/get/scan与独立put/delete；未发布的CAS仅指导 | 写操作需已有明确用户授权及Write grant |
| JSON 文档 | collection、过滤、更新、聚合 | 使用 SonnetDB 原生 document 语义 | 不是 MongoDB wire/官方 Driver 兼容 |
| 全文检索 | BM25、中文/多语言、模糊检索 | 先确认索引、字段和分词策略 | 不凭字段名猜索引 |
| 向量检索 | 相似度、ANN、Hybrid Search | 确认维度、距离和 embedding profile | 不猜维度，不把测试模型当质量证据 |
| 对象存储 | bucket、对象、Range、版本、multipart | 区分元数据和内容传输 | 不是分布式 S3 集群 |
| SonnetMQ | topic、consumer group、offset、pull/ack、replay | 区分消息实例与数据库目录 | 单库备份不能覆盖实例 MQ |
| 原生属性图（Graph Beta） | 顶点、边、标签、属性、邻接 | list/get/expand及独立版本化写工具 | Beta，不是完整 GQL/Cypher 或生产承诺 |

对于每次回答，Agent 应说明：目标数据库、实际模型、schema 来源、时间范围、行数上限、是否截断、查询假设和未验证边界。

`query_sql` 始终只读；写入和受支持建模使用独立 `execute_sql` 或对应模型写工具。该入口拒绝控制面、多语句、事务、INSERT SELECT及RETURNING。写入超时、取消、断连或 `write_outcome_unknown` 先回读，不盲重试，特别是不重复发布MQ。KV/文档的 `nextCursor`、对象范围/游标和MQ offset按现场schema续读，不能以部分页冒充完整结果。

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
- `tools/list`、43工具schema、默认Read读取/写拒绝、已有写授权下Write回读、预算/游标、错误码及Token轮换分别记录。
- 九种模型至少一项代表性访问/使用说明；不能验证的模型必须明确标为未验证。
- 部署、升级、备份、恢复和维护指导的版本对应关系。
- 不包含真实 Token、密码、客户数据和内部敏感日志的清洁包。

审核材料必须区分：

- 本地静态配置验证。
- 真实 MCP 服务联调。
- SonnetDB 产品自身测试证据。
- WorkBuddy 连接器审核结果。

不能把其中一类证据冒称为另一类，也不能把 Skill/资料库中的指导冒称为已执行的运维动作。

2026-11-03历史r2报告绑定镜像revision455bf3b5、九工具和六模型MCP，不修改为43工具通过。新MCP切片49731955的NativeAOT43/36与118/89属于生产实现证据；本次0.2.0静态包和文案校验不能替代新版WorkBuddy宿主、HTTPS或平台审核。
