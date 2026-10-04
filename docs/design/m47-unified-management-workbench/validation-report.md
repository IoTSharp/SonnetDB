# M47 原型检查记录

日期：2026-10-05（Asia/Shanghai）。结论：**可交付外轮廓、导航、典型页面与共享流程评审，仍为 REVIEW_DRAFT。** 用户尚未确认，生产前端实现未开始。

## 实际检查

| 项目 | 结果与范围 |
|---|---|
| 目录结构 | 七个一级、30 个全局页面、九模型、166 任务，模型任务 60；页面 ID 唯一，任务标题与目录顺序一致，示例表格宽度一致 |
| 页面点击 | IAB 逐项打开 39 个入口和 166 个任务页签，均有可见内容；浏览器 error 日志为空。只证明静态渲染与点击接线 |
| 二级任务 | 字段、专用表格、JSON/SQL 输入、图表配置、导入映射、说明及 Inspector 接线；规划任务使用说明视图，不能执行后端调用 |
| 草稿 | SQL 输入切换页面后恢复；字段草稿按页面/任务保存在内存中。未验证跨刷新、跨宿主或持久恢复 |
| 选中行 | 点击后样式、Inspector 和 aria-selected 同步；工作区打开与关闭为独立按钮 |
| 删除门禁 | 未确认时禁用；错误大小写仍禁用；对象原名 FactoryTopology + 风险勾选后启用。返回编辑，没有执行确认动作 |
| 无权限 | 主数据、底部载荷和 Inspector 数据隐藏；读取动作不可用 |
| 导入停止 | 示例显示已停止后续批次、已提交 300 / 未提交 200；已提交批次不写成回滚或无重复续传 |
| 全局状态 | 正常、空、加载、局部失败、只读、离线、无权限、错误、超时、截断、长内容 11 状态逐项切换 |
| 响应式 | 1600×1000 桌面；1280×900、1100×800 保留左侧并默认收起检查器；390×844 默认左右面板和重复底部结果收起。后三尺寸未发现文档横向溢出，宽表自身横向滚动 |
| Graph | 四顶点、三边，与 Inspector 的 Pump-01 → Station-01 关系一致；保持 Beta |
| 结果能力 | 只有 SQL 主编辑器显示图表 / EXPLAIN 示例；其它页面仅表格 / JSON，专用任务使用自己的数据，避免展示异模型温度或计划 |
| 语法与空白 | Node --check 检查 app.js、catalog.js、task-details.js、preview.mjs；git diff --check 无诊断 |

页面点击详细记录见 [page-task-checks.json](screenshots/page-task-checks.json)。这些是设计夹具检查，不代表 39 页面、166 任务的生产功能已完成。

## 截图与有界检查

- [桌面外壳](screenshots/desktop-shell.png)：1600×1000，顶部、一级轨、左资源树、中央 SQL、右 Inspector、下结果和状态栏。
- [Graph](screenshots/desktop-graph.png)：同一示例快照，Graph Beta 与作用域可见。
- [危险审批](screenshots/desktop-approval.png)：原名与影响范围确认；本地流程预览。
- [1280 桌面](screenshots/desktop-1280.png)：检查器默认收起。
- [390 巡检](screenshots/mobile-inspection.png)：单主表，按需打开导航/Inspector。

先以两页、五任务试运行（45 秒上限），再检查剩余 37 页、161 任务（90 秒、39 页/166 任务总数上限）。状态检查最多 11 项、25 秒；单交互设置 1.2–2 秒超时。没有无界轮询或完整测试扩张。

设计复核进行了两轮。首轮发现的重复列表、Graph 快照、异模型结果、偏好尺寸、辅助文字对比、删除占位指令和嵌套按钮问题均已修正，并重新捕获截图。最终只读复核 10 项（5 源文件 + 5 图）未发现阻断这次评审的新问题。多页签时当前页签自动滚入可见区域，留待逐页交互定稿确认。

## 机械检查限制

impeccable detector 只扫描原型目录，一次执行、退出码 0、JSON `[]`。但检查器明确报告 **DEGRADED**：htmlparser2/css-select/css-tree/domutils 不可用，降级 regex；CSS 变量、选择器及计算对比度未被完整评估。因此只记录“regex 未报问题”，不能记为完整 impeccable PASS 或可访问性验收。

人工复核辅助文字 token #607085 对 canvas/status 约为 4.75:1 / 4.70:1。原生 dialog、Escape、可见焦点、页签箭头及原名门禁已有设计基础；完整键盘树导航、屏幕阅读器、各字段错误提示与全部状态组合仍需后续独立核验。

## 生产边界与交接

- web/src 与 VS Code 生产代码未修改；没有运行生产构建、真实 Server、AI/MCP、Native AOT、安装或 Extension Host 验收。
- 九模型保持原生语义，统一数据库逻辑上下文导航；SonnetMQ Topic 的数据库命名空间/权限与物理实例存储分别说明，当前单库目录备份不包含共享 MQ 日志。保留 SQL 名称大小写合同，Graph 始终 Beta。
- 采用 HTML 评审布局与交互；未调用图片生成模型，未宣称使用 GPT Image 2.5。
- 当前是全目录、任务规范及可点击设计稿。真实数据量、接口能力、宿主边界和逐页最终像素仍待确认与细化。
- 文档与原型保留未提交，等待设计评审；本轮未运行提交前 restore/format，也未执行 commit/push。下次提交必须在最终工作树执行仓库要求的完整格式门禁。
- 原型预览两次运行均在 loopback、白名单文件及 30 分钟自动超时范围内。最后的 Node PID 66288 / PowerShell 父 PID 20460 已经停止并核验不存在；旧 PID 51720 已被无关 MCP 进程复用，未操作该进程。没有遗留任务进程或临时文件，已移除本轮撤回源码后留下的空目录，并恢复浏览器临时视口。已加载原型可继续交互；预览服务结束后刷新需重新启动，HTML 文件仍可直接在普通浏览器打开。

## WB-00 交接基线门禁（2026-10-05）

- `dotnet restore SonnetDB.slnx`：通过，所有项目均为最新。
- `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`：通过；未发现格式差异。
- `node --check`：`prototype/app.js`、`catalog.js`、`task-details.js`、`preview.mjs` 均通过。
- `git diff --check`：通过；设计包链接、7/30/9/166/60 目录标记及 MQ `scope=database` / `persistenceScope=instance`、实例共享 `.system/mq` 与单库备份边界已回读核对。
- 以上仅证明设计交付基线与静态原型证据可审查，不代表生产前端、真实 Server、三宿主、AOT、安装或发布门禁通过。
- 设计交付基线已在本地提交 `3484aafd`；本记录与该提交同属 WB-00，未推送远端。

下一步：先确认外壳和导航，再确认共享流程，随后按页面逐项定稿；只有确认的设计进入生产实现。

## MQ 导航更正（2026-10-05）

用户指出消息与事件也需要存储，质疑 MQ 为什么被独立分组。核查生产代码后更正初稿：Topic 已按数据库名称限定并使用数据库读写权限，物理日志由 Server 实例共享。前稿把物理存储范围直接用于导航与页签身份，造成数据库语义丢失；现调整为九模型统一在数据库资源树，MQ 同时标明逻辑 scope=database / persistenceScope=instance。仅更正设计，未改变消息存储实现。

生产依据：[数据库范围发布与拉取](../../../src/SonnetDB/Endpoints/Routes/MessageQueueEndpoints.cs)、[Server 注册共享日志](../../../src/SonnetDB/Hosting/SonnetDbServiceRegistration.cs)、[消息日志与 ACK 记录](../../../src/SonnetDB.Core/Mq/SonnetMqStore.cs)。现状采用专用 append-only 日志，不宣称已经复用 KV/关系表或具备跨模型原子提交。单库备份不覆盖共享消息日志的限制继续在 Inspector 与备份流程呈现。

原来的 39 页/166 任务点击记录属于初稿证据；更正后的验证限定 MQ 七任务、统一导航、数据库上下文与持久化说明，另记录结果，避免把旧检查写成新版全部验收。

更正验证：七个 MQ 任务均已实际打开，逻辑数据库与对象上下文保持 factory / DeviceEvents，物理范围说明含 .system/mq；整体恢复任务保留 factory 上下文并明确实例共享 Store 范围。首次断言过度要求恢复说明合并显示“数据库 / Topic”，实际整体恢复以数据库上下文加实例存储说明呈现，读 DOM 核对后调整断言，未修改恢复能力。浏览器 error 日志为空，39/166/60/7 结构与 Node 语法、git diff --check 通过。390 巡检未横向溢出，重复底部结果仍关闭。记录见 [mq-scope-checks.json](screenshots/mq-scope-checks.json)，[更新后的 MQ 页面](screenshots/desktop-mq.png)；原有五张截图已同步新导航。
