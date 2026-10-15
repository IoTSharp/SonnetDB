# GH-Issue #314：DECIMAL round 本地实现

核查日期：2026-10-16（Asia/Shanghai）。隔离实施基线为 `89674db059928aa7f1bea1cfef8a13e97c43d648`，工作树为 `C:\Users\mysti\.codex\worktrees\issue314-decimal-round\SonnetDB`。本报告完成时全部修改未提交、未推送、未发布；未对 Issue 发评论或关闭。根智能体负责审查、逐 Issue 提交与批次推送。

## 原合同和实现

已通过 `gh issue view 314 --repo IoTSharp/SonnetDB --json number,title,body,comments,labels,state` 读取当时 OPEN 的原文，未有评论。原问题的 DECIMAL 参数绑定已精确，损失发生于 `FunctionRegistry.EvaluateRound` 的 `RequireDouble`。本次在任何 double 转换前按运行时 Decimal 分流，直接调用 `Math.Round(decimal, digits, MidpointRounding.ToEven)`。

- `round(value)` 等价于十进制 digits=0；`round(value,digits)` 接受 0～28 的整值数值参数。负值、29 以上、小数、NaN、Infinity、字符串和 NULL digits 明确拒绝；不先截断位数，不先把 Decimal 转为 double。
- SQL typed Decimal 参数、`DECIMAL '…'`、`CAST(... AS DECIMAL)`、关系 DECIMAL 列和 Decimal 算术统一进入此分支。SELECT/WHERE/UPDATE 使用同一内置标量求值器，后续行位数失败保留 UPDATE 原子性，已更新金额通过嵌入式数据库重开验证。
- `round` 的声明推断仅针对内置且合法参数数量的调用。Decimal 输入声明为 Decimal，INT/FLOAT 输入声明为 Float64；继承输入 nullable，清除列键属性。`round(DECIMAL(28,8),2)` 的结果 precision/scale 为未知，不能把可能进位的结果继续声称为原声明；空表、过滤零行、JOIN、视图、嵌套 round 和 typed 参数都保留 Decimal 类型。
- 自定义同名 `round` 不采用内置推断，推断不调用 UDF。动态 UDF 返回 Decimal 仍可在运行时精确舍入，而静态声明保持未知。
- INT/FLOAT 的既有 double 结果、0～15 位限制、原位数截断、非有限值与 NULL 拒绝行为保持。DECIMAL NULL 值/NULL digits 也沿用原有明确拒绝，不新增 NULL 传播。未扩展 `abs`/`floor`/`ceil`，未改二进制格式、协议、JSON DTO、依赖或 public API。

## 验证

构建进程设置 `DOTNET_PROCESSOR_COUNT=2`、`MSBUILDDISABLENODEREUSE=1`；构建均使用 `-m:2`。日志与 TRX 位于本隔离树忽略目录 `artifacts/issue-314-20261016/`，不计为提交内容。

| 命令/产物 | 实际结果 |
|---|---|
| `dotnet build tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release -m:2 --nologo` / `core-build.log` | exit 0，0 warning / 0 error |
| 下列 Core 定向测试 / `core-focused.trx`、`core-focused.log` | exit 0，158/158，0 failed / 0 skipped；其中本次新增 18 项 |
| 首次 `dotnet build tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release -m:2 --nologo` / `server-build.log` | exit 1：隔离树两个子模块未初始化，1 warning / 132 errors；不算产品代码验证通过 |
| 初始化锁定子模块后相同 Server build / `server-build-initialized.log` | exit 0，0 warning / 0 error |
| 下列真实 ADO 定向测试 / `server-round.trx`、`server-round.log` | exit 0，6/6，0 failed / 0 skipped |
| 下列两个项目的改动文件定向 Format Check / `core-format-targeted.log`、`server-format-targeted.log` | 均 exit 0；只作为局部格式证据 |

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~SqlDecimalRoundTests|FullyQualifiedName~SqlDecimalParameterTests|FullyQualifiedName~SqlMathScalarFunctionTests|FullyQualifiedName~Query.Functions.FunctionRegistryTests' --logger 'trx;LogFileName=core-focused.trx' --results-directory artifacts/issue-314-20261016

git -c http.proxy=http://127.0.0.1:7890 submodule update --init --recursive -- extensions/IoTSharp.CoAP.NET extensions/MQTTnet.AspNetCore.Routing

dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~DecimalRound_ParametersColumnsAndEmptyMetadata_PreservePrecision' --logger 'trx;LogFileName=server-round.trx' --results-directory artifacts/issue-314-20261016

dotnet format tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --verify-no-changes --no-restore --severity warn --include src/SonnetDB.Core/Query/Functions/FunctionRegistry.cs src/SonnetDB.Core/Sql/Execution/RelationalSelectExecutor.cs tests/SonnetDB.Core.Tests/Sql/SqlDecimalRoundTests.cs

dotnet format tests/SonnetDB.Tests/SonnetDB.Tests.csproj --verify-no-changes --no-restore --severity warn --include tests/SonnetDB.Tests/RemoteAdoDecimalRoundTests.cs tests/SonnetDB.Tests/RemoteAdoHttp2TransportTests.cs
```

子模块分别锁定 `797a85992ad33be02c4aa4f4ea9c849ed382c2cc`、`9e0c8202fc93737b2bbde013ed488d37948ad5c7`，未改 gitlink。真实 ADO 使用已有 Kestrel fixture；六组为 embedded/rest/frame-http2 各同步/异步执行参数、SELECT/WHERE/UPDATE 与 schema。REST 和 Frame 请求逐条检查实际 HTTP/1.1 / HTTP/2，h2c 包含 `/v1/frame` 与写 SQL endpoint，未使用 mock 代替服务。

精度断言包括 ±9007199254740993.125→两位、±1.015→±1.02、四个 ±2.5/±3.5、20 位及 28 位结果、Decimal 最大/最小值。Core 穷举所有 29 个合法位数；真实 ADO 检查 `GetValue` 的 CLR Decimal、`GetDecimal`、`GetFieldType`、`GetDataTypeName`、`GetColumnSchema` 与经典 `GetSchemaTable`，在读取行前检查声明，额外验证空表及非空表过滤零行。

## 集成与剩余验收

子智能体未执行 commit。根已将冻结源码按 SHA256 核验后集成到 `b99bf142` 主树，两个 Release build 均 0 warning / 0 error；扩展 Decimal/函数集合215/215，SELECT列元数据与ADO集合52/52，真实ADO新回归与已有列元数据传输8/8，全部零失败/零跳过。与子树158/6重叠，不累加。根结果保存在 `artifacts/issue-supervision-20261016/issue314/`。

本实现随本报告独立提交；最终待提交树必须执行原完整 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`，实际均 exit 0 后才执行 commit，收据在上述根证据目录，提交号以 Git log 为准。局部格式验证不能替代此门禁。未运行完整 Core/Server suite、本次远端 CI、NativeAOT publish、正式包或 FreeSql 消费者；本批尚未推送。

源码的前四条最小验收及既有 INT/FLOAT 保持已有本地证据；最后一条的首次可安装版本和 FreeSql PR #2234 用发布包验证既有 `Math.Round(decimal)`/`Math.Round(decimal,int)` 仍未完成，版本不预填。不要把本报告写成已发布或全部 Issue 可关闭；发布继续关联 #230。
