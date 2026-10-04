# KV 物理读取预算

不可变 KV state 的按位置读取按数据库共享请求预算。默认同时执行 8 个请求读，最多等待 64 个，单次等待许可最长 5 秒。可从服务端配置调整：

```json
{
  "SonnetDBServer": {
    "Kv": {
      "MaxConcurrentStateReads": 8,
      "MaxQueuedStateReads": 64,
      "StateReadWaitTimeoutMilliseconds": 5000
    }
  }
}
```

服务端分别约束到 1–256、0–4096、1–120000 毫秒；不支持无限等待。Core 允许零等待，但拒绝负数或超出 `SemaphoreSlim` 毫秒范围的值。队列容量为零时，只允许立即拿到许可的请求。

排队满或等待超时返回 `kv_read_overloaded`。尚未开始响应的 REST 请求使用 HTTP 503 和 `Retry-After: 1`；SQL NDJSON 与 Frame 已开始发送后以协议错误终止当前操作。客户端应短暂退避并收敛查询范围。写入请求遇到该错误时仍需核对幂等身份与实际结果，不能假定整个批次没有执行。

checkpoint 的旧 state 合并显式使用每数据库独立保留的一个维护读槽。因此总物理读并发上限是配置的请求读并发加一；请求过载不会拒绝 checkpoint。维护读有独立 120 秒等待时限并观察取消，超时抛 `TimeoutException`，沿用 checkpoint 失败处理和后续重试。旧快照与 WAL 保留，错误不会标记为永久写故障；持续卡死的维护 I/O 仍可能暂时阻止 checkpoint，须通过维护超时指标排查。该期限只覆盖等待许可，不代表操作系统文件 I/O 具有 120 秒总截止时间。文件引用仍保护 CRC 和返回值期间的文件生命周期，而 I/O 许可在 CRC 计算前归还。

指标由 `SonnetDB` Meter 输出，启用现有 OpenTelemetry/Prometheus 导出后可观测：

| 指标 | 意义 |
| --- | --- |
| `sonnetdb.kv.state.read.active` | 活跃物理读，`kind=request/maintenance` |
| `sonnetdb.kv.state.read.queued` | 等待读许可，`kind=request/maintenance` |
| `sonnetdb.kv.state.read.rejected` | 拒绝累计数，`reason=queue_full/wait_timeout` |
| `sonnetdb.kv.state.read.maintenance.timeouts` | 维护槽超过独立等待时限 |
| `sonnetdb.kv.state.read.wait.duration` | 等待物理读许可耗时 |
| `sonnetdb.kv.state.read.duration` | 实际按位置读取耗时 |

调优应先对比实际读取与许可等待时延。如果实际读取短但许可等待长，应先减少重复全表扫描和提高有硬件依据的并发；如果实际读取本身长，应检查设备 I/O 时延和读取量。单独提高队列容量只会容纳更多等待者，不增加吞吐。本文描述代码合同，不代表已经取得特定生产机器上的性能验证。
