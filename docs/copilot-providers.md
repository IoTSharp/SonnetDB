---
layout: default
title: "Copilot Provider 与模型目录"
description: "SonnetDB 的 provider-neutral Chat / Embedding 抽象、模型分组契约，以及云端、兼容网关和本地模型配置示例。"
---

# Copilot Provider 与模型目录

SonnetDB 将“如何调用模型”和“使用哪个模型”分开处理：

- `IChatProvider` / `IEmbeddingProvider` 是服务端能力边界。
- `OpenAICompatibleChatProvider` / `OpenAICompatibleEmbeddingProvider` 负责 OpenAI-compatible HTTP 协议，不绑定具体模型厂商。
- Web Admin 的 CopilotDock 通过 sonnetdb.com 账号绑定使用云端 Copilot Runtime；模型目录来自 Gateway，SonnetDB 不按模型名猜测厂商。
- 外部 Agent 应通过授权 MCP 工具访问 SonnetDB，不应直接读取数据库目录或内部系统表。

## 模型分组

`GET /v1/copilot/models` 保留旧客户端使用的 `default` 与 `candidates`，并新增 `groups`：

| 分组键 | UI 名称 | 含义 |
|---|---|---|
| `platform-default` | 平台默认模型 | 未显式选择模型时由平台维护的默认项 |
| `custom` | 自定义模型 | 用户或部署方显式发布的远程模型 |
| `local` | 本地模型 | 由本地或私有运行时发布的模型 |

分组描述的是部署来源，不是 Provider 品牌。Gateway 可以在 OpenAI-compatible `/v1/models` 响应中提供可选的 `display_name`、`group` 和 `is_default` 元数据：

```json
{
  "data": [
    { "id": "balanced", "display_name": "Balanced", "is_default": true },
    { "id": "qwen-max", "display_name": "Qwen Max", "group": "custom" },
    { "id": "qwen2.5:7b", "display_name": "Edge Qwen", "group": "local" }
  ]
}
```

没有分组元数据时，首项作为平台默认模型，其余项进入自定义模型。SonnetDB 不使用 `gpt`、`qwen`、`ollama` 等名称片段推断来源。CopilotDock 只有在用户显式选择模型时才发送 `model`；“平台默认模型”不会固定具体模型 ID。

## Chat 配置

底层 Chat 抽象绑定路径为 `SonnetDBServer:Copilot:Chat`。当前实现使用 `Provider: openai` 表示 OpenAI-compatible 协议适配器，并不表示只能使用 OpenAI 模型。

### OpenAI-compatible 服务

```json
{
  "SonnetDBServer": {
    "Copilot": {
      "Chat": {
        "Provider": "openai",
        "Endpoint": "https://gateway.example.com/v1/",
        "ApiKey": "${COPILOT_API_KEY}",
        "Model": "model-id",
        "AvailableModels": ["model-id", "model-id-fast"]
      }
    }
  }
}
```

### Azure OpenAI

当前适配器使用标准 `/v1/chat/completions` 和 Bearer 认证。Azure 部署应通过企业 API Gateway、APIM policy 或其他 OpenAI-compatible adapter 统一成该契约，再把 adapter 地址配置为 `Endpoint`：

```json
{
  "Provider": "openai",
  "Endpoint": "https://ai-gateway.contoso.com/azure-openai/v1/",
  "ApiKey": "${AZURE_AI_GATEWAY_TOKEN}",
  "Model": "gpt-4.1-production"
}
```

不要把需要 `api-key` 请求头和 `api-version` 查询参数的 Azure 原生 deployment URL 直接填入当前适配器。

### 国内兼容网关

DashScope、DeepSeek、SiliconFlow、火山方舟等服务只要暴露标准 OpenAI-compatible `/v1` 契约，即可使用同一配置形状：

```json
{
  "Provider": "openai",
  "Endpoint": "https://compatible-gateway.example.cn/v1/",
  "ApiKey": "${DOMESTIC_AI_KEY}",
  "Model": "qwen-plus"
}
```

服务端只依赖协议契约；模型名称、计费、内容过滤和区域可用性由所选 Gateway 管理。

### 本地 Ollama

Ollama 提供 OpenAI-compatible `/v1` 入口。当前适配器要求非空 `ApiKey`，本地无鉴权部署可使用占位值：

```json
{
  "Provider": "openai",
  "Endpoint": "http://127.0.0.1:11434/v1/",
  "ApiKey": "ollama-local",
  "Model": "qwen2.5:7b"
}
```

### 本地 vLLM

```json
{
  "Provider": "openai",
  "Endpoint": "http://127.0.0.1:8000/v1/",
  "ApiKey": "${VLLM_API_KEY}",
  "Model": "Qwen/Qwen2.5-7B-Instruct"
}
```

这些底层配置用于自托管 provider 组件和集成代码。当前 Web Copilot 对话仍由云端 Copilot Runtime 编排；仅修改 `Copilot:Chat` 不会把 CopilotDock 改成直连本地模型。需要完全本地的 Agent 时，应由外部 Agent 使用本地模型并通过 SonnetDB MCP 工具访问数据。

## Embedding 配置

Embedding 与 Chat 独立配置，避免为了切换 Chat 模型重建知识索引。

| Provider | 配置重点 | 适用场景 |
|---|---|---|
| `builtin` | 无外部配置，固定 384 维 | 首次启动、离线兜底、功能验证 |
| `local` | `LocalModelPath` | 预留的本地 ONNX 配置；当前执行尚未接通 |
| `openai` | `Endpoint`、`ApiKey`、`Model` | OpenAI-compatible 云端或私有 embedding 服务 |

```json
{
  "SonnetDBServer": {
    "Copilot": {
      "Embedding": {
        "Provider": "local",
        "LocalModelPath": "./models/bge-small-zh-v1.5-int8.onnx"
      }
    }
  }
}
```

> 当前 `LocalOnnxEmbeddingProvider.EmbedAsync` 会抛出 `NotSupportedException`。上面的配置只记录目标形态，在本地模型执行和回归测试完成前不得用于生产或宣称“数据不出域”。离线功能验证应使用 `builtin`；完整本地 Agent 可暂由外部本地模型通过授权 MCP 工具访问 SonnetDB。

切换 embedding 模型或向量维度后必须重建文档与技能索引。API Key 应通过环境变量、Secret Manager 或容器 secret 注入，不要提交到配置文件。

## 兼容边界

- `default` / `candidates` 是兼容字段，不会因启用分组而移除。
- 新客户端应优先消费 `groups`，并对未知分组键做忽略或回退处理。
- 模型选择只影响当前请求，不改变服务端默认配置。
- Provider 负责模型调用；MCP、权限、审计和写入确认仍由 SonnetDB 控制面约束。
