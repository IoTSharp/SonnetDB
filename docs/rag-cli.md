# 本地 RAG bundle 摄取与续跑

`sndb rag` 把完整内容快照和预计算向量写入本地数据库，复用 `RagIngestionWriter` 的持久任务、Document/FullText/Vector 派生索引和 generation 原子发布。所有 JSON 使用 source-generated metadata；命令不访问网络，不生成替代 embedding。

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

当前 CLI 仅提供 Text/Document 分块的本地预计算向量模式。远程摄取、自动文件解析、在线 provider 接线、媒体分段、Copilot 可回滚迁移、固定硬件容量和真实模型质量评测仍未完成。SDK 合同见 [RAG 持久摄取](rag-ingestion-core.md)。
