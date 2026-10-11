# M47-P07 候选12实物、分发与原始证据冻结

2026-10-11，源码 `5bcd3b811f2dae5005fc28fd70f6ff934e454c7a`，tree `2cebe49ba46b77bd0fedbad52c1f4156eca4c7c3`，版本 `4.5.0-preview.1.12`。源码已正常推至不可变 `codex/m47-preview12-source`；本地交付分支 `codex/m47-preview12-release`，main 和 parity-results 保持。候选11发布预检失败及所有旧 FAIL/UNKNOWN 保留。

原 P04 run38145580702 / attempt1 success，两 Actions 归档 size/digest 独立通过；ZIP 46,781,896 bytes，SHA256 `27787c8ce6af47327cd54f975e4b6eaede30d66c345aea9c6d051391e88ae824`。290 项库存逐字节、NativeAOT/PE、Server/Web/version/source/profile 和 VerifyOnly 全部通过。

实际套件 172 秒，通过 first/reopen/rollback/legacy 四阶段共37项记录；正常停止、61文件快照恢复与五类错配/端口/权限拒绝通过。Chrome 150.0.7871.115、Edge 155.0.4283.45 来自本轮原 About 记录。独立审计17回执/244身份、零残留任务进程/监听、ACL恢复和两个1000行导出通过；只证明本机Windows R1及合成数据/明示浏览器故障注入，不证明干净机器或生产。

实物243依赖、215项原许可文件引用已核；5项build-only实际无发出模块/二进制，Mapbox 2.0.3 MIT、自有指令源码、四项VC runtime及微软原文/Community用户资格记录/下游保护完整绑定。许可输入审查PASS，整体仍须原P07评审。新候选和接受审计绑定本轮非空实测；保留candidate10历史空窗作为排除记录，不伪造candidate12空窗。

[分发冻结](preview-1-distribution-12.json)只新增外部notes与独立三行checksum，原P04包/清单/checksum不变。[原文库存](preview-1-p07-evidence-12/inventory.json)的18份原JSON以gzip无损保存，原文SHA复核；不归档密码、环境/spec或截图。新远端prepare与P07尚待执行；十二同源门禁正在运行，当前NOT_READY、P08未发布/匿名首跑NOT_RUN、workbench PAUSED。后续结果追加原报告，不改冻结记录。
