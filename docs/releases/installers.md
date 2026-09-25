---
layout: default
title: 安装包
description: Windows MSI 与 Linux DEB/RPM 安装包的默认目录、安装命令和启动方式。
permalink: /releases/installers/
---

## Windows MSI

### Studio MSI

`sonnetdb-studio-<version>-win-x64.msi` 安装桌面管理工作台及同版本托管 Server。Studio bundle 自带 .NET runtime；默认安装目录为 `%ProgramFiles%\SonnetDB Studio`；Studio bundle 内的 Server 位于 `server\` 子目录。

Studio 默认把数据库写入 `%LocalAppData%\SonnetDB\Studio\data`，该目录不属于 MSI 安装目录，升级或卸载不会自动删除。可通过 Studio 启动参数 `--data-root` 选择其他目录。安装包合同和宿主自动化测试已覆盖；WebView2 依赖、首次启动、升级/卸载保留和端口冲突仍需在干净 Windows 真机验收。

### 打开已有嵌入式数据库

先关闭正在使用该目录的嵌入式应用，避免数据库文件锁冲突。在 Studio 工作区右上角的本地 Server 设置中选择“打开已有嵌入式数据库”，然后选择嵌入式连接串 `Data Source=...` 指向的**数据库目录本身**，而不是它的父目录。Studio 会使用自己的 `DataRoot` 保存本地 Server 控制面，只把选中的已有目录挂载为一个数据库；Explorer 自动选中该库，可浏览 schema、执行 SQL 和经确认修改数据。退出 Studio 或停止本地 Server 后，原嵌入式应用可重新打开同一目录。

此操作直接使用原数据文件，不复制数据库。挂载的数据库不能通过 Server 的“删除数据库”操作删除，接口返回 HTTP 409；要删除原目录必须在 Studio 外另行处理。选择空目录或占用中的库会报错，切换启动失败时恢复先前由 Studio 托管的本地 Server。普通“Data root”设置仍接受**多个数据库目录的父目录**。

本地构建的 Server 与浏览器工作台已验证打开、查询、修改和重开原库；干净无网 Windows 电脑上的 MSI、WebView2 Runtime 与首次启动仍待独立实机验收。见[本机验收记录](../audits/issue-91-offline-studio-20260925.md)。

默认安装目录通常为：

```text
%ProgramFiles%\SonnetDB Server
```

MSI 会安装并启动 Windows 服务：

```powershell
msiexec /i sonnetdb-<version>-win-x64.msi
Get-Service SonnetDB
```

默认数据目录为：

```text
C:\ProgramData\SonnetDB\data
```

安装时可通过 `DATAROOT` 指定数据目录：

```powershell
msiexec /i sonnetdb-<version>-win-x64.msi DATAROOT="D:\sonnetdb-data"
```

该路径会写入服务启动参数，并同步设置系统环境变量 `SONNETDB_SonnetDBServer__DataRoot`。

安装程序还会把安装目录加入系统 `PATH`。重新打开终端后，可在任意目录运行：

```powershell
sndb version
sndb remote --url http://127.0.0.1:5080 --database metrics --token sonnetdb-admin-token
```

## Linux DEB / RPM

默认安装目录通常为：

```text
/opt/sonnetdb
```

安装示例：

```bash
sudo dpkg -i sonnetdb-<version>-linux-x64.deb
sudo rpm -i sonnetdb-<version>-linux-x64.rpm
```

安装完成后，一般可以直接运行：

```bash
sonnetdb
sndb version
```

Linux 安装包通过 `/usr/bin/sonnetdb` 与 `/usr/bin/sndb` 软链接暴露全局命令，不修改 shell 配置文件。
