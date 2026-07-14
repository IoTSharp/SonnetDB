---
title: "SonnetDB 里程碑三：AI Copilot 与生态系统发布——嵌入智能的时序数据平台"
date: 2026-04-25
---

# 新闻稿

> **2026-07-14 勘误**：发布稿原文把设计目标写成了已交付事实。当前实现采用 `Microsoft.Extensions.AI` 风格抽象和自研 `CopilotAgent`，没有接入 Microsoft Agent Framework；本地 ONNX provider 尚未执行模型；Web Copilot 对话仍由云端 Runtime 编排。以下能力说明已按实际实现修正。

## SonnetDB 里程碑三：AI Copilot 与生态发布
### 内建 AI 助手、MCP 协议集成、批量写入引擎与 VS Code 扩展

**[城市/日期]** —— 开源时序数据库 SonnetDB 今日宣布完成 AI 能力与生态系统的第三个里程碑。该版本引入了内建 Copilot AI 助手、MCP（Model Context Protocol）集成、高性能批量写入引擎以及 VS Code 扩展预览，将 SonnetDB 从数据库引擎升级为智能数据平台。

### SonnetDB Copilot：数据库内建的 AI 助手

SonnetDB Copilot 由自研 `CopilotAgent` 编排，并通过 provider 抽象组合知识库、技能和 MCP 工具：

**嵌入提供程序**：
- BuiltinHashEmbedding：零依赖的 SHA-256 + 词袋哈希投影 384 维嵌入
- LocalOnnxEmbedding：仅有配置与类型占位，当前版本尚不可执行
- OpenAICompatibleEmbedding：兼容 OpenAI API 的远程嵌入

**聊天提供程序**：支持 OpenAI、Azure OpenAI、DashScope（阿里通义千问）、ZhiPu（智谱）、Moonshot（月之暗面）、DeepSeek

**知识库系统**：自动扫描 `docs/` 和 `copilot/skills/` 目录，分块、嵌入、索引至系统内置 `__copilot__` 数据库，支持语义检索。

**技能库**：6 个内建技能——查询聚合、PID 调优、预测指南、慢查询排查、Schema 设计、批量写入。

**SQL 自纠正**：对话中生成的 SQL 自动执行验证，失败时自动重试（最多 3 次），确保生成可执行的正确 SQL。

### CopilotDock：Web 界面中的全局 AI 面板

- 全局浮动窗口，支持拖拽、折叠、全屏
- **页面感知**：自动感知当前页面上下文，提供快捷操作
- **读写模式**：只读浏览模式与可写执行模式切换
- **模型选择器**：支持多个 AI 模型一键切换
- **会话历史**：服务端持久化并按 owner 隔离，支持跨设备同步
- **SQL 发送**：对话中生成的 SQL 一键发送到控制台执行
- **权限审批**：写入操作需用户确认批准
- **启动模板**：7 类预设提示模板

### MCP 协议集成

SonnetDB 实现了 Model Context Protocol（MCP）标准端点 `/mcp/{db}`，暴露以下工具：
- query_sql、list_measurements、describe_measurement
- list_databases、sample_rows、explain_sql
- docs_search、skill_search

这使任何 MCP 主机应用（如 Claude Desktop）可直接连接 SonnetDB 并执行数据库操作。

### 高性能批量写入

三端点三种格式：
- **Line Protocol**（`/lp`）：InfluxDB 兼容格式
- **JSON**（`/json`）：结构化 JSON 数组
- **SQL VALUES**（`/bulk`）：列式批量格式

配合三模式 Flush（`?flush=false|true|async`），灵活平衡写入性能与持久化保障。

### 可观测性

- **Prometheus 指标端点**：`/metrics`
- **SSE 实时事件流**：`/v1/events`，实时推送指标、慢查询、数据库事件
- **健康检查**：`/healthz`

### Web 管理后台

完善的 Vue 3 + Naive UI 单页应用，包含：
- 仪表盘、SQL 控制台（四视图：文本/表格/图表/地图）
- 轨迹地图、数据库管理、用户/授权/Token 管理
- Copilot AI 设置

### VS Code 扩展（预览版）

SonnetDB for VS Code 扩展已进入开发阶段，规划功能包括：
- 远程服务器连接管理器
- 数据库浏览器（树形视图）
- SQL 编辑器与多结果查看器
- Copilot AI 辅助面板
- 本地管理服务器

### 关于 SonnetDB

SonnetDB 是一款开源（MIT 许可证）时序数据库，专为 IoT 物联网、工业控制、运维监控和实时分析场景设计。自发布以来，SonnetDB 已建立包括核心引擎、SQL 查询、50+ 分析函数、向量检索、地理空间、PID 控制和 AI Copilot 在内的完整能力矩阵。项目地址：https://github.com/maikebing/SonnetDB

### 路线图展望

SonnetDB 将持续发展，下一步规划包括：
- M17：可观测性与运行时可见性增强
- M18：VS Code 扩展正式版
- 社区贡献指南与插件生态建设

### 媒体联系

- 项目主页：https://github.com/maikebing/SonnetDB
- 文档地址：https://github.com/maikebing/SonnetDB/docs

# # #
