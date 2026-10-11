# ASP.NET Core Identity / EF Core 兼容性分析与样例验证

日期：2026-10-07（Asia/Shanghai）。本报告包含初始源码分析和随后完成的样例替换、Provider 修复与实际验证。初始分析基线为 `main` 的 `11f358c698005877b3e0362946ef9cfd06afa3f1`；实现和验证针对本次工作树，不代表该提交或已发布 NuGet 包已经包含全部改动。

## 结论

`samples/SonnetDB.AspNetCoreWebMvcWithIdentity` 已通过微软标准 Identity EF Core Store 使用 SonnetDB。应用启动代码只需将 `UseSqlServer(connectionString)` 改为 `UseSonnetDB(connectionString)`；数据库 Provider 引用、连接字符串和 Provider 专属迁移同步替换。`ApplicationDbContext`、`AddDefaultIdentity`、`AddEntityFrameworkStores<ApplicationDbContext>`、控制器、视图及业务逻辑保持模板原样，无需另写 `IUserStore` / `IRoleStore`。

当前实现的 MVC / Identity 集成测试 **18/18 通过，无跳过项**，EF Core Provider 完整回归 **78/78 通过，无跳过项**，关系引擎长度 CHECK 专项 **6/6 通过**。已重新生成含长度约束的迁移并通过命令行建立新库。这证明样例所用的传统 Identity 可以采用标准组件替换方式，Provider 的提交异常与字符串限长合同已补齐；并不代表所有 SQL Server 能力或 Identity schema v3 的 Passkey 已经等价支持。

用户明确要求新建 SonnetDB 数据库。本次不迁移已有 SQL Server 用户数据，也不直接复用 SQL Server 专属迁移。

## 最小替换范围

| 文件/组件 | 修改及作用 |
| --- | --- |
| 样例 `Program.cs` | 仅 `UseSqlServer` → `UseSonnetDB`；扩展方法仍在 `Microsoft.EntityFrameworkCore` 命名空间 |
| 样例项目引用 | 删除 SQL Server Provider，引用仓库内 `SonnetDB.EntityFrameworkCore`；现有 Microsoft 包沿用中央版本 `10.0.12` |
| `appsettings.json` | `DefaultConnection` 改为 `Data Source=./identity-data`，以目录持久化新库 |
| 服务依赖配置 | 去掉开发环境 MSSQL 服务依赖 |
| Identity 迁移及 model snapshot | 使用 SonnetDB Provider 重新生成；创建七张传统 Identity 表及迁移历史、唯一索引、复合主键、外键级联和 10 条字符串长度 CHECK |
| 样例说明及忽略规则 | 记录运行方法，忽略本地 `identity-data/` |

样例原有 `RequireConfirmedAccount=true`、开发迁移异常页和迁移端点保持原样。没有为验证增加启动时自动迁移或自定义 Identity Store。

独立应用的对应替换是引用与其 EF Core 版本匹配的 `SonnetDB.EntityFrameworkCore`。本轮使用仓库源码项目引用，未发布新的包；不能将本轮源码测试结果写成某个公开 NuGet 版本的验收结果。

## 实际补齐的 Provider 合同

本次发现：多命令保存将关系约束检查延迟到事务提交时，`TableConstraintException` 会直接传出，而 EF Core 调用者预期接收带有修改实体条目的保存异常。

新增 `SonnetDbRelationalDatabase` 并注册为 `IDatabase`，在同步/异步 `SaveChanges` 的提交异常边界完成转换：唯一、外键和 CHECK 约束映射为 `DbUpdateException`，并发冲突映射为 `DbUpdateConcurrencyException`。转换保留原异常和实际修改条目。已经由命令批处理包装的 EF 异常不重复包装；无关传输错误、I/O、取消及 schema 错误仍按原合同传出。

该修复同时识别本地约束错误码和远端 `SndbServerException.Error`。回归中注入远端异常仅验证分类及传播合同，没有连接真实 SonnetDB Server，不能作为远程 Identity 流程的通过证据。

后续复核还发现样例可达的长度合同缺口：Identity 已声明用户名/邮箱等字符串 `MaxLength=256`，并为外部登录和 Token 复合键声明 `MaxLength=128`，但原 SonnetDB 普通字符串列没有自动执行该长度限制。模板注册页面可能将超长邮箱提交到 Store，不能依赖页面校验代替数据库约束。

新增 `SonnetDbStringLengthConvention`，在模型完成时把字符串属性的正整数 `HasMaxLength` 转换为可见的关系 CHECK：`"Column" IS NULL OR char_length("Column") <= N`。约束纳入 EF 模型、snapshot 和标准迁移差异，限长变更可通过 `DropCheckConstraintOperation` / `AddCheckConstraintOperation` 管理。共享表列只生成一条约束并选择最小有效限长，同名自定义 CHECK 冲突明确拒绝，不静默替换。应用无需增加自定义模型配置或业务校验代码。

关系引擎已有 `length` / `char_length` 函数，本次仅将它们的单参数调用加入 CHECK 白名单，没有扩大为任意函数调用。长度沿用现有 `string.Length` 语义（UTF-16 代码单元），包括尾部空格；空值处理保留 CHECK 的 SQL 合同。核心专项验证了写入、更新、边界、空值、重开恢复及非法函数形式拒绝，Provider 和样例限长边界也已执行通过。

## 实际验证

环境：Windows、.NET SDK `10.0.401`、EF Core / ASP.NET Core Identity `10.0.12`，本地嵌入式 SonnetDB。设计时工具 `dotnet-ef` 为 `10.0.5`，生成和应用迁移成功；工具提示补丁版本低于运行时。使用者应选择与运行时匹配的 .NET 10 工具版本。

| 验证 | 结果 | 证据 |
| --- | --- | --- |
| 含新迁移的样例及测试项目构建 | 0 警告、0 错误 | `identity-tests-with-length-final.stdout.log` / `.stderr.log` |
| 含长度约束的初始迁移生成 | 成功，`20261007135609_CreateIdentitySchema` 包含 10 条长度 CHECK | `generate-migration-with-length.json` 及 stdout |
| 设计时命令应用迁移 | 成功创建 `cli-data-with-length` 新库 | `cli-database-update-with-length.json`；从样例目录执行 `dotnet ef database update --no-build --connection ...` |
| MVC / Identity 集成测试 | 18/18 PASS，0 跳过，包含 TOTP 和两项限长边界 | `identity-tests-with-length-final.trx` / `.json` |
| 完整 EF Core Provider 回归 | 78/78 PASS，0 跳过，包含 10 项提交约束及 8 项长度回归 | `ef-provider-length-verified.trx` / `.json` |
| 关系引擎长度 CHECK 专项 | 6/6 PASS，0 跳过 | `core-length-check-tests.trx` / `.json` |
| 完整解决方案 restore | 退出 0 | `solution-restore-verified.json` |
| CI 等价格式检查 | 退出 0，无格式错误；有工作区加载警告 | `solution-format-verified.json`；使用原 `--verify-no-changes --no-restore --severity warn --exclude extensions/` 参数 |

以上是当前实现已取得的结果。首轮格式检查发现自动生成迁移的 CRLF 行尾和测试导入顺序，修正后完整解决方案检查通过；行尾归一化没有改变迁移逻辑。构建和功能测试结果与格式门禁分别记录。

以上记录位于 `artifacts/identity-mvc-20261007`，命令回执的 `CleanupVerified=true`。构建和测试使用有界进程包装器；证据目录不是需要提交的构建产物。

新增 `tests/SonnetDB.AspNetCoreWebMvcWithIdentity.Tests` 直接启动样例 `Program`，使用原 `ApplicationDbContext`、标准 `UserManager` 和官方 Razor Identity 页面。覆盖范围如下：

- 迁移、七张表、唯一索引、迁移历史及重复应用迁移；原开发 `/ApplyDatabaseMigrations` 端点对新库的初始化。
- 注册、邮件确认、正确/错误密码、登录、退出及 Cookie；资料、密码、邮箱修改，忘记/重置密码与已消费重置令牌拒绝。
- 个人数据下载与账号删除；Claims、外部登录记录、认证 Token、角色表及角色关联的持久化和删除级联。样例没有增加 `RoleManager` 服务或角色管理页面，角色验证使用原上下文的表模型。
- 重复规范化用户名的数据库约束和整批回滚、陈旧 `ConcurrencyStamp`、锁定与解锁、更新 `SecurityStamp` 后旧 Cookie 失效。
- Authenticator key 持久化、恢复码登录、重复使用恢复码拒绝、恢复码重建与关闭双因素认证；官方 TOTP 启用页面、重启后的 key 与状态恢复、`LoginWith2fa` 验证和受保护页访问。
- 完整释放并重新创建应用宿主，使用同一数据库和 Data Protection key 目录，恢复用户、Claims 及持久登录 Cookie。

两项限长测试也已通过：原注册页面提交 257 字符邮箱被数据库拒绝且不留下用户，256 字符邮箱边界可注册确认登录；外部登录和 Token 的 128 字符键可持久化，129 字符键保存失败，重启后已保存的边界值仍可读。测试保留模板现有行为，没有增加自定义友好错误页面或改注册逻辑。

测试为每个实例分配独立的本地数据库，通过 `ConfigureDbContext` 覆盖连接目录并断言实际 `DataSource`，避免模板提前捕获连接串导致多个测试共用默认库。临时目录带所有权标记，释放宿主后才精确删除。

测试替换 `IEmailSender` 为内存记录器，并将 `SecurityStampValidatorOptions.ValidationInterval` 缩短为零；Data Protection keys 写入该测试独占目录。应用逻辑和 Identity 配置没有因此修改。HTTP 验证通过 `WebApplicationFactory` 的测试服务器执行，不等同于生产部署或真实外部 SMTP/OAuth。

早期失败回执保留：首轮测试暴露连接目录隔离问题及上述提交约束异常；隔离和 Provider 修复后又校正恢复码测试中已失效 Cookie 的退出流程，最终取得上述通过结果。最终结果不覆盖或删除早期失败证据。

## 原有实现与初始分析的更正

现有 Provider 和关系层已具备传统 Identity 所需的基础类型、复合主键、唯一索引、外键及级联、条件 UPDATE/DELETE 的影响行数检查和持久化。原 `tests/SonnetDB.IoTSharpCompat.Tests` 也有完整业务上下文的 Identity 测试源码，但默认外部 IoTSharp 项目缺失时会条件移除该文件。新样例测试不依赖这些外部项目，因此本次通过结果有独立执行证据。

**本样例使用传统 Identity schema。** 原 `AddDefaultIdentity` 的 `StoreOptions.SchemaVersion` 默认为 `0.0`，Identity 将其按 v1 处理；`MaxLengthForKeys` 为 128。`AddDefaultIdentity` / `AddDefaultUI` 不会自动启用 schema v3，本轮也没有修改这些选项或加入 Passkey 表。

**更正初始 Passkey 分析。** 对照实际依赖版本 ASP.NET Core `v10.0.12`，schema v3 的 Passkey Data 使用 `OwnsOne(p => p.Data).ToJson()`，不是初始报告写的 `ComplexProperty(...).ToJson()`。EF Core Relational 已有 `ByteArraySequenceEqualTranslator`，不能仅因 SonnetDB 没有专用 `SequenceEqual` translator 就判断这类查询缺失。JSON owned entity 映射、凭据查询、完整 v3 模型和 WebAuthn 仍需单独实际验证；传统样例通过不提供这些证据。

## 保留边界

1. 本轮验证为本地嵌入式数据库。真实远程服务、网络断开及远程恢复流程尚未验收。
2. 只验证外部登录记录与 Token 的存储合同，没有连接 GitHub/OIDC/OAuth 服务；邮件确认和重置使用记录器，没有实际发邮件。
3. TOTP 测试使用测试内按 RFC 6238 生成的验证码通过官方启用/登录页面；没有验证真实手机验证器、二维码扫描或生产时钟同步。
4. 宿主重启证明正常关闭重开的持久化和 Cookie 恢复，没有在本轮执行崩溃/断电注入。
5. Provider 对迁移 DDL 使用 transaction-suppressed；DML 保存回滚通过不等于整个多步迁移具有原子回滚保证。
6. EF Provider 已明确 `IsAotCompatible=false`；样例遵循该边界。本轮不声明 Identity EF Store Native AOT 兼容，不改变核心引擎的依赖与 JSON source generation 要求。
7. 关系事务实际隔离为 ReadCommitted + read-your-writes。ADO 接受 Serializable 枚举不证明串行化隔离；依赖无幻读、范围判断或跨行判断的应用需另验。
8. 这是一项针对现有样例的组件替换验收，没有测试所有 SQL Server 特性、所有自定义 Identity 模型或全部部署环境。

## 运行方式

从 `samples/SonnetDB.AspNetCoreWebMvcWithIdentity` 目录执行：

```powershell
dotnet ef database update
dotnet run
```

默认数据库目录为当前工作目录下的 `identity-data`，开发地址为 `http://localhost:5013`。注册后按原模板确认账号再登录。详见 [样例 README](../../samples/SonnetDB.AspNetCoreWebMvcWithIdentity/README.md)。

## 官方依据

- [Identity 模型定制](https://learn.microsoft.com/aspnet/core/security/authentication/customize-identity-model?view=aspnetcore-10.0)
- [ASP.NET Core Identity 概述](https://learn.microsoft.com/aspnet/core/security/authentication/identity?view=aspnetcore-10.0)
- [v10.0.12 IdentityUserContext](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Identity/EntityFrameworkCore/src/IdentityUserContext.cs)
- [v10.0.12 UserStore](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Identity/EntityFrameworkCore/src/UserStore.cs)
- [v10.0.12 AddDefaultIdentity](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Identity/UI/src/IdentityServiceCollectionUIExtensions.cs)
- [EF Core v10.0.12 ByteArraySequenceEqualTranslator](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore.Relational/Query/Internal/Translators/ByteArraySequenceEqualTranslator.cs)
