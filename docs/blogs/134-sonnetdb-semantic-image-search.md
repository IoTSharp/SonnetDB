## SonnetDB 新增语义图片检索：用 SigLIP2 + USearch 实现文搜图与图搜图

对象存储里的图片越来越多之后，最先失效的往往不是容量，而是文件名。

`camera-01/2026/07/26/000184.jpg` 对程序来说是一个准确的 Key，但对人来说几乎没有检索价值。我们真正想问的通常是：“找到夜间车道上的红色重型卡车”“有没有和这张故障泵照片相似的图片”“只看木垒现场一号车道的 JPEG”。

SonnetDB 现在补上了这条完整链路：图片继续保存在 Object Bucket 中，SigLIP2 把文字和图片编码到同一个向量空间，USearch 或托管 HNSW 负责近似最近邻检索，Studio 和 REST API 则提供文搜图、图搜图和相似图片查询。

更重要的是，这并不是一个只能演示的同步接口。Bucket 可以选择开启持久化异步摄取和 WebP 缩略图，任务支持失败重试、重启恢复、存量补录，以及对象删除和生命周期过期后的派生数据清理。

### 这次新增了什么

这次图片工作流包含以下能力：

- 使用 SigLIP2 ONNX 为文字和图片生成同一维度的 embedding；
- 无过滤查询优先使用 USearch，不受支持的平台自动回退到 SonnetDB 托管 HNSW；
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

这里有一个重要设计：USearch 只是可重建的内存加速层，Document 中保存的向量才是权威数据。即使 USearch 在当前平台无法加载，原图、元数据和向量也不会丢失，服务可以回退到托管 HNSW。

### SigLIP2、USearch 和“纯 C#”的边界

这次选择的原则不是“所有依赖必须纯 C#”，而是“SonnetDB 应用和合同保持 Native AOT 兼容，同时在可用平台优先采用成熟、高性能实现”。

SigLIP2 通过 ONNX Runtime CPU provider 执行。应用代码、source-generated JSON、tokenizer 接线和图片预处理可以通过 AOT 分析，但 ONNX Runtime 本身仍带有各平台的原生推理库。

USearch 同样是原生 ANN 引擎的 .NET 封装，不是纯 C#。它在受支持平台上提供更好的向量检索性能；平台不受支持或原生库加载失败时，`Backend=auto` 会使用 SonnetDB 的纯托管 HNSW。

因此，这里的准确表述是：

> SonnetDB Server 可以作为 Native AOT 应用发布，并调用 ONNX Runtime 和 USearch 的原生资产；Native AOT 兼容不等于整个运行栈都是纯 C#。

这也让部署策略更务实：x64 服务器优先使用 USearch，ARM64 Linux 仍可运行 SigLIP2 ONNX 推理，并通过 managed HNSW 完成检索。

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
curl http://127.0.0.1:5080/v1/semantic-search/status
```

响应中的 `ready` 应为 `true`。`configuredBackend` 是配置值，`effectiveBackend` 是当前 RID 实际使用的后端。例如配置为 `auto`，在 Windows x64 上可能返回 `usearch`，在 Linux ARM64 上则会返回 `managed`。

如果模型路径、tensor 名称或输出维度不正确，`reason` 会给出 Provider 未就绪的原因。不要在看到 `enabled=true` 后就假定模型已经加载成功，应同时检查 `ready` 和 `effectiveBackend`。

### 两种摄取方式

SonnetDB 提供两条图片摄取路径。

第一条是直接调用图片语义 API：

```bash
curl -X PUT \
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
curl -X PUT \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"purpose":"camera-images"}' \
  http://127.0.0.1:5080/v1/db/demo/s3/camera-images
```

新 Bucket 的异步语义摄取和缩略图都默认关闭。只有明确保存以下选项后，普通对象上传才会产生派生任务：

```bash
curl -X PUT \
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
curl \
  -H "Authorization: Bearer $TOKEN" \
  "http://127.0.0.1:5080/v1/db/demo/s3/camera-images?semantic"
```

### 上传图片，并保留可过滤的业务信息

图片仍然通过对象存储 API 上传，不需要转成 Base64。metadata 使用 `x-amz-meta-*`，tag 使用 `x-amz-tagging`：

```bash
curl -X PUT \
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

普通 PUT、Presigned PUT、CopyObject、Multipart Complete 和 Frame PUT 都会走同一套派生任务逻辑。任务先写入 KV keyspace，再尝试投递到容量为 256 的内存 Channel，所以队列暂时满了或 Server 重启都不会静默丢任务。

### 轮询处理状态

按对象查询最新版本的任务状态：

```bash
curl \
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

失败任务最多指数退避重试 5 次。覆盖同一个对象时，旧版本尚未完成的任务会被标记为 `superseded`，防止旧 embedding 覆盖新内容。

如果对象存在但还没有任务，也可以手工重新排队：

```bash
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  "http://127.0.0.1:5080/v1/db/demo/s3/camera-images/lane-01/truck-001.jpg?processing"
```

处理完成后，状态响应会包含 `semanticImageId` 和 `thumbnailUrl`。缩略图也可以按原对象地址读取：

```bash
curl \
  -H "Authorization: Bearer $TOKEN" \
  -o truck-001.webp \
  "http://127.0.0.1:5080/v1/db/demo/s3/camera-images/lane-01/truck-001.jpg?thumbnail"
```

缩略图采用 WebP，按最大宽高等比缩放，不会把小图放大。

### 给存量图片做 Backfill

开启 Bucket 选项不会自动假设所有历史对象都已经处理。可以显式发起一次补录：

```bash
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  "http://127.0.0.1:5080/v1/db/demo/s3/camera-images?semantic"
```

响应示例：

```json
{
  "bucket": "camera-images",
  "scannedObjects": 1200,
  "queuedObjects": 1186,
  "skippedObjects": 14
}
```

Backfill 只为当前可见的 `image/*` 对象建立任务，可以重复调用；已经处理或无需处理的对象会被跳过。

### 文搜图

自然语言查询直接以 JSON 发送：

```bash
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "夜间车道上的红色重型卡车",
    "topK": 10,
    "minScore": 0.2
  }' \
  http://127.0.0.1:5080/v1/db/demo/images/search/text
```

SigLIP2 会把这段文字编码到与图片相同的 768 维向量空间。响应中的 `score` 是 `1 - cosineDistance`，越大越相似；`distance` 越小越相似。

### 图搜图

查询图片同样直接发送二进制内容：

```bash
curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: image/jpeg" \
  --data-binary @query.jpg \
  "http://127.0.0.1:5080/v1/db/demo/images/search/image?topK=10&minScore=0.2&explain=true"
```

这条路径适合以一张现场图片寻找同类设备、相似车辆、相近缺陷或同一场景的历史记录。

### 按已摄取图片查相似图片

如果源图片已经在语义目录中，可以直接复用它的 embedding，不需要再次读取和推理原图：

```bash
curl -X POST \
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

没有过滤条件时，查询使用 USearch 或 managed HNSW。存在有效过滤条件时，当前实现使用 `exact-filtered` 完整精确余弦扫描：先过滤 profile 和对象属性，再计算距离。这样不会因为 ANN 候选截断而漏掉符合业务条件的结果。

设置 `explain=true` 后，响应会额外返回：

- `searchMode`：例如 `ann` 或 `exact-filtered`；
- `candidateCount`：检索产生的候选数量；
- `filteredCandidateCount`：通过 profile 和属性过滤后的候选数量。

这些字段很适合用于确认当前平台是否走了预期后端，也能帮助判断过滤条件是否过窄。

### 生命周期过期会联动清理派生数据

原对象删除后，如果向量和缩略图仍然存在，搜索结果就会变成“幽灵记录”。因此普通删除、批量删除和生命周期过期都会排入异步清理任务。

执行 Bucket 生命周期后，响应会明确返回本次真正过期的当前对象和清理任务数量：

```json
{
  "bucket": "camera-images",
  "expiredCurrentObjects": 2,
  "removedNoncurrentVersions": 5,
  "removedDeleteMarkers": 1,
  "expiredObjects": [
    {
      "key": "lane-01/truck-001.jpg",
      "versionId": "<version-id>",
      "contentType": "image/jpeg"
    }
  ],
  "semanticCleanupJobs": 2
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

### 平台支持

当前 USearch 2.26.0 的 NuGet 原生资产范围如下：

| 运行平台 | ANN 后端 |
| --- | --- |
| Windows x64 | USearch |
| Linux x64 | USearch |
| macOS ARM64 | USearch |
| Windows ARM64 | managed HNSW |
| Linux ARM64 | managed HNSW |
| macOS x64 | managed HNSW |

ONNX Runtime 1.27.1 则提供 Windows x64/ARM64、Linux x64/ARM64 和 macOS ARM64 的 CPU 资产。因此 Linux ARM64 可以完成 SigLIP2 推理，只是 ANN 加速层会使用托管 HNSW。

正式部署前，应在目标 RID 上执行 Native AOT publish，并用真实模型完成一次启动、文本编码、图片编码和搜索 smoke test。包里存在某个 RID 的原生资产，不等于模型、CPU 指令集和部署目录在目标机器上一定都正确。

### 当前边界

这版已经完成从对象写入到语义检索、缩略图和生命周期清理的闭环，但还有几个边界需要如实说明：

- 带过滤条件的查询当前使用精确扫描，还没有实现预过滤 ANN 优化；
- Server 不负责自动下载或更新模型；
- 更换模型或预处理策略时需要创建新的 embedding profile；
- 真实图片检索质量、容量上限和目标硬件 P95 延迟仍需要专项报告；
- GPU 或其他 ONNX execution provider 需要逐个平台完成 Native AOT 与原生资产审计后再接入。

### 小结

这次新增的不只是两个搜索接口，而是一套可运营的图片数据工作流。

图片仍由 Object Bucket 管理；是否异步生成 embedding、是否生成缩略图由 Bucket 显式决定；SigLIP2 负责跨模态语义，USearch 在支持的平台承担高性能 ANN，managed HNSW 提供可移植回退；持久化任务、Backfill、重试恢复和生命周期清理则保证这条链路在长期运行中不会悄悄失真。

从现在开始，SonnetDB 里的图片既可以按 Key 精确访问，也可以按它“看起来是什么”来搜索。
