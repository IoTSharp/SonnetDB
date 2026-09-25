# GH-Issue #91 本机嵌入式数据库可视化验收

日期：2026-09-25。范围：Windows 开发机、独立工作树和隔离数据目录；这是本机真实 Server/Web UI 证据，不是干净无网电脑的安装证明。

## 操作与观察

1. 使用 Release `sndb sql --connection "Data Source=<source>/inspection"` 在独立目录创建 `inspections` 表，写入 `(1, 'offline sample')`；关闭 CLI 后重新查询得到 1 行。
2. 使用同工作树的 Release Server，设置 `SonnetDBServer:DataRoot=<control>`、`MountedDatabasePath=<source>/inspection`、`MountedDatabaseName=inspection`、`AutoLoadExistingDatabases=false`，仅监听本机 `127.0.0.1`。`DataRoot` 与原嵌入式目录不同。启动日志确认冷开 `inspection.inspections`。
3. Edge 加载生产 `wwwroot` 中的 Studio，使用本地测试管理员登录。Explorer 显示 `inspection` 和 `inspections`，`SELECT id, note FROM inspections` 返回原行。
4. 在 SQL 编辑器提交 `INSERT INTO inspections (id, note) VALUES (2, 'managed in Studio')`，经写入预览确认后查询得到 2 行。[工作台截图](../assets/issue-91-offline-studio.png)显示原行和新增行。
5. 停止 Server，使用嵌入式 CLI 直接重开原目录，`SELECT id, note FROM inspections ORDER BY id` 返回 `offline sample` 和 `managed in Studio` 两行。浏览器记录中的动态请求全部指向 `127.0.0.1`；未修改系统网络或防火墙。

自动化：Core 挂载/空目录/误删保护 2/2；Studio 宿主和 bridge 专项 16/16（含数据库被占用时恢复原 Server）；浏览器目录选择入口 e2e 1/1；Server Release 与 Web 生产构建成功。Server `win-x64` NativeAOT 发布使用 `SonnetDbPublishAot=true` 和 `/warnaserror`，退出码为 0，未出现 IL/AOT 警告。目录选择 e2e 使用浏览器 fixture，宿主进程测试使用真实 Server；两者不替代 WebView2 真机操作。

## 剩余证据

离线安装介质和 WebView2 Runtime 准备、干净 Windows 无网首次启动、升级/卸载与端口冲突仍未在独立机器验证。#91 应保持 open，直至这组发布现场证据到位。
