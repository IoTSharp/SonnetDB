# M47 管理工作台设计评审包

状态：`CONFIRMED_BASELINE`，2026-10-05。用户已确认外壳、导航、九模型数据库逻辑资源树、MQ database + Topic / instance `.system/mq` 边界、Graph Beta 以及共享流程与六态语义；切片推进至 WB-29 KV 新Web→本机Kestrel游标/Get/当前JSONL、普通WRITE审批NX成功/未应用影响0/交换及撤权锁存首轮真实3/3，三份成功JSON与SHA256 manifest独立落盘。生产组件/Server/共享runner不改，完整atomic/恢复/Server预算另验。范围和分开的fixture/真实服务证据见[队列](work-queue.md)与[验证记录](validation-report.md)。该状态只表示设计基线获准实施，不表示全量生产、九模型真实旅程、三宿主、安装、发布或 AOT 验收完成。

## 建议评审顺序

1. 看顶部、一级轨、左导航、中央、右检查器、下结果和状态栏的外轮廓。
2. 看七个一级入口和全部页面归属，避免重复入口和作用域混淆。
3. 从九模型对象进入二级任务，再检查连接、审批、危险删除、导入与历史流程。
4. 通过右下“设计目录”检查空、错、只读、离线、无权限、超时、截断、长内容及宿主预览。
5. 先确认上述框架与交互方向，再按页面规范逐页定稿，最终形成可实施的像素基线。

## 原型

[打开 HTML 原型](prototype/index.html)。HTML、样式、脚本、任务目录和 logo 均在本目录内，直接用普通浏览器打开可运行；无外部字体/CDN、Server、AI 或 MCP 请求。所有数据与状态均为设计示例，不保存真实凭据，不执行写入。SQL 和字段草稿仅保留在当前页面内存中，刷新后清空。

目录覆盖七个一级模块、30 个全局页面、九模型工作区、166 个任务页签（其中模型任务 60 个）。已逐项点击检查；最终像素定稿仍按下面的评审顺序推进。

需要 HTTP 预览时，在仓库根目录执行：

```powershell
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -Command 'node docs/design/m47-unified-management-workbench/prototype/preview.mjs'
```

预览只监听 `127.0.0.1` 动态端口，打印 URL/PID/父进程/启动时间/命令；仅提供固定名称的原型文件，30 分钟自动退出。Ctrl+C 结束。不把仓库、凭据或用户目录作为 HTTP 根目录。

## 文件

| 文件 | 作用 |
|---|---|
| [navigation.md](navigation.md) | 现状菜单/路由、七一级、二级页面、资源作用域和三宿主范围 |
| [layout-spec.md](layout-spec.md) | 五区外轮廓、尺寸、视觉 token、响应式与像素迁移顺序 |
| [interaction-spec.md](interaction-spec.md) | 对话框、提示、状态、审批失效、导入停止/恢复、未知写结果与可访问性 |
| [screen-specs.md](screen-specs.md) | 30 个区域页面 + 九模型、60 个模型任务页签的详细设计与能力状态 |
| [prototype/catalog.js](prototype/catalog.js) | 完整页面目录及可追溯的已有路由映射 |
| [prototype/task-details.js](prototype/task-details.js) | 二级任务的专用字段、数据结构、主动作和规划说明 |
| [validation-report.md](validation-report.md) | 实际检查结果与范围，保留未通过/未验证项目 |
| [work-queue.md](work-queue.md) | 用户授权的新会话持续推进、独立派单、验证与提交闭环 |
| [screenshots](screenshots/) | 典型桌面、窄屏和共享流程评审图 |

## 取舍与边界

- 保留浅色工业数据库 IDE 和 SonnetDB 品牌；使用 HTML 先验证布局与交互。未调用图片生成模型，不把不可确认的 GPT Image 2.5 型号写成已使用。
- 七一级：概览、工作台、观测、数据流、AI 与 MCP、治理、设置。模型对象在工作台资源树；全局治理和模型操作分层。
- 九模型统一在逻辑数据库上下文下导航，SonnetMQ Topic 保留数据库命名空间和现有 Read/Write 授权；对象页签 identity 同时包含数据库与 Topic，不省略数据库。物理 MQ 仍是 Server 单例的 `.system/mq` 自有日志，实例级配置与整体快照/恢复另行标明；单库备份不包含该物理目录。资源 `scope` 与 `persistenceScope` 分别规划，本次不改变生产存储。
- 消息/事件可以来自 KV、关系、文档等模型的应用工作流，存储复用与导航归属分别判断。前稿按物理目录把 MQ 独立为第九条实例导航分支，现已修正为数据库中的九模型分组；保留实例恢复证据边界。
- Graph 保持 Beta。Notebook、AI Connect/stdio、统一任务索引、告警与发布矩阵等规划页清楚标注，不因原型能点击而宣布实现。
- Studio 预览体现同一 Web 壳的宿主入口；VS Code 模式是开发者边界示意，既有扩展命令不被本原型移除。真实 WebView2、安装、Extension Host 与三宿主合同仍待独立验收。
- 全页面目录与任务规范已整理；本轮为外壳/导航/典型任务评审稿。逐页最终视觉、真实大数据量、接口能力、键盘树导航、状态组合及产品接线须在评审后继续细化，不能把通用模板视为全部生产页面已完成。

## 确认记录

| 评审项 | 状态 |
|---|---|
| 外轮廓与视觉方向 | 已确认，可按基线实施 |
| 一级 / 二级导航与页面归属 | 已确认，可按基线实施 |
| 对话框与状态语义 | 已确认，可按基线实施 |
| 逐页最终像素基线 | 待逐页细化与确认 |
| 生产前端迁移 | WB-05 结果/草稿/历史/审批与 WB-06 外壳/导航切片已完成并通过本地证据；后续页面与真实服务仍逐切片验收，不能视为全量完成 |
