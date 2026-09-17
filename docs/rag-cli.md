# RAG bundle 摄取、在线 embedding 与续跑

`sndb rag` 把完整内容快照写入本地数据库，复用 `RagIngestionWriter` 的持久任务、Document/FullText/Vector 派生索引和 generation 原子发布。默认使用预计算向量；显式提供在线端点与外发授权后，可由 OpenAI-compatible provider 自动生成向量。所有 JSON 使用 source-generated metadata，不生成替代 embedding。

## 执行

```text
sndb rag ingest --input ./rag-bundle.json --path ./data --stream manuals --dry-run
sndb rag ingest --input ./rag-bundle.json --path ./data --stream manuals --replace-snapshot
sndb rag resume --input ./rag-bundle.json --path ./data --stream manuals
```

`ingest` 接受**完整期望快照**，省略上次的内容会从新 active generation 删除该内容，所以实际写入必须显式传入 `--replace-snapshot`。空 `manifests` 发布空集合。`--dry-run` 只验证输入、profile 和向量绑定，不打开数据库，也不预测目标端删除数量；输出 `targetNotRead: true`。

失败或取消保留 staging 与冻结任务。`resume` 从数据库读取该任务，忽略 bundle 的 `snapshot`（可以省略），只需要原来的完整 `profile` 和尚缺的预计算向量。已成功的 chunk 可以复用。已有未完成任务时，新 `ingest` 会拒绝替换；SDK 的 `DiscardPendingAsync` 可显式放弃尚未发布的任务。重启后的续跑受数据库 WAL 耐久配置约束；受控重开测试不代表硬件掉电证据。

命令只打开明确指定的本地数据库目录，应在其没有被其他进程占用时执行。输入是单个最多 16 MiB 的 JSON 文件，不递归扫描目录；最多 10,000 个清单、100,000 个分块/向量，正文总量受 writer 的 4 Mi UTF-16 字符预算约束。默认期限 120 秒，`--timeout` 可设为 1–600 秒，Ctrl+C 传播取消。完成时 stdout 输出 JSON，错误写入 stderr 并返回非零退出码。

## Bundle 合同

根字段：`schemaVersion: 1`、`profile`、`snapshot`（ingest 必需）和 `vectors`。

每个向量通过 `(chunkId, textSha256)` 绑定正文；SHA-256 使用精确 UTF-8 字节，大小写十六进制均可。命令检查完整维度和有限数值，writer 进一步检查 profile 归一化合同。不能用同一个 profile ID 更换 provider、模型、revision、维度或其他合同字段；模型换代需要新 ID。

以下双维向量仅用于执行合同示例，不是语义模型或检索质量证据：

```json
{
  "schemaVersion": 1,
  "profile": {
    "id": "example-fixture-v1",
    "provider": "offline-fixture",
    "model": "contract-example-only",
    "revision": "1",
    "dimensions": 2,
    "metric": "Cosine",
    "normalization": "None",
    "supportedModalities": ["Text"]
  },
  "snapshot": {
    "schemaVersion": 1,
    "manifests": [{
      "id": "manual-1",
      "objectRef": { "bucket": "manuals", "key": "manual-1.txt", "versionId": "fixture-v1" },
      "contentHash": "8ed3f6ad685b959ead7022518e1af76cd816f8e8ec7ccdda1ed4018e8f2223f8",
      "mimeType": "text/plain",
      "modality": "Text",
      "sizeBytes": 5,
      "embeddingProfileId": "example-fixture-v1",
      "chunks": [{ "id": "manual-1:0", "ordinal": 0, "text": "alpha" }]
    }]
  },
  "vectors": [{
    "chunkId": "manual-1:0",
    "textSha256": "8ed3f6ad685b959ead7022518e1af76cd816f8e8ec7ccdda1ed4018e8f2223f8",
    "values": [1, 0]
  }]
}
```

生产 bundle 的分块可由 `RagTextChunker` 生成，向量由调用方选定的真实 provider 预计算；调用方负责原始对象、来源、外发治理和审计。此命令不上传或验证原始对象内容。

## 发布与边界

成功输出 `status: "published"`、revision、generationId、新增/更新/删除内容数，以及本次生成/复用 chunk 数。相同完整快照不产生新 revision。`resume` 没有任务时输出 `no_pending_task`。

读取端必须通过 `database.Generations.AcquireActive(stream)` 获取租约，再按 `RagIngestionWriter.ChunksResourceRole` 打开本版本的 Document collection；正文和向量索引分别为 `rag_text` 与 `rag_vector`。旧版本仍服务已有租约，`CleanupRetired` 在租约释放后清理旧 Document、FullText、Vector 和快照 KV。

当前 CLI 支持 Text/Document 分块的预计算向量和显式在线 embedding。远程数据库摄取、自动文件解析、媒体分段、固定硬件容量和真实模型质量评测仍未完成。SDK 合同见 [RAG 持久摄取](rag-ingestion-core.md)，Copilot 接线见 [可回滚迁移](copilot-rag-migration.md)。

## 在线 provider

```text
sndb rag ingest --input ./rag-online.json --path ./data --stream manuals --replace-snapshot --endpoint https://provider.example/v1/ --api-key-env EMBEDDING_API_KEY --allow-egress --audit ./rag-audit.jsonl
sndb rag resume --input ./rag-online.json --path ./data --stream manuals --endpoint https://provider.example/v1/ --api-key-env EMBEDDING_API_KEY --allow-egress --audit ./rag-audit.jsonl
```

在 bundle 中保留完整 `profile` 和 `snapshot`，省略 `vectors` 或使用空数组。`profile.provider` 必须为 `openai`，`model` 是发送给 provider 的实际模型名称，`revision` 由部署方绑定模型版本；模态、维度、归一化必须与真实模型一致。外发策略示例：

```json
"dataEgressPolicy": {
  "mode": "ConfiguredProvider",
  "target": "https://provider.example/v1/",
  "auditRequired": true
}
```

端点必须为以 `/` 结尾的 HTTPS 基地址，无 URL 凭据、query 或 fragment；策略 target 必须与规范化后的完整基地址一致，包含路径。请求发往该地址的 `embeddings`，禁用重定向。凭据只从指定环境变量读取，不写入 bundle、checkpoint、报告或审计。请由 shell 或凭据工具设置环境变量，不把真实 key 放入命令参数。

实际写入还必须提供 `--allow-egress`；profile 要求审计时必须提供 `--audit`。每次请求先追加脱敏 `started` 并刷盘，再发送正文，完成后追加相同 request ID 的终态。审计记录 profile、端点、正文 SHA-256 和状态，不含正文、向量、API key 或上游错误正文。该 JSONL 是调用方管理的文件，不包含在数据库备份内；须独立保留。文件 flush/fsync 和底层同步数据库操作是协作超时边界，不保证操作系统 I/O 可硬中断。

单次 HTTP 超时 60 秒、响应上限 1 MiB；完整任务仍受 `--timeout` 约束。408、429、5xx 和网络异常由 writer 最多尝试 3 次；其他状态、重定向、非法 JSON、错模型、错 index、错维度和归一化不符直接失败。失败或取消保留旧 active generation 和冻结任务，`resume` 使用原完整 profile 继续缺失分块。在线模式不能混入预计算向量。

`--dry-run` 校验 profile、端点、外发参数和完整快照，既不请求 provider，也不要求凭据已经设置，不打开数据库或审计文件，因此不证明 provider 可达、模型输出正确或语义质量。
