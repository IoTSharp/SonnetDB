# GH-Issue #273：公开包迁移脚本合同验收

2026-10-15（Asia/Shanghai）。按原 Issue 允许的方案，在生成阶段明确拒绝不受支持的幂等脚本；没有新增幂等部署脚本能力。已回复 [验收结果](https://github.com/IoTSharp/SonnetDB/issues/273#issuecomment-6196247725)，GitHub API 回读为 `closed/completed`。

## 固定消费者

独立 .NET 10 / EF Core 10.0.12 console，只使用 NuGet.org，全新私有 packages/http-cache、无 fallback、无 ProjectReference。三个 SonnetDB 包固定 `4.5.0-preview.ef266.5`，nuspec 与实际 Provider InformationalVersion 均绑定源码 `1af23d8f299febfdf66b040ab3419980396acf46`。本次没有重新发布包，也没有使用本批工作树 DLL 代替公开包。

| NuGet 包 | nupkg SHA256 |
|---|---|
| SonnetDB.Core | `10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268` |
| SonnetDB（ADO.NET） | `0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4` |
| SonnetDB.EntityFrameworkCore | `9688BA682E160639681AF3E4882DE6A58F70CF289B42552987FA7CD85D87E125` |

## 实际结果

- Release 构建 0 warning / 0 error。
- `IMigrator.GenerateScript` 对 `Idempotent` 与 `Idempotent | NoTransactions` 分别覆盖 initial、downgrade、same-version、empty，均抛出说明缺少迁移历史条件保护的 `NotSupportedException`；每个进程 8 次明确拒绝，不返回误标的普通脚本。
- 普通 Default / NoTransactions 脚本保留业务 DDL 与历史写入。
- 初始迁移后插入 Id=41，再进行真实 AddColumn 升级；连续 `MigrateAsync` 重跑通过。
- 第二个独立 dotnet 进程重开同一数据库后重复选项检查和迁移；历史精确两条、原数据精确一条，Unicode 值“公开包升级保留数据”保持。两个实际进程均退出 0。

这是公开包 console 真实运行证据，没有 TRX。完整源、项目配置、公开包、资产 SHA256、退出码与日志留在本地忽略目录 `artifacts/batch-20261015/issue273-public-ef2665-8bd14ec2/`：`receipt.json`、`Program.cs`、`Repro.csproj`、`restore.log`、`build-final-v2.log`、`exercise-final.log`、`reopen-final.log`。

原 4.0.0 首轮成功/重放失败证据保持。probe 初次直接引用内部类型引发 EF1001，改为程序集元数据读取；原 12 位 migration ID 无法用于指定目标的回退生成，改用标准 14 位 ID，两个 flags 入口及简单 CreateTable 合同保持。原失败日志与误写 ADO 包名的初始收据均保留；最终收据已核实际包名 `SonnetDB` 和非空 hash。隔离 restore 关闭包漏洞审计，不据此得出依赖安全结论。

## 其它 EF Issue

| Issue | 状态与精确剩余验收 |
|---|---|
| #270 | 已有 LIKE ESCAPE 实现和本地/真实传输记录；公开包应用旅程只见普通作者值，还缺 `%`、`_`、反斜杠原复现及真实作者列表/Count 的公开包验收。 |
| #271 | 公开包 IdentityUserStore Passkey 读写与重建、真实 REST/h2c 已有证据；仍缺普通 byte[] 相等、Any+SequenceEqual 原独立复现、边界/回滚矩阵与 SQLite 对照的公开包重跑。 |
| #272 | 当前源码测试覆盖三通道命名 FK 删除/替换/回退/重开；旧本地测试通过不代替独立公开包重跑该合同。 |

#270～#272 保持开放；公开 preview 本身满足“公开修复包”条件，不因仍为预览一律阻止关闭。原 Issue 要求正式稳定发行或特定 ORM 消费时，则继续按其原验收记录。#273 的关闭不推导 Server、完整应用恢复或 #227 DDL 事务完成。
