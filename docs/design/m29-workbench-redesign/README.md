# M29 多模型管理工作台全量重设计

本目录是 Milestone 29 管理界面的设计基线，不直接修改 `web/src`，也不改变现有 API、权限、写审批或模型语义。

## 交付物

- `design-brief.md`：总体判断、框架尺寸与实施边界。
- `design-system.md`：色彩、排版、间距、密度、状态和响应式规则。
- `screen-matrix.md`：M29 全部页面、状态、关键动作与原型覆盖情况。
- `interaction-spec.md`：导航、页签、检查器、历史、导出与危险操作规范。
- `validation-report.md`：浏览器、视口和关键交互验证记录。
- `prototype/index.html`：Web Admin / Studio 的可交互桌面原型。
- `prototype/vscode.html`：VS Code Remote-first 子集原型。
- `prompts/`：按关键工作流拆分的 GPT Image 提示词。
- `concept-framework-microsoft365-light.png`：浅色母版。
- `concept-mq.png`：早期 MQ 深色概念基准。

## 查看原型

直接在浏览器打开 `prototype/index.html`。左侧资源树可切换八种数据模型；各工作台的二级页签、检查器、历史抽屉、结果抽屉和写审批弹窗均可操作。

`prototype/vscode.html` 单独展示 M29 #259 的 VS Code 开发者子集，避免把完整治理能力错误地带入扩展端。

## 视觉命题

一套安静、精确、可长时间工作的工业数据库 IDE：以近白数据平面承载密集信息，以 64px 功能轨和 304px Explorer 稳定定位，以单一 Fluent 蓝表达交互，以清晰的检查器和审批层承接复杂度。

## 内容计划

1. 全局外壳负责连接、命令、对象上下文和跨模型切换。
2. 八模型工作台分别围绕浏览、查询、编辑或治理的核心任务组织。
3. 共享结果、历史、导出和写审批保持操作语义一致。
4. Studio 增加本地 Server 与原生文件桥；VS Code 只保留只读浏览、SQL 与 Copilot。

## 交互命题

- 对象切换采用工作区页签和 140ms 内容淡入，避免整页重载感。
- Explorer、检查器和底部结果区使用可预测的折叠与分隔条反馈。
- 高风险操作必须经过 staged preview，再进入明确确认；危险色只出现在风险本身和最终提交动作。
