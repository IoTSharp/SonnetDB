---
layout: default
title: "SonnetDB Workbench"
description: "Web Admin SQL Console 升级后的 Workbench 首版说明、协议复用和手工验收清单。"
permalink: /web-workbench/
---

# SonnetDB Workbench

SonnetDB Workbench 是 Web Admin 里 `/admin/app/sql` 的首版工作台。它沿用现有的 Vue + CodeMirror + Naive UI 技术栈，把原 SQL Console 升级成更完整的工作区，Copilot 继续保持原来的全局浮窗。

## 页面布局

- Schema Explorer（数据库树 + measurement）
- SQL Editor
- Staged Preview
- Result Grid（表格 / 图表 / 轨迹地图）
- Trajectory 模式
- 全局 CopilotDock 浮窗

## 复用的接口

- `GET /v1/db`
- `GET /v1/db/{db}/schema`
- `POST /v1/db/{db}/sql`
- Copilot SSE stream 协议（仍由全局 CopilotDock 使用）

## 交互规则

- `SELECT` / `SHOW` / `DESCRIBE` / `EXPLAIN` 直接执行。
- `INSERT` / `CREATE` / `ALTER` / `DROP` / `DELETE` / `GRANT` / `REVOKE` 先进入 staged preview。
- `DELETE` / `DROP` / `GRANT` / `REVOKE` / `CREATE USER` / `DROP USER` / `ALTER USER` / `ISSUE TOKEN` 归为危险操作，必须二次确认后才能提交。
- 左侧 Schema Explorer 以数据库树展示可见数据库和 measurement；管理员可以在 Workbench 内直接新建或删除数据库。
- 预览内容和目标数据库变化后会自动判定为过期，需要重新预览。
- Copilot 继续保持全局浮窗，不在 Workbench 内单独占一栏。

## 手工验收

1. 打开 `/admin/app/sql`，确认页面标题为 SonnetDB Workbench。
2. 选择一个业务数据库，确认左侧 Schema Explorer 按数据库树加载 measurement。
3. 在管理员账号下点击 Create 弹出独立对话框，输入新数据库名后确认创建，确认列表刷新且新数据库可被选中。
4. 删除一个非系统数据库，确认有二次确认提示且删除后树会刷新。
5. 输入 `SELECT ...`，点击运行，确认结果直接进入 Result Grid，且有数据时可在表格 / 图表 / 轨迹地图之间切换。
6. 输入带时间列和数值列的查询，切到图表页，确认可手动选择时间轴和值轴并显示折线图。
7. 输入 `EXPLAIN SELECT ...`，确认 Result Grid 显示 `key` / `value` 估算行。
8. 输入 `INSERT` 或 `CREATE MEASUREMENT`，确认先出现 Staged Preview，而不是直接写入。
9. 输入 `DROP` / `DELETE` / `GRANT` / `REVOKE` / `CREATE USER`，确认出现危险操作提示和确认勾选。
10. 切换到 Trajectory 模式，确认轨迹地图仍能在 Workbench 内加载并回放。
11. 切换数据库后，确认预览状态失效并需要重新生成。
12. 打开右下角 CopilotDock，确认它仍是全局浮窗。

## 备注

Workbench 只是前端工作区升级，没有引入新的后端 SQL API。所有读写仍走现有的数据库列表、schema、SQL 执行与 Copilot stream 协议。
