# #253：真实 Chloe 无主键写入

本目录验证 #243 已有引擎实现与 Chloe PR435 的消费合同，不新增引擎代码。固定 Chloe head 为 `bb7ec8735bd4511c35453eb009f70cc606795703`；候选仅删除 `SonnetDBContextProvider.EnsureInsertMappingSupported` 与 `SonnetDBTableGenerator.GenCreateTableScript` 中拒绝无主键模型的四行代码。

本轮结果：

| 消费方式 | 结果 |
|---|---|
| 正式包 4.0.0 原生 embedded | 3 PASS / 4 无主键写入 FAIL |
| 预览包 4.5.0-preview.ef266.5 原生 embedded | 3 PASS / 4 无主键写入 FAIL |
| 同原生消费者换用当前源码 Core/Data | 7/7 |
| 原 Chloe provider + 源码引擎，三协议 | 每协议 9 PASS / 11 guard FAIL |
| 候选 Chloe + 源码引擎，三协议 | 每协议 20/20 |
| 候选独立进程重开，三协议 | 每协议 5/5 |

两个公开包都有独立构建目录与零 SonnetDB ProjectReference 的资产文件。正向消费者虽然使用固定公开包编译，但运行目录显式换入工作树构建的 Core/Data DLL；日志打印了实际 DLL 路径、版本与 SHA，必须把它标作**源码验证**。真实远程 Server 同样来自源码，未验证发布版 Server。正式发行与 Chloe 上游交付仍待完成。

场景包括：重复相同行、NULL、带引号及中文的参数、实体/表达式/批量/异步 INSERT、跨批 CHECK 失败回滚、WHERE UPDATE/DELETE、事务提交与回滚、已有只读视图、SQL RETURNING 与透明元数据。实体对象 UPDATE/DELETE 仍要求业务主键；引擎无主键 ON CONFLICT 仍明确拒绝。没有给用户模型添加伪主键。

`native-returning-boundary-no-hidden-columns` 使用 Chloe Session 的原生 SQL 验证 INSERT/UPDATE/DELETE RETURNING，仅证明 SQL 边界；没有新增 ORM 的 RETURNING API。无主键表达式 INSERT 返回 null identity。

本轮工作目录为 `D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue253-work`，TEMP/数据库为独占 `D:/sndb-t/i253-*`。有界命令器固定本轮缓存路径；迁移目录时同步调整。每次执行使用新 Label，旧失败不得覆盖。

复现顺序：

1. 复制本目录隔离 `Directory.*`、`NuGet.Config` 和执行脚本到工作目录。将原生项目及源码复制到 `native-public4`、`native-public5`；将 Chloe 项目及源码复制到 `chloe-consumer-source`。
2. 分别构建原生项目；4.0.0 使用 `-p:SonnetPackageVersion=4.0.0`，另一个保留默认固定预览版本。每个 DLL 接收两个参数：连接字符串和结果标签。对两者预期 exit 1，四次写入失败均须匹配 `TableConstraintException` 无主键空表错误。
3. 从固定 SHA 的 Chloe 源码构建原 provider，指定 `-p:TargetFrameworks=net10.0 -p:NuGetAudit=false -p:SourceRevisionId=bb7ec8735bd4511c35453eb009f70cc606795703`。原源码存在 38 个警告，保留，不压制。保存完整原输出。
4. 用 `-p:ChloeBin=<原provider输出目录>` 构建消费者；复制输出到独立源码验证目录，替换 Core/Data 为本轮源码 DLL。候选复制这个目录，仅替换 `Chloe.SonnetDB.dll`。
5. 在干净 Chloe 源码执行 `git apply --check` 与 `git apply chloe-keyless-write.patch`，用相同构建参数生成候选。本轮对归档 1210 文件逐个 SHA 比较，仅两个补丁目标不同；实际 apply 后文件 SHA 与编译候选一致。补丁保留上游 CRLF。
6. 构建源码 Server（保留默认 AOT/trim 分析），执行两个矩阵：

   ```powershell
   ./Invoke-ConsumerMatrix.ps1 -WorkRoot $taskWork -ConsumerDirectory $taskOriginal -ServerDll $taskServer -Label chloe-original-01 -ExpectedFailures 11
   ./Invoke-ConsumerMatrix.ps1 -WorkRoot $taskWork -ConsumerDirectory $taskCandidate -ServerDll $taskServer -Label chloe-candidate-01 -Reopen
   ```

   脚本使用随机本地端口、真实 HTTP/1.1 SQL 与 HTTP/2 frame。候选完成三协议写入后，按自身 PID/start-time 结束 Server，再用相同数据目录和新 PID 启动 Server；各协议的 reopen 都是新的消费者进程。此处是请求完成后的进程终止/重开，不能代替事务 journal 各阶段故障注入。

7. 源码定向测试：`SqlKeylessDmlTests|SqlKeylessRecoveryTests`（25/25）和 `RemoteKeylessDmlTests`（6/6，同步/异步三协议）。本地 h2c 执行不要携带还原包用的网络代理。
8. `Record-Evidence.ps1` 按本轮固定目录与 Label 验证 12 个消费者进程、实际协议、Server 重启、包版本、原 guard 失败原因以及二进制 SHA，并写出 UTF-8 无 BOM / LF 审计 JSON。

首次归档目录判断错误、首次包装器进程观测异常、三处批量 API 缺失 table 实参的编译失败均保留。最终消费者与源码 Server/测试项目构建为零警告、零错误；外部 Chloe 原版与候选均仍有 38 个原有警告。
