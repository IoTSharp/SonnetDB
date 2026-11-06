# JSON 产品补货应用（GH-Issue #395）

这个独立控制台应用使用 EF Provider **包引用**执行产品补货业务：按 SKU 与显式 RowVersion 更新补货策略对象、标签数组、供应商报价的固定元素，以及命令 Guid 和带 offset/ticks 的时间。产品库存值、目标外第三方 JSON 字段和数值原文必须保留；旧版本命令返回 0 行，事务回滚后策略保持，下一进程可以读取结果。

应用不读取实体后回写。`update` 阶段在首次业务更新前没有 SELECT，拦截器要求一次 ExecuteUpdate 只执行一条带参数 UPDATE。RowVersion 由应用显式加入谓词并递增；它不是数据库自动生成的 rowversion。JSON 名称包含中文、点、单引号和 emoji，以模型实际映射为准。

## 隔离运行

先将本目录的 `Issue395.JsonApp.csproj` 和 `Program.cs` 复制到仓库外的**新目录**。指定准确的 `ProviderVersion`，使用只含预期包源的新 NuGet.Config、新 packages 目录和新 HTTP 缓存执行 restore/build；不能从仓库导入 ProjectReference 或共享缓存。候选本地 feed 与 NuGet.org-only 公开验收必须分别运行和记录，旧公开 5.0.0 只可作负控。

构建后，以三个独立进程依次执行同一 DLL：

```text
dotnet Issue395.JsonApp.dll seed   <绝对数据库目录>
dotnet Issue395.JsonApp.dll update <同一绝对数据库目录>
dotnet Issue395.JsonApp.dll verify <同一绝对数据库目录>
```

默认使用该目录的嵌入式数据库。远程验收可通过进程环境变量 `SONNETDB_APP_CONNECTION` 指定独立测试 Server 的连接串，服务必须与候选源/版本匹配，REST 与 Frame 各自执行；不得连接生产。进程各有 90 秒取消预算，外部执行器还须提供墙钟超时与所属进程树回收。保留数据库与回执作为证据，不按目录前缀批量删除。

每个成功进程手写 JSON 回执，包含 phase、SQL/参数数以及输出目录中 SonnetDB DLL 的绝对路径与 SHA-256。外层还须保存包 ID/版本、来源 feed、恢复依赖图、源码 SHA、nupkg/DLL 清单和服务身份。应用不声明 EF NativeAOT 支持；其 JSON 回执不使用反射序列化。

## 验收边界

这个应用提供独立进程、真实数据库和包依赖的业务验收，不能代表已有生产应用已经升级。以当前分支源码构建的本地包 PASS 也不能写成公开新包 PASS。公开新 Provider、匹配 Server/依赖和 NuGet.org-only 空缓存复验的结果统一记录在 `docs/audits/issue-395-completion-20261106.md`，母 issue 只有全部适用条件满足后才可关闭。
