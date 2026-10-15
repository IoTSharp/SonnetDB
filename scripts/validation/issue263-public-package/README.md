# #263 公开预览包与 Chloe 原始参数验收

验证公开 `SonnetDB.Core` / `SonnetDB` **4.5.0-preview.ef266.5** 与固定 Chloe PR #435 源码 `bb7ec8735bd4511c35453eb009f70cc606795703`。不修改 Chloe 的已跟踪文件，不引用 SonnetDB 引擎源码、不替换包 DLL，不修改全局缓存。

需要 PowerShell 7、Git、.NET 10，以及可选的已构建且冻结的 SonnetDB Server。所有构建、clone、包缓存保存在新证据目录；Windows 使用短数据库/TEMP 路径，避免目录刷新 API 的长路径限制。

```powershell
./scripts/validation/issue263-public-package/Run.ps1 `
  -EvidenceDirectory D:/validation/i263-run `
  -RuntimeDirectory D:/sndb-t/i263-run `
  -Proxy http://127.0.0.1:7890 `
  -ServerDirectory D:/validation/frozen-server `
  -ServerSourceCommit '<server source SHA>'
```

省略最后两个参数只验嵌入式；无须代理时省略 `-Proxy`。每次必须使用全新证据/运行目录，旧失败不覆盖。脚本从 GitHub clone 固定 Chloe 提交，只添加空的局部 MSBuild 配置以阻断父目录设置；所有 SonnetDB 包都从唯一 NuGet.org 源恢复到私有缓存。Chloe 自身源码 ProjectReference 是实际未发布消费者的一部分，与公开 SonnetDB 客户端包分别记录。

`Probe` 每次仅执行一个入口及阶段：`ado|chloe exercise|reopen owned-directory`。嵌入式使用独立新进程重开；远程在两种入口的 exercise 完成后终止自有 Server 进程，启动新进程再分别 reopen。实际 Kestrel 日志断言 REST 为 HTTP/1.1、h2c 为 HTTP/2，且 h2c 存在 `/v1/frame`；DDL/DML/事务的 HTTP/2 REST 与 Frame 查询分开记录。

矩阵包含 sbyte/ushort/uint 的上下界、对应三个底层类型的真实枚举、非空 Nullable、NULL/DBNull、参数谓词、原始多行 INSERT、同步/异步、事务插入/更新/删除回滚、值和 reader Int64 元数据及重开。原始 SndbParameter 的 Value 引用和 DbType 不得改变。Chloe 的原始 DbParam、Session、SqlQuery 通过拦截器验证到达驱动的 CLR 类型；普通 DbParam 的枚举由原 Chloe 转成原底层类型，ExplicitParameter 路径另验真正的 boxed enum。已有 ORM 扩大为 Int64 的实体/InsertRange/表达式路径作为独立对照，不能替代原始合同。

额外 UInt64 控制单列：公开 `.5` 嵌入式接受 Int64 范围内 UInt64，远程原始 UInt64 仍全部拒绝；高位 UInt64/枚举继续拒绝。当前源码中后续新增的范围内 UInt64 远程支持不能反推到旧公开包。探针将这种既有拒绝验证为预期边界，不声称该能力已交付。

Chloe 固定源码构建存在 38 条既有 CS8632 警告，本工具不修改或屏蔽；新增 probe 自身启用 Nullable/ImplicitUsings/TreatWarningsAsErrors。消费者不声明 Native AOT。脚本保留数据库、包、日志、进程身份及 SHA 清单；这些产物不提交。正式稳定驱动/Server 与正式 Chloe 发行验收仍是 #263 的关闭前置条件，详见对应审计报告。
