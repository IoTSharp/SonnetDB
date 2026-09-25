# GH-Issue #91 本机嵌入式数据库可视化验收

日期：2026-09-25。范围：Windows 开发机、独立工作树和隔离数据目录；这是本机真实 Server/Web UI 证据，不是干净无网电脑的安装证明。

## 操作与观察

1. 使用 Release `sndb sql --connection "Data Source=<source>/inspection"` 在独立目录创建 `inspections` 表，写入 `(1, 'offline sample')`；关闭 CLI 后重新查询得到 1 行。
2. 使用同工作树的 Release Server，设置 `SonnetDBServer:DataRoot=<control>`、`MountedDatabasePath=<source>/inspection`、`MountedDatabaseName=inspection`、`AutoLoadExistingDatabases=false`，仅监听本机 `127.0.0.1`。`DataRoot` 与原嵌入式目录不同。启动日志确认冷开 `inspection.inspections`。
3. Edge 加载生产 `wwwroot` 中的 Studio，使用本地测试管理员登录。Explorer 显示 `inspection` 和 `inspections`，`SELECT id, note FROM inspections` 返回原行。
4. 在 SQL 编辑器提交 `INSERT INTO inspections (id, note) VALUES (2, 'managed in Studio')`，经写入预览确认后查询得到 2 行。[工作台截图](../assets/issue-91-offline-studio.png)显示原行和新增行。
5. 停止 Server，使用嵌入式 CLI 直接重开原目录，`SELECT id, note FROM inspections ORDER BY id` 返回 `offline sample` 和 `managed in Studio` 两行。浏览器记录中的动态请求全部指向 `127.0.0.1`；未修改系统网络或防火墙。

自动化：Core 挂载/空目录/误删保护 2/2；Studio 宿主和 bridge 专项 16/16（含数据库被占用时恢复原 Server）；浏览器目录选择入口 e2e 1/1；Server Release 与 Web 生产构建成功。Server `win-x64` NativeAOT 发布使用 `SonnetDbPublishAot=true` 和 `/warnaserror`，退出码为 0，未出现 IL/AOT 警告。目录选择 e2e 使用浏览器 fixture，宿主进程测试使用真实 Server；两者不替代 WebView2 真机操作。

## 安装介质与本机宿主复核

- 在联网开发机下载 Microsoft 官方 WebView2 x64 Evergreen Standalone Installer（`https://go.microsoft.com/fwlink/p/?LinkId=2124701`），文件为 212,213,456 字节，SHA-256 为 `771042DB15CB5C463BAC51A8408E70183D7130E8AC946709384C2223DA582C1B`，Authenticode 状态 `Valid`，签名者为 Microsoft Corporation。开发机原有 WebView2 Runtime 版本为 `153.0.4234.48`；下载介质并不证明离线机器已完成安装。
- WiX 5.0.2 在独立工作树构建 `sonnetdb-studio-0.0.0-issue91-win-x64.msi`，文件为 160,749,935 字节，SHA-256 为 `E883CF12C1BFE73C066AE407A97CBB581BEE83C66484964E1AA38C99409AEE82`。该本地 MSI **未签名**。`msiexec /a <msi> TARGETDIR=<isolated-dir> /qn` 返回 0，提取内容包含 `SonnetDB.Studio.exe`、`server/SonnetDB.exe` 和 Studio/Server 的 `wwwroot/index.html`。`/a` 是管理提取，不是 MSI 交互安装或升级/卸载验收。
- 从提取目录在联网开发机启动 Studio，观察到 `SonnetDB Studio` 原生窗口句柄、由它派生的 WebView2 进程，以及托管 Server 监听 `127.0.0.1:52391`；`/healthz/ready` 返回 HTTP 200，存储和关系表预热检查正常。总体健康状态为 `Degraded`，原因是未配置可达的 chat provider。运行数据位于隔离目录，测试进程结束后停止。桌面会话锁屏，无法取得可用的原生窗口像素截图；进程和健康检查不等于用户可见交互已验收。
- 本次 `eng/release.ps1` 的常规路径先因 `SonnetDB.Core` 对 3.0.1 基线的 `CP0002`/`CP0011` API 兼容检查失败。为了单独验证 Studio MSI 构建，在隔离输出目录预建空 `nuget` 目录，使脚本跳过 NuGet pack；生成的其他 bundle 缺少 NuGet 包。因此本节仅证明本机 Studio MSI 的构建、管理提取和提取后启动，不表示完整发布流水线通过；正式兼容门禁由独立工作项处理。

## 剩余证据

WebView2 离线安装介质已准备，但干净 Windows 无网机器上的 WebView2 Runtime 安装、Studio MSI 交互安装及首次可见操作、升级/卸载保留和端口冲突仍未验证。本机没有可用的干净 Windows 虚拟机，当前开发机已有 WebView2 且联网。#91 应保持 open，直至这组发布现场证据到位。
