## SonnetDB 新增语义图片检索：用 SigLIP2 + USearch 实现文搜图与图搜图

对象存储里的图片越来越多之后，最先失效的往往不是容量，而是文件名。

`camera-01/2026/07/26/000184.jpg` 对程序来说是一个准确的 Key，但对人来说几乎没有检索价值。我们真正想问的通常是：“找到夜间车道上的红色重型卡车”“有没有和这张故障泵照片相似的图片”“只看木垒现场一号车道的 JPEG”。

SonnetDB 当前实现连接了这些处理入口：图片继续保存在 Object Bucket 中，SigLIP2 把文字和图片编码到同一个向量空间，USearch 或托管 HNSW 负责近似最近邻检索，Studio 和 REST API 则提供文搜图、图搜图和相似图片查询。

Bucket 可以选择开启持久化异步摄取和 WebP 缩略图，任务包含重试、重启恢复、存量补录，以及对象删除和生命周期过期后的派生数据清理。本文依据当前 main 的文档与源码校正历史稿；正式 [4.0.0 release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 需独立核对，不能把后续 main 功能全部当作正式 4.0.0 已交付。这里没有重新运行真实模型、Server 或各宿主端到端测试。

### 这次新增了什么

这次图片工作流包含以下能力：

- 使用 SigLIP2 ONNX 为文字和图片生成同一维度的 embedding；
- 无过滤查询可使用 USearch；在允许回退的配置与支持条件下使用 SonnetDB 托管 HNSW；
- 提供文搜图、上传图片搜图和 similar-by-id 三种检索入口；
- 支持 Bucket、对象 Key 前缀、Content-Type、metadata 和 tag 过滤；
- 支持 `explain`，返回实际后端、执行模式和过滤前后候选数量；
- Bucket 可独立开启异步语义摄取和 WebP 缩略图，两项默认都关闭；
- 异步任务先持久化再进入有界队列，支持重试、背压补偿和重启恢复；
- 可以对 Bucket 中已有的 `image/*` 对象执行 Backfill；
- 覆盖对象时旧任务会失效，删除和生命周期过期会清理向量、ANN 记录和缩略图；
- SonnetDB Studio 新增“图片语义”工作区；
- 提供可运行、可 Native AOT 发布的工业图片样例。

整个数据流可以简化为：

```text
Object Bucket
    -> 持久化派生任务
    -> SigLIP2 ONNX 文本/图片编码
    -> Document 中的元数据与权威向量
    -> USearch 或 managed HNSW 加速检索
    -> REST API / SonnetDB Studio
```

USearch 是可重建的内存加速层，Document 中保存的向量才是权威数据。后端加载与回退不会把 USearch 索引变成另一份权威数据；是否可以切换托管 HNSW，仍以实际配置、错误类型和状态回执为准，不能把任何服务错误都当作可以静默回退。

### SigLIP2、USearch 和“纯 C#”的边界

这次选择的原则不是“所有依赖必须纯 C#”，而是“SonnetDB 应用和合同保持 Native AOT 兼容，同时在可用平台优先采用成熟、高性能实现”。

SigLIP2 通过 ONNX Runtime CPU provider 执行。应用代码、source-generated JSON、tokenizer 接线和图片预处理可以通过 AOT 分析，但 ONNX Runtime 本身仍带有各平台的原生推理库。

USearch 同样是原生 ANN 引擎的 .NET 封装，不是纯 C#。它为支持的部署提供原生 ANN 路径；`Backend=auto` 按支持条件选择后端。实际吞吐量和延迟需要在目标硬件上比较，不从原生实现推定性能优势。

因此，这里的准确表述是：

> SonnetDB Server 可以作为 Native AOT 应用发布，并调用 ONNX Runtime 和 USearch 的原生资产；Native AOT 兼容不等于整个运行栈都是纯 C#。

部署时需要分别验证 ONNX 推理资产、图片处理资产和 ANN 后端，不能用其中一项可以加载代替整套服务已经可用。

### 准备 SigLIP2 模型

参考模型是 [`onnx-community/siglip2-base-patch16-224-ONNX`](https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX)。SonnetDB 不会在启动或请求期间自动下载模型，部署者需要准备：

```text
models/siglip2-base-patch16-224/
  text_model.onnx
  vision_model.onnx
  tokenizer.model
```

纯 CPU 部署也可以使用同仓库的 `text_model_int8.onnx` 和 `vision_model_int8.onnx`。量化模型通常更小、更快，但建议使用独立的 profile 名称，并在真实图片集上比较 Recall@K、P95 延迟和常驻内存后再决定是否替换 FP32 基线。

`Profile` 是向量兼容边界。只要模型、量化方式、输入尺寸或预处理语义发生变化，就应该使用新的 profile，不能把不兼容的向量混入旧索引。

当前本地 SigLIP2 Provider 自动为有效 profile 添加 `:skia-rgba-v1` 后缀。例如配置 `siglip2-base-patch16-224`，实际使用 `siglip2-base-patch16-224:skia-rgba-v1`。这是从 ImageSharp 迁移到 SkiaSharp 后的预处理隔离：已有 Bucket 图片需 Backfill，直接摄取图片需重新提交原图；旧 profile 的原图与向量不会自动删除。

图片处理由 SkiaSharp 4.152.1 和 TiffLibrary 0.6.65 承担，Core 不引入图片库。接口明确支持 PNG/APNG、JPEG、WebP、GIF、BMP、ICO、TIFF；动图取第一帧，TIFF 取第一页，处理方向后再缩放。未知、损坏或超限图片属于永久输入失败，即使伪造 MIME 也不会因此获得支持。Skia 原生解码没有硬中断接口，取消主要在阶段前后检查，超时预算不能被描述为原生 CPU 的硬截止时间。

### 启用服务端 Provider

在 `SonnetDBServer` 下配置 `SemanticSearch`：

```json
{
  "SonnetDBServer": {
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
}
```

启动 Server 后先检查运行状态：

```bash
curl --fail-with-body --max-time 30 -H "Authorization: Bearer $TOKEN" \
  http://127.0.0.1:5080/v1/semantic-search/status
```

响应中的 `ready` 应为 `true`。`configuredBackend` 是配置值，`effectiveBackend` 是当前实际使用的后端。例如配置为 `auto`，有效后端可能为 `usearch` 或 `managed`，以本机状态回执为准。

如果模型路径、tensor 名称或输出维度不正确，`reason` 会给出 Provider 未就绪的原因。不要在看到 `enabled=true` 后就假定模型已经加载成功，应同时检查 `ready` 和 `effectiveBackend`。

### 两种摄取方式

SonnetDB 提供两条图片摄取路径。

第一条是直接调用图片语义 API：

```bash
curl --fail-with-body --max-time 30 -X PUT \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: image/jpeg" \
  --data-binary @truck-001.jpg \
  "http://127.0.0.1:5080/v1/db/demo/images/truck-001?fileName=truck-001.jpg&sourceUri=camera-01"
```

它适合调用方已经明确把图片作为语义对象管理的场景。同一个 `id` 再次写入会替换当前 profile 下的图片目录记录和向量。

第二条也是更适合对象存储工作流的一条：先把图片写入 Bucket，再由 Bucket 的持久化异步任务生成 embedding 和缩略图。接下来重点演示这条路径。

### 创建 Bucket 并显式开启异步派生

先创建 Bucket：

```bash
curl --fail-with-body --max-time 30 -X PUT \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"purpose":"camera-images"}' \
  http://127.0.0.1:5080/v1/db/demo/s3/camera-images
```

新 Bucket 的异步语义摄取和缩略图都默认关闭。只有明确保存以下选项后，普通对象上传才会产生派生任务：

```bash
curl --fail-with-body --max-time 30 -X PUT \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "asyncIngestionEnabled": true,
    "thumbnailEnabled": true,
    "thumbnailMaxWidth": 320,
    "thumbnailMaxHeight": 320,
    "thumbnailQuality": 80
  }' \
  "http://127.0.0.1:5080/v1/db/demo/s3/camera-images?semantic"
```

这两个开关彼此独立。只开启 `thumbnailEnabled` 时不要求 SigLIP2 Provider 就绪；只开启 `asyncIngestionEnabled` 时则不会额外生成缩略图。

可以随时读取当前 Bucket 配置：

```bash
curl --fail-with-body --max-time 30 \
  -H "Authorization: Bearer $TOKEN" \
  "http://127.0.0.1:5080/v1/db/demo/s3/camera-images?semantic"
```

### 上传图片，并保留可过滤的业务信息

图片仍然通过对象存储 API 上传，不需要转成 Base64。metadata 使用 `x-amz-meta-*`，tag 使用 `x-amz-tagging`：

```bash
curl --fail-with-body --max-time 30 -X PUT \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: image/jpeg" \
  -H "x-amz-meta-site: mulei" \
  -H "x-amz-meta-lane: lane-01" \
  -H "x-amz-tagging: vehicleType=truck&source=camera" \
  --data-binary @truck-001.jpg \
  "http://127.0.0.1:5080/v1/db/demo/s3/camera-images/lane-01/truck-001.jpg"
```

成功排队时，写入响应会带上：

```text
x-sonnetdb-processing-job-id: <job-id>
```

普通 PUT、Presigned PUT、CopyObject、Multipart Complete 和 Frame PUT 都会走同一套派生任务逻辑。任务先写入 KV keyspace，再尝试投递到有界内存 Channel；当前默认容量为 128、worker 为 1，满载任务通过持久 due 队列恢复。它为重启恢复提供基础，实际存储故障和恢复状态仍需查看任务回执。默认每轮恢复最多读 64 项、访问 8 个数据库，共用 250 ms 预算；这些是调度边界，不能当作吞吐量指标。

### 轮询处理状态

按对象查询最新版本的任务状态：

```bash
curl --fail-with-body --max-time 30 \
  -H "Authorization: Bearer $TOKEN" \
  "http://127.0.0.1:5080/v1/db/demo/s3/camera-images/lane-01/truck-001.jpg?processing"
```

任务可能处于：

```text
pending
processing
retry
completed
failed
superseded
cancelled
```

当前默认最多执行 5 次，包含首次执行，不是首次之外再重试 5 次；可重试失败使用退避，永久图片输入错误不重试。覆盖同一个对象时，旧版本尚未完成的任务会被标记为 `superseded`。默认单任务执行期限为 120 秒，但不响应取消的原生操作仍没有硬中断保证。

如果对象存在但还没有任务，也可以手工重新排队：

```bash
curl --fail-with-body --max-time 30 -X POST \
  -H "Authorization: Bearer $TOKEN" \
  "http://127.0.0.1:5080/v1/db/demo/s3/camera-images/lane-01/truck-001.jpg?processing"
```

处理完成后，状态响应会包含 `semanticImageId` 和 `thumbnailUrl`。缩略图也可以按原对象地址读取：

```bash
curl --fail-with-body --max-time 30 \
  -H "Authorization: Bearer $TOKEN" \
  -o truck-001.webp \
  "http://127.0.0.1:5080/v1/db/demo/s3/camera-images/lane-01/truck-001.jpg?thumbnail"
```

缩略图采用 WebP，按最大宽高等比缩放，不会把小图放大。

### 给存量图片做 Backfill

开启 Bucket 选项不会自动假设所有历史对象都已经处理。可以显式发起一次补录：

```bash
curl --fail-with-body --max-time 30 -X POST \
  -H "Authorization: Bearer $TOKEN" \
  "http://127.0.0.1:5080/v1/db/demo/s3/camera-images?semantic"
```

请求内只处理一页，默认最多 128 个对象与 250 ms 处理预算，后续页由恢复循环继续。游标和累计计数持久化，队列满载时也不等待全部任务完成。

以下仅是三个对象完成扫描时的响应形状示例，数字不是本文运行结果：

```json
{
  "bucket": "camera-images",
  "scannedObjects": 3,
  "queuedObjects": 3,
  "skippedObjects": 0,
  "hasMore": false,
  "continuationToken": null,
  "completed": true
}
```

`completed` 表示这轮 Bucket 回填扫描完成，不代表所有派生任务已完成。尚有页时，`hasMore` 为 true，返回当前持久游标。Backfill 扫描可见对象，只给符合图片与 Bucket 开关条件的对象建任务；已经处理或无需处理的对象会跳过。重复请求可以继续未完成扫描，完成后再次发起则开始新一轮核对。

### 文搜图

自然语言查询直接以 JSON 发送：

```bash
curl --fail-with-body --max-time 30 -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "夜间车道上的红色重型卡车",
    "topK": 10,
    "minScore": 0.2
  }' \
  http://127.0.0.1:5080/v1/db/demo/images/search/text
```

本例配置下，SigLIP2 把文字编码到与图片相同的 768 维向量空间。响应中的 `score` 是 `1 - cosineDistance`，越大越相似；`distance` 越小越相似。score 不是识别概率，`minScore` 需要用实际图片集确定。

### 图搜图

查询图片同样直接发送二进制内容：

```bash
curl --fail-with-body --max-time 30 -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: image/jpeg" \
  --data-binary @query.jpg \
  "http://127.0.0.1:5080/v1/db/demo/images/search/image?topK=10&minScore=0.2&explain=true"
```

这条路径适合以一张现场图片寻找同类设备、相似车辆、相近缺陷或同一场景的历史记录。

### 按已摄取图片查相似图片

如果源图片已经在语义目录中，可以直接复用它的 embedding，不需要再次读取和推理原图：

```bash
curl --fail-with-body --max-time 30 -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"topK":10,"minScore":0.2,"explain":true}' \
  http://127.0.0.1:5080/v1/db/demo/images/truck-001/similar
```

该接口默认从结果中排除源图片自身。上例的 `truck-001` 来自前面的直接摄取 ID；如果图片由 Bucket 异步摄取，应改用 `?processing` 响应中的 `semanticImageId`。

### 过滤条件和 explain

工业场景里很少只按视觉相似度搜索。我们通常还需要限定现场、车道、对象目录或业务标签。

文搜图可以在请求体中加入过滤条件：

```json
{
  "text": "红色重型卡车",
  "topK": 10,
  "filter": {
    "sourceBucket": "camera-images",
    "sourceKeyPrefix": "lane-01/",
    "contentType": "image/jpeg",
    "metadata": {
      "site": "mulei"
    },
    "tags": {
      "vehicleType": "truck"
    }
  },
  "explain": true
}
```

`metadata` 和 `tags` 都是全部键值精确匹配。图搜图则通过查询参数传递同一组条件：

```text
sourceBucket=camera-images
sourceKeyPrefix=lane-01/
contentType=image/jpeg
metadata.site=mulei
tag.vehicleType=truck
explain=true
```

没有过滤条件时，查询使用配置允许的 USearch 或 managed HNSW。带 source bucket、metadata 或 tag 条件时，Document path/wildcard index 先产生候选 ID，再由当前后端执行 filtered ANN。源码已有 USearch `TrySearchFiltered` 与 managed HNSW 两条路径，不能继续把过滤能力描述为仅由 managed 实现。

候选不足、派生索引缺少允许的 ID 或物化预算不足时，服务转精确补偿或分页精确回退；只有 Key Prefix/Content-Type 等无可用候选索引的条件也走精确回退。显式要求 USearch 且关闭 managed fallback 时，原生后端不可用会返回错误，不能把所有带过滤请求都描述为必定返回 503。过滤 ANN 仍是近似查询，不能据此承诺达到完整召回率。

`SemanticSearch:Query` 当前提供以下默认预算：

| 选项 | 默认值 | 控制内容 |
| --- | ---: | --- |
| `AnnCandidateLimit` | 512 | filtered ANN 候选预算 |
| `ExactCompensationLimit` | 4096 | 可物化并用于精确补偿的过滤 ID 数量 |
| `CandidatePageSize` | 256 | 预过滤与精确扫描单页行数 |
| `MaxScannedCandidates` | 100000 | 单次查询累计扫描上限 |
| `TimeoutMilliseconds` | 30000 | 检索阶段协作超时，不含生成 embedding |

超过扫描或时间预算时，查询返回 `semantic_query_budget_exceeded`，读者应保留该失败回执，不能把受限扫描当作完整结果。预算配置的存在也不能代替目标硬件的质量与容量报告。

设置 `explain=true` 后，响应会额外返回：

- `searchMode`：例如 `ann`、`prefiltered-ann`、`prefiltered-ann-exact-compensation` 或 `exact-filtered-fallback`；
- `candidateCount`：检索产生的候选数量；
- `filteredCandidateCount`：通过 profile 和属性过滤后的候选数量。

这些字段用于检查是否走了预期后端与过滤路径。候选数量不等于检索质量，不能替代真实 Recall@K 或排序评测。

### 生命周期过期会联动清理派生数据

原对象删除后，如果向量和缩略图仍然存在，搜索结果就会变成“幽灵记录”。因此普通删除、批量删除和生命周期过期都会排入异步清理任务。

执行 Bucket 生命周期后，响应返回本次过期的当前对象和清理任务数量。以下只是字段形状示例：

```json
{
  "bucket": "camera-images",
  "expiredCurrentObjects": 1,
  "removedNoncurrentVersions": 5,
  "removedDeleteMarkers": 1,
  "expiredObjects": [
    {
      "key": "lane-01/truck-001.jpg",
      "versionId": "<version-id>",
      "contentType": "image/jpeg"
    }
  ],
  "semanticCleanupJobs": 1
}
```

被 retention 或 legal hold 跳过的对象不会进入 `expiredObjects`，也不会排入语义清理任务。

### 在 SonnetDB Studio 中使用

不想手写 REST 请求时，可以进入 Studio 的对象桶工作台，选择“图片语义”。

这个工作区可以完成：

- 读取和保存异步摄取、缩略图尺寸与质量选项；
- 对存量图片发起 Backfill；
- 查看当前对象的持久化任务状态并手工重新入队；
- 预览受权限保护的 WebP 缩略图；
- 执行文搜图、图搜图和 similar-by-id；
- 设置 Bucket、Key Prefix、Content-Type、metadata 和 tag 过滤；
- 查看 explain 返回的后端、执行模式和候选统计。

Studio 与 REST API 使用同一套权限边界。数据库级摄取和删除需要 write 权限，读取与搜索需要 read 权限。

### 运行完整工业图片样例

仓库中的 `samples/SonnetDB.SemanticImages` 已经把整个流程串起来：创建数据库和 Bucket、开启派生处理、上传图片、等待任务完成、Backfill，然后依次执行无过滤文搜图、过滤文搜图、图搜图和 similar-by-id。

先启动已经配置好 SigLIP2 模型的 SonnetDB Server，再运行：

```powershell
$env:SONNETDB_TOKEN='<admin-or-database-token>'

dotnet run --project samples/SonnetDB.SemanticImages -- `
  --server http://127.0.0.1:5080 `
  --images D:\industrial-images `
  --text '夜间车道上的红色重型卡车'
```

如果图片目录为空，样例会生成叉车、泵组和输送线三张确定性 PNG，方便验证链路是否跑通。要评估真实检索质量，仍应使用现场图片集。

样例也可以发布为 Native AOT：

```powershell
dotnet publish samples/SonnetDB.SemanticImages `
  -c Release `
  -r win-x64 `
  -p:PublishAot=true
```

### 平台与部署验证

当前仓库集中配置 ONNX Runtime 1.30.0、USearch 2.26.1、SkiaSharp 4.152.1。模型、图片编解码和 ANN 各自带有不同原生资产；旧稿的包版本与 RID 支持表不作为当前部署依据。

| 检查项 | 要取得的证据 |
| --- | --- |
| Server Native AOT | 目标 RID publish 通过，部署目录保留实际原生资产 |
| 图片编解码 | 目标机器实际解码、缩放与生成缩略图 |
| SigLIP2 | 使用实际模型完成启动、文本编码、图片编码 |
| ANN | 状态返回实际后端，并完成持久向量加载与搜索 |
| 工业检索质量 | 真实图片集的 Recall@K、排序、P95 延迟和容量报告 |

包内存在某个 RID 的资产，不等于目标 CPU、模型、运行时环境与动态库加载全部正确。建议先用小数据集做部署 smoke test，再在固定硬件上评测；managed 后端是否可用也需要模型与图片路径各自成功。

### Provider 治理与审计

文本、图片和 Bucket 异步 embedding 共用外发策略与持久调用审计。默认 `DataEgressPolicy:Mode=LocalOnly`，只有明确声明本地的 provider 可以运行；内置 SigLIP2 是本地 provider，未知实现的 `IsLocal` 默认为 false。使用外部 provider 需要在服务器配置准确的模式与目标，调用方不能在请求中覆盖策略。

调用审计保存在目标数据库的 `__semantic_embedding_audit` KV keyspace，记录 provider、实际 profile、状态与耗时，不保存图片内容或向量；默认保留 30 天，读取需要数据库 Admin 权限。异常中断可能留下 `started`，它不能证明 provider 没有执行，也不构成 exactly-once 保证。

### 当前边界

当前已实现对象写入、语义检索、缩略图和生命周期清理的相关路径，部署和质量证据仍需分别取得：

- filtered ANN 与查询预算已在当前源码中实现；实际原生后端加载、检索质量及固定硬件性能仍需独立验证；
- Server 不负责自动下载或更新模型；
- 更换模型或预处理策略时需要创建新的 embedding profile；
- 真实图片检索质量、容量上限和目标硬件 P95 延迟仍需要专项报告；
- GPU 或其他 ONNX execution provider 需要逐个平台完成 Native AOT 与原生资产审计后再接入。

### 图片工作流的使用方式

使用这些接口时，先确认模型 ready 与有效 profile，再上传少量图片并检查任务终态，最后评估检索结果。

图片由 Object Bucket 管理；是否异步生成 embedding、是否生成缩略图由 Bucket 显式决定；SigLIP2 负责跨模态编码，USearch 或 managed HNSW 提供检索路径。持久任务、Backfill 和清理状态让维护过程可以观察，但不替代长期恢复与模型质量验证。

从现在开始，SonnetDB 里的图片既可以按 Key 精确访问，也可以按它“看起来是什么”来搜索。
