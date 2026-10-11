---
title: SonnetDB Copilot Provider-neutral 配置
categories: SonnetDB,Copilot,模型配置
draft: false
---

SonnetDB 不使用 international/domestic 品牌枚举选择 Provider。Chat 与远程 Embedding 通过 OpenAI-compatible 协议适配，模型目录按平台默认、自定义、本地分组；分组描述部署来源，不凭模型名字猜厂商。

本篇保留原稿主题并补充当前主线的可操作配置。正式 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 与主线新增本地接线分开核对，未在本文执行真实模型调用。

## 本地兼容端点示例

已运行并提供 OpenAI-compatible `/v1` 的 Ollama 或其它本地网关，可按其实际模型名配置：

```json
{
  "SonnetDBServer": {
    "Copilot": {
      "Chat": {
        "Provider": "openai",
        "Endpoint": "http://127.0.0.1:11434/v1/",
        "ApiKey": "ollama-local",
        "Model": "actual-installed-model-id"
      }
    }
  }
}
```

模型名必须替换为端点实际发布的模型。当前适配器要求非空 ApiKey；本地无鉴权网关可以使用占位值，远程鉴权服务必须由部署方通过安全配置提供真实凭据，不能把真实 key 放入公开稿、浏览器历史或版本库。

## 云端和企业网关

同一配置形状也用于标准 `/v1/chat/completions` 与 Bearer 认证的远程服务。Azure 部署若使用其它认证或路径，应由明确的兼容 adapter/APIM 网关转成该合同，不能仅替换域名就假定全部可用。

绑定 Cloud Token 时，Web Copilot 优先使用云端 Runtime。未绑定且本地 Chat readiness 满足要求时，当前 Server 可以通过已注册 `IChatProvider` 接线。本地 HTTP 分支保持只读，read-write 请求返回 `local_write_confirmation_required`；配置存在不等于服务可达或模型可用。

## Embedding 独立管理

切换 Chat 模型不应随意重建知识索引。Embedding profile 固定 provider、模型、revision、维度、距离度量和归一化；更换语义模型需要按索引合同迁移。

builtin hash 是确定性回退，不是语义质量证据。本地 ONNX 必须配套明确模型 profile，不能看到 `EmbeddingFallback=false` 就推断模型质量验收完成。

参考：[完整 Provider 配置与 ONNX profile](https://github.com/IoTSharp/SonnetDB/blob/main/docs/copilot-providers.md)、[Copilot RAG 迁移](https://github.com/IoTSharp/SonnetDB/blob/main/docs/copilot-rag-migration.md)、[typed MCP](https://github.com/IoTSharp/SonnetDB/blob/main/docs/mcp-contract.md)。
