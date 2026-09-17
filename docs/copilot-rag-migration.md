# Copilot 知识库的可回滚 RAG 迁移

M35 #302 为本地 Copilot 文档工具提供显式 `legacy` / `rag` 后端选择。默认 `legacy` 保留原 `__copilot__.docs` 与 `docs_state` 行为；`rag` 复用通用 writer、完整快照、持久续跑和 Document/FullText/Vector generation。云端聊天的路由与技能库不受此开关影响。

## 配置与迁移

先保留现有数据库备份和旧文档索引，再在 `SonnetDBServer:Copilot:Docs` 配置完整 profile 和独立 stream。以下仅是 builtin hash 合同示例，不代表真实模型质量：

```json
{
  "StorageMode": "rag",
  "RagStream": "copilot-docs-v1",
  "Roots": ["D:/manuals"],
  "AutoIngestOnStartup": false,
  "ChunkSize": 800,
  "ChunkOverlap": 100,
  "RagProfile": {
    "Id": "builtin-docs-v1",
    "Provider": "builtin",
    "Model": "builtin-hash",
    "Revision": "1",
    "Dimensions": 384,
    "Metric": "Cosine",
    "Normalization": "None",
    "SupportedModalities": ["Text", "Document"],
    "DataEgressPolicy": { "Mode": "LocalOnly", "AuditRequired": true }
  }
}
```

1. 将 provider 与 `RagProfile` 配成一致，重启 Server。RAG 尚未发布时，查询明确返回无 active generation 错误。
2. 使用已有受管理权限保护的 `sndb copilot ingest --dry-run` 检查扫描、UTF-8、分块和快照预算，再执行 `sndb copilot ingest`。该命令调用 Server 已配置的 roots。实际写入支持全文档更新和删除；roots 中不再存在的文件从新 active 快照删除。
3. 确认知识库状态和文档检索可用。失败时旧 active 继续服务；再次摄取会先续跑前次冻结任务，再处理当前完整快照。相同内容与 profile 不产生新 revision，也不重复 embedding。
4. 回滚时把 `StorageMode` 改为 `legacy` 并重启。旧表未被迁移过程覆盖或删除，仍返回迁移前的内容。回滚后可按旧摄取流程刷新旧表。

RAG 的 `--force` 仍通过 writer 的内容/profile 幂等合同；它不会强制为相同内容重算向量。需要模型换代时使用新的 profile ID 和 stream，保留旧配置可切回旧 stream；同 ID 更换模型合同被拒绝。更换 query profile 但未完成对应 generation 会失败，不把旧向量与新 query 向量混用。

## provider 与内容边界

`RagProfile` 必须同时支持 Text 和 Document。`builtin` 仅接受 `builtin-hash` revision `1` / 384 维；`openai` 必须匹配 `Copilot.Embedding.Model`，并用 `DataEgressPolicy` 显式批准完整 HTTPS 基地址。RAG 在线请求使用独立的禁止重定向、1 MiB 响应上界的 HTTP client；单项 index、返回模型和输出向量受检查，错误不回显上游正文。

本地 ONNX 必须显式配置 `Embedding.Model`、`Embedding.ModelProfile.ModelId` 与 `Revision`，并与 RAG 的 Model、Revision、Dimensions 和 Normalization 相同。部署者负责版本与实际模型/tokenizer文件绑定；RAG 检测到 hash fallback 时拒绝写入和查询，不将其计作真实语义输出。

每次 embedding 复用持久化 `__semantic_embedding_audit` 审计；要求审计时先写 `started` 并 fsync 再调用 provider，成功、失败和取消均保存脱敏状态。仍由现有管理/API 权限约束访问，RAG 不引入匿名摄取入口。

RAG roots 必须为 1–32 个存在的绝对路径；根目录缺失会拒绝发布，避免目录挂载失败被解释为删除。扫描最多 10,000 个文档、每根目录最多 20,000 个目录项、深度 16；单文件和总原文预算均为 4 MiB，分块上限 100,000，摄取协作期限 120 秒。读取严格 UTF-8 的 Markdown/HTML 原文，使用通用 `RagTextChunker`；HTML 标签保留为原文，本切片不提供富文档解析。文件以正文 hash 保存到 `copilot-rag-sources` Object Bucket，manifest 引用真实版本。原始快照保留，不随派生 generation 清理；保留期、物理删除和管理入口归 #305。

## 验证范围

本次合同测试覆盖在线外发拒绝、脱敏审计、取消/重试/resume、迁移幂等、失败保留旧 generation、完整快照删除、profile 不匹配以及切回旧表。当前 RAG 搜索使用 generation 租约与向量索引；通用融合/rerank 由 #303 交付。测试使用确定性 fixture，不证明真实模型的质量、延迟、成本、硬件掉电恢复或固定硬件容量。
