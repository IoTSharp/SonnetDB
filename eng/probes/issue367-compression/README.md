# Issue 367 压缩切片探针

固定生成五组 8192 行 `(time, value FLOAT64, quality INT64)`：常数质量、每256行切换质量、交替质量、递增整数及完整 Int64 极值游程。`value = 100 + (i % 100) * 0.01`，时间间隔1000ms。时间戳和值都显式开启现有 Delta 选项，`FsyncOnCommit=true`，报告完整 `.SDBSEG` 文件和两字段的物理载荷字节。

`dotnet run --project eng/probes/issue367-compression/CompressionProbe.csproj -- write <全新输出目录>` 生成候选段、校验全部点/跨游程窄范围，并写 `measurements.csv`。进程内预算90秒，五组固定样本；外部仍须使用有界执行器并限制 CPU/构建并发。

比较基线时，将修改前的真实 `SonnetDB.Core.dll`、`SonnetDB.Protocol.dll` 及其原有 `System.IO.Hashing.dll` 依赖保留在独立目录，使用 `-p:LegacyCoreDirectory=<该绝对目录>` 构建到独立 `--output`，随后直接运行该目录的探针 DLL。不能只修改段版本号就声称旧引擎已验收。探针 `read <文件>` 验证实际旧段读取，`read <v7文件> reject` 验证旧 reader 明确拒绝新版本。编译基线与候选时必须使用不同输出目录，避免程序集混用。

新段为v7，旧DeltaValue Int64载荷仍保持raw；新RLE编码使用独立标志，旧格式不能改标志假装升级。升级前应关闭数据库并完整备份；旧引擎回退需恢复备份，升级后的新写入另行导出/回补。该切片不改变通用FileHeader/WAL/目录格式，不表示共享时间戳、浮点新算法、WAL压缩或真实生产压缩率已完成。计时是本地单轮观察，不是P95/P99或CPU门禁。
