## SonnetDB Copilot Provider-neutral 配置

SonnetDB 不再使用 `international` / `domestic` 之类的品牌化 Provider 枚举。Chat 与远程 Embedding 统一依赖 OpenAI-compatible 协议，本地 Embedding 可选择 builtin hash 或 ONNX；Web Admin 模型目录按“平台默认、自定义、本地”分组，并保留旧的扁平候选列表兼容字段。

当前配置、模型分组契约、OpenAI-compatible、Azure 适配网关、国内兼容网关、Ollama、vLLM 和 Embedding 示例统一维护在：

- [Copilot Provider 与模型目录](../copilot-providers.md)

请以该文档为准。历史文章中直接切换国际站/国内站以及在 Web UI 保存任意 API Key 的描述已不再适用于当前版本。
