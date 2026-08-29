---
layout: default
title: "M27 #185 本地 ONNX model profile 证据"
description: "Copilot 本地文本 embedding 的模型 profile、tokenizer、输入绑定、pooling 和验证门禁。"
permalink: /benchmarks/m27-provider-model-profile/
---

# M27 #185 本地 ONNX model profile 证据

## 当前结论

本页冻结 M27 #185 的本地文本 embedding 合同和证据门禁。审计基线为
`510efbf8`，并记录该基线之上的本次研发改动。首次 `EmbedAsync` 会在 profile
完整且模型/ tokenizer/ tensor 元数据一致时创建 ONNX Runtime session，执行
tokenizer、输入绑定、pooling 和归一化；缺少明确 profile、资源或运行时不可用时，
provider 使用可观测的 384 维确定性 hash fallback，并不是 ONNX 语义推理。因此
本项在真实目标模型证据归档之前保持：

| 门禁 | 状态 | 说明 |
| --- | --- | --- |
| Copilot `IEmbeddingProvider` 抽象与 builtin fallback | `PASS` | 离线首次启动路径可运行，但 hash 向量不等价于语义模型。 |
| 本地 ONNX model profile 合同 | `PASS` | 已显式描述 tokenizer、输入 tensor、输出、pooling、归一化和维度；无效配置会稳定失败或进入可观测 fallback。 |
| 合成 tiny ONNX 合同测试 | `PASS` | `LocalOnnxEmbeddingProviderTests` 43/43 通过；仅证明 tiny ONNX/tokenizer 绑定和数值语义，不代表目标模型质量。 |
| Native AOT 发布 | `NOT_READY` | 显式 `win-x64` 发布被现有 `IoTSharp.CoAP.NET` 子模块的 `SYSLIB1100`/`SYSLIB1101` 阻断；本次 M27 未修改该范围外模块。 |
| 目标模型真实推理 | `NOT_READY` | 需要实际模型文件、tokenizer 和可追溯配置。 |
| 语义质量、延迟和内存报告 | `NOT_READY` | 未取得真实数据集和目标环境报告前不得宣称通过。 |

`CopilotReadiness.Evaluate()`、`/healthz/ready` 和知识库状态是 lazy/configuration
检查：它们只检查路径、profile 基本字段（包括特殊 token 的最小预算）和 provider 对象当前是否已经记录
fallback，不会为了探测而加载 ONNX graph、构造 tokenizer 或执行一次真实推理。
因此在首次 embedding 调用以前，`EmbeddingFallback=false` 或 readiness 通过都不
能证明模型已经加载、输入合同正确或语义推理成功。首次调用发现合同错误时必须
fail closed；只有缺失资源或明确的 native/runtime 装载失败才允许转入可观测 hash
fallback。

`NOT_READY` 是有意的发布结论。模型文件不存在、使用占位模型、开发机 quick
smoke、hash fallback、历史运行结果或仅通过 schema 校验，均不能升级为真实
ONNX 证据。

### 本地合同运行记录（2026-08-29）

在 Windows x64、.NET SDK 10.0.400 / runtime 10.0.11、ONNX Runtime 1.27.1 下，provider
定向筛选为 `43/43 PASS`；将知识状态端点测试一并筛选时为 `44/44 PASS`，embedding
preview 错误合同另行筛选为 `2/2 PASS`。测试项目 Release 构建和
`SonnetDB.Tests` Release 全量筛选为 `642/642 PASS`、0 skipped（最终重跑）。显式
`PublishAot=true; SelfContained=true; UseAppHost=true` 的 `win-x64` 发布退出码为 1，
被现有 `extensions/IoTSharp.CoAP.NET/CoAP.NET/Server/Hosting/CoapServiceCollectionExtensions.cs:314`
的 `SYSLIB1100`（`ICoapConfig` 无公共构造函数）和 `SYSLIB1101`（`Default` 属性不受支持）阻断；
因此本页不记录 Native AOT PASS。上述本地数字只验证仓库内的合成 fixture、配置绑定和状态合同，
不包含目标模型 SHA、语义质量、延迟/内存、固定硬件或连续 nightly 证据；这些门禁继续保持
`NOT_READY`。

## 适用范围

本页只针对 M27 #185 的 `CopilotEmbeddingOptions.Provider = local` 文本
embedding。M35 的 SigLIP2 图片/文字 provider 有独立的模型、预处理和报告合同，
不能把两者的通过结果互相替代。在线 Chat 的 `IChatProvider` 接线、云 Gateway
优先级和 M27 #187 eval 成本报告也分别按各自合同验收。

## Model profile 合同

`ModelProfile` 是向量兼容边界。更换模型、tokenizer、最大长度、pooling、
归一化方式或输出维度时，必须以新的 profile 标识记录并重建受影响的知识库索引；
不得把不同语义的向量混入同一 profile。profile 至少需要冻结以下字段：

| 维度 | 必须冻结的语义 | 典型配置字段 |
| --- | --- | --- |
| 模型文件 | 本地 ONNX 文件路径、文件存在性和模型来源/版本；provider 不自动下载。 | `LocalModelPath` |
| tokenizer | `bert-wordpiece` 或 `sentencepiece` 等明确算法、词表/模型路径、大小写/基础分词/CJK 处理、特殊 token、截断和 padding 规则。BERT 的 `AddSpecialTokens` 与 SentencePiece 的 `AddBeginningOfSentence` / `AddEndOfSentence` 分别控制各自特殊 token。 | `TokenizerType`, `TokenizerModelPath`, `LowerCaseBeforeTokenization`, `ApplyBasicTokenization`, `IndividuallyTokenizeCjk`, `UnknownToken`, `ClassificationToken`, `SeparatorToken`, `PaddingToken`, `MaskingToken`, `MaxTokens` |
| 输入 tensor | `input_ids` 的名称、元素类型（`int32`/`int64`）和序列形状；可选 `attention_mask`、`token_type_ids`、`position_ids` 的名称、类型和是否发送；其它模型输入必须显式忽略。 | `InputIdsName`, `AttentionMaskName`, `TokenTypeIdsName`, `PositionIdsName`, `SendAttentionMask`, `SendTokenTypeIds`, `SendPositionIds`, `IgnoredInputNames` |
| 序列处理 | 支持右侧和左侧 padding；超长文本使用 tokenizer 的有界 max-token API 截断，并尽量保留尾部 EOS/SEP；BOS/EOS/CLS/SEP 是否计入长度必须冻结。左填充时有效 token 保持原顺序，`position_ids` 从 0 重新编号，padding 槽为 0。 | `MaxTokens`, `PaddingSide`, `AddSpecialTokens`, `AddBeginningOfSentence`, `AddEndOfSentence`, `PadTokenId`, `PaddingToken` |
| 输出 tensor | 输出名称、允许的 `[1,S,H]`、`[S,H]` 或 `[1,H]` 形状、隐藏维度和 profile 维度一致性。 | `OutputName`, `Dimensions` |
| pooling | `mean` 必须使用 attention mask 排除 padding；`cls` 必须取约定位置；`auto` 只能在 profile 明确允许时使用，不能按名称猜测。 | `Pooling` (`mean`/`cls`/`auto`) |
| 后处理 | 是否执行 L2 normalization、零向量行为、浮点异常处理和最终维度。 | `Normalize`, `Dimensions` |

字段名以当前实现的 `CopilotEmbeddingModelProfile` 为准。`TokenizerType`、tokenizer
路径、pooling 和维度必须由 profile 冻结；`InputIdsName`/`OutputName` 为空时可以
使用实现提供的稳定候选解析，但实际选中的名称必须经过模型元数据校验，并由部署者在 profile/运行报告中留存，
候选不唯一时必须失败。`SendAttentionMask`、`SendTokenTypeIds` 和 `SendPositionIds`
是三态开关：`null` 按常用名称保守地自动绑定，`true` 要求指定输入存在且类型/形状
正确，`false` 明确禁用该输入；如果图仍声明被禁用的 tensor，还必须同时列入
`IgnoredInputNames`。没有绑定或列入 `IgnoredInputNames` 的其它输入会使
合同校验失败。缺少必填字段、名称不存在、类型/形状不匹配、维度不一致或 tokenizer
无法加载时，必须返回稳定的未就绪原因或进入显式 fallback，并在知识库状态中暴露
`EmbeddingFallback`；不能悄悄生成看似有效的向量。

`MaxTokens` 同时是固定 shape 模型要求的精确序列长度和动态 shape 模型的 padding
上限，范围为 1..32768。provider 将 token 数限制传入 tokenizer，避免先完整展开
超长文本再截断而造成无界内存/CPU；若配置的特殊 token 会超出上限，合同应失败或
保持有界截断。SentencePiece 的 padding id 优先使用 `PadTokenId`，否则从 profile
指定的 `PaddingToken`（默认 `<pad>`）在模型词汇表或 tokenizer 已提供的 special-token 表中推导；
当前 profile 不提供 added-special map，因此只存在于外部 added-special map 的自定义 padding token
必须显式配置 `PadTokenId`。无法确定时必须 fail closed，不能静默假定 0。`mean`/`auto` 对序列输出按 attention/pooling
mask 求平均，`cls` 选择第一个未屏蔽行（左填充时不是固定行 0）；
`ExcludeSpecialTokensFromPooling=true` 只对 mean/auto pooling 从 mask 中排除配置的
首尾特殊 token，不能改变 cls 对分类 token 的选择。

当前内置 Copilot 文档和技能知识库仍使用固定的 `VECTOR(384)` schema：
`DocsIngestor`、`DocsSearchService` 和 `SkillRegistry` 都会拒绝非 384 维向量。
因此，只有 `Dimensions = 384` 的 profile 能接入现有 docs/skills 摄入与检索；其它
维度可用于直接调用 provider 或合同测试，但不能据此宣称内置知识库可用，除非后续
另行完成 schema/index 维度参数化和迁移。

## 本地配置示例

下面的形状展示完整 profile 所需的信息。路径、模型版本和 tensor 名称必须按目标
ONNX 导出物的元数据替换；这段配置本身不是运行证据：

```json
{
  "SonnetDBServer": {
    "Copilot": {
      "Embedding": {
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
    }
  }
}
```

如果目标模型只接受其中一部分输入，profile 必须明确把其余输入标记为不发送；如果图
仍声明被禁用的 tensor，还要同时列入 `IgnoredInputNames`。`true`/`false`/`null` 的三态
规则不能用“尽力发送”替代。如果输出已经是 pooled `[H]`、`[1,H]`，不得再次按
token 维度求平均。`LowerCase` 是 `LowerCaseBeforeTokenization` 的兼容别名。
`Dimensions`、padding/position 语义、pooling、tokenizer 选项和 normalization 的
任何变化都属于新的向量兼容边界。

## 自动化合同门禁

合成 tiny ONNX 与小型 tokenizer fixture 可以在本地验证以下确定性合同：

1. tokenizer 的大小写、特殊 token、截断和 padding 结果，以及 `MaxTokens` 边界；BERT 使用
   `AddSpecialTokens`，SentencePiece 使用 `AddBeginningOfSentence` / `AddEndOfSentence`；
2. `int32`/`int64` 输入绑定、三态 attention mask/token type ids/position ids、动态/静态序列长度和左右 padding 对齐；
3. SentencePiece 非零 `<pad>` id 推导、`mean` pooling 的 mask 处理、`cls` 的首个未屏蔽行、特殊 token 排除和已 pooled 输出；
4. 输出名称、输出形状、维度和 L2 normalization 校验；
5. 缺失文件/运行时资源的可观测 fallback，以及错误名称、类型/形状不匹配和无效 profile
   合同的稳定 fail-closed；
6. 有界 tokenizer、取消、Dispose、重复调用和线程并发下 session 的资源释放。

这些 fixture 只证明 provider 执行合同，不证明任何特定模型的中文/多语言语义
质量、召回率或生产性能。测试通过后仍必须保留 `realModelEvidence=NOT_READY`，
直到实际目标模型运行报告归档。

## 真实模型证据门禁

要把本项从 `NOT_READY` 改为可发布的研发结果，报告至少要同时包含：

- 目标 ONNX 与 tokenizer 的 SHA-256、来源、许可证、模型版本和 profile JSON；
- 实际 ONNX Runtime 版本、OS/架构/RID、execution provider、线程配置和运行命令；
- tokenizer/input/pooling/normalization 的实际回显，证明不是默认猜测；名称解析结果应写入 profile 或运行报告，不能把当前状态端点的布尔值当作完整回显；
- 非空文本、空白/超长文本、Unicode/CJK、mask padding、批次边界和错误模型的
  原始结果；
- 固定语料上的 Recall@K 或等价语义质量基线，以及 P50/P95 延迟、峰值 RSS/托管
  内存和失败计数；
- 报告绑定的 commit、配置、原始 JSON/日志和可重放 artifact。

固定目标硬件、连续 nightly、Studio 安装/宿主和真实 broker/provider journey
属于外部现场验证，必须单独登记为 `NOT_READY` 或 `DEFERRED`；本地 tiny fixture
和开发机数字不能代替这些证据。

## Evidence → Finding → Path

| Evidence | Finding | Path / 后续动作 |
| --- | --- | --- |
| `LocalOnnxEmbeddingProviderTests` 的 tiny graph、BERT vocab 和 SentencePiece fixture；定向 xUnit 命令输出 | 输入 tensor 类型/名称、三态绑定、左右 padding、position ids、pooling、维度和资源生命周期合同可重复通过 | 保留为 `contractEvidence=PASS`；fixture 不升级为真实模型证据 |
| `CopilotReadiness`、`/healthz/ready` 和 `/v1/copilot/knowledge/status` 的配置检查 | 状态是 lazy；首次推理前不能证明 graph/tokenizer/语义执行成功，`EmbeddingFallback=false` 也不是质量结论 | 报告中显式记录 `readiness=configuration_only`，首次真实调用需留存原始结果 |
| `ManagementEmbeddingPreviewErrorTests` 的 fake provider 端到端测试 | provider 合同错误在向量预览边界稳定映射为 `503 embedding_failed`，保留可诊断消息 | 保留为 API error-contract 证据；不替代真实模型质量/性能报告 |
| 真实目标 ONNX/tokenizer SHA、profile 回显、质量/性能原始日志 | 当前工作区尚未提供可追溯目标模型或目标环境结果 | `realModelEvidence=NOT_READY`；补齐来源/license、Recall@K、P50/P95、RSS/托管内存和可重放 artifact 后再复核 |
| 固定目标硬件、7 天 nightly、Studio 安装/宿主、真实 broker/provider | 不属于本地 fixture 能证明的范围，当前均后置 | 各自保持 `NOT_READY`/`DEFERRED`，不得写入本页 PASS |

## 本地复现

构建和测试时使用 PowerShell 7。以下命令只验证仓库内合同，不会自动下载目标
模型，也不会生成发布质量结论：

```powershell
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoLogo -NoProfile -Command 'dotnet build tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release -r win-x64 --no-restore --nologo'
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoLogo -NoProfile -Command 'dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release -r win-x64 --no-build --no-restore --nologo --filter "FullyQualifiedName~LocalOnnxEmbeddingProviderTests|FullyQualifiedName~CopilotKnowledgeStatusEndpointTests"'
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoLogo -NoProfile -Command 'dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release -r win-x64 --no-build --no-restore --nologo --filter "FullyQualifiedName~ManagementEmbeddingPreviewErrorTests"'
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoLogo -NoProfile -Command 'dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release -r win-x64 --no-build --no-restore --nologo'
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoLogo -NoProfile -Command 'dotnet publish src/SonnetDB/SonnetDB.csproj -c Release -r win-x64 -p:SonnetDbPublishAot=true -p:PublishAot=true -p:SelfContained=true -p:UseAppHost=true --no-restore --nologo'
```

本轮观察结果依次为构建 `0 warning / 0 error`、ONNX/状态定向 `44/44 PASS`、
preview 错误合同 `2/2 PASS`、全量 `642/642 PASS`（0 skipped）；Native AOT publish
`NOT_READY`（退出码 1，见上面的 CoAP `SYSLIB1100`/`SYSLIB1101` 阻断）。

Windows 上显式指定 `win-x64` 是为了让测试宿主选择匹配的 ONNX Runtime 原生资产；
Linux/macOS 应改用当前主机对应的 RID。RID 选择只解决本地运行时装载，不增加任何
真实模型或发布硬件证据。

M27 #187 的 provider usage、质量和成本报告仍需使用
[`m27-copilot-eval-cost.md`](m27-copilot-eval-cost.md) 的 `m27-copilot-eval-v1`
合同；`ScriptedChatProvider` 或本页的合成 ONNX fixture 都不能通过
`-RequireReady`。

## 相关入口

- [Copilot Provider 与模型目录](../copilot-providers.md)
- [M27 #187 Copilot Eval 与成本报告](m27-copilot-eval-cost.md)
- [M27 工业诊断样例](../../samples/SonnetDB.IndustrialDiagnostics/README.md)
- [当前路线图](../../ROADMAP.md)
