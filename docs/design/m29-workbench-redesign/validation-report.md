# M29 重设计原型验证记录

验证日期：2026-07-11

## 浏览器

- Microsoft Edge（Playwright CLI，`msedge` channel）
- Web/Studio 原型：`prototype/index.html`
- VS Code 原型：`prototype/vscode.html`

## 视口

| 视口 | 验证内容 | 结果 |
| --- | --- | --- |
| 1536×1024 | 八模型切换、二级页签、Inspector、表格、编辑器 | 通过 |
| 1280×800 | Explorer + 工作区、对象治理双列布局 | 通过 |
| 800×900 | Explorer 收起、指标收起、表单单列化、工作区页签 | 通过 |
| 1536×1024 | VS Code Explorer、SQL、Table/Raw/Chart、Copilot | 通过 |

## 已验证交互

- Explorer 切换 SQL/时序、关系、文档、KV、MQ、向量、全文和对象桶。
- 自动化穷举八模型共 31 个二级任务页；每页均成功生成对象标题和工作区内容，且文档宽度未超过 1536px 视口。
- 关系表切换数据、设计器、索引、导入导出、ER 和 DDL。
- 文档切换 Documents、查询、Validator、索引和导入导出。
- MQ 切换概览、消息、消费者组和配置。
- 结果抽屉、历史抽屉、连接 / Studio bridge popover。
- staged write approval 打开、确认 checkbox 解锁与结果抽屉反馈。
- VS Code Read-only → Read-write 原生确认弹窗。
- Explorer 搜索和数据库分组折叠。

## 结果

- 两个原型控制台均为 0 errors / 0 warnings。
- 未发现文字与按钮重叠、三栏互相遮挡或固定区被树滚动挤出的问题。
- 800px 视口下复杂治理表单切为单列，并保持独立滚动；此宽度定位巡检与有限编辑，不宣称移动端完整治理体验。
- Playwright 验证截图保存在 `SonnetDB/output/playwright/m29-redesign/`，不作为产品运行时资源。

## 未执行

GPT Image 位图渲染未执行：当前机器未配置 `OPENAI_API_KEY` / `OPENAI_BASE_URL`。页面级提示词和输出清单已经完整保存在 `prompts/`，不影响 HTML 原型评审。
