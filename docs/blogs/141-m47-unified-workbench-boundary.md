---
title: SonnetDB 统一管理工作台：Web Admin、Studio 与 VS Code 的共同边界
categories: SonnetDB
draft: false
---

SonnetDB 的管理体验正在收敛为“一套核心、三个宿主、三个发布物”。Web Admin 承载多模型管理和治理，Studio 包装同一 Web 核心并提供本地文件、连接库与托管 Server，VS Code 面向开发者的远程查询、schema 和结果查看。这篇文章关注如何判断一个操作属于哪个宿主、哪个数据库，以及页面成功反馈能证明什么。

**复核日期：2026-10-08。** 源码基线为 `3027a1aac9a5cea2d625c94cefe2239a372dd060`，并读取当天工作树的 M47 验证记录；开发中的变动另按文件 SHA-256 登记，不视为已发行内容。GitHub `v4.0.0` Release 和 NuGet `SonnetDB.Core` 4.0.0 已实际存在，但新 M47 切片不能据此宣称包含在该发行包中。本文示例经源码核对，本次编辑没有重新运行真实 Server、Studio 或 Extension Host。

## 先分清已实现、设计和完整验收

M47 的外壳、导航与交互基线已经确认，生产迁移也已有实现。当前路由提供概览、工作台、观测、数据流、AI、治理和设置七个模块入口；其中数据流仍落到已有 Modbus 页面，设置仍落到关于页面。一级入口存在不代表每个规划任务已有独立页面。

已有真实 Web 服务旅程覆盖了部分模型的读取、写审批及授权撤销场景，验证记录包括 MQ、Graph Beta 和对象桶的切片。VS Code 已有 SQL、结果与历史实现，开发树还修复了查询终态 `elapsedMilliseconds` 与旧 `elapsedMs` 消费端之间的兼容问题。缺失或非法耗时保持未知，不补造零毫秒。

这些都不应再称为空壳。另一方面，Studio 的数据库选择、恢复及正常退出等旅程仍有失败记录，VS Code 的局部查询成功也不能覆盖全部历史、清理与宿主合同。**三宿主整体尚未闭环**。HTML 原型、浏览器 fixture、本地单元合同、真实 Kestrel、真实桌面和安装验收需要分别阅读。

| 入口 | 可以承担的任务 | 需要另外确认的边界 |
|---|---|---|
| Web Admin | SQL、九模型对象任务、授权与治理 | 页面是否已有真实接口接线；本次身份是否获准执行 |
| Studio | 共享 Web 页面，加原生文件、连接库与 Managed Local | WebView2、目录选择、进程归属、退出及恢复的真实宿主验收 |
| VS Code | SQL、Explain、结果、schema 和模型只读预览 | 不提供完整治理工作台；历史命令逐项保留其实际能力 |

VS Code 的 Remote-first 定位不等于“所有写命令已经移除”。既有创建 measurement、批量导入或 Managed Local 等命令应按实际版本及命令贡献检查，不能用新设计覆盖旧产品事实。规划中的统一 Notebook、AI Connect 向导与 `sonnetdb mcp` stdio bridge，也不能只因为原型有按钮就当成发行能力。

## 练习一：确认查询到底发往哪里

准备一台已初始化的 Server 和一枚具有目标数据库 Read 权限的 token。使用真实部署地址，不把示例连接名当成服务器实例身份；新部署的 loopback 默认监听也不等于已经允许跨机器访问。

1. 在 Web Admin 打开 `/admin/app/sql`，先检查当前连接与数据库，再从 Explorer 选中一个已有关系表。工作台模块入口 `/admin/app/workbench` 会重定向到 SQL 页面。
2. 假设练习库已有 `device_status` 表及 `id`、`status` 列，在编辑器输入下面的只读查询，然后手动执行。没有这张表时，应替换为自己实际的表和列。
3. 在结果区检查列、行、错误及截断提示；保存当前数据库和查询文本作为核对依据。
4. 在 VS Code 使用同一个远程服务、数据库与授权配置，执行相同查询。凭据通过扩展的 SecretStorage 管理，不写进可提交的 SQL 文件。

```sql
SELECT id, status
FROM device_status
ORDER BY id
LIMIT 2;
```

相同数据库状态下，两端应取得相同的列与有序行。结果不同，应先检查目标数据库、权限、并发数据变化及客户端预算。`LIMIT 2` 限制输出行数，不能证明查询执行内存已经被限制为两行；空结果也不能直接解释为“没有权限”。

扩展的命令面板提供 SQL 查询、Explain 与 `SonnetDB: Show Query History` 等入口。查看历史时应核对连接、数据库和 SQL；“恢复输入”只恢复草稿与上下文，执行仍需明确操作。不要把打开历史当成重放请求，也不要把看到历史标签当成磁盘持久化已成功的证明。

## 练习二：检查 MQ 和 Graph 的资源身份

同一实例下的两个数据库可以各有一个逻辑 Topic `DeviceEvents`。在 Explorer 中打开 MQ 对象后，检查页签和详情中的数据库与 Topic；切换数据库后，同名 Topic 应视为另一资源。

MQ 有两个需要同时保存的作用域：

- 逻辑名称和 Read/Write 权限属于数据库，身份包含 `database + topic`。
- Server 的物理队列日志属于实例，位于 `<DataRoot>/.system/mq`；实例快照与恢复使用独立合同。

因此，单库备份不能冒称包含 Server 实例 MQ，也不能用数据库 Read 权限推导整体实例恢复权限。Explorer 的旧 node key 只是路由兼容输入，不应替代结构化资源身份。

Graph 对象在资源树、页签和能力说明中持续标记 **Graph Beta**。画布有节点、边和导出预算；看到截断提示意味着当前快照不完整，不能推导“图中没有其他关系”。画布或 SQL/PGQ 能运行，也不能推导固定硬件容量、外部图语义或长期生产门禁已通过。

## 写审批需要重新核对目标

在具有 Write 权限的测试库中，修改对象前先查看 staged preview：目标连接、数据库、对象、版本、原值和新值是否正确。取消审批应保留或清除草稿，具体取决于该流程合同；取消不能解释成已经撤销服务器执行过的操作。

权限、目标或版本改变后，旧审批不能沿用到新对象。服务器明确拒绝时，按错误说明处理；请求发送后断连且终态未知时，保留请求身份与已有响应，使用该操作实际提供的终态或审计入口核对。没有可用核对入口就交接处理，不因客户端超时自动重新执行。

前端批准只表示用户确认了当前预览，Server 仍须执行鉴权及并发检查。局部成功不能替代完整写矩阵、文件对话框、安装升级、正常退出与 AOT 发布验收。

## 阅读资料时保留时间边界

管理矩阵中的“完整”主要描述专用入口的覆盖，不是三宿主生产验收结论。M47 导航稿仍保留设计阶段的历史字样，判断当前实现时须结合源码、队列和验证记录，不从一句旧状态推翻已经完成的切片。

以下公共资料固定到已公开的 `c785f0479686a3d6584787e1a2b40bd67b0e38c5` 文档基线，便于追溯设计与既有管理合同；本文提到的更新开发树证据不能当成这些历史链接已经包含的新内容。

- [管理工具与三面能力矩阵](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/management-tools.md)
- [M47 设计评审包](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/design/m47-unified-management-workbench/README.md)
- [M47 导航与资源作用域](https://github.com/IoTSharp/SonnetDB/blob/c785f0479686a3d6584787e1a2b40bd67b0e38c5/docs/design/m47-unified-management-workbench/navigation.md)
- [4.0.0 正式 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)
