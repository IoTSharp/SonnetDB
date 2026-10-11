## HTTP API 参考：SonnetDB 全部 REST 端点详解

SonnetDB Server 把数据库管理、SQL、批量摄入和各模型的数据面组织在不同路由中。接口会随版本扩展，本篇先给出可用于接入的核心路线图，再指向各模型完整的路由与请求合同；不要把历史稿中的 `/api/v1/sql` 当作通用入口。

本文以当前 Server 路由为依据，正式 4.0.0 与 main 分开核对。部署者应使用与实际服务版本对应的文档，不应把当前源码中的新增端点视为所有旧版本都支持。

### 数据库与 SQL

| 方法 | 路由 | 用途 |
| --- | --- | --- |
| GET | `/v1/db` | 列出当前身份可见的数据库 |
| POST | `/v1/db` | admin 创建数据库，正文 `{"name":"metrics"}` |
| DELETE | `/v1/db/{db}` | admin 删除数据库，挂载状态等限制单独判断 |
| POST | `/v1/db/{db}/sql` | 执行单条 SQL，正文 `{"sql":"..."}` |
| POST | `/v1/db/{db}/sql/batch` | 批量 SQL，正文 `{"statements":["...","..."]}` |
| GET | `/v1/db/{db}/schema` | 查看 schema |
| POST | `/v1/db/{db}/maintenance` | 维护操作，依对应合同与权限调用 |

假设 `metrics` 和 `sensor_data` 已经存在：

```bash
curl --fail-with-body --max-time 30 \
  "http://127.0.0.1:5080/v1/db/metrics/sql" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  --data '{"sql":"SELECT time, temperature FROM sensor_data ORDER BY time DESC LIMIT 10"}'
```

SQL 查询采用 NDJSON 结果合同，消费者应按记录解析并检查结束/错误状态，不应把整个响应当作任意固定 JSON 数组。批量 SQL 不等于跨所有数据模型的原子事务；当前专用 `/sql/transactions` 会话服务的是同库关系表轻事务。

### 三种批量摄入

| POST 路由 | 正文 | 成功回执 |
| --- | --- | --- |
| `/v1/db/{db}/measurements/{m}/lp` | Line Protocol 文本 | JSON 计数 |
| `/v1/db/{db}/measurements/{m}/json` | JSON points | JSON 计数 |
| `/v1/db/{db}/measurements/{m}/bulk` | Bulk VALUES 文本 | JSON 计数 |
| `/write?db={db}&precision=ms` | Influx v1 兼容 LP | 204 |
| `/api/v2/write?bucket={db}&precision=ms` | Influx v2 兼容 LP | 204 |

专用批量端点从 URL 指定 measurement，支持 `flush`、`onerror`，回执字段为 `writtenRows`、`skippedRows`、`elapsedMilliseconds`。Influx 兼容端点从每行 LP 读取 measurement，默认时间精度为纳秒；专用 `/lp` 使用毫秒，不能混用同一个数字而不转换。

JSON points 的实际形态是：

```json
{"m":"sensor_data","points":[{"t":1713676800000,"tags":{"device_id":"s01"},"fields":{"temperature":23.5}}]}
```

### 其他模型的接口入口

| 模型 | 典型路由 | 接入要点 |
| --- | --- | --- |
| 文档 | `/v1/db/{db}/documents/{collection}/insert-one`、`/insert-many`、`/find`、`/bulk-write` | 多为 POST；按文档过滤、更新、validator 合同处理 |
| KV | `/v1/db/{db}/kv/{keyspace}/get`、`/get-many`、`/set-many`、`/incr`、`/remove`、`/ttl` | 操作正文携带键和值，读取也使用 POST |
| Graph | `/v1/db/{db}/graphs`，`/{graph}/vertices/{id}`，`/{graph}/edges/{id}` | 列表/创建和节点/边操作分开，算法路由另有合同 |
| 对象 | `/v1/db/{db}/s3` | S3 风格参数与对象流传输，不能当作普通 JSON CRUD |
| MQ | `/v1/db/{db}/mq/{topic}/publish`、`/pull`、`/ack`、`/nack`、`/stats` | POST；按消费组、确认与恢复边界使用 |

各模型完整列表与具体字段见 [Server 路由目录](https://github.com/IoTSharp/SonnetDB/tree/main/src/SonnetDB/Endpoints/Routes) 和 [文档模型 OpenAPI](https://github.com/IoTSharp/SonnetDB/blob/main/docs/openapi/document-api.yaml)。这里的入口表不能替代各模型权限、分页、容量和错误合同。

### 初始化、MCP 与可观测性

`GET /v1/setup/status` 检查 `needsSetup`，`POST /v1/setup/initialize` 用于首次安装。MCP 是 `/mcp/{db}` 的标准协议入口，需要 MCP 客户端握手和工具发现；不是旧稿中想象的 `GET /api/v1/mcp/tools`。

健康检查有 `/healthz`、`/healthz/live`、`/healthz/ready`。`/metrics` 默认保留最小指标，启用 Prometheus exporter 才获得完整指标集。诊断使用 `/v1/diagnostics/slow-queries`、`/v1/diagnostics/top-queries` 等实际路由，并依权限和配置开放。

### 鉴权、错误与重试

数据面按身份和目标库权限检查读取、写入、管理能力，建库/删库需要 admin。常见错误正文采用 `{"error":"sql_error","message":"..."}`，不是统一套一层 `error.code`。除了 HTTP 状态，还应记录具体业务错误与响应是否完整。

当前 SQL admission 拒绝可返回 `503 sql_overloaded` 和 `Retry-After`，不能假定所有限流都返回 429。超时的写请求应先对账，避免重复执行。正文格式也按路由分别选择：JSON、文本 LP、NDJSON、S3/二进制帧等；不能宣称所有端点通用支持 MessagePack。

参考：[ADO.NET/REST 结果](https://github.com/IoTSharp/SonnetDB/blob/main/docs/ado-net.md)、[批量摄入](https://github.com/IoTSharp/SonnetDB/blob/main/docs/bulk-ingest.md)、[MCP 合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/mcp-contract.md)、[可观测性](https://github.com/IoTSharp/SonnetDB/blob/main/docs/observability.md)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
