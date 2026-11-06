# OwnsMany 装箱清单应用（GH-Issue #394）

这个独立控制台应用以运单、装箱批次及零件集合执行真实 EF 跟踪生命周期。唯一 SonnetDB 依赖为具有准确版本的 `SonnetDB.EntityFrameworkCore` PackageReference；应用不使用 ProjectReference 或 ExecuteUpdate。运单中的 `OwnsOne` 包含二层 `OwnsMany`，另一张装箱单直接将根 `OwnsMany` 持久为 JSON 数组。

将本目录的 csproj 和 Program.cs 复制到仓库外的新目录，指定准确的 `ProviderVersion`，使用独立 NuGet.Config、packages 和 HTTP 缓存 restore/build。候选 feed、CI 候选和 NuGet.org-only 正式包分别建账；构建、服务及每次执行由主监督持共享独占重负载锁并串行排队。

构建后依次启动三个独立进程：

```text
dotnet Issue394.OwnsManyApp.dll seed   <绝对数据库目录>
dotnet Issue394.OwnsManyApp.dll mutate <同一绝对数据库目录>
dotnet Issue394.OwnsManyApp.dll verify <同一绝对数据库目录>
```

默认使用嵌入式目录。REST 与原生 Frame 分别用进程级 `SONNETDB_APP_CONNECTION` 指定隔离测试 Server 的连接串（既有 `Protocol=rest` / `Protocol=frame-http2` 合同）。应用不启动或停止 Server；Server 由主监督独占生命周期并核对匹配 source/版本/RID/程序指纹。远程连接串不写进回执。外层必须独立检查实际 HTTP/2 与 Frame 请求，不能只由连接串宣称协议已验。

`seed` 插入完整、空集合、JSON null 集合、SQL NULL 容器及缺失集合成员，验证重复值的不同元素、Guid、可空属性和值转换器。`mutate` 通过 SaveChanges 修改嵌套零件与批次属性，删除首个父批次并追加新批次，核对新序号和 JSON；再验证以下 EF10 限制，然后显式 Clear/重新查询并继续嵌套修改、清空、从空集合追加。另验根数组的元素修改及追加、事务隔离/回滚、显式 Stamp 乐观并发与实际零影响行数。`verify` 在另一进程重新打开，核对最终集合、顺序、converter 的模型/存储表示、null/空区分、外部 JSON 数值原文及胜出版本。

EF10 的父 ordinal 限制是验收合同的一部分：含二层集合的父数组删除/追加保存后，同一跟踪器立即清空集合须报告 identifying-key 异常，且没有下一条 SQL、持久 JSON 不变。随后由应用显式 `ChangeTracker.Clear()` 并重新查询，才继续变更。这个受限旅程不代表任意重排、跨 owner 转移或无需重载即可继续修改均受支持。上游行为如有变化，必须独立复核合同，不能删除或跳过该断言。

每个 phase 最多执行 120 秒，命令最多128条、单命令最多256参数、完成更新最多64条、业务断言最多96条，取消和超时不自动重试。所有输出循环均有 Take 上限。外层执行器还需有墙钟期限和所属子树回收。成功时手写 JSON 输出 `status=PASS`、phase、PID、业务断言、SQL/参数数、更新影响行数，以及实际已加载 SonnetDB 程序集的绝对路径/版本/SHA-256；未使用反射 JSON 序列化，也不声明 EF NativeAOT 支持。

原始失败、进程身份、候选包来源与恢复图、DLL/Server/source 清单和三个协议的独立进程回执由主监督写入 `artifacts/provider-next-phase-20261107/owns-many/`。候选消费通过仅证明对应候选；公开新包和实机门禁未完时 #394 继续 OPEN。专题状态见 [本阶段审计](../../../docs/audits/provider-candidate-20261107-ownsmany.md)。
