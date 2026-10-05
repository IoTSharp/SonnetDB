---
title: SonnetDB 语义图片检索续篇：从能运行到可发布的证据边界
categories: SonnetDB,向量检索,AI,对象存储
draft: false
---

上一篇文章介绍了语义图片检索的完整数据流。本篇只讨论发布前最容易被忽略的证据边界：对象版本、模型 generation、过滤条件、恢复行为和真实质量都必须单独核验，不能把“样例能跑”写成生产承诺。SonnetDB 的路径是：图片保存在 Object Bucket，SigLIP2 将文字和图片编码到同一向量空间，再由 USearch 或托管 HNSW 做近似最近邻检索；Studio 和 REST 暴露文搜图、图搜图及相似图片查询。

## 数据流

1. 上传图片并保存对象 key、版本和校验信息。
2. 由派生任务读取对象，生成 embedding 和模型/profile 标识。
3. 将向量索引与对象版本绑定，发布完整 generation。
4. 查询时先检查数据库、Bucket、权限和已发布 generation，再执行 Top-K。

```text
POST /v1/db/{db}/images/search/text
{
  "bucket": "factory-images",
  "query": "夜间车道上的红色重型卡车",
  "topK": 10
}
```

当前服务端入口是 `/v1/db/{db}/images/search/text`，另有图片搜索和相似图片入口；请求字段与版本以 REST/OpenAPI 和样例为准，不要把文章中的示例 URL 当成跨版本永久合同。

## 筛选与恢复

metadata filter 可以限制站点、设备或 MIME 类型。索引换代时，新 generation 完整写入并通过校验后才切换读指针；旧 generation 在仍有租约时保留。对象上传、派生任务、索引发布和删除必须分别记录，不能把一次 HTTP 成功当成整条链路完成。

## 当前边界

语义图片检索已经有可运行的工业样例和 Native AOT 发布路径，但真实模型质量、Recall、目标硬件容量、跨网络部署和长期稳定性仍需独立证据。确定性生成图片、hash fallback 或小型合成语料只证明流程可运行，不证明 SigLIP2 在真实现场的检索效果。ONNX Runtime 和 USearch 的原生资产也应按发行物和平台单独核验。

参考：[`semantic-image-search.md`](../semantic-image-search.md)、[`object-transfer-manager.md`](../object-transfer-manager.md)、[`vector-lifecycle-preflight.md`](../vector-lifecycle-preflight.md)、[`samples/SonnetDB.SemanticImages`](../../samples/SonnetDB.SemanticImages/README.md)。
