# #247 SqlSugar 读取器输出参数探针

`ReaderOutputProbe.cs.txt` 通过真实 SqlSugar `UseStoredProcedure` 执行 23 个场景。准备一个独占运行目录，放入匹配版本的 `SonnetDB.Core/Data/Protocol`、固定 SqlSugar 核心/原版或候选 provider，以及 `System.IO.Hashing`、`System.Numerics.Tensors` 和 SqlSugar 的实际运行依赖；不能把缺少依赖而未进入用例当成消费者通过。

从此目录执行 `dotnet build ReaderOutputProbe.csproj -c Release -p:RuntimeDirectory=<绝对运行目录>`。应用候选补丁的目录是固定 SqlSugar 源码 `65e8226ce25a8e1cb9cca2590683f1fd6778cab3` 中的 `Src/Asp.NetCore2/SqlSugar.SonnetDBCore`，使用 `git apply --check --unidiff-zero` 检查、再使用相同选项应用 `sqlsugar-reader-output.patch`。只重编 provider，保持原 ORM 核心 DLL 不变。

探针参数为连接字符串和结果标签。远程连接字符串可通过 `ISSUE247_CONNECTION` 环境变量提供，此时首个参数使用 `--connection-env`。分别验证 embedded、显式 `Protocol=rest` 和 `Protocol=frame-http2`；后者必须从产品 Server 请求记录核对实际 HTTP/2 和 Procedure opcode 3，不能只依赖标签。

原版三协议均为 9 PASS / 14 FAIL；候选三协议均为 23 PASS / 0 FAIL。23 个场景中 provider 复用场景执行两次调用，因此成功候选共 24 次过程请求。上游提交、稳定包/Server 发行和最终整合 SHA 的 CI 仍独立验收。详见 `docs/audits/issue247-consumer-20261019.md` 和外置哈希/进程/网络回执。
