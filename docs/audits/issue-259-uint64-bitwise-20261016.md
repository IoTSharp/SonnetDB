# #259 完整 UInt64 按位与/或实现与验证

日期：2026-10-16，Asia/Shanghai。工作树基线：`cfa92314b5eeb152f358adc1ea99c177cb6b6a91`。根监督代理按 SHA 同步 #261/#260 冻结依赖后，#259 增加两个原生等价函数。此报告记录候选源码与真实消费者，不表示正式发行或 Issue 已关闭。

## 实现结果

新增 `bit_and_uint64(left,right)` 与 `bit_or_uint64(left,right)`，完整覆盖 0..18446744073709551615。两者直接用 UInt64 执行按位运算，结果以现有 Decimal 精确承载，执行前声明为 **DECIMAL(20,0)**。结果不会经过 Double 或有符号 Int64 转换，没有新增 UInt64 存储类型、文件格式或 XOR 接口。

原 Issue 明确允许整数 Decimal 的原生等价函数。因此旧 `&`、`|` 保留 Int64 合同：有符号负值语义不变，Decimal 操作数仍拒绝。等价写法例如：

```sql
SELECT bit_and_uint64(CAST('7' AS DECIMAL), 3); -- Decimal 3
SELECT bit_or_uint64(CAST('7' AS DECIMAL), 8);  -- Decimal 15
SELECT bit_and_uint64(
  parse_uint64('18446744073709551615'),
  parse_uint64('9223372036854775808'));         -- Decimal 9223372036854775808
```

`parse_uint64` 属于前置 #260；本项不重做其解析器。

## 输入、输出与失败合同

| 输入/行为 | 合同 |
|---|---|
| SQL INT | 接收非负 Int64 |
| SQL DECIMAL | 必须是精确整数且在 0..UInt64.MaxValue；`7.000` 可接受，`7.5` 拒绝 |
| helper 的 CLR 整数值 | byte/sbyte/short/ushort/int/uint/long/ulong，带符号类型须非负；Decimal 依前述规则 |
| FLOAT/Double/Single | 即使 `7.0` 也拒绝，不用浮点推断原整数 |
| 字符串、Bool、Blob | 拒绝隐式转换；文本可先调用 #260 的严格 parse_uint64 或显式 DECIMAL CAST |
| 负值、小数、溢出 | 明确抛出含函数名与完整允许值域的错误，不截断、不取模 |
| NULL/DBNull | 任一函数输入值为 NULL 则返回 NULL；此时不再校验另一输入值。嵌套表达式仍按既有 SQL 求值规则先执行 |
| 参数个数 | 必须恰好 2 个，由内建函数注册器校验 |
| 输出 | Decimal，scale=0；类型声明 precision=20、scale=0；左右输入合并可空性 |
| 空结果/全 NULL | 在读取首行之前提供 Decimal 类型与精度/scale，不靠首行猜测 |
| 同名 UDF | 保留用户函数覆盖，不误写内建声明；两个输入各求值一次 |

函数处理完整数值域，不等于改变所有 ADO CLR UInt64 参数行为。现有 binder 对 `ulong <= Int64.MaxValue` 可绑定；更大的裸 CLR UInt64 在嵌入式出现 OverflowException、远程出现 NotSupportedException。本次不修改 binder。完整范围应使用 `(decimal)value` / `DbType.Decimal` 参数；Chloe 原适配器已经使用字符串参数加 `CAST(@P AS DECIMAL)`，候选继续该路径。

无新依赖、unsafe、反射 JSON 或 AOT 警告抑制。只新增函数 helper、声明 helper，以及注册表与执行器入口；存储、传输 codec 和参数 binder 不变。Core 生产项目本来启用 AOT/trim 分析，最终生产编译零警告/错误；未执行 NativeAOT publish 或运行验证。

## 原始失败与最终验证

原引擎上的三项初始测试为 **2 FAIL / 1 PASS**：新函数及其空结果声明不存在；既有 Int64 位运算及 Decimal 拒绝控制通过。保留 `baseline-core-tests` 日志与 `baseline-results/baseline.trx`。首轮候选相同三项全部通过。

扩展 Core 组合 **145/145** 通过，零失败/跳过，包含本项 helper/SQL、既有表达式、#260 解析和 Decimal 声明回归；不与首轮 3 项累计。最终远程专属 **9/9** 通过：三协议同步/异步的六个旅程，以及三协议裸 CLR UInt64 既有上界控制。

六个旅程各覆盖：30 组参数值/掩码的与/或精确对拍、0/7/2^53+1/Int64.MaxValue/2^63/MaxValue、NULL 参数、常量非法输入、parse_uint64 组合、空表及空 JOIN 声明、全 NULL、持久 Decimal 列、WHERE、成功 UPDATE、多行 INSERT 失败原子性、UPDATE 失败原子性、事务回滚以及宿主/数据库重开。失败原子性通过读取所有行及原值确认。请求记录确认 REST 的 HTTP/1.1 和 h2c 的 HTTP/2；Frame 连接的写入/事务控制面沿用 REST 路由。

首次远程测试曾 **0 PASS / 6 FAIL**：测试错误地把超出 Int64 范围的裸 UInt64 当成已支持的原生参数。错误结果完整保存在 `remote-bitwise-tests-1.*` 和 `results/remote-bitwise.trx`。后续按本项 Decimal 等价合同修正参数，同时新增三项控制固定原有裸参数边界；没有改生产代码掩盖失败。

完整证据命令及计数见同名 JSON。关键收据标签：

| 标签 | 结果 |
|---|---|
| `baseline-core-build` | 零警告/错误 |
| `baseline-core-tests` | 2 FAIL / 1 PASS，原始缺口 |
| `candidate-core-build-1` / `candidate-core-build-2` | 零警告/错误 |
| `first-candidate-core-tests` | 3/3 |
| `core-bitwise-tests` | 145/145 |
| `candidate-server-build-1` / `candidate-server-build-2` | 零警告/错误 |
| `remote-bitwise-tests-1` | 0/6，错误参数假设保留 |
| `remote-bitwise-tests-2` | 9/9，零失败/跳过 |
| `format-server-includes` / `format-core-includes` | 本项文件 whitespace verify 通过 |

邻接远程组合 **36/36** 通过，见 `server-adjacent-tests`，与 9 项专属回归重叠，不累加。没有运行完整 Core/Server 全量、性能或长稳。#262 旧 DateTimeOffset 断言由根代理另行修正，本项未改其测试或生产代码。

## 固定 Chloe 的实际消费

重新读取 PR #435，head 固定为 `bb7ec8735bd4511c35453eb009f70cc606795703`；源码来自相同 SHA 的干净 git archive，归档 SHA 为 `FDD72DD90965C6271F49C15428AECE05F2EC383235C43D4CCE0975D1D8FD9E87`。

原始 provider 在嵌入式/REST/h2c 各 **3 PASS / 13 guard FAIL**，总计 9 PASS/39 FAIL。三项原有精确存取、比较、整数除法不受缺口影响。原来 13 项按位表达式在翻译阶段明确抛出指向 #259 的 NotSupportedException，原始保护及失败日志保留。

单文件最小候选只修改 `SqlGenerator.cs` 的 `VisitBitAnd`、`VisitBitOr`：识别 UInt64 表达式时输出 `bit_and_uint64(left,right)`、`bit_or_uint64(left,right)`；其他类型继续原翻译。无存储映射、参数规则、客户端值计算或其他 Issue 补丁。

候选三协议各 **16/16 PASS**，总计 **48/48**。实际执行包括参数掩码、列/列、常量、NULL、嵌套、最高位过滤、异步、空结果及 UPDATE；生成 SQL 中的新函数名也作了断言。原始/候选使用完全相同的消费者及 Core/Data/Chloe DLL，只替换 Chloe.SonnetDB.dll，避免消费者改动混淆适配效果。运行中打印的路径/信息版本/SHA 以及候选源码、补丁 SHA 收录在 JSON；程序集信息版本反映构建环境，不充当 Chloe 固定源码 SHA。

补丁 `eng/probes/issue259-uint64-bitwise/chloe-native-uint64-bitwise.patch` 已在原始文件上 `git apply --check` 和实际 apply，结果字节 SHA 与编译候选完全相等。复现工程、脚本、README 随交付。Server 是实际独立进程，两个协议分别使用动态回环端口；每次运行 Server 均确认退出，保留进程与 stdout/stderr 收据。

第一轮外部 Chloe 构建错误继承了上级 SonnetDB 的 IsAotCompatible，产生 **195 个 IL 错误**，记录为 `chloe-original-build`。在外部归档根加入独立空 Directory.Build.props/targets/Packages.props，恢复 ORM 自己的非 AOT 构建边界，再以 `Nullable=annotations` 和 `TreatWarningsAsErrors=true` 构建原始及候选，均零警告/错误。未压制 IL 警告，也不据此宣称 Chloe 支持 NativeAOT。

## 交付状态和剩余条件

本项六份生产/测试文件：FunctionRegistry.cs、RelationalSelectExecutor.cs、SqlUnsignedBitwiseFunctions.cs、RelationalSelectExecutor.UInt64Bitwise.cs、SqlUInt64BitwiseTests.cs、RemoteUInt64BitwiseTests.cs。前两文件包含根同步的 #261/#260 前置增量，根应按顺序集成，不能覆盖后续 #256 修改。四份生产文件已提前冻结并交根同步；最终清单列出本项探针/文档与全部 SHA，前置 11 文件同步收据单独保留。

本子代理不修改共享 HANDOFF/CHANGELOG，不 commit/push/外发或关闭 Issue。根负责最终主树的完整 `dotnet restore SonnetDB.slnx` 和原 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`，通过后单任务提交、批次推送及原验收核对。局部 whitespace 与本报告不能替代完整提交门禁。

引擎和本地 Chloe 候选已实现验证，**正式 Server/Data 包及 Chloe 适配发布仍待完成**。没有把源代码候选称为已发布版本，没有将 48 个消费者场景与 xUnit 用例混为总测试数。
