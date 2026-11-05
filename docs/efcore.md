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

本源码在 GH-Issue #392 中将已有关系 DDL 事务接入 EF 迁移：受支持的关系表、列、索引、外键及 CHECK 操作保留 EF 事务，可与种子数据及 migration history 一起提交或回滚。只有处于同一个未被事务外操作打断的 EF 事务内的命令，才具有这个原子边界。已有引擎的依赖、无损转换、自引用主键变更、取消与预算限制继续生效，不表示任意 schema 演进都已支持。

| MigrationOperation / 入口 | 事务处理与范围 |
| --- | --- |
| `CreateTable` / `DropTable` / `RenameTable`；`AddColumn` / `AlterColumn` / `DropColumn` / `RenameColumn` | 保留事务；关系 DDL workspace 校验及持久提交。 |
| `CreateIndex` / `DropIndex`；`AddForeignKey` / `DropForeignKey`；`AddCheckConstraint` / `DropCheckConstraint` | 保留事务；DDL 失败时可撤销本组先前的变更。 |
| `AddPrimaryKey` / `DropPrimaryKey` / `AddUniqueConstraint` / `DropUniqueConstraint` | 保留事务标志；实际支持仍以引擎接受的约束名称、主键迁移及类型合同为准，事务标志不扩展 SQL 方言。 |
| `InsertData` / `UpdateData` / `DeleteData` 及 migration history | 保留关系事务；history 写入失败也撤销同一事务内此前的 DDL。 |
| `CreateSequence` / `DropSequence` | 独立序列不属于关系 DDL workspace，命令标为 transaction-suppressed；只支持引擎已有的无 schema、Int64、非循环序列语义。 |
| `AlterSequence` / `RestartSequence` / `RenameSequence`、`EnsureSchema` / `DropSchema` | 不进入关系 DDL 事务；不因此新增引擎或 Provider 原先不支持的操作。 |
| `RenameIndex`；`AlterDatabase` / `AlterTable` 的注解变化 | 保留既有行为：`RenameIndex` 不支持；无 SQL 的注解变化不产生事务命令，不据此承诺数据库控制面或表注释演进。 |
| 视图、measurement、用户/权限、其它控制面或多模型 DDL | 没有新增 typed MigrationOperation 支持；使用引擎已支持的手写 SQL 时，显式设置 `suppressTransaction: true`，并承担独立提交的边界。 |
| `migrationBuilder.Sql(...)` | 严格尊重显式 `suppressTransaction`；受支持关系 DDL 可保持默认 `false`，事务外 DDL 必须显式 `true`。不会通过文本猜测或自动提交掩盖错误。 |

迁移历史表的首次初始化保持既有独立提交。普通迁移历史行随对应事务组保存；包含任何 transaction-suppressed 命令的迁移会分成多个提交组，仍不能当作全迁移原子提交。迁移中断后的降级应继续依据升级前备份、目录格式 floor 与业务新写入的回补合同。

上述为 #392 修复后的源码合同；公开 `SonnetDB.EntityFrameworkCore` 5.0.0 仍采用原来的 DDL 事务抑制行为，修复的最低公开包版本及真实应用验收尚待后续发布确认。当前验证与未执行项见 [#392 审计](audits/issue-392-20261105.md)。

## 注意事项

- Provider 当前实现关系型表的 CRUD、JOIN、SHOW INDEXES、EXPLAIN、显式事务回滚等；不实现分布式事务、复杂的物化视图、cross-database 跨库查询。
- 典型 ASP.NET Core `ApplicationDbContext` 兼容样例可参考 `tests/SonnetDB.IoTSharpCompat.Tests/ApplicationDbContextSonnetDbCompatTests.cs`，里面给出了与 `IdentityUser`、`AddIdentity`、模型构建选项的完整接线方式；上层产品的整体迁移方案应在对应产品仓库维护。
- 远程连接走 ADO.NET 同样的 token 鉴权与异常映射逻辑，参见 [ADO.NET 参考]({{ site.docs_baseurl | default: '/help' }}/ado-net/#远程异常处理)。
