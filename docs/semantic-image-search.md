---
layout: default
title: "语义图片检索"
description: "配置 SigLIP2 ONNX，并通过 SonnetDB REST API 执行文搜图和图搜图。"
---

# 语义图片检索

SonnetDB Server 可以使用 SigLIP2 把文本和图片编码到同一个向量空间，并通过 REST API 提供文搜图和图搜图。该能力默认关闭，模型文件由部署者提供；Server 不会在启动或请求期间自动下载模型。

当前实现已经包含可恢复异步摄取、缩略图、metadata/tag 过滤、similar-by-id、explain、生命周期清理、管理界面和工业图片样例。可索引的 source bucket、metadata 和 tag 条件使用 managed HNSW 预过滤 ANN，并保留精确补偿/回退；通用内容 chunk/segment、调用审计、质量评测和容量报告仍是后续工作。

## 组件与持久化

| 环节 | 实现 |
| --- | --- |
| 文本 tokenizer | `Microsoft.ML.Tokenizers` 的 SentencePiece Unigram，lowercase、EOS、64 token 右侧 padding |
| 图片预处理 | ImageSharp 解码，EXIF auto-orient，双线性缩放到 `224x224`，RGB `[0,255]` 映射到 `[-1,1]` |
| 模型推理 | ONNX Runtime CPU provider，启用全图优化和 MLAS CPU SIMD |
| 原始图片 | 当前数据库的内部 Object Bucket `sonnetdb-semantic-images` |
| 元数据与向量 | 按 embedding profile 隔离的内部 Document Collection 与持久化向量字段 |
| Bucket 异步摄取 | KV 持久化任务 + 256 容量 Channel，支持退避重试、取消、背压补偿和重启恢复 |
| 缩略图 | ImageSharp 生成 WebP，Lanczos3 等比缩放，保存在内部 Bucket `sonnetdb-semantic-thumbnails` |
| 默认 ANN | `auto`：受支持 RID 使用 USearch，否则使用 SonnetDB 纯托管 HNSW |

USearch 是可丢弃、可从 Document 向量重建的内存加速层，不是第二份权威数据。即使 USearch 加载失败，图片与向量仍完整保留，搜索可回退到 managed HNSW。当前过滤 ANN 只由 managed HNSW 实现；`Backend=usearch` 且 `FallbackToManaged=false` 时，带过滤的查询返回 503，而不是静默切换后端。

## 模型文件

参考模型为 `onnx-community/siglip2-base-patch16-224-ONNX`，至少需要以下三个文件：

```text
models/siglip2-base-patch16-224/
  text_model.onnx
  vision_model.onnx
  tokenizer.model
```

纯 CPU 部署可改用同仓库的 `text_model_int8.onnx` 与 `vision_model_int8.onnx`，通常能显著降低模型体积和推理开销；FP32 模型适合作为质量基线。量化模型应使用独立 profile，例如 `siglip2-base-patch16-224-int8`，并在目标硬件上以 Recall@K、P95 延迟和常驻内存决定最终方案。

配置默认 tensor 名称为：

| 模型 | 输入 | 输出 |
| --- | --- | --- |
| 文本 | `input_ids` | `pooler_output` |
| 视觉 | `pixel_values` | `pooler_output` |

不同 ONNX 导出若使用不同名称，可通过 `TextInputName`、`TextOutputName`、`VisionInputName` 和 `VisionOutputName` 覆盖。Provider 会在首次加载 session 时校验名称，并校验输出维度与 `Dimensions` 一致。

## 配置

在 `SonnetDBServer` 下加入：

```json
{
  "SemanticSearch": {
    "Enabled": true,
    "Provider": "siglip2-onnx",
    "Profile": "siglip2-base-patch16-224",
    "TextModelPath": "./models/siglip2-base-patch16-224/text_model.onnx",
    "VisionModelPath": "./models/siglip2-base-patch16-224/vision_model.onnx",
    "TokenizerModelPath": "./models/siglip2-base-patch16-224/tokenizer.model",
    "Dimensions": 768,
    "MaxTextTokens": 64,
    "ImageSize": 224,
    "MaxImageBytes": 20971520,
    "TextInputName": "input_ids",
    "TextOutputName": "pooler_output",
    "VisionInputName": "pixel_values",
    "VisionOutputName": "pooler_output",
    "Backend": "auto",
    "FallbackToManaged": true,
    "DefaultTopK": 10,
    "MaxTopK": 100
  }
}
```

`Profile` 是向量兼容边界。更换模型、量化方案或预处理语义时，应使用新的 profile 名称。SonnetDB 会创建新的 profile 隔离索引，避免把不兼容向量混入旧索引；旧 profile 的索引和对象不会被自动删除。

## REST API

所有数据库级端点继续使用 SonnetDB Bearer Token 和数据库权限。摄取/删除需要 write 权限，读取/搜索需要 read 权限。

## SonnetDB Studio

在 Studio 的对象桶工作台选择“图片语义”可以完成以下操作：

- 读取和保存 Bucket 的异步语义摄取、WebP 缩略图及尺寸/质量选项；
- 对存量对象发起 Backfill，查看当前对象任务状态并手工重新入队；
- 查看受鉴权保护的缩略图；
- 执行文搜图、上传图片图搜图和 similar-by-id；
- 设置 Bucket、Key Prefix、Content-Type、metadata 和 tag 过滤，并查看 explain 的后端、执行模式和候选统计。

这些选项在新 Bucket 上默认关闭，只有管理员显式保存配置后才会产生派生任务。

### 查看状态

```http
GET /v1/semantic-search/status
Authorization: Bearer <token>
```

响应会同时返回 `configuredBackend` 和 `effectiveBackend`。例如显式配置 `usearch`，但当前 RID 不受支持且允许回退时，前者为 `usearch`，后者为 `managed`，`reason` 说明回退原因。

### 摄取图片

图片直接作为请求体上传，不使用 Base64：

```bash
curl -X PUT \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: image/jpeg" \
  --data-binary @truck-001.jpg \
  "http://127.0.0.1:5080/v1/db/demo/images/truck-001?fileName=truck-001.jpg&sourceUri=camera-01"
```

同一 `id` 再次写入会替换当前 profile 的图片目录记录与向量。对象键使用 profile、业务 ID 和内容 hash 派生，不把用户文件名直接作为磁盘路径。

### 文搜图

```bash
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"text":"红色重型卡车","topK":10,"minScore":0.2}' \
  http://127.0.0.1:5080/v1/db/demo/images/search/text
```

### 图搜图

```bash
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: image/jpeg" \
  --data-binary @query.jpg \
  "http://127.0.0.1:5080/v1/db/demo/images/search/image?topK=10&minScore=0.2"
```

搜索响应中的 `score` 是 `1 - cosineDistance`，越大越相似；`distance` 越小越相似。`contentUrl` 是受同一权限保护的原图读取地址。

### Bucket 异步摄取与缩略图

两项能力是 Bucket 级持久化选项，默认都关闭。先创建 Bucket，再显式开启需要的能力：

```bash
curl -X PUT \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"purpose":"camera-images"}' \
  http://127.0.0.1:5080/v1/db/demo/s3/camera-images

curl -X PUT \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "asyncIngestionEnabled":true,
    "thumbnailEnabled":true,
    "thumbnailMaxWidth":320,
    "thumbnailMaxHeight":320,
    "thumbnailQuality":80
  }' \
  "http://127.0.0.1:5080/v1/db/demo/s3/camera-images?semantic"
```

配置和补录端点：

```http
GET  /v1/db/{db}/s3/{bucket}?semantic
PUT  /v1/db/{db}/s3/{bucket}?semantic
POST /v1/db/{db}/s3/{bucket}?semantic
```

`POST` 会为当前可见的 `image/*` 对象建立补录任务。启用后，普通 PUT、Presigned PUT、CopyObject、Multipart Complete 和 Frame PUT 都会排入派生任务；成功排队的写响应包含 `x-sonnetdb-processing-job-id`。任务先写入 KV keyspace，再投递到有界内存队列，因此队列满或进程重启不会静默丢任务。

```http
GET  /v1/db/{db}/s3/{bucket}/{key}?processing
POST /v1/db/{db}/s3/{bucket}/{key}?processing
GET  /v1/db/{db}/s3/{bucket}/{key}?thumbnail
GET  /v1/db/{db}/images/{id}/thumbnail
```

任务状态包括 `pending`、`processing`、`retry`、`completed`、`failed`、`superseded` 和 `cancelled`，失败最多指数退避重试 5 次。覆盖同一对象时旧版本任务会标记为 `superseded`；普通删除、批量删除和生命周期过期都会异步清理语义文档、ANN 记录和缩略图。生命周期响应额外返回实际过期对象的 `key`、`versionId`、`contentType` 以及 `semanticCleanupJobs`，retention 或 legal hold 跳过的对象不会排入清理任务。

只开启 `thumbnailEnabled` 时不要求语义 provider 就绪。缩略图不会放大小图，最大解码像素数为 100,000,000。

### metadata/tag 过滤与 explain

文搜图在 JSON 请求中传递过滤条件。`metadata` 和 `tags` 都采用全部键值精确匹配：

```json
{
  "text": "红色重型卡车",
  "topK": 10,
  "filter": {
    "sourceBucket": "camera-images",
    "sourceKeyPrefix": "lane-01/",
    "contentType": "image/jpeg",
    "metadata": { "site": "mulei" },
    "tags": { "vehicleType": "truck" }
  },
  "explain": true
}
```

图搜图使用查询参数表达同一组条件：

```text
sourceBucket=camera-images
sourceKeyPrefix=lane-01/
contentType=image/jpeg
metadata.site=mulei
tag.vehicleType=truck
explain=true
```

没有过滤条件时继续使用 USearch 或 managed HNSW。带 `sourceBucket`、`metadata` 或 `tags` 时，Document path/wildcard index 选择最小候选入口并按 256 行分页读取，每页和每行都观察请求取消；managed HNSW 可穿过未允许节点保持图连通，但只把允许 ID 放入结果。ANN 候选不足时，允许集合不超过 4096 项会在向量索引内做精确补偿；更大的集合不再整体保留候选 ID/文档，而是沿同一索引分页计算 exact top-K。HNSW 派生图缺少任一允许 ID 时也会要求精确回退。只包含 `sourceKeyPrefix` 或 `contentType` 时没有可用的 path prefilter，会分页执行完整精确回退；它们与可索引条件组合时仍会作为 residual filter 复核候选。

`explain=true` 时响应额外返回 `searchMode`、`candidateCount` 和 `filteredCandidateCount`；默认不返回这些字段。实际路径对应关系如下：

| `backend` | `searchMode` | 含义 |
| --- | --- | --- |
| `usearch` / `managed` | `ann` | 无过滤 ANN |
| `managed` | `prefiltered-ann` | Document 预过滤后由 managed HNSW 返回足量结果 |
| `managed` | `prefiltered-ann-exact-compensation` | 小候选集在 ANN 不足后完成索引内精确补偿 |
| `exact-filtered` | `exact-filtered-fallback` | 不可索引过滤或大候选 ANN 不足后的精确回退 |

显式 `Backend=usearch` 且允许 managed fallback，或 `Backend=auto` 时，过滤查询会如实返回 `backend=managed`；显式 `usearch` 且关闭 fallback 时返回 `semantic_provider_unavailable` 503。

### 按已摄取图片查相似图片

```bash
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"topK":10,"minScore":0.2,"explain":true}' \
  http://127.0.0.1:5080/v1/db/demo/images/truck-001/similar
```

该端点直接复用已保存的 embedding，不重新读取或推理源图片，并默认从结果中排除源图片自身。请求也可以携带与文搜图相同的 `filter`。

### 元数据、原图与删除

```http
GET    /v1/db/{db}/images/{id}
GET    /v1/db/{db}/images/{id}/content
GET    /v1/db/{db}/images/{id}/thumbnail
DELETE /v1/db/{db}/images/{id}
```

## 可运行工业图片样例

仓库中的 [`samples/SonnetDB.SemanticImages`](https://github.com/IoTSharp/SonnetDB/tree/main/samples/SonnetDB.SemanticImages) 会创建数据库和 Bucket、开启派生处理、等待任务完成，并执行四条检索路径。图片目录为空时会生成叉车、泵组和输送线三张确定性 PNG；质量评估时应改用真实现场图片目录。

```powershell
$env:SONNETDB_TOKEN='<admin-or-database-token>'
dotnet run --project samples/SonnetDB.SemanticImages -- `
  --server http://127.0.0.1:5080 `
  --images D:\industrial-images `
  --text '夜间车道上的红色重型卡车'
```

样例输出 `backend`、`searchMode`、过滤前后候选数、分数、源对象和缩略图 URL，可直接确认当前 RID 使用 USearch 还是 managed HNSW。

## Native AOT 与平台边界

应用代码、JSON 合同、tokenizer 和图片预处理均通过 SonnetDB 的 AOT analyzer 构建。ONNX Runtime CPU provider 仍包含平台原生推理库；“可由 Native AOT 程序调用”不等于“纯 C# 推理”。

`Cloud.Unum.USearch 2.26.0` 当前 NuGet 原生资产范围为：

| RID | USearch |
| --- | --- |
| `win-x64` | 支持 |
| `linux-x64` | 支持 |
| `osx-arm64` | 支持 |
| Windows/Linux ARM64、macOS x64 | 不支持，`auto` 使用 managed HNSW |

ONNX Runtime 1.27.1 包含 Windows x64/ARM64、Linux x64/ARM64 和 macOS ARM64 CPU 资产。因此 ARM64 Linux 可以运行 SigLIP2 ONNX，但 ANN 会使用 SonnetDB managed HNSW。部署前仍应针对目标 RID 执行 Native AOT publish 和真实模型 smoke test。
