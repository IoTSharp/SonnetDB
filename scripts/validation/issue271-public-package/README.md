# #271 公开包验收

固定 NuGet.org 的 `SonnetDB.Core`、`SonnetDB`、`SonnetDB.EntityFrameworkCore` **4.5.0-preview.ef266.5**，SQLite/EF Core **10.0.12**。这是一组独立消费者，不引用引擎源码项目，不替换包 DLL，不修改全局缓存。`Original/Program.cs` 保留线上 Issue 原复现；`Matrix` 扩展二进制查询与生命周期边界。

要求 PowerShell 7、.NET 10。ABP 账户旅程额外需要 Git、Node.js/npm；脚本只恢复项目依赖，不安装全局工具。每次使用新证据目录与新运行目录，保留旧失败。Windows 建议将数据库和 TEMP 放在短路径且空间足够的盘上。

```powershell
# SQLite、嵌入式、原四进程复现、扩展矩阵和独立进程重开。
./scripts/validation/issue271-public-package/Run.ps1 `
  -EvidenceDirectory D:/validation/i271-public `
  -RuntimeDirectory D:/sndb-t/i271-public `
  -Proxy http://127.0.0.1:7890

# 另验真实 REST 与精确 h2c；传入已构建且冻结的 Server 目录和源码 SHA。
./scripts/validation/issue271-public-package/Remote.ps1 `
  -EvidenceDirectory D:/validation/i271-remote `
  -RuntimeDirectory D:/sndb-t/i271-remote `
  -ServerDirectory D:/validation/frozen-server `
  -ServerSourceCommit '<server commit>' `
  -MatrixAssembly D:/validation/i271-public/consumer/Matrix/bin/Release/net10.0/Matrix.dll

# 固定 SonnetDB.Abp 841567fd963621a8656a904121462234c6c80c0e 的原 Passkey/账户测试。
./scripts/validation/issue271-public-package/Run-Abp.ps1 `
  -EvidenceDirectory D:/validation/i271-abp `
  -RuntimeDirectory D:/sndb-t/i271-abp `
  -Proxy http://127.0.0.1:7890
```

无须代理时省略 `-Proxy`。所有环境覆盖只传给子进程。`Run.ps1` 先把消费者源复制到证据目录，再以全新 packages/http-cache、固定 lock file、NuGet.org-only 配置恢复；`obj/project.assets.json` 必须只有 NuGet.org 和该私有包目录。局部 MSBuild props/targets 阻断仓库中央版本设置与 SDK 隐式离线源。包 nuspec commit、nupkg SHA256、实际 DLL SHA256、进程退出码、stdout/stderr 与证据清单分别记录。

远程脚本复制并逐文件校验 Server，使用随机环回端口和仅驻留子进程环境的临时令牌。它在 exercise 完成后**终止自己启动的 Server 进程**，再启动新进程执行 reopen；这是实际进程终止后的恢复证据，不称为优雅停机。协议检查覆盖 `/v1/db/...` 与 `/v1/frame` 的实际 Kestrel 请求日志。公开客户端与源码 Server 的来源分别记录。

ABP 脚本保留固定仓库所有已跟踪代码，唯一附加文件为隔离 restore 源的 `Directory.Build.targets`。ABP 仓库内部应用/适配项目引用属于真实消费者；其中引擎/ADO/EF 仍来自公开包，不跨仓库引用 SonnetDB 源码。前端资源按其原 CI 执行 `npm ci` 和 `eng/Copy-BookStoreAssets.cjs`，不关闭 libs 检查、授权、安全日志或事务。两个原测试分别运行并输出 TRX；不依赖 PostgreSQL测试。

矩阵包含同内容不同实例、多用户多凭据、空数组、零字节、高位字节、缺失/长度/内容负例、NULL、普通等值与相关 EXISTS、真实 Binary 参数、二进制索引/FK、更新删除、回滚和重开。默认独立无索引 BLOB 主键验证 65,531 字节及 65,532 字节拒绝；这取决于编码和默认 key 上限，不是通用 BLOB 列上限。65,536 字节非主键 payload 另验。ABP 验证完整实际 Store/账户应用，但不代表浏览器 WebAuthn、远程 ABP 或完整 A06 验收。

本次结果及保留的环境/探针失败见 `docs/audits/issue-271-public-package-20261016.md`。脚本保留数据库及缓存供复查，日志/数据库/bin/obj/nupkg 不纳入提交。
