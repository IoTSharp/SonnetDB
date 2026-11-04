# 对象前缀首请求：隔离副本验证

此工具打开独占元数据副本，依次读取最多三页，每页三个对象，并从权威 `latest` 和版本记录独立对拍。列表会写派生索引和审计，所以参数必须指向本次验证创建的副本；不能指向原库。

```powershell
dotnet build eng/validation/object-prefix-probe/object-prefix-probe.csproj -c Release /warnaserror
dotnet eng/validation/object-prefix-probe/bin/Release/net10.0/object-prefix-probe.dll <owned-candidate-db> vehicle-recognition flows/2026/11/04/
```

目标目录名必须以 `-db` 结尾，且存在 `object-prefix-probe-owned.txt`。复制目录、核对所有权、配置外部进程期限以及回收副本由调用方负责。工具不获取现场凭据，也不访问网络。

| 阶段 | 固定边界 |
| --- | --- |
| 冷打开 | 由外部进程预算限制，单独输出 `OpenMilliseconds` 和阶段日志。不能计入列表的八秒观察窗口。 |
| 列表 | 每个请求八秒；最多三页、每页三个对象；校验游标推进、原始名称排序、重复和版本身份。 |
| Oracle | 读取整个桶的权威 latest，最多 4,194,304 项、45 秒；仅保留最小 128 个目标前缀键，再读取其版本和删除标记。不复用产品的 Base64 前缀定位算法。 |
| 打开后总期限 | 九十秒，包含请求和对拍。预算不足时保留失败，不把局部结果解释为完整验收。 |
| 关闭 | JSON 先输出，再关闭数据库；只有退出码为零且有 `phase=dispose_end` 才表示本次进程正常完成。 |

打包必须包含输出目录的全部运行时 DLL，以及同名 deps/runtimeconfig JSON。不能只取 Core/Protocol 两个 DLL；`System.IO.Hashing` 与 `System.Numerics.Tensors` 也是当前已有的运行时闭包。现场对照还必须保持各自程序集和依赖身份，不能混用不同版本后称作同源发行物。

SSH wrapper 创建的原生子进程直接继承 stdout，PowerShell `| Out-String` 不能捕获这些字节。运行现场脚本时使用本目录 `Capture-FieldOutput.ps1 -ScriptPath <field-script.ps1> -OutputPath <new-receipt.json>`，由独占 PS7 子进程边界重定向输出。包装器验证非空 JSON、拒绝覆盖已有回执、记录 PID/父PID/启动时间/argv，并在超时或异常时回收本任务子进程树。它已用原生 Cordonix stdout 小输入验证；现场 r2～r7 的原始 stdout 从当前会话执行器记录原文恢复，来源记录在审计目录的 `stdout-provenance.json`。

2026-11-04 的现场验证采用 XFS reflink：先用极小文件验证副本修改不影响来源，再固定元数据文件集合、源 inode 和活跃 WAL 捕获长度；只在独占副本中运行。原始记录和失败尝试见 `docs/audits/object-pagination-20261104/`。大文件未全量计算 SHA，完整语料指纹继续单独保留边界。

这是受限的托管验证工具。内部测试钩子的反射计数不构成 NativeAOT 验收；三页正确也不代表高变更率全桶分页、容量、全部模型恢复、现场部署或 168 小时长稳已完成。
