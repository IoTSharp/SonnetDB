---
title: 自动文档摄入管道：Chunking、Embedding 与向量检索
categories: SonnetDB,RAG,知识库
draft: false
---

文档摄取经历扫描、分块、向量生成和索引发布。SonnetDB 已有文档摄取和持久 RAG 组件，但自动化仍需要明确来源、预算、模型 profile 与失败恢复，不能写成“全程零人工干预”或把所有向量固定为 384 维。

## 先核对来源与分块

现有 Copilot 文档入口可以先 dry-run，根目录由服务端解释，不能把客户端路径误当成自动上传本地目录：

```bash
sndb copilot ingest --root ./docs --endpoint http://127.0.0.1:5080 --dry-run
```

服务端需要配置相应 Copilot 子系统与管理员授权。实际请求凭据可由部署方通过 `SONNETDB_COPILOT_TOKEN` 等安全配置提供。dry-run 不证明实际向量写入或模型质量。

当前 Core 还提供稳定文本分块：

```csharp
using SonnetDB.SemanticContent;

var snapshot = RagTextChunker.Chunk(
    "manuals/pump-1001",
    "泵的维护说明：定期检查密封、振动和工作温度。",
    new RagTextChunkingOptions
    {
        MaxCharacters = 800,
        OverlapCharacters = 100,
        MaxInputCharacters = 4 * 1024 * 1024,
        MaxChunks = 10_000,
    },
    CancellationToken.None);

Console.WriteLine(snapshot.ContentHash);
Console.WriteLine(snapshot.Chunks.Count);
```

相同 ID、正文和选项产生稳定内容 hash 与 chunk 身份。Embedding 由调用方选定的 provider/profile 生成；维度与归一化必须匹配索引，不能混入另一个模型的向量。

## 持久摄取与续跑

对按 RAG bundle 合同准备的完整快照，可以先离线校验再明确发布：

```bash
sndb rag ingest --input ./rag-bundle.json --path ./data --stream manuals --dry-run
sndb rag ingest --input ./rag-bundle.json --path ./data --stream manuals --replace-snapshot
sndb rag resume --input ./rag-bundle.json --path ./data --stream manuals
```

这些命令是当前主线 RAG 合同，不能未经版本核对直接等同于任何旧包。输入是完整期望快照，省略旧内容会形成删除，因此需要 `--replace-snapshot`。失败保留冻结任务和 staging，完整 generation 才原子发布；重开续跑仍受 WAL 耐久配置约束。

## 检索与证据

检索结果要保留来源、内容 hash、chunk 和 profile 身份，便于回答引用。hash fallback、合成向量与离线成功只能说明合同路径；真实语义召回、错误引用和成本需要使用真实模型与标注样本评估。

本文未运行扫描、摄取或模型请求。正式 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)、当前主线及真实生产证据分别核对。

参考：[RAG Core](https://github.com/IoTSharp/SonnetDB/blob/main/docs/rag-ingestion-core.md)、[RAG CLI 与 bundle 完整结构](https://github.com/IoTSharp/SonnetDB/blob/main/docs/rag-cli.md)、[Provider](https://github.com/IoTSharp/SonnetDB/blob/main/docs/copilot-providers.md)、[Copilot 迁移](https://github.com/IoTSharp/SonnetDB/blob/main/docs/copilot-rag-migration.md)。
