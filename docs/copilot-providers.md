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

这些底层配置用于自托管 provider 组件和集成代码。绑定云账号时 Web Copilot 对话仍由云端 Copilot Runtime 编排；未绑定云账号且 Chat readiness 完整时，服务端会按下文的本地接线直接使用 `IChatProvider`。CopilotDock 是否展示本地模式取决于宿主 UI 的配置发现；外部 Agent 仍应通过授权 MCP 工具访问 SonnetDB 数据。

## Embedding 配置

Embedding 与 Chat 独立配置，避免为了切换 Chat 模型重建知识索引。

| Provider | 配置重点 | 适用场景 |
|---|---|---|
| `builtin` | 无外部配置，固定 384 维 | 首次启动、离线兜底、功能验证 |
| `local` | `LocalModelPath` | 本地模型文件路径校验；未提供明确 tokenizer/input profile 时使用本地确定性 hash fallback |
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

> `LocalOnnxEmbeddingProvider` 会校验 `LocalModelPath`。由于 ONNX 文本模型的 tokenizer、输入名和 pooling 规则并不统一，当前配置未携带 model profile 时不会加载 native session 或猜测输入；provider 会在本地自动回落到与 `builtin` 相同的 384 维确定性 hash 向量，并在知识库状态中标记 `EmbeddingFallback=true`。这条路径可用于离线功能验证，但不等价于真实语义模型，也不应宣称已完成 ONNX 推理质量验收。需要真实 ONNX 语义质量时，必须为目标模型补充 tokenizer/input profile 和回归样本。

## 本地/在线 Chat 接线

绑定 sonnetdb.com Cloud Token 时，`/v1/copilot/chat` 继续优先使用云端 Copilot Runtime（本地仅执行授权工具）。未绑定云账号且 `Copilot:Chat` readiness 完整时，同一个端点会直接使用已注册的 `IChatProvider`（当前为 OpenAI-compatible `/v1/chat/completions`），并复用本地 `CopilotAgent`、数据库只读权限、会话持久化和 NDJSON/SSE 事件合同：

```json
{
  "SonnetDBServer": {
    "Copilot": {
      "Chat": {
        "Provider": "openai",
        "Endpoint": "http://127.0.0.1:11434/v1/",
        "ApiKey": "ollama-local",
        "Model": "qwen2.5:7b"
      }
    }
  }
}
```

Cloud Token 与本地 Chat 配置同时存在时，云端模式优先；本地 provider 不会绕过权限边界。当前本地 HTTP 分支拒绝 `read-write` 请求并返回 `local_write_confirmation_required`，写入仍需云端风险审查或另一个显式确认入口。未配置 Cloud Token 且 Chat readiness 不满足时，端点仍返回 `cloud_not_bound`，避免把“配置存在”误报为模型可用。

切换 embedding 模型或向量维度后必须重建文档与技能索引。API Key 应通过环境变量、Secret Manager 或容器 secret 注入，不要提交到配置文件。

## 兼容边界

- `default` / `candidates` 是兼容字段，不会因启用分组而移除。
- 新客户端应优先消费 `groups`，并对未知分组键做忽略或回退处理。
- 模型选择只影响当前请求，不改变服务端默认配置。
- Provider 负责模型调用；MCP、权限、审计和写入确认仍由 SonnetDB 控制面约束。
