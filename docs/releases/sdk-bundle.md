---
layout: default
title: SDK Bundle
description: 面向开发者的 SDK 打包说明，包含 NuGet 包、本地 CLI 与配套文档。
permalink: /releases/sdk-bundle/
---

SDK Bundle 面向开发者，目标是“一次下载，直接拥有完整开发接入包”。

## 包含内容

- `packages/SonnetDB.Core.<version>.nupkg`
- `packages/SonnetDB.<version>.nupkg`
- `packages/SonnetDB.EntityFrameworkCore.<version>.nupkg`
- `packages/SonnetDB.Caching.<version>.nupkg`
- `packages/SonnetDB.Cli.<version>.nupkg`
- `cli/` 本地命令行工具
- `docs/` 使用说明

## 常见使用方式

```bash
dotnet add package SonnetDB.Core
dotnet add package SonnetDB
dotnet add package SonnetDB.EntityFrameworkCore
dotnet tool install --global SonnetDB.Cli
```

本地 CLI 示例：

```bash
sndb version
sndb sql --connection "Data Source=./demo-data" --command "SELECT count(*) FROM cpu"
```
