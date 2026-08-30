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
| `local` | `LocalModelPath` + `ModelProfile` | 显式 tokenizer/input/pooling 语义的本地 ONNX；缺少 profile 或资源时使用可观测 hash fallback，无效合同 fail closed。接入内置 docs/skills 知识库时维度必须为 384 |
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

### 本地 ONNX model profile（M27 #185）

为目标模型配置 `ModelProfile` 后，provider 会在首次 `EmbedAsync` 时创建 ONNX
Runtime session。profile 把 tokenizer、输入/输出 tensor、序列长度、pooling、
归一化和维度固定为一个向量兼容边界；更换其中任一语义时必须记录新的 profile
标识并重建知识库索引。示例：

```json
{
  "Provider": "local",
  "LocalModelPath": "./models/example/model.onnx",
  "ModelProfile": {
    "TokenizerType": "bert-wordpiece",
    "TokenizerModelPath": "./models/example/vocab.txt",
    "InputIdsName": "input_ids",
    "AttentionMaskName": "attention_mask",
    "TokenTypeIdsName": "token_type_ids",
    "PositionIdsName": null,
    "SendAttentionMask": null,
    "SendTokenTypeIds": null,
    "SendPositionIds": null,
    "IgnoredInputNames": [],
    "MaxTokens": 128,
    "PaddingSide": "right",
    "Pooling": "mean",
    "OutputName": "last_hidden_state",
    "Normalize": true,
    "Dimensions": 384,
    "AddSpecialTokens": true,
    "UnknownToken": "[UNK]",
    "ClassificationToken": "[CLS]",
    "SeparatorToken": "[SEP]",
    "PaddingToken": "[PAD]",
    "MaskingToken": "[MASK]",
    "LowerCaseBeforeTokenization": true,
    "ApplyBasicTokenization": true,
    "IndividuallyTokenizeCjk": true,
    "ConsiderPreTokenization": true,
    "ConsiderNormalization": true,
    "AddBeginningOfSentence": false,
    "AddEndOfSentence": true,
    "PadTokenId": 0,
    "ExcludeSpecialTokensFromPooling": false
  }
}
```

`TokenizerType` 当前支持 `bert-wordpiece` 和 `sentencepiece`。BERT 使用
`vocab.txt`，SentencePiece 使用 tokenizer model 文件；大小写、特殊 token、
截断和 padding 规则必须与目标导出物一致。`AddSpecialTokens` 仅控制 BERT 的
特殊 token；SentencePiece 使用 `AddBeginningOfSentence` / `AddEndOfSentence`。
`PaddingSide` 支持 `right` 和 `left`：
左填充时有效 token 保持顺序，`position_ids` 对有效 token 从 0 重新编号，padding
槽为 0。`MaxTokens` 是固定 shape 模型要求的精确序列长度，也是动态 shape 模型的
padding 上限（1..32768）；provider 使用 tokenizer 的有界 max-token overload，避免
先完整展开超长输入。`mean`/`auto` pooling 按 attention/pooling mask 排除 padding，
`cls` 取首个未屏蔽行（左填充时不是固定行 0），`auto` 对 pooled 输出直接采用该
向量。`Normalize=true` 时对最终向量执行 L2 归一化。

`SendAttentionMask`、`SendTokenTypeIds` 和 `SendPositionIds` 均为三态：`null`
按稳定的常用名称自动绑定，`true` 要求相应 tensor 存在且类型/形状正确，`false`
明确禁用；如果图仍声明被禁用的 tensor，还必须同时列入 `IgnoredInputNames`。没有绑定或列入 `IgnoredInputNames` 的其它输入会使 profile 合同失败，
避免把必需输入静默丢给 ONNX Runtime。SentencePiece 的 padding id 优先使用
`PadTokenId`，否则从 `PaddingToken`（默认 `<pad>`）词汇表/特殊 token 表推导；
无法确定时 fail closed，不会默认假定为 0。

`InputIdsName` 和 `OutputName` 可以留空以启用实现提供的稳定候选解析，但实际选中
的名称必须通过模型元数据校验，并由部署者在 profile/运行报告中留存；候选不唯一、元素类型不是
`int32`/`int64`、输入形状不兼容、输出维度不等于 `Dimensions` 或 tokenizer 无法
加载时，provider 必须 fail closed 或进入可观测 fallback，不能静默生成向量。

`CopilotReadiness`、`/healthz/ready` 和 `/v1/copilot/knowledge/status` 只执行
路径/profile 基本检查并读取已记录的 provider 状态，属于 lazy readiness；它们不会
主动加载 ONNX graph 或执行 tokenizer。首次推理前 readiness 通过、状态里的
`EmbeddingFallback=false` 都不能当作模型已加载或语义质量证据。profile/合同错误
会在首次调用时 fail closed；缺少 profile/资源或明确 native/runtime 装载失败时才
允许进入可观测的 384 维 hash fallback。

当前内置 `__copilot__` 文档与技能索引的 schema 固定为 `VECTOR(384)`，摄入和检索
路径会拒绝其它维度。`ModelProfile.Dimensions` 可以在 provider 直调或合同测试中
使用其它值，但这类 profile 必须使用独立的向量索引；仅修改配置不会自动迁移内置
知识库。

本地 ONNX profile 的字段定义、tiny ONNX 合同测试和真实模型证据要求见
[`M27 #185 provider model profile 证据`](benchmarks/m27-provider-model-profile.md)。
合成 fixture 只证明输入绑定和 pooling 数值语义；在目标模型 SHA-256、语义质量、
延迟/内存及可重放报告归档前，真实模型状态仍为 `NOT_READY`。

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

## 客户端运行模式合同

M27 #340 的 Web 首切片把 CopilotDock 接到统一 client runtime。构建变量
`VITE_COPILOT_RUNTIME_MODE` 可显式选择 `ServerRelay`、`BrowserDirect`、
`StudioNative` 或 `Disabled`；省略时为保持现有部署兼容而固定使用 `ServerRelay`，
不会依据一次探活在模式之间切换。

`ServerRelay` 与 `BrowserDirect` transport 已注册到真实聊天入口。ServerRelay 先通过当前活动 SonnetDB API 客户端检查
本地 `/healthz`，把客户端公网 readiness 明确标记为 `not-required`，再将数据库
Bearer Token 发送到同一活动连接派生出的固定 `/v1/copilot/chat/stream` 端点。
请求使用 `credentials: omit` 和 `redirect: error`，readiness 与携带数据库 Token/消息/页面上下文的
POST 都拒绝 3xx 跳转；不接受任意 URL，也没有外部 AI Token 输入。
BrowserDirect 只接受 `VITE_COPILOT_BROWSER_DIRECT_PUBLIC_BASE_URL` 指定的 HTTPS 地址，
且其 origin 必须位于 `VITE_COPILOT_BROWSER_DIRECT_APPROVED_ORIGINS`；公网 readiness 与
流式 POST 只携带独立的 public-client token。该 token 仅驻留内存，必须具有未来过期时间且
TTL 不超过两小时；与数据库 token 同值、缺配置、缺 token、过期或登出时均 fail closed，
不会静默回退到 ServerRelay。StudioNative 在 transport 和凭据边界完成前仍稳定拒绝。

BrowserDirect 本地工具出域还必须把
`VITE_COPILOT_BROWSER_DIRECT_ALLOW_DATA_EGRESS` 精确设为 `true`，并在
`VITE_COPILOT_BROWSER_DIRECT_ALLOWED_TOOLS` 中列出允许的工具名；空 allowlist 拒绝全部工具。
客户端从当前请求的数据库派生固定 `/mcp/{db}`，数据库 Bearer 只发送到该本地端点，
public-client token 只发送到已批准公网 origin。每次会话执行 MCP `initialize`、`tools/list`
和 `tools/call`，只接受完整声明 read-only、non-destructive、idempotent、closed-world 的工具，
并校验参数/结果 schema 和 typed contract v1。`VITE_COPILOT_BROWSER_DIRECT_MAX_RESULT_BYTES`
可收紧单次出域结果，默认 64 KiB；单轮最多 8 次工具 loop。generic MCP error、typed tool error、
未知/未批准工具、schema/版本不匹配或超预算都在发送公网 continuation 前失败。

统一状态机为每次运行维护 `runId`、严格递增 `sequence`、opaque `cursor` 和
`toolCallId`。只有 transport 提供稳定 `toolCallId` 时，完全相同的重复工具调用/结果才可幂等；
同 ID 的参数冲突、工具名错配、乱序、空 `final`、缺失 readiness、未知模式和未注册 transport
均 fail closed，并把 AbortSignal 贯穿 readiness 与流读取。
每条流必须先返回唯一的 `final` 或 `error` outcome，再返回 `done`；仅有 `done`、
缺少 `done` 或 outcome 后仍继续发送事件都会被拒绝。云端 runtime 未返回 outcome 时，
ServerRelay 会先生成稳定 `error` 再结束为 `done`，不会把截断响应误报为成功。
现有服务端 SSE 尚未发布这些 envelope 字段，因此 ServerRelay 适配器只在当前运行
内按工具名 FIFO 合成 ID 并配对 `tool_call` / `tool_result`；它不能识别 provider 重放的重复
调用，也不构成跨刷新续流或服务端幂等证据。Dock 的停止、关闭、切换/删除会话、登出与组件
卸载都会取消当前 AbortSignal；未收到完整终态的临时回答会清除，并从服务端重新同步会话。
SQL 工具页签和最终回答中的 SQL 只在完整 `done` 验证后提交。SSE 解码覆盖 LF、CRLF、
bare CR、跨行结束符分片、多行 data 与严格 JSON。
BrowserDirect 已接入本地 typed MCP tool-call loop。公网段必须以稳定 `tool_call` 收口，
本地成功结果只作为结构化、不可信数据发送到构造时已批准的固定公网 endpoint；下一段首事件
必须逐字回显对应 `tool_result`，否则 fail closed。同 `toolCallId` 同规范化参数复用单次运行内
缓存结果而不二次执行，同 ID 冲突在再次执行前拒绝；重复回放也计入 8 次 loop 上限。
当前尚无可信 Device Flow/PKCE 获取入口，也没有已部署公网 continuation、CSP/CORS 或真实
双网联调证据。Studio Native、外部 OAuth/BYOK、跨刷新续流和服务器无公网出口的真实 journey
仍保持未完成。

切换 embedding 模型、profile 语义或向量维度后必须重建文档与技能索引；当前内置
docs/skills 索引只接受 384 维，非 384 维需要独立 schema/index。API Key 应通过环境
变量、Secret Manager 或容器 secret 注入，不要提交到配置文件。

## 兼容边界

- `default` / `candidates` 是兼容字段，不会因启用分组而移除。
- 新客户端应优先消费 `groups`，并对未知分组键做忽略或回退处理。
- 模型选择只影响当前请求，不改变服务端默认配置。
- Provider 负责模型调用；MCP、权限、审计和写入确认仍由 SonnetDB 控制面约束。
