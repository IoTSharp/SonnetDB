---
layout: default
title: "EF Core Provider"
description: "SonnetDB.EntityFrameworkCore 包的注册、连接字符串、迁移与使用示例。"
permalink: /efcore/
---

## 概述

SonnetDB 提供官方 Entity Framework Core Provider，NuGet 包名 `SonnetDB.EntityFrameworkCore`，命名空间 `SonnetDB.EntityFrameworkCore.Extensions`。Provider 在内部复用 ADO.NET 提供程序 `SonnetDB.Data`，因此连接字符串语法与 [ADO.NET 参考]({{ site.docs_baseurl | default: '/help' }}/ado-net/) 完全一致——本地嵌入式、HTTP 远程都用同一份。

## 安装

```bash
dotnet add package SonnetDB.EntityFrameworkCore
```

## 注册 DbContext

在 `IServiceCollection` 上调用 `AddDbContext`，使用 `UseSonnetDB` 扩展：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.EntityFrameworkCore.Extensions;

var services = new ServiceCollection();
services.AddDbContext<AppDbContext>(options =>
    options.UseSonnetDB("Data Source=./demo-data"));
```

如需自定义 EF Core 关系行为（例如指定 migrations 程序集），用 `UseSonnetDB` 的 builder 参数：

```csharp
services.AddDbContext<AppDbContext>(options =>
    options.UseSonnetDB(
        "Data Source=./demo-data",
        builder => builder.MigrationsAssembly("AppDbContext.Migrations")));
```

也可以直接传入一个已经打开的 `DbConnection`（例如要在多个 DbContext 之间共享连接的场景）：

```csharp
using SonnetDB.Data;

var connection = new SndbConnection("Data Source=./demo-data");
services.AddDbContext<AppDbContext>(options => options.UseSonnetDB(connection));
```

## 连接字符串

`UseSonnetDB` 的第一个 string 参数原样转交给 `SndbConnection`，支持的形式：

```text
Data Source=./demo-data                                            # 本地嵌入式
Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=xxxxx     # 远程 HTTP
```

详见 [ADO.NET 参考]({{ site.docs_baseurl | default: '/help' }}/ado-net/#连接字符串)。

## 建库与迁移

第一次运行时通常会调用 `EnsureCreated` 或 `Migrate`：

```csharp
using var scope = app.Services.CreateScope();
var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
ctx.Database.EnsureCreated();
```

EF Core 标准 migrations 流水线 (`dotnet ef migrations add`, `dotnet ef database update`) 也可正常使用；如果迁移类放在独立程序集，记得在 `UseSonnetDB` 里通过 `MigrationsAssembly` 指定。

## 注意事项

- Provider 当前实现关系型表的 CRUD、JOIN、SHOW INDEXES、EXPLAIN、显式事务回滚等；不实现分布式事务、复杂的物化视图、cross-database 跨库查询。
- 典型 ASP.NET Core `ApplicationDbContext` 兼容样例可参考 `tests/SonnetDB.IoTSharpCompat.Tests/ApplicationDbContextSonnetDbCompatTests.cs`，里面给出了与 `IdentityUser`、`AddIdentity`、模型构建选项的完整接线方式；上层产品的整体迁移方案应在对应产品仓库维护。
- 远程连接走 ADO.NET 同样的 token 鉴权与异常映射逻辑，参见 [ADO.NET 参考]({{ site.docs_baseurl | default: '/help' }}/ado-net/#远程异常处理)。
