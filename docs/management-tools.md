---
layout: default
title: "管理工具与三面能力矩阵"
description: "SonnetDB 八模型管理工作台、Web Admin / Studio 桌面 / VS Code 三面 parity、对标产品、权限边界和 e2e smoke 说明。"
permalink: /management-tools/
---

# 管理工具与三面能力矩阵

SonnetDB 的管理工具共用一套服务端契约和权限模型，但面向三种不同场景：

- **Web Admin** 是旗舰管理面，承载完整的多模型浏览、编辑、治理和监控工作台。
- **Studio 桌面** 打包同一套 Web Admin，并增加本地文件对话框、磁盘连接库和托管本地 Server。
- **VS Code** 是 Remote-first 的开发者子集，重点是 schema / 多模型只读浏览、SQL、结果三视图和 Copilot，不复制完整治理工作台。

本文矩阵以 2026-07-10 的仓库实现为准。`完整` 表示当前交付面有专用入口；`部分` 表示可通过 SQL、共享结果面板或有限维护动作完成，但没有该模型的完整专用流程；`不支持` 表示当前没有可交付入口。对标产品用于确定交互方向，不表示 wire protocol、集群能力或全部功能等价。

## 八模型工作台

| 数据模型 | SonnetDB 工作台 | 重点能力 | 对标单品 | Web Admin | Studio 桌面 | VS Code |
| --- | --- | --- | --- | --- | --- | --- |
| 时序 measurement | Schema Explorer + SQL + Trajectory | schema、SQL、结果图表、轨迹 | InfluxDB Explorer / Grafana Explore | 完整 | 完整 | 部分 |
| 关系表 | Relational Table Workbench | 数据网格、行编辑、EXPLAIN、设计器、索引、ER、导入导出 | pgAdmin / SSMS / Navicat / DBeaver | 完整 | 完整 | 部分 |
| 文档集合 | Document Collection Workbench | find/count/distinct/aggregate、CRUD、validator、JSONL | MongoDB Compass | 完整 | 完整 | 部分 |
| KV keyspace | KV Keyspace Workbench | 前缀扫描、TTL、类型化值、批量操作 | RedisInsight | 完整 | 完整 | 只读子集 |
| SonnetMQ | SonnetMQ Workbench | topic、消息、publish/ack、lag、吞吐、retention | Kafka UI / RabbitMQ Management / EMQX Dashboard | 完整 | 完整 | 只读子集 |
| 向量 | Vector Search Workbench | 索引参数、raw/embed query、Top-K、metadata filter | Milvus Attu / Qdrant Console | 完整 | 完整 | 只读子集 |
| 全文 | FullText Search Workbench | BM25、评分高亮、analyzer、查询构建、rebuild | Kibana / OpenSearch Dashboards | 完整 | 完整 | 只读子集 |
| 对象桶 | Object Bucket Workbench | 桶/对象、上传下载、版本、multipart、治理、审计 | MinIO Console / S3 Browser | 完整 | 完整 | 不支持 |

![Web Admin SonnetMQ 工作台与统一多模型 Explorer]({{ site.docs_baseurl | default: '/help' }}/assets/management-workbench-mq.png)

图中页面由 #260 e2e fixture 驱动，展示统一 Explorer、MQ 消息浏览、消费者 lag、吞吐/积压、retention 和消息 inspector。fixture 只替代后端数据，截图中的路由、组件、布局和交互代码均为生产 Web Admin。

## Web Admin parity

| 数据模型 | 浏览 | 查询 | 编辑 | 导入导出 | 监控 |
| --- | --- | --- | --- | --- | --- |
| 时序 | 完整：measurement/列/索引 | 完整：SQL、图表、Trajectory | 部分：SQL staged write，无专用点编辑器 | 部分：结果 CSV/JSON，无专用 measurement 文件导入 | 部分：全局 Events/Monitoring |
| 关系 | 完整：表、列、主外键、索引 | 完整：分页/过滤/排序、SQL、EXPLAIN | 完整：行编辑、表设计、索引维护，统一 preview | 完整：CSV/JSON/JSONL、DDL | 部分：历史和全局监控，无 per-table 实时面板 |
| 文档 | 完整：集合、索引、validator | 完整：find/count/distinct/aggregate | 完整：CRUD、validator、rebuild，统一 preview | 完整：JSON/JSONL | 部分：操作历史，无 change feed viewer |
| KV | 完整：keyspace、prefix、TTL、值视图 | 完整：scan/get-many | 完整：set/remove/expire/persist/prefix delete，统一 preview | 部分：批量文本操作和结果导出，无文件 round-trip | 部分：key/expiry 统计 |
| SonnetMQ | 完整：topic、offset、header、payload | 完整：offset browse、时间定位 | 完整：publish/ack，统一 preview | 部分：结果导出，无消息文件导入 | 完整：lag、吞吐、backlog、retention、DLQ 提示 |
| 向量 | 完整：索引/维度/metric/HNSW 参数 | 完整：raw/embed Top-K、filter | 不支持：playground 保持只读 | 不支持 | 部分：row count 与索引统计 |
| 全文 | 完整：索引/doc/term/tokenizer | 完整：BM25、fuzzy/phrase/boolean、analyze | 部分：仅 staged rebuild | 不支持：数据导入归文档工作台 | 部分：doc/term 统计 |
| 对象桶 | 完整：bucket/prefix/object/version/multipart | 完整：metadata、range preview、audit | 完整：上传、复制、删除、tag、policy/lifecycle/retention/quota/hold | 完整：上传/下载 | 完整：容量、quota、版本和 audit |

所有危险写、导入、删除和 rebuild 都必须经过 `WriteApprovalPanel`、dry-run 或明确确认之一。权限仍由当前 bearer token 和数据库 grant 决定，前端不绕过服务端鉴权。

## Studio 桌面 parity

Studio 桌面复用上表全部 Web Admin 模型能力，因此模型级 parity 与 Web Admin 相同。桌面增量如下：

| 桌面能力 | 当前状态 | 实现边界 |
| --- | --- | --- |
| 原生打开/保存文件 | 完整 | loopback bridge + 启动期随机 token；共享结果导出和关系导入导出已接线 |
| 磁盘连接库 | 完整 | profile 落 `connections.json`；鉴权 token 不落盘 |
| 托管本地 Server | 完整 | 指定 `data root` 启停进程并轮询 `/healthz`；可选择退出时保留 |
| 浏览器降级 | 完整 | bridge 不可用时回退 `localStorage`、浏览器下载和 file input |
| 对象/备份专用原生文件接线 | 完整 | 对象上传/下载与 Multipart 分片优先走二进制原生对话框；备份、恢复和 data root 使用原生目录选择器，浏览器环境自动降级 |
| 宿主原生菜单 | 完整 | File / View / Local Server 动作已映射为 Win32 菜单，并通过 NativeWebHost JS bridge 复用工作台文件、结果、历史与 Server 流程 |

![Studio 桌面 bridge 状态与托管本地 Server 控件]({{ site.docs_baseurl | default: '/help' }}/assets/studio-native-bridge.png)

bridge 仅监听 `127.0.0.1`，请求必须携带当前 Studio 进程生成的 token。连接库不保存 bearer token；Web Admin 登录会话继续负责 API 凭据。

## VS Code parity

| 数据模型 | 浏览 | 查询 | 编辑 | 导入导出 | 监控 |
| --- | --- | --- | --- | --- | --- |
| 时序 | 完整：measurement/列 | 完整：SQL + Table/Raw/Chart | 不支持：不计入任意 SQL 写入能力 | 不支持 | 不支持 |
| 关系 | 完整：表/列 | 完整：SQL + Table/Raw/Chart | 不支持 | 不支持 | 不支持 |
| 文档 | 完整：集合/index metadata | 部分：SQL，无专用 Document find 面板 | 不支持 | 不支持 | 不支持 |
| KV | 完整：keyspace/entry preview | 完整：只读 scan preview | 不支持 | 不支持 | 不支持 |
| SonnetMQ | 完整：topic/message preview | 完整：只读 browse | 不支持 | 不支持 | 不支持：client 有 monitor 契约，当前无交付面 |
| 向量 | 完整：index metadata | 完整：search-preview/embed-preview | 不支持 | 不支持 | 不支持 |
| 全文 | 完整：index metadata | 完整：search-preview/analyze | 不支持 | 不支持 | 不支持 |
| 对象桶 | 不支持 | 不支持 | 不支持 | 不支持 | 不支持 |

VS Code 的 Query Result Webview 提供 Table / Raw / Chart 三视图。连接 profile 存在 `globalState`，token 存在 `SecretStorage`。Copilot 默认 `read-only`；切换 `read-write` 会先显示 VS Code modal 确认，但这不等于提供完整 per-model 编辑工作台。

## 三面共享边界

| 能力 | Web Admin | Studio 桌面 | VS Code |
| --- | --- | --- | --- |
| 服务端契约 | `/v1/db`、schema、SQL、#245 多模型管理契约 | 与 Web Admin 相同 | 同一批 HTTP 契约的开发者子集 |
| 连接持久化 | 浏览器 `localStorage` | 磁盘 `connections.json` | VS Code `globalState` |
| token 存储 | 当前登录会话 | 当前登录会话，不进连接库 | `SecretStorage` |
| 结果面板 | Table / Raw / JSON / Chart，GEO 可用 Map | 与 Web Admin 相同，保存可走原生对话框 | Table / Raw / Chart |
| 写审批 | 跨模型 staged preview / dry-run / confirm | 与 Web Admin 相同 | 无完整模型写工作台；Copilot read-write 仅做前置确认 |
| Copilot | 全局 CopilotDock | 与 Web Admin 相同 | 独立 streaming webview |

## 自动化 smoke

Web Admin 使用 Playwright 在真实 Chromium 中加载生产 Vue 页面，并通过统一 fixture 验证：

- SQL/时序、关系、文档、KV、MQ、向量、全文、对象桶八个工作台均可由路由进入。
- 统一 Explorer 同时出现 Measurements、Tables、Collections、KV、Vector、FullText、MQ 和 Buckets。
- Studio bridge 能加载磁盘连接 profile，并展示本地 Server 的 Health / Stop 状态。
- 截图与 smoke 共用 fixture，避免文档示例和实际契约分叉。

```powershell
cd web
npx playwright install chromium
npm run test:e2e
npm run docs:screenshots
```

VS Code 扩展使用 loopback HTTP server 对 `SonnetDbClient` 做消费端 smoke，覆盖 schema、SQL NDJSON、KV、向量、全文和 MQ：

```powershell
cd extensions/sonnetdb-vscode
npm test
```

完整 VS Code workbench UI 自动化、VSIX 和 Marketplace 截图仍归 M18 #108；当前 #260 不把 HTTP consumer smoke 描述成 VS Code Electron UI e2e。

`.github/workflows/management-workbench-smoke.yml` 会在管理面相关路径变化时运行 Web build、九项 Chromium smoke 和 VS Code consumer smoke；Web 失败 trace 会上传为 workflow artifact。

## #258 / #259 核查结论

| 范围 | 已实现证据 | 收口结论 |
| --- | --- | --- |
| #258 bridge 鉴权与 manifest | `StudioBridgeHost.cs`、`StudioBridgeContracts.cs` | 已实现 loopback + token、capability manifest 和状态 API |
| #258 文件与连接库 | `StudioFileDialogService.cs`、`StudioConnectionLibrary.cs`、`studioNativeBridge.ts` | 文本/二进制打开保存、目录选择、关系与对象导入导出、备份/恢复目录和磁盘连接库均已接线 |
| #258 托管本地 Server | `StudioManagedServerHost.cs`、`Program.cs` | 已实现进程启停、data root、健康检查与退出策略 |
| #258 原生菜单 | `StudioDesktopActions.cs`、`StudioNativeMenu.cs` | manifest 与 Win32 菜单共用动作目录；窗口命令经 NativeWebHost JS bridge 回到共享工作台流程 |
| #259 多模型消费 | `sonnetdbClient.ts`、`sonnetdbTreeDataProvider.ts`、`extension.ts` | KV/向量/全文/MQ 只读浏览与预览已接线 |
| #259 结果与 Copilot | `queryResultPanel.ts`、`copilotPanel.ts` | Table/Raw/Chart 与 streaming Copilot 已实现；完整编辑仍不属于 VS Code 定位 |

这两个范围没有引入新的存储、查询或写入语义；三面继续以授权 HTTP 契约为边界。
