# 语义 embedding provider 与对象调用

M35 #300 在既有文本/图片 provider 上补齐对象能力发现、内容外发检查和数据库内调用审计。现有图片上传、文搜图、图搜图和 Bucket 异步语义摄取均经过同一治理服务。该服务不替代 Copilot 的文本 provider 配置，也不表示真实模型质量、成本或容量评测已经完成。

`GET /v1/semantic-search/status` 继续返回既有状态，并增加 `objectProvider`、`objectContentTypes`、`dataEgressMode` 和 `providerIsLocal`。默认对象 adapter 复用 `IMultimodalEmbeddingProvider` 的文本/图片向量空间：支持严格 UTF-8 的 `text/plain`、`text/markdown`，以及 PNG、JPEG、WebP、GIF、BMP 图片。PDF、音视频和其他二进制对象返回不支持，不会被当成字符串或图片强行编码。扩展可以注册 `IObjectEmbeddingProvider`；接口本身只处理已解析内容，服务层负责对象读取、授权后的外发检查和审计。

## 调用对象 embedding

拥有目标数据库 Read 权限的主体可以调用：

```http
POST /v1/db/example/semantic/embeddings/object
Content-Type: application/json

{"object":{"bucket":"documents","key":"guide.md","versionId":"固定版本"}}
```

必须提供 `versionId` 或 `eTag`，同时提供时两者都必须匹配；仅提供 ETag 时，先解析当前版本，再按解析出的固定版本读取。返回实际对象引用、provider、profile 和 `embedding`。该入口生成向量，不自动创建索引，不接受远程 URL。Bucket 异步摄取继续使用现有图片索引任务，调用时携带源对象版本；独立对象 provider 的 profile 和维度必须与图片检索相符，才能用于该图片索引。

`SonnetDBServer:SemanticSearch` 新增：

| 配置 | 默认值 | 边界 |
|---|---:|---|
| `DataEgressPolicy:Mode` | `LocalOnly` | 未明确声明本地执行的 provider 默认被拒绝 |
| `DataEgressPolicy:Target` | 空 | 非本地模式必须指定目标，精确区分大小写 |
| `EmbeddingTimeoutSeconds` | 30 | 1–300 秒，provider 必须响应取消令牌 |
| `MaxObjectEmbeddingBytes` | 20 MiB | 1 字节至 100 MiB |
| `MaxTextEmbeddingBytes` | 1 MiB | 1 字节至 4 MiB；对象文本同样受限 |

对象图片还受既有 `MaxImageBytes` 限制。输入长度、媒体类型和对象版本在调用之前检查；向量维度及有限数值在返回之前检查。取消和超时传递给实际 provider，不启动脱离请求的后台调用；不响应取消的第三方 provider 不具备硬中断保证。

## 外发与审计

`LocalOnly` 只允许 `Info.IsLocal=true`；内置 SigLIP2 ONNX 明确声明本地执行，不下载模型。未知实现的 `IsLocal` 默认 false。`ConfiguredProvider` 要求 Target 精确匹配 provider 名称；`ExternalProvider` 要求 Target 精确匹配 provider 声明的目标标识。请求不能覆盖服务器策略。凭据不得放入 provider 名称、profile 或目标标识。

外发拒绝返回 `403 semantic_egress_denied` 且不会调用 provider。每次审计提交后都显式同步 WAL，即使普通 KV 关闭逐写 fsync 也不能放宽此要求。审计初始记录写入或同步失败同样阻止调用；成功终态写入失败不会返回向量。每次调用保留 `started`，随后更新为 `succeeded`、`failed`、`denied`、`cancelled` 或 `timed_out`；进程异常中断会留下 started，不能据此断言 provider 没有执行。所有调用都保留审计，即使 `AuditRequired=false`。

审计存在目标数据库的 `__semantic_embedding_audit` KV keyspace，复用 WAL 和数据库备份恢复，记录保留 30 天。记录包括调用主体、provider/profile、模态、字节数、耗时、稳定错误码和对象版本身份摘要；不保存内容、向量、对象路径、凭据或 provider 异常消息。该合同不宣称 exactly-once，也不替代长期外部审计归档。

```http
GET /v1/db/example/semantic/embeddings/audit?limit=100
```

读取审计需要目标数据库 Admin 权限。该审计 keyspace 在通用 REST/Frame KV 及管理扫描中属于保留名称，并从公开 keyspace 列表隐藏；Admin 也不能通过原始 KV API 改写审计，只能通过本审计端点读取。每页 `limit` 为 1–200；响应包含 `entries` 和可选 `continuationToken`，下一页需 URL 编码后传回同名查询参数。扫描有候选数量及 200 ms 预算，调用总预算为 5 秒，记录按开始时间倒序，分页期间的新调用通过重新从第一页读取可见。
