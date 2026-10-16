# Native 生命周期历史证据

对应已推送首片 `1d02c1c5f410b2ca90e83c56fd9993e52c1f77f4`，早于 D01 程序集拆分。

`native-final.trx.gz` 解压后是原最终 TRX 的字节一致副本，SHA256 为 `8FBB40A11F962EF5A3EBF58DE8D9B96F5D262CDFE9F07210EE81AABE2BAF8C28`。原 Windows 换行保持，入库压缩文件的 SHA 在 index 中单列。原冻结文件把 XML Counters 节点序列化成了空数组；保留原文件和原 FAIL，`acceptance-supplement.json` 用明确 XML 命名空间和属性读取补充 total/executed/passed=10，其余指定计数为 0，并绑定原源码、DLL/PDB、TRX 和包/库身份，没有重跑测试。

补充摘要中的绝对路径和 `verifiedCurrentPath` 表示**旧机器补充采集时**的状态，不是换机后的可读路径。`D:/t/m49-01a1440e/consumer-final-build/Release/sonnetdb_result_lifetime.exe` 已回收，只保留历史运行哈希；旧机器曾保存哈希相同的审计副本，但该 EXE、AOT 库和工程包本体不随 Git 提交。`c-consumer.stdout.log.gz` 与 `portable-index.json` 保留实际运行输出、时间/退出码和库身份关联；运行时模块哈希没有另行采集，不作该项声明。

这些历史 Windows x64 AOT/C 结果不升级为 D01 新源码的 AOT 验收。新 D01 的托管 Native 回归另在相邻 D01 证据目录登记。
