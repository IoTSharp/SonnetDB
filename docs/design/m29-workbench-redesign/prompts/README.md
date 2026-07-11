# M29 高保真位图提示词

所有提示词用于 `ui-mockup`，以 `../concept-framework-microsoft365-light.png` 作为输入参考图。生成时必须先拼接 `00-shared-framework.txt`，再拼接具体页面提示词。

推荐使用 GPT Image edit，而不是无参考 generate，以保持 1536×1024 的全局几何稳定。

## 输出清单

| 文件 | 页面 |
| --- | --- |
| `prototype-01-sql-timeseries.png` | SQL / 时序查询 |
| `prototype-02-write-approval.png` | 共享写审批 |
| `prototype-03-relational-data.png` | 关系数据网格 |
| `prototype-04-relational-designer.png` | 表设计器 / 索引 |
| `prototype-05-relational-import-er.png` | 导入映射 / ER |
| `prototype-06-document.png` | Document Explorer |
| `prototype-07-document-validator.png` | Validator / 导入 |
| `prototype-08-kv.png` | KV 浏览器 / 批量治理 |
| `prototype-09-mq-overview.png` | MQ 概览 |
| `prototype-10-mq-messages.png` | MQ 消息浏览 |
| `prototype-11-mq-consumers.png` | MQ 消费者组 / 配置 |
| `prototype-12-vector.png` | 向量检索 / 索引详情 |
| `prototype-13-fulltext.png` | 全文检索 / Analyzer |
| `prototype-14-objects.png` | 对象浏览器 |
| `prototype-15-object-governance.png` | 对象治理 / Multipart / Audit |
| `prototype-16-studio-bridge.png` | Studio 原生桥 / 本地 Server |
| `prototype-17-vscode.png` | VS Code Explorer / SQL / Copilot |

17 张关键帧已生成到 `../renders/`，统一使用 `gpt-image-2`、1536×1024 和 high quality。Web/Studio 的 16 张关键帧使用参考图 edit；VS Code 帧为避免继承 Web 外壳而使用独立 generate。可在配置 `OPENAI_API_KEY` 与 `OPENAI_BASE_URL` 后运行 `./render-all.ps1` 复现；脚本默认跳过已有文件，使用 `-Force` 可覆盖。
