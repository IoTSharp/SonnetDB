# 时序批次接收与背压（M36 #314）

`SndbTimeSeriesWriter` 在接收输入时限制批次大小，随后复用现有 Core / Frame / REST 写入路径。`BatchSize` 控制单次传输点数；新增 `MaxBatchPoints` 控制一次 `WriteBatchAsync` 调用可以接收的点数，默认 8192，范围 1～65536。超过上限时，整个调用在入队前抛出 `ArgumentOutOfRangeException`，不会写入已枚举的前缀。

```csharp
using var client = new SndbTimeSeriesClient("Data Source=./data");
await using var writer = client.CreateWriter("cpu", new()
{
    BatchSize = 512,
    MaxBatchPoints = 8192,
    MaxPendingBatches = 4,
});
var result = await writer.WriteBatchAsync(points, cancellationToken);
await writer.FlushAsync(cancellationToken);
```

枚举最多读取上限加一项，以区分刚好达到上限和超限。等待接收资格、每次 `MoveNext` 前后以及进入队列时均检查取消；枚举失败、超限或取消都会释放枚举器，并且不接收部分批次。空输入也遵循取消和 writer 已释放检查。

`MaxPendingBatches` 同时限制等待队列中的批次数，以及正在物化或等待入队的生产者数量。尚未取得接收资格的生产者不枚举输入；队列、当前处理批次和正在接收的批次各有点数边界。这是点数与批次数的边界，不是字节上限；单点中字符串、向量的大小仍由既有点与传输合同约束。同步 `IEnumerable` 的单次 `MoveNext` 必须由调用方保证能返回，取消无法抢占任意同步用户代码。

`DisposeAsync` 停止接收新请求并排空已进入队列的批次；还在枚举或等待接收的调用不算已接受。关闭信号直接唤醒尚未取得接收许可的调用并报 `ObjectDisposedException`，不会让其等待另一生产者的同步枚举返回；已经进入同步用户枚举的调用在下一检查点拒绝继续接收。正常批次仍按输入顺序返回逐项错误，并按 `BatchSize` 拆分传输。超出新默认上限的调用需拆批，或将 `MaxBatchPoints` 调整到不超过 65536；这一限制是明确的行为收紧。

本地回归覆盖无限枚举的有界拒绝、精确上限、拆块与逐项错误、枚举失败、取消、空输入、释放后调用以及并发生产者背压。代码合同与本地回归已完成；嵌入式自动化结果和 AOT 分析不替代远程服务 parity、固定硬件容量或长期证据，这些内容转入真机验证待办。
