---
layout: default
title: "SonnetDB Studio"
description: "SonnetDB Studio 的产品能力说明，覆盖 Schema Explorer、SQL Editor、结果视图、轨迹地图、CopilotDock、写入审批和手工验收。"
permalink: /web-workbench/
---

# SonnetDB Studio

SonnetDB Studio 是 Web Admin 里的核心工作界面，当前语义入口为 `/admin/app/studio`，兼容入口为 `/admin/app/sql`。它把数据库浏览、SQL 编辑、写入预览、结果分析、轨迹地图和 Copilot 智能协作放在同一个操作面里，让 SonnetDB 不只是一个后端引擎，也是一套可以直接交付给开发者、运维人员和数据使用者的管理工作台。

Studio 沿用 Vue + CodeMirror + Naive UI 技术栈，并复用服务端已经存在的数据库列表、schema、SQL 执行、事件和 Copilot stream 协议。它不要求额外部署 BI 或数据库管理工具，启动 SonnetDB Server 后即可使用。桌面客户端通过 `SonnetDB.Studio` 使用 NativeWebHost 打开同一套 Studio 入口。

## 适合完成的工作

- 浏览数据库、measurement、关系表、文档集合、索引和备份状态。
- 编写、格式化、执行和分标签管理 SonnetDB SQL。
- 对 `INSERT`、`CREATE`、`DELETE`、`DROP`、授权和 Token 类操作进行 staged preview 和危险确认。
- 在同一个结果区查看表格、折线图和轨迹地图。
- 使用 Copilot 生成 SQL、解释 SQL、修复 SQL、优化查询、梳理 schema、排查事件和生成授权语句。
- 在页面上下文中把 Copilot 生成的 SQL 同步为可编辑 tab，再由用户确认执行。

## 页面布局

- **Schema Explorer**：展示数据库树、measurement、关系表、文档集合、索引和备份状态；管理员可创建或删除数据库。
- **SQL Editor**：基于 SonnetDB 方言高亮，支持多 tab、Copilot 草稿同步、执行状态和常用 SQL 操作。
- **Staged Preview**：写入和控制面变更先进入预览，危险语句需要二次确认。
- **Result Grid**：支持表格、图表、地图三种结果视图，适合从 SQL 查询直接切到趋势和轨迹分析。
- **Trajectory 模式**：面向 GEOPOINT 数据，支持选择数据库、measurement、字段、TAG 和时间范围加载轨迹。
- **CopilotDock**：全局浮窗，可在 Studio、事件、权限、设置等页面共享会话和上下文。

## Copilot 协作

Studio 中的 Copilot 不是孤立聊天框，而是伴随式数据库智能体：

- 能读取当前页面、当前数据库、SQL 编辑器内容和选中的数据库上下文。
- 能使用 `list_databases`、`list_measurements`、`describe_measurement`、`sample_rows`、`query_sql`、`explain_sql`、`draft_sql`、`execute_sql` 等工具。
- 默认可走只读查询；写入模式需要用户切换，最终仍受当前凭据权限约束。
- 写入 SQL 会同步到 Studio tab 或 staged preview，避免无感修改数据。
- 回答中可展示文档、技能、schema 和工具结果引用，便于追溯依据。

## 复用的接口

- `GET /v1/db`
- `GET /v1/db/{db}/schema`
- `POST /v1/db/{db}/sql`
- Copilot SSE stream 协议（仍由全局 CopilotDock 使用）
- `/mcp/{db}` 工具入口（供外部 Agent / MCP Host 使用）

## 交互规则

- `SELECT` / `SHOW` / `DESCRIBE` / `EXPLAIN` 直接执行。
- `INSERT` / `CREATE` / `ALTER` / `DROP` / `DELETE` / `GRANT` / `REVOKE` 先进入 staged preview。
- `DELETE` / `DROP` / `GRANT` / `REVOKE` / `CREATE USER` / `DROP USER` / `ALTER USER` / `ISSUE TOKEN` 归为危险操作，必须二次确认后才能提交。
- 左侧 Schema Explorer 以数据库树展示可见数据库和 measurement；管理员可以在 Studio 内直接新建或删除数据库。
- 预览内容和目标数据库变化后会自动判定为过期，需要重新预览。
- Copilot 继续保持全局浮窗，不在 Studio 内单独占一栏。

## 手工验收

1. 打开 `/admin/app/studio` 或 `/admin/app/sql`，确认页面标题为 SonnetDB Studio。
2. 选择一个业务数据库，确认左侧 Schema Explorer 按数据库树加载 measurement。
3. 在管理员账号下点击 Create 弹出独立对话框，输入新数据库名后确认创建，确认列表刷新且新数据库可被选中。
4. 删除一个非系统数据库，确认有二次确认提示且删除后树会刷新。
5. 输入 `SELECT ...`，点击运行，确认结果直接进入 Result Grid，且有数据时可在表格 / 图表 / 轨迹地图之间切换。
6. 输入带时间列和数值列的查询，切到图表页，确认可手动选择时间轴和值轴并显示折线图。
7. 输入 `EXPLAIN SELECT ...`，确认 Result Grid 显示 `key` / `value` 估算行。
8. 输入 `INSERT` 或 `CREATE MEASUREMENT`，确认先出现 Staged Preview，而不是直接写入。
9. 输入 `DROP` / `DELETE` / `GRANT` / `REVOKE` / `CREATE USER`，确认出现危险操作提示和确认勾选。
10. 切换到 Trajectory 模式，确认轨迹地图仍能在 Studio 内加载并回放。
11. 切换数据库后，确认预览状态失效并需要重新生成。
12. 打开右下角 CopilotDock，确认它仍是全局浮窗。

## 备注

Studio 只是前端工作区升级，没有引入新的后端 SQL API。所有读写仍走现有的数据库列表、schema、SQL 执行与 Copilot stream 协议。
