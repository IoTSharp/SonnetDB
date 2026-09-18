# RAG 管理、恢复与模型换代

M35 #305 在持久摄取和 generation 发布之上提供 Core 管理 API、数据库管理员 REST 接口和 Web「RAG 管理」页面。管理操作复用原有 KV/WAL、Document、FullText、Vector 和单库备份，不维护第二套提交日志。

## 管理入口

在管理工作台打开「RAG 管理」（`/admin/app/rag`），选择数据库并输入摄取时使用的 stream。页面显示 active revision/profile、内容和分块数、未发布任务身份及审计记录。重建、续跑、丢弃、清理均需显式提交操作预览；页面加载和刷新不会发起写入。

所有 REST 入口都要求目标数据库的 `Admin` 权限，普通 `Read`、`Write` 权限不足。接口前缀为 `/v1/db/{db}/semantic/rag`：

| 方法与路径 | 合同 |
|---|---|
| `GET /status?stream=docs` | 返回单个 stream 的 active/pending 元数据，不返回正文、向量或 provider 凭据。 |
| `GET /profiles` | 返回服务器已配置的可信 profile 摘要。 |
| `POST /rebuild` | 从持久 active 快照重新生成全部向量并发布新的 generation。 |
| `POST /resume` | 续跑指定持久任务；已持久化的完整分块可复用。 |
| `POST /discard` | 丢弃指定未发布任务和 staging，不删除已发布版本。 |
| `POST /cleanup` | 在数量和发布时间预算内清理 retired generation；有查询租约的版本延后。 |
| `GET /audit?limit=50` | 当前数据库的脱敏审计，支持 `continuationToken`，每页最多 200 条。 |

所有写操作必须显式提供 `stream` 和 `expectedRevision`。例如重建请求：

```json
{"stream":"docs","expectedRevision":7,"profileId":"manual-text-v2"}
```

`resume` 和 `discard` 还必须携带最近一次状态响应中的 `pendingGenerationId`。即使 active revision 没变，旧页面也不能处置后来替换的 pending 任务。`cleanup` 必须提供 `retiredBeforeUtc`，可设置 `maxGenerations`（默认 20，范围 1–100）；该数量限制包含因租约或保留时间而延后的候选，截止点按 generation 的发布时间判断。

请求最大 16 KiB，未知 JSON 字段被拒绝。状态和审计请求最多五秒，写操作使用 120 秒协作取消预算。provider 必须响应取消令牌；此预算不是对任意外部代码的强制线程终止。revision 或任务冲突返回 `409`，页面必须刷新状态后重新预览。

## 模型配置与换代

管理接口只使用服务器 `Copilot.Docs.RagProfile` 和相符的既有 embedding provider。当前配置列表至多一个 profile。请求不能提供任意 endpoint、API key、文件路径或新模型定义；profile 配置错误时，状态、审计、丢弃和清理仍可使用。

部署新模型时，先给模型、revision、维度、距离度量和归一化合同分配新的 profile ID，并更新可信服务器配置；随后从 active 快照执行重建。相同 profile 的重建也会重新调用模型，不能把它当作普通摄取的幂等 no-op。重建完成前不发布部分数据；失败时旧 active 保留，管理页提供续跑或丢弃入口。

查询向量必须与已发布 generation 的完整 profile 一致。服务器改用新 provider 后，旧 active 不会自动兼容新向量，查询可能因 profile 不匹配而拒绝。要求模型升级期间连续服务时，应沿用[独立 stream 迁移](copilot-rag-migration.md)，完成候选 stream 后切换配置，并保留旧 stream/profile 作为回退路径。清理 retired 版本会消除对应的历史资源，不能声称清理后仍可直接回滚。

管理器在重建和续跑前核对仍保留的 generation，禁止同一个 profile ID 改成不同模型合同。每次最多核对 1024 个版本，超过此限需先按保留策略分批清理。这不是永久 profile ID 注册表；清理历史后，调用方仍应为新模型分配新 ID。

## 失败与审计

管理操作先在数据库内写入脱敏 `started` 审计并同步 WAL，然后才调用 provider 或改变派生资源。完成记录包含操作、主体、stream 哈希、预期/结果 revision 和稳定错误码，不记录正文、向量、凭据、外发地址或异常全文；每次 embedding 继续使用既有外发策略和调用审计。

若操作成功后完成审计无法持久化，接口返回 `503 rag_audit_completion_failed`，保留 `started` 记录，不把已提交操作反写为失败。请求断开、客户端取消和超时也不能证明服务端没有提交。刷新状态和审计再决定后续动作，禁止盲目重试。审计默认保留 30 天，随数据库备份恢复；它不是独立外部不可篡改审计系统。

内部 `rag-ingestion-jobs`、`rag_<generationId>` 和管理审计资源不通过普通 REST/Frame/SQL 暴露；大小写和 Windows 末尾点别名同样受保护。可信嵌入式 Core SDK 仍用于 writer 和管理器访问，宿主必须在调用 SDK 前落实授权。

## 单库备份与重建

备份复用现有 `BackupService`。停止离线 CLI 的目标数据库宿主后，可使用：

```shell
sndb backup create --path ./data --output ./backup
sndb backup verify --path ./backup
sndb backup dry-run --path ./backup --target ./restored
sndb backup restore --path ./backup --target ./restored --rebuild-indexes
```

恢复包含数据库内的原始对象、已发布快照、generation catalog 和持久任务。重新配置可信 provider 后，先读取状态；若有 pending，按其完整 profile 和任务身份续跑，或明确丢弃后再重建。对仍有完整快照的已丢失派生集合，可以重新生成 Document/FullText/Vector generation。缺失或损坏权威快照、丢失原始对象、不可用模型均不能靠重建接口凭空恢复。

retired 清理只回收其派生集合和快照，不自动删除可能被多个 stream 共用的原始 Object Bucket 内容。原始对象使用现有对象生命周期和显式删除合同管理；备份保留期需要独立制定。此处是单数据库目录的恢复范围，不包含 Server 实例 MQ、外部源文件、在线 provider 的模型文件或凭据。

Core 应用可使用 `RagIngestionManager.GetStatusAsync`、`RebuildAsync`、`ResumeAsync`、`DiscardPendingAsync`、`CleanupRetiredAsync`。DTO 可通过 `RagIngestionManagementJsonContext` 的 source-generated 类型信息序列化；Core 本身不执行宿主身份验证或 provider 外发审计。

本项的合同测试使用受控向量/provider，覆盖持久恢复、原对象随备份重开、换代、查询租约、冲突、权限、审计故障及 UI 请求取消。10k/100k 容量、真实模型质量/成本、固定硬件和现场掉电证据仍按总里程碑后置。
