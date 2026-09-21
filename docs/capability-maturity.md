# Capability Maturity

SonnetDB publicly describes fourteen capabilities: nine native data models and five cross-cutting platform capabilities. The machine-readable source of the current mapping is [`fourteen-capability-evidence-index.json`](audits/fourteen-capability-evidence-index.json).

The public status vocabulary is deliberately narrow:

| Status | Meaning |
|---|---|
| `supported` | The named slice has an implementation, an external entry point, and matching automated evidence. |
| `partial` | Some implementation or validation exists, but the acceptance boundary is not closed. |
| `planned` | The remaining implementation or execution is scheduled and is not delivered. |
| `not_planned` | The capability is explicitly outside the current SonnetDB contract. |
| `beta` | A bounded preview contract is available; production gates are not passed. |

The status applies to the named slice, not to every possible workload. A local test, fixture, mock, or quick profile does not replace fixed-hardware, cross-process recovery, external semantic comparison, real-provider, or long-running evidence. Graph remains `beta` until its production gates pass. Single-database backup does not include the instance-scoped SonnetMQ directory.

## Maturity / 成熟度

SonnetDB 对外公开十四项能力：九种原生数据模型和五项跨模型平台能力。当前口径以 [`fourteen-capability-evidence-index.json`](audits/fourteen-capability-evidence-index.json) 为机器可读来源。

状态只使用以下五种值：

| 状态 | 含义 |
|---|---|
| `supported` | 指定切片已有实现、对外入口和对应自动化证据。 |
| `partial` | 已有部分实现或验证，但验收边界尚未闭环。 |
| `planned` | 剩余实现或执行已排期，尚未交付。 |
| `not_planned` | 明确不属于当前 SonnetDB 合同。 |
| `beta` | 有限预览合同可用，但生产门禁尚未通过。 |

状态只适用于索引中注明的切片，不代表所有规模和部署方式都已完成。单元测试、fixture、mock 或 quick profile 不能替代固定硬件、跨进程恢复、外部语义对拍、真实 provider 或长期运行证据。Graph 在生产门禁通过前始终标为 `beta`；单库备份不包含实例级 SonnetMQ 目录。
