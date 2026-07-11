# M29 页面覆盖矩阵

## 覆盖口径

“界面”按用户任务划分，不按 Vue 文件数量划分。一个高保真关键帧可以覆盖同一任务中的正常状态，但空、错、只读和危险确认必须在交互规范中单独定义。

| ID | 交付面 | 页面/状态 | 核心任务 | 原型入口 |
| --- | --- | --- | --- | --- |
| A01 | Web/Studio | 全局框架 + Explorer | 切换连接、数据库、八模型对象和工作区页签 | `prototype/index.html` |
| A02 | Web/Studio | SQL / 时序查询 | 编写 SQL、查看 Table/Raw/JSON/Chart/Map 结果 | Explorer → `Measurements` |
| A03 | Shared | 结果抽屉 | 跨模型查看、复制、CSV/JSON 导出 | 顶部“结果” |
| A04 | Shared | 历史抽屉 | 按模型/状态筛选并恢复操作上下文 | 顶部“历史” |
| A05 | Shared | 写审批 | staged preview、风险说明、dry-run 与确认 | 任一“暂存…”按钮 |
| A06 | Shared | 连接库 | Managed Local / Remote、默认库、健康状态 | 顶部连接按钮 |
| B01 | Web/Studio | 关系数据网格 | 分页、排序、过滤、行内 INSERT/UPDATE/DELETE | `Tables → machines` |
| B02 | Web/Studio | 表设计器 | 创建表、增删改列、预览 DDL | 关系页签 `设计器` |
| B03 | Web/Studio | 索引管理 | 查看、创建、重建和删除索引 | 关系页签 `索引` |
| B04 | Web/Studio | 导入/导出 | 文件选择、列映射、dry-run、进度和错误报告 | 关系页签 `导入 / 导出` |
| B05 | Web/Studio | ER 图 | 浏览表关系、聚焦对象、导出 | 关系页签 `ER` |
| B06 | Web/Studio | DDL | 查看和导出对象定义 | 关系页签 `DDL` |
| C01 | Web/Studio | KV 浏览器 | 前缀树、游标扫描、类型化值、TTL 编辑 | `KV Keyspaces → device-cache` |
| C02 | Web/Studio | KV 批量治理 | 批量 set/remove、前缀删除、清理过期 | KV `批量操作` |
| C03 | Web/Studio | MQ 概览 | topic 吞吐、积压、retention、DLQ 摘要 | MQ 页签 `概览` |
| C04 | Web/Studio | MQ 消息浏览 | offset/时间 seek、header/payload、publish/ack | MQ 页签 `消息` |
| C05 | Web/Studio | MQ 消费者组 | lag、ack、订阅状态和进度 | MQ 页签 `消费者组` |
| C06 | Web/Studio | MQ 配置 | retention、段与 durable 配置只读/维护 | MQ 页签 `配置` |
| C07 | Web/Studio | 向量检索 | Raw/Text Embed、Top-K、filter、命中详情 | `Vector Indexes → embeddings.vector` |
| C08 | Web/Studio | 向量索引详情 | 维度、metric、HNSW 参数与 row count | 向量右侧检查器 |
| C09 | Web/Studio | 全文检索 | BM25、All/Any/Phrase/Fuzzy、分页和高亮 | `FullText Indexes → docs.search` |
| C10 | Web/Studio | Analyzer | Jieba/CJK 切词和 token 预览 | 全文页签 `Analyzer` |
| D01 | Web/Studio | 对象浏览器 | bucket、前缀、对象、上传下载和预览 | `Object Buckets → raw-data` |
| D02 | Web/Studio | 对象治理 | 版本、lifecycle、retention、policy、quota、hold | 对象页签 `治理` |
| D03 | Web/Studio | Multipart / Audit | 会话、分片、完成/中止与审计 | 对象页签 `Multipart / 审计` |
| D04 | Web/Studio | 文档 Explorer | find/filter/projection/sort、分页和文档详情 | `Collections → device-events` |
| D05 | Web/Studio | 文档查询 | count/distinct/aggregate 与结果 | 文档页签 `查询` |
| D06 | Web/Studio | Validator | 查看、编辑、样本预检、保存/删除 | 文档页签 `Validator` |
| D07 | Web/Studio | 文档索引/导入导出 | JSON path/FullText rebuild 与 JSONL | 文档页签 `索引 / 导入导出` |
| E01 | Studio | 原生桥与本地 Server | Health、Start/Stop、data root、磁盘连接库 | 顶部连接菜单 |
| E02 | Studio | 原生文件对话语义 | 导入/导出/上传下载优先使用宿主能力 | 相关工作台操作 |
| E03 | VS Code | Remote Explorer | 连接、schema 与 KV/向量/全文/MQ 只读树 | `prototype/vscode.html` |
| E04 | VS Code | SQL 三视图 | SQL + Table/Raw/Chart | `prototype/vscode.html` |
| E05 | VS Code | Copilot | 流式只读对话；read-write 前置确认 | `prototype/vscode.html` |

## 每页必须覆盖的状态

每个工作台在实现设计评审时至少验证：

- 正常有数据。
- 首次进入的空状态。
- 请求中且布局不跳动。
- 当前模型端点失败、其他模型仍可用。
- 无写权限的只读状态。
- 长名称、长 JSON、超宽表格和大量树节点。
- Inspector 收起/展开。
- Explorer 收起和 1100px 窄桌面布局。

## 当前实现状态

截至 2026-07-11，A01-E05 的 34 个页面/任务面均已完成核心任务接线。此前剩余项已收口：A06 提供逐连接健康探测与状态；D03 提供服务端 Multipart 会话/分片分页枚举和跨标签页恢复；E01 提供可选择、持久化并用于启停的 data root；E02 已覆盖文本、二进制和目录原生对话语义，并保留浏览器降级。

状态矩阵中的空、错、只读、长内容和响应式条目仍是每次设计评审与回归测试必须持续验证的质量要求，不因核心任务完成而取消。

## 位图提示词覆盖

`prompts/` 为 A01、A05、B01-B05、C01、C03-C10、D01-D07、E01、E03-E05 提供关键帧提示。所有提示词均以 `concept-framework-microsoft365-light.png` 为几何和风格参考，不允许生成营销页、卡片仪表盘或改变外壳结构。
