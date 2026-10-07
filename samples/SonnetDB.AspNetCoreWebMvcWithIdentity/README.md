# ASP.NET Core MVC + Identity + SonnetDB

此样例使用微软 MVC + Individual Accounts 模板、官方 Identity EF Core Store 和 SonnetDB EF Core Provider。`ApplicationDbContext`、Identity 配置、控制器和视图沿用模板。

从 SQL Server 切换只需替换数据库 Provider、连接字符串和 Provider 专属迁移元数据：

```csharp
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSonnetDB(connectionString));
```

仓库样例通过 `ProjectReference` 引用 `src/SonnetDB.EntityFrameworkCore`；独立应用的对应组件替换是删除 `Microsoft.EntityFrameworkCore.SqlServer` 包，改为引用与 EF Core 版本匹配的 `SonnetDB.EntityFrameworkCore` 包。此次验证使用仓库源码，没有发布新的 NuGet 包。Identity 的 `AddDefaultIdentity` 和 `AddEntityFrameworkStores<ApplicationDbContext>` 调用保持原样，`Program.cs` 只改了 `UseSqlServer` → `UseSonnetDB`。

## 运行

需要 .NET 10 SDK 和 `dotnet-ef` 10.0 工具（建议与 EF Core 包补丁版本一致）。从样例目录执行：

```powershell
dotnet ef database update
dotnet run
```

`appsettings.json` 的 `DefaultConnection` 为 `Data Source=./identity-data`，指向当前工作目录下的 SonnetDB 数据库目录。此样例建立新数据库；旧 SQL Server 迁移不可直接用于 SonnetDB。

开发环境保留 `AddDatabaseDeveloperPageExceptionFilter` / `UseMigrationsEndPoint`。也可在首次数据库异常页面按模板提供的按钮应用迁移。

浏览器打开 `http://localhost:5013`，从 Register 创建用户。模板仍要求确认账号：开发环境在注册确认页点击确认链接后登录，随后使用账户管理页修改资料、密码或设置双因素认证。实际发送邮件及第三方登录仍需按 ASP.NET Core 模板配置对应服务。

EF Core / Identity Store 使用运行时模型，此样例不声明 Native AOT 兼容。

## 验证

2026-10-07 使用 .NET SDK 10.0.401、EF Core / ASP.NET Core Identity 10.0.12 完成当前实现的验证：

- 含新迁移的样例构建通过，0 个警告、0 个错误；从样例目录执行 `dotnet ef database update`，成功创建含 10 条长度 CHECK 的新数据库。
- 独立 MVC / Identity 集成测试 18/18 通过，无跳过项。覆盖迁移及开发迁移端点、注册与确认、登录和 Cookie、资料/密码/邮箱修改、密码重置、个人数据导出与删除、恢复码及 TOTP 登录、锁定与并发戳、Claims/外部登录记录/Token/角色表关联、字符串限长边界，以及宿主重启后的用户和持久 Cookie 恢复。
- EF Core Provider 回归测试 78/78 通过，无跳过项，包含 10 项提交约束异常和 8 项长度约束回归；关系引擎长度 CHECK 专项 6/6 通过。

Provider 补齐了两处能力：提交约束错误遵循 EF Core 保存异常合同；模型 Convention 将已有 `HasMaxLength` 自动转换为可见的数据库 CHECK。无需改 `ApplicationDbContext` 或注册逻辑，样例用户名/邮箱 256、外部登录和 Token 键 128 的限长由数据库执行，约束可由标准迁移增加或删除。完整解决方案 restore 与 CI 等价格式检查均已通过。

集成测试使用真实样例宿主、官方 Identity 页面和本地 SonnetDB 数据库；邮件发送替换为测试内记录器，Cookie 安全戳校验间隔仅在测试中缩短。上述结果不代表实际 SMTP、OAuth 服务、远程 SonnetDB 或 Passkey/WebAuthn 验收。样例保留原来的传统 Identity schema 和确认账号配置。

测试项目：`tests/SonnetDB.AspNetCoreWebMvcWithIdentity.Tests`。验证记录位于 `artifacts/identity-mvc-20261007`，详细范围见 [兼容性报告](../../docs/audits/2026-10-07-aspnet-core-identity-compatibility.md)。
