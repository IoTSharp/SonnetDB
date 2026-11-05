# 对象下载取消测试的断连观测时序

PR #405 的 `10fc9e33cdd5b75bf61468c335ef0473f48cd116` 在 Ubuntu 和 Windows rest 都只有 `ObjectTransferCancellationTests.Download_InFlightCancellationOrDisconnect_ReleasesResponseAndPreservesFile(disconnect: false)` 失败：第91行等待 `Aborted` 五秒超时。两个平台 Core 8073、EF 360 均通过；此处不是把压缩算法测试失败归类为网络问题。

旧fixture在首64B后暂停。客户端取消后释放暂停，余下约3MiB可能整体进入套接字缓冲，使handler和唯一RequestAborted观测registration先于真实断连完成。同时，旧暂停等待直接用RequestAborted令牌取消，其取消延续也可能先于观测回调退出并注销registration。

修复仅修改该测试fixture：暂停释放后至多再写/flush一个4KiB探测块，保留剩余正文未完成；等待唯一真实RequestAborted回调后再退出。等待采用用例自身有界deadline，不用被观测的令牌提前结束；探测写入的I/O/取消异常在回调发生后原样重抛。没有修改产品传输代码或伪造Aborted成功信号。

保留原先64B、3MiB、五秒、真实RequestAborted/Exited、目标文件不变、临时文件清理及后续完整下载checksum等全部断言。真实CI失败日志与本地复跑、完整restore/规定Format结果保存在canonical `artifacts/branch-cleanup-20261105/`；Linux、Windows和远端CI按实际通道分别记账。本修复使用独立PR，不混入#367压缩PR。
