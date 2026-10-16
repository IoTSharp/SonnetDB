# #260 的 Chloe 本地候选

此目录保留一个实际 Chloe 表达式翻译与消费探针。它依赖 SonnetDB **本次源码实现**和固定 Chloe 源码，不验证公开 `4.5.0-preview.ef266.5` 已提供 `parse_uint64`，也不代表 Chloe 上游已合并或发布适配。

Chloe 源码固定在 `maikebing/Chloe` PR #435 的 `bb7ec8735bd4511c35453eb009f70cc606795703`。先在未应用补丁的版本运行 `guard`，确认原先 `ulong.Parse` 的 #260 保护确实拒绝执行；再应用本目录 `ChloeUInt64Parse.patch`。补丁仅将单字符串参数的 `ulong.Parse` 表达式翻译为 `parse_uint64(...)`，不修改一般 UInt64 参数或存储映射。已有 `Parse_HandlerBase.CanProcess` 仍拒绝区域/样式重载。

## 语义边界

- 使用 [原生函数合同](../../sql-uint64-parse.md) 中的有界 ASCII 格式和 1024 UTF-16 代码单元上限；拒绝所有负号（包括 `-0`）、尾随 NUL、小数、指数、Unicode 数字/空白和溢出。这不是 CLR `UInt64.Parse` 所有重载与格式的完整等价替换。
- SQL 的 NULL 传播与 CLR `ulong.Parse(null)` 不同。探针使用可空投影 `ulong?` 验证 SQL NULL，不将 NULL 强制转成 0。
- 服务端以精确 `Decimal(20,0)` 返回，实际 Chloe 将其物化为 `ulong?`；完整范围不经过 Double 或有符号 Int64。
- 仅在包含本函数的 Core、Data 与 Server 源码组合中执行候选。旧 Server 对未知函数的拒绝不是新功能通过证据。
- 只读 h2c 查询使用 Frame；写入和事务使用 HTTP/2 REST。候选没有实现 Frame 写入。

## 构建和运行

使用 .NET 10，`ChloeRoot` 指向上述固定提交的独立克隆，`SonnetDbRoot` 指向本次 SonnetDB 工作树。克隆目录若位于其他仓库内部，应隔离其继承的 `Directory.Build.props`、`Directory.Build.targets` 和 `Directory.Packages.props`，避免误引入宿主仓库构建设置。此类空隔离文件不属于 Chloe 补丁。

```powershell
git -C $chloeRoot checkout --detach bb7ec8735bd4511c35453eb009f70cc606795703
dotnet build ./ChloeProbe.csproj -c Release -m:1 -nr:false `
  -p:UseSharedCompilation=false -p:ChloeRoot=$chloeRoot `
  -p:SonnetDbRoot=$sonnetDbRoot -p:TargetFrameworks=net10.0

# 独占运行目录必须先有 .owner 标记。应用补丁前保留原二进制，执行 guard。
dotnet $probeAssembly guard $guardRuntime
git -C $chloeRoot apply $candidatePatch
# 以相同参数重新构建，然后在新的独占目录执行。
dotnet $probeAssembly exercise $runtime
dotnet $probeAssembly reopen $runtime
```

实际验证的完整构建参数、私有缓存、命令、退出码、文件哈希和运行目录由同级审计报告记录。固定 Chloe 源码首次完整构建存在 38 条既有 CS8632 警告；增量候选构建的零警告不代表已修复这些警告。

`exercise` 创建并写入 `parsed_consumer`，检查真正 `ulong.Parse(row.Text)` 生成的 SQL、同步/异步投影、UInt64 最大值谓词、原生 SQL 物化、实际 ORM UPDATE、非法文本、后续行失败原子性和事务回滚。`reopen` 必须由第二个独立进程执行；嵌入式重开数据库，远程应先终止原 Server 并启动新 Server，复用同一独占数据目录。`reopen` 不重新建表或插入数据。

设置进程环境变量 `ISSUE260_CONNECTION` 可切换为远程连接；未设置时使用 `$runtime/db` 嵌入式数据库。远程连接中的临时令牌仅驻留于进程环境，不写入命令参数、日志或源码。使用 REST 与 frame-http2 分别运行两阶段，并检查 Server 实际请求协议。此探针不负责启动、停止或发布 Server。

本候选没有发送到外部 PR。正式 Core/Data/Server 发行及 Chloe 正式交付仍需单独确认。
