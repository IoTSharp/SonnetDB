---
title: SonnetDB 语义图片检索续篇：从能运行到可发布的证据边界
categories: SonnetDB,向量检索,AI,对象存储
draft: false
---

仓库已有语义图片检索数据流文章，但其旧稿尚未取得博客园账号侧确认，不在这里假定它已发布。本篇讨论发布前容易被忽略的证据边界：对象版本、模型 profile、过滤条件、恢复行为和真实质量必须分别核验，不能把“样例能跑”写成生产承诺。SonnetDB 的路径是：图片保存在 Object Bucket，SigLIP2 将文字和图片编码到同一向量空间，再由 USearch 或托管 HNSW 做近似最近邻检索；Studio 和 REST 暴露文搜图、图搜图及相似图片查询。

本文于 2026-10-07 对照当前 DTO、REST 路由、图片服务与样例复核，公开源码文档基线为 `c785f0479686a3d6584787e1a2b40bd67b0e38c5`。已核实 GitHub `v4.0.0` Release 与 NuGet.org `SonnetDB.Core 4.0.0` 存在；本次未启动 Server、下载模型或执行真实质量评测。

## 数据流

1. 上传图片并保存对象 key、版本和校验信息。
2. 由派生任务读取对象，生成 embedding 和模型/profile 标识。
3. 在当前 embedding profile 的目录中保存元数据与持久向量，维护可重建的 ANN 加速层。
4. 查询时检查数据库权限、provider 状态与过滤条件，再执行 Top-K。

图片异步任务的对象版本校验不能等同于 Text/Document RAG 的完整 generation 原子发布合同。覆盖、删除和派生清理也有各自的异步窗口，须通过对象与任务状态核对。

## 准备模型和输入

该能力默认关闭，部署者须提供 `text_model.onnx`、`vision_model.onnx` 和 `tokenizer.model`，Server 不自动下载模型。在 `SonnetDBServer:SemanticSearch` 配置 `Enabled=true`、`Provider=siglip2-onnx`、明确的 `Profile`、模型路径与 `Dimensions`；参考 base-patch16-224 的维度为 768、图片尺寸为 224。其他模型导出须核对 tensor 名称和输出维度，不能直接套用。

模型、量化或预处理改变时使用新 profile 并重摄取。当前本地 provider 给有效 profile 添加 `:skia-rgba-v1` 以隔离预处理；旧向量不会因模型文件替换自动变成兼容数据。

先在已初始化的 Server 上创建数据库，准备有 Read/Write 权限的 Token。下面在 PowerShell 7 中运行工业样例，`D:\industrial-images` 应是用于本次验证的真实图片目录：

```powershell
$env:SONNETDB_TOKEN='<admin-or-database-token>'
dotnet run --project samples/SonnetDB.SemanticImages -- `
  --server http://127.0.0.1:5080 `
  --images D:\industrial-images `
  --text '夜间车道上的红色重型卡车'
```

样例会创建数据库和 Bucket、开启异步摄取与缩略图、上传并等待任务，随后执行无过滤/metadata 过滤文搜图、图搜图和 similar-by-id。目录为空时生成三张确定性工业 PNG，只适合链路回归，不能据其结果宣称真实现场 Recall。

## 用实际 DTO 发起文搜图

以下是 Bash/curl 示例；Token 来自运行环境，`demo` 与 `camera-images` 必须替换为实际数据库和源 Bucket。源桶至少应有已完成派生处理的图片。

```bash
curl --max-time 30 -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"text":"夜间车道上的红色重型卡车","topK":10,"filter":{"sourceBucket":"camera-images"},"explain":true}' \
  http://127.0.0.1:5080/v1/db/demo/images/search/text
```

```text
POST /v1/db/{db}/images/search/text
{
  "text": "夜间车道上的红色重型卡车",
  "topK": 10,
  "filter": { "sourceBucket": "camera-images" },
  "explain": true
}
```

当前服务端入口是 `/v1/db/{db}/images/search/text`，DTO 使用 `text`，没有本文旧草稿中曾出现的顶层 `query` 和 `bucket` 字段。另有 `/images/search/image` 与 `/images/{id}/similar`；请求字段与版本以实际部署合同为准。

响应中 `score = 1 - cosineDistance`，越大越相似；`distance` 越小越近。`contentUrl` 仍需鉴权，不是公开图片下载链接。`explain=true` 时检查 `backend`、`searchMode`、`candidateCount` 和 `filteredCandidateCount`。空结果可能由过滤、阈值或未完成摄取造成，不能只凭空结果判断模型失效。

## 筛选与恢复

metadata/tag 采用全部键值精确匹配；MIME 使用 `contentType`，源桶使用 `sourceBucket`。无过滤通常走 ANN；可索引条件可走 managed HNSW 的 `prefiltered-ann`，不足时按条件执行精确补偿/回退。只含 key prefix 或 MIME 的过滤可能走分页精确回退，分页不等于整个索引的内存上限。

显式 `Backend=usearch` 且关闭 managed fallback 时，带过滤查询返回 `semantic_provider_unavailable` 503，不会偷偷换后端。开启允许回退时，应在结果里核对实际 `backend` 与 `searchMode`，而不是仅记录配置名。

普通对象上传响应的 `x-sonnetdb-processing-job-id` 代表排队，不代表 embedding 完成。通过 `GET /v1/db/{db}/s3/{bucket}/{key}?processing` 检查任务：`pending/processing/retry` 仍在处理，`completed` 表示该任务完成，`failed` 需处理，覆盖后旧任务可能为 `superseded`。对象上传、派生处理和删除清理必须分别记录；持久任务重开续跑的实现不能替代目标环境断电恢复证据。

若原始图片通过 `SndbObjectTransferManager` 上传，其 resume manifest 与 SHA-256 是文件传输边界：分片可有限重试，普通 PUT 和 Complete 发送后的未知结果不能盲重放。传输完成也不证明语义派生完成。

## 当前边界

语义图片检索已经有可运行的工业样例和 Native AOT 发布路径，但真实模型质量、Recall、目标硬件容量、跨网络部署和长期稳定性仍需独立证据。确定性生成图片、hash fallback 或小型合成语料只证明流程可运行，不证明 SigLIP2 在真实现场的检索效果。ONNX Runtime 和 USearch 的原生资产也应按发行物和平台单独核验。

USearch 是可丢弃的内存加速层，权威数据是持久图片/文档向量。通用 VectorData 的 `PreflightAsync`、健康与已加载图重建属于另一个入口，当前远程 lifecycle 明确不支持；`verified` 仅表示 profile 合同一致，不代表 Recall 已通过。正式发布应另存模型文件摘要、profile、RID、真实语料与标注、结果/失败样本以及目标硬件报告。本篇不提供未经实测的延迟、吞吐或准确率数字。

参考：[语义图片合同与完整配置](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/semantic-image-search.md)、[图片 DTO](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/src/SonnetDB/Contracts/SemanticSearchContracts.cs)、[Object Transfer Manager](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/object-transfer-manager.md)、[VectorData 生命周期](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/vector-lifecycle-preflight.md)、[工业图片样例](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/samples/SonnetDB.SemanticImages/README.md)。
