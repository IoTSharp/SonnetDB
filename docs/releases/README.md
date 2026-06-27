---
layout: default
title: 发布与打包
description: 了解 NuGet、SDK Bundle、Server Bundle 和安装包的组成与默认启动方式。
permalink: /releases/
---

SonnetDB 当前的发布物主要分为五类：

| 类型 | 产物 | 说明 |
| --- | --- | --- |
| NuGet | `SonnetDB.*.nupkg` | 嵌入式核心库、ADO.NET、EF Core Provider、缓存扩展与 CLI 工具包 |
| SDK Bundle | `sndb-sdk-<version>-<rid>` | 面向开发者，包含 NuGet 包、本地 CLI 与配套文档 |
| Server Bundle | `sonnetdb-full-<version>-<rid>` | 面向部署者，包含 `SonnetDB`、前端、CLI 与默认启动配置 |
| Installer | `.msi` / `.deb` / `.rpm` | 面向最终安装的操作系统包 |
| Docker Image | `iotsharp/sonnetdb` / `ghcr.io/<owner>/sonnetdb` | 面向容器化部署的服务端镜像，包含后台、帮助中心与默认运行配置 |

## 默认启动信息

完整服务端发布物通常默认监听：

```text
http://127.0.0.1:5080
```

常见入口包括：

- `/admin/`
- `/help/`
- `/healthz`
- `/metrics`

## 当前发布说明

- [SonnetDB 2.5.0]({{ site.docs_baseurl | default: '/help' }}/releases/2-5-0/)

## 本地 Windows 打包

在 Windows 开发机上可使用一键脚本同时生成 NuGet 包与 `win-x64` ZIP Bundle：

```powershell
.\eng\build-windows.ps1 -Version <version>
```

默认最终产物会汇总到 `artifacts/windows/final/`，并同时生成 Windows MSI（需本机已安装 WiX CLI）。`publish/`、`staging/`、`nuget/`、`bundles/`、`installers/` 等中间目录会在汇总后自动清理。如只验证 .NET 发布链路、不重建管理后台前端，可追加 `-SkipAdminUi`；如暂不生成 MSI，可追加 `-SkipInstaller`。

`final/` 目录中只保留可发布文件，例如：

- `SonnetDB.Core.<version>.nupkg`
- `SonnetDB.<version>.nupkg`
- `SonnetDB.EntityFrameworkCore.<version>.nupkg`
- `SonnetDB.Caching.<version>.nupkg`
- `SonnetDB.Cli.<version>.nupkg`
- `sndb-sdk-<version>-win-x64.zip`
- `sonnetdb-full-<version>-win-x64.zip`
- `sonnetdb-<version>-win-x64.msi`
- 对应 `.sha256`

MSI 默认安装并启动 `SonnetDB` Windows 服务，数据目录默认为 `C:\ProgramData\SonnetDB\data`，并把安装目录加入系统 `PATH`，让 `sndb` 可在任意目录使用。安装时可覆盖：

```powershell
msiexec /i sonnetdb-<version>-win-x64.msi DATAROOT="D:\sonnetdb-data"
```

## 推荐阅读顺序

1. [SDK Bundle]({{ site.docs_baseurl | default: '/help' }}/releases/sdk-bundle/)
2. [Server Bundle]({{ site.docs_baseurl | default: '/help' }}/releases/server-bundle/)
3. [安装包]({{ site.docs_baseurl | default: '/help' }}/releases/installers/)
4. [Docker 镜像]({{ site.docs_baseurl | default: '/help' }}/releases/docker-image/)
