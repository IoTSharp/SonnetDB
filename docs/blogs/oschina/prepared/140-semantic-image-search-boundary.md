---
title: SonnetDB 语义图片检索续篇：从能运行到可发布的证据边界
categories: SonnetDB,向量检索,AI,对象存储
draft: false
---

上一篇文章介绍了语义图片检索的数据流。本篇讨论对象版本、模型 profile、过滤条件、恢复行为和真实质量的证据边界。SonnetDB 可以把图片保存在 Object Bucket，使用显式配置的 SigLIP2 将文字和图片编码到同一向量空间，再执行向量检索；Studio 和 REST 提供文搜图、图搜图及相似图片入口。本文核查当前主分支文档和接口，没有在本次准备中运行样例或真实模型评测。

语义搜索默认关闭，模型文件由部署者提供，Server 不在启动或请求时下载模型。未经配置的实例不能直接复制搜索请求就取得可用的语义结果。

## 数据流

1. 上传图片并保存对象 key、版本和校验信息。
2. 直接图片入口生成 embedding，或由显式开启的 Bucket 异步摄取任务读取图片。
3. 保存与对象引用、版本和 profile 绑定的 Document 元数据及向量，更新派生检索层。
4. 查询时校验数据库 Read 权限、provider 和请求，应用所选过滤条件并执行 Top-K。

例如，数据库 `demo` 已有完成摄取的 `factory-images` 图片，provider 已就绪、凭据拥有 Read 权限时，可以向当前接口发送：

```http
POST /v1/db/demo/images/search/text HTTP/1.1
Host: 127.0.0.1:5080
Authorization: Bearer <token>
Content-Type: application/json

{
  "text": "夜间车道上的红色重型卡车",
  "topK": 10,
  "filter": {
    "sourceBucket": "factory-images",
    "contentType": "image/jpeg",
    "metadata": { "site": "demo-site" }
  },
  "explain": true
}
```

这是未执行的协议示例，`text` 和 `filter.sourceBucket` 是当前字段，不能替换为 `query` 和顶层 `bucket`。实际部署的地址、TLS、字段与版本以 REST/OpenAPI 为准。另有 `/images/search/image` 和 `/images/{id}/similar` 入口，图片搜索的 body 与查询参数合同不同。

## 筛选与恢复

metadata/tag 使用全部键值精确匹配，`contentType` 表达 MIME 条件。当前可索引条件通过 managed HNSW 预过滤，必要时做精确补偿或回退；只有 key prefix 或 MIME 条件时会走精确过滤回退。应检查 explain 中实际 `backend` 和 `searchMode`，不能把每个过滤请求都称为 USearch ANN。显式 `usearch` 且禁止 managed fallback 时，带过滤查询返回不可用错误。

模型、量化或预处理变化需要独立 profile，现有图片需按对应方式 Backfill 或重新摄取。图片服务当前按 profile 隔离 Document/向量记录并更新图片，不应套用通用 RAG 的“每次写入发布完整 generation、旧代租约保留”合同。通用 generation 发布的校验与租约是另一个需要单独验证的工作流。

Bucket 异步任务持久化并支持重开恢复，任务可以是 pending、processing、retry、completed、failed、superseded 或 cancelled。上传成功或得到 job id 只证明写入/排队阶段，仍要核对派生任务终态、对象版本、删除清理及搜索结果，不能把一次 HTTP 成功当成整条链路完成。

## 当前边界

语义图片检索已有工业流程样例，并提供样例的 Native AOT 发布命令，但提供命令不等于本文完成了发布或目标平台验收。真实模型质量、Recall、目标硬件容量、跨网络部署和长期稳定性仍需独立证据。样例没有图片目录时会生成三张确定性 PNG；它们证明流程与展示，不证明 SigLIP2 在真实现场的检索效果。ONNX Runtime 和 USearch 的原生资产也应按发行物和平台单独核验。

参考：[`semantic-image-search.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/semantic-image-search.md)、[`object-transfer-manager.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/object-transfer-manager.md)、[`vector-lifecycle-preflight.md`](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-lifecycle-preflight.md)、[`samples/SonnetDB.SemanticImages`](https://github.com/IoTSharp/SonnetDB/blob/main/samples/SonnetDB.SemanticImages/README.md)。
