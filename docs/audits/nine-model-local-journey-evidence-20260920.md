# 九模型本机最小旅程证据

记录日期：2026-09-20。此记录对应 M36 #310/#311；机器可读输入、步骤覆盖和结果状态见 [journey matrix](nine-model-journey-matrix-20260919.json)。

## 已执行本机切片

时间序列、关系 SQL、Document、全文、向量、对象、MQ 和 Graph 的矩阵所列精确 xUnit 方法共 23 项通过。它们使用临时本机目录和小型确定数据，分别覆盖模型原生的写入/读取或查询、局部分页或订阅、更新或删除、失败或取消，以及可用时的重新打开和结果对账。

KV 协议旅程运行了 11 个 case：embedded、REST 和 auto 的 8 个 case 通过；3 个 `frame-http2` case 失败。失败发生在任何模型操作验证之前，原因是精确 HTTP/2 请求无法建连，稳定错误码为 `frame_transport_error`。因此 HTTP/2 Frame 在本机记录为 `failed`/`NOT_READY`；REST 或 auto 的成功不替代该结果。

矩阵结构与证据引用也已运行：`NineModelJourneyMatrixTests` 通过（1 项）。

## 边界

这些结果是本工作树的本机 Core/测试宿主证据。它们不代表远程 Parity、真实外部 provider 或 broker、固定硬件性能、干净 Windows 安装、备份恢复、七天 scheduled 窗口，或生产门禁。九模型总体仍为 `partial`：矩阵中未列出的 required closure steps 仍待单独验证。
