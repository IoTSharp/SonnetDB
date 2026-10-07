# ASP.NET Core Identity / EF Core 兼容性分析

日期：2026-10-07（Asia/Shanghai）。本次为源码和官方合同核查，没有运行构建、测试、真实登录流程或发布。

核查时仓库：`main`，HEAD `11f358c698005877b3e0362946ef9cfd06afa3f1`。仓库中央依赖将 EF Core 与 `Microsoft.AspNetCore.Identity.EntityFrameworkCore` 固定到 `10.0.12`；框架合同另对照官方 ASP.NET Core `v10.0.1` 源码，不据此声明每个后续补丁版本都已验证。

## 结论

SonnetDB 可以通过现有 EF Core provider 接入微软标准 Identity EF Store。已有 `UseSonnetDB(...)` 与 `AddEntityFrameworkStores<ApplicationDbContext>()` 接线及用户创建、密码验证的集成测试源码，无需先实现专用 `IUserStore` / `IRoleStore`。

已有 EF Core provider 并不自动证明 Identity 全部功能兼容。目前证据支持直接采用标准接入方式；完整角色、Claims、外部登录、令牌、锁定、MFA、Passkey、真实登录 HTTP 流程和持久恢复仍应分项验收。

## 已有源码证据

| 能力 | 证据 | 本次可得结论 |
| --- | --- | --- |
| 标准 Identity 接线 | `tests/SonnetDB.IoTSharpCompat.Tests/ApplicationDbContextSonnetDbCompatTests.cs:25`，`AddIdentity` / `AddEntityFrameworkStores` 在 28–30 行 | 已有正式 EF Store 接入方式 |
| 用户创建和密码验证 | 同文件 `Identity_WithApplicationDbContext_CanCreateUserAndVerifyPassword`，105 行 | 测试源码覆盖 CreateAsync、FindByNameAsync、正确及错误密码；不是本轮运行 PASS |
| Identity 唯一用户名及事务回滚 | 同文件 `SaveChanges_WhenUniqueIdentityConstraintFails_RollsBackAllPendingChanges`，321 行 | 覆盖相同 NormalizedUserName 导致保存失败且不留部分用户 |
| 建库和迁移 | 同文件 EnsureCreated、migration history、Migrate 场景，42 / 60 / 78 行 | 已有完整业务模型的建库与迁移测试源码 |
| Identity 常用列映射 | `tests/SonnetDB.EntityFrameworkCore.Tests/SonnetDbProviderTests.cs:625` | 独立 provider 项目有 Identity 用户子集测试 |
| 陈旧并发戳 | 同文件 `SaveChangesAsync_StaleConcurrencyToken_ThrowsConcurrencyException`，114 行 | 覆盖两个上下文更新后的过期 ConcurrencyStamp 保存冲突 |
| 真实影响行数校验 | `src/SonnetDB.EntityFrameworkCore/Update/Internal/SonnetDbModificationCommandBatch.cs:27`，`SonnetDbUpdateSqlGenerator.cs:37` | UPDATE / DELETE 使用条件列，零行或非一行按 EF 合同抛并发异常 |
| 基础类型 | `src/SonnetDB.EntityFrameworkCore/Storage/Internal/SonnetDbTypeMappingSource.cs:14` | 有 string、bool、int、DateTimeOffset、byte[] 等传统 Identity 所需映射 |
| 主键、外键和唯一索引 DDL | `src/SonnetDB.EntityFrameworkCore/Migrations/Internal/SonnetDbMigrationsSqlGenerator.cs:46`、211、429、440 行 | provider 已有相应生成逻辑，不宜当作待新建功能 |
| 存储层复合键及唯一约束 | `src/SonnetDB.Core/Tables/TableKeyCodec.cs:27`、`TableStore.cs:1946` | 有复合主键编码以及写锁内既有数据和同批数据的唯一性检查 |
| 关联删除及跨表恢复 | `src/SonnetDB.Core/Tables/TableManager.cs:1044`、779、103 行 | 已有外键级联、跨表 before-images 与未完成事务重开撤销逻辑 |

## 关键验证边界

1. **现有完整 Identity 测试存在条件编译缺口。** `tests/SonnetDB.IoTSharpCompat.Tests/SonnetDB.IoTSharpCompat.Tests.csproj:7`–10 依赖外部 IoTSharp 两个项目；缺失时 33–35 行直接移除该测试文件。本机默认解析到的 `D:\source\IoTSharp.Data\IoTSharp.Data.csproj` 和 `D:\source\IoTSharp.Data.Storage\IoTSharp.Data.SonnetDB\IoTSharp.Data.SonnetDB.csproj` 均不存在。因此不能将普通 SonnetDB 构建成功或测试源码存在计为完整 Identity 集成已经运行通过。
2. **独立 provider 的 IdentitySubset 不是完整 IdentityDbContext。** 它不能代替 RoleManager、UserManager 全功能与 SignInManager / Cookie 的真实应用验证。
3. **ASP.NET Core 10 Passkey 需要额外的 EF 能力。** 官方 schema v3 添加 AspNetUserPasskeys，Data 使用 `ComplexProperty(...).ToJson()`，凭据查找使用 `byte[].SequenceEqual(...)`。当前 provider 的 JSON 映射只有普通 string 映射，限定检索未发现专用 JSON 复杂类型或相应查询测试；SQL 引擎具备 JSON 能力也不能直接证明该 EF 模型可用。v1/v2 不包含此实体，不能将传统 Identity 路径的证据升级为 v3 Passkey 支持。
4. **迁移 DDL 无整体回滚保证。** provider 将 DDL 标记为 transaction-suppressed；关系 DML 的事务能力不等于多步建表、改表和迁移全部原子回滚。
5. **EF 路径没有 Native AOT 兼容承诺。** provider 项目显式 `IsAotCompatible=false`，见其 csproj:13。该结论不改变核心引擎的零第三方运行时依赖与生产 JSON source generation 要求。
6. **实际隔离合同为 ReadCommitted + read-your-writes。** `src/SonnetDB.Core/Sql/Execution/SqlTransactionContext.cs:9` 明确无事务快照或读锁。ADO 接受 Serializable 枚举不证明串行化隔离实际成立。传统 Identity 的唯一约束与乐观并发可以依赖现有实现；应用额外依赖范围读、无幻读或跨行判断时需另验。

## 推荐接入方式

应用引用匹配版本的 `SonnetDB.EntityFrameworkCore` 和 `Microsoft.AspNetCore.Identity.EntityFrameworkCore`，让上下文继承 IdentityDbContext，并在自定义 OnModelCreating 时保留 `base.OnModelCreating(builder)`。

```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SonnetDB.EntityFrameworkCore.Extensions;

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSonnetDB("Data Source=./identity-data"));

builder.Services.AddIdentity<IdentityUser, IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

public sealed class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<IdentityUser, IdentityRole, string>(options)
{
}
```

这是存储与服务注册示例。应用仍按常规 ASP.NET Core 配置认证中间件、登录界面或端点，并通过 SonnetDB provider 生成和应用自身迁移；替换其他数据库时不应直接复用其专用迁移 SQL。

## 后续顺序

1. 在不依赖 IoTSharp 外部源码的独立测试项目中使用真实 IdentityDbContext、UserManager、RoleManager 和 SonnetDB provider。
2. 验证建库 / Migrate、注册和用户名规范化、重复用户名、密码校验和重置、用户及角色并发戳、角色关联、Claims、外部登录、Token、Lockout、MFA / 恢复码与删除关联。
3. 将嵌入式与远程模式分别验收，补数据库重开恢复和真实 HTTP 登录 / Cookie 旅程，保留数据库约束异常和 IdentityResult 的实际行为。
4. 将 schema v3 Passkey 的 JSON 复杂类型映射、BLOB 相等查询和真实 WebAuthn 流程作为单独合同验收。

本次没有新增实现或测试，没有将源码分析写成生产验收 PASS。

## 官方依据

- [Identity 模型定制](https://learn.microsoft.com/aspnet/core/security/authentication/customize-identity-model?view=aspnetcore-10.0)
- [ASP.NET Core Identity 概述](https://learn.microsoft.com/aspnet/core/security/authentication/identity?view=aspnetcore-10.0)
- [v10.0.1 IdentityUserContext](https://github.com/dotnet/aspnetcore/blob/v10.0.1/src/Identity/EntityFrameworkCore/src/IdentityUserContext.cs)
- [v10.0.1 UserStore](https://github.com/dotnet/aspnetcore/blob/v10.0.1/src/Identity/EntityFrameworkCore/src/UserStore.cs)
- [v10.0.1 EF Store 注册](https://github.com/dotnet/aspnetcore/blob/v10.0.1/src/Identity/EntityFrameworkCore/src/IdentityEntityFrameworkBuilderExtensions.cs)
