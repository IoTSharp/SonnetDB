# #251 DateTimeOffset 公开负基线

此探针只核对原始公开包与固定 Chloe 的真实缺口，不代表 #251 已实现。

使用 .NET 10、PowerShell 7，输出和短运行目录必须全新；NuGet 缓存、构建结果放输出目录。原始 Chloe 来源固定为 PR #435 的 `bb7ec8735bd4511c35453eb009f70cc606795703`，生产源码不打补丁、不替换类型转换器。SonnetDB 使用 NuGet.org 的精确 `4.0.0` / `4.5.0-preview.ef266.5`，无 SonnetDB ProjectReference。

```powershell
pwsh -NoProfile -File .\Run-Public.ps1 `
  -OutputDirectory D:\evidence\issue251-public `
  -RuntimeDirectory D:\sndb-t\251-public `
  -ChloeSource D:\source\fixed-chloe-bb7ec873
```

每个版本运行两个独立进程：首次批量写入与事务提交/回滚、独立重开。验证 UTC、+08:00、-07:00 的同一时刻、无 offset 文本、UTC DateTime、NULL、参数比较；分别记录普通/空/all-NULL 结果的通用值及 schema 类型。原 `DATETIME` 通用 reader 为 DateTime，专用 `GetFieldValue<DateTimeOffset>` 可以转换；这两项不能等同为原生 DateTimeOffset 声明。

真实 Chloe `SonnetDBTableGenerator` 在执行前拒绝映射；`SqlQuery<DateTimeOffset>` 和对象投影绕过 DDL 后，均实际进入原默认 `DateTimeOffset_ValueConverter` 并抛出 `InvalidCastException`。DateTime 正向物化和默认转换器直接接受原生 DateTimeOffset 作为控制。没有手写转换器模仿框架行为。

`public-01` 保存了探针内联主键语法不兼容 4.0.0 的失败；改为表级主键的 `public-02` 四进程全部完成预期负基线。原 Chloe 首次编译有 38 条既有警告；后续仅改公开引用的增量构建为 0 警告，不能写成 Chloe 源码全量零警告。

后续原生声明、存储/目录屏障、REST/h2c、旧客户端副作用前拒绝、真实 Chloe 新类型正向映射都尚未实现/验证。共享生产改动需等 #252 的 DURATION 基线冻结后串行执行。
