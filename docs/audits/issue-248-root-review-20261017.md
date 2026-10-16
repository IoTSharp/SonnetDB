# #248 主线人工整合复审（2026-10-17）

基线为已合并 M49 和在线演示分支的 `8f28c82f`。根核冻结 manifest `850E256D4C1078140B1E12AE4590CDCA27E64F580FEA8F9B8CEC6DD3E0B05736`，40 源、257 证据、837 运行文件与原 SqlSugar 603 源逐项 SHA256 无差异，原 Core3986/Server342 的 TRX 全通过。只按40文件清单三方集成；Tsdb 同时保留在线配置的 RuntimeOptions，50f RSE/Identity 修复和 M49 Protocol 拆分保持。

根在当前组合源码实际执行：完整 solution Release build 零警告错误；Core **7606/7606** 零失败跳过；Data 显式 AOT/Trim 与 Server Rebuild 零警告错误；原 SqlSugar 新编译消费者 embedded/REST/h2c 各 **34/34**。回读消费者 Core/Data 与整合树测试输出一致，服务端完整输出独立复制，保留各请求/协议及运行 SHA。机器级日志在 `artifacts/integration-20261017`，根摘要与绑定见同名 JSON。

完整 solution 测试仍由本次人工任务继续；首轮 Accuracy 的 Docker 上下文忽略规则失败独立留档、单独修复，不将其混入本 Issue。其它项目和最终 CI 以最终整合审计为准。

合同：显式事务内单关系基表 FOR UPDATE [NOWAIT]；NOWAIT 冲突稳定立即失败，普通等待仍可取消/超时；写者、COMMIT、DDL/索引/identity 和主键迁移共享锁生命周期；稳定表 token、锁后 sequence/谓词/排序复验，连接/会话结束释放。轻事务仍乐观缓冲、COMMIT复验；无 gap/phantom 保护，DDL/延迟触发器 COMMIT 使用保守库级锁。REST/h2c 事务仍为既有 JSON 事务接口。未做正式发包、上游 ORM 发布、NativeAOT 发布运行或断电；#233 的全部 Chloe/NoLock 条件不在本项完成声明中。

提交前在最终待提交树重新完整 restore 和规定 Format；只有两者真实通过且 tree 未变才独立提交。CI/远端与关闭回读随最终人工整合审计保存，定时监督保持 PAUSED。
