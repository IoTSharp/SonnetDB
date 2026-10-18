# Issue #245：真实 SqlSugar Fastest 消费者验收

固定 SqlSugar 源码 `65e8226ce25a8e1cb9cca2590683f1fd6778cab3`，官方源码 ZIP SHA256 `46615584F90E8E7857A91140130581F9E4C3D7E7AE20B48A9C9F05383633D766`。原版与候选使用同一 SqlSugar.dll，SHA256 `990DDF0A71E0F0B25415E319451EF7110E08F35C2C981D02CC8E78F9853EAE8E`。候选仅修改provider的FastBuilder，不修改ORM核心，未把逐条更新模拟为批量能力。

原版支持一万行普通工作表集合更新，但工作表在执行期间能被另一连接看见，不满足原生TEMP/会话隔离。候选改用空集TEMP CTAS复制输入列声明，不继承主键、默认值或自增；继续通过原有Fastest批量导入、集合UPDATE/INSERT和DROP生命周期执行。

| 消费者 | 嵌入式 | REST | 实际HTTP/2 |
| --- | --- | --- | --- |
| 原版：一万行BulkCopy/BulkUpdate | 2/2 | 2/2 | 2/2 |
| 原版：原生TEMP/会话隔离负控制 | 2项FAIL | 2项FAIL | 2项FAIL |
| 候选：完整10场景 | 10/10 | 10/10 | 10/10 |

候选包括调用方已有事务内一万行BulkUpdate及提交/回滚；观察者在提交前保持原数据，Rollback恢复原数据，没有隐式提交。另验证同步BulkUpdate、BulkMerge更新与插入、真实复制前失败后的无目标变更及临时表清理。每协议6个实际TEMP工作表，均对所有者可见、对另一连接不可见，完成/失败后均清理。

HTTP/2候选共有107次实际请求，其中14个原生Query Frame，所有观测请求均为HTTP/2，没有HTTP/1回退。事务控制、写入及metadata使用既有HTTP/2 REST端点，不能称为纯Frame协议。

首次外部provider构建意外继承仓库中央包管理，NU1008失败保留；隔离外部构建配置后成功，4项原有nullable告警保留。探针与宿主各0warning/0error。首次两行试跑1 PASS/9 FAIL，发现Windows长TEMP根导致CreateFileW无法打开超过260字符路径；根a7861a0b修复原生扩展路径且保持fsync/I/O失败合同，实际回归34/34。两次中间试跑6 PASS/4 FAIL来自探针未重新启用Fastest关闭的逐SQL日志，观测/失败注入没有发生；修正观测后两行10/10，再以10000行执行上述全部正式规模测试。所有旧失败仍在证据中。

入库后的首次探针构建又暴露外部ORM宿主意外继承生产IsAotCompatible分析，物理DLL诊断Assembly.Location触发IL3000；失败保留。本目录恢复与实际消费者相同的本地隔离构建配置，生产项目AOT/trim要求保持。这里的构建不计作SqlSugar或产品的AOT验收。

验证运行DLL对应19b59165的连接生命周期及a7861a0b的fsync源码；构建时前者informational version仍为19b59165，固定外部ORM/provider还继承了外层Git版本后缀，因此以固定源码、文件SHA及运行DLL SHA为准，不以该后缀冒称来源。root和各子进程均有有界执行/清理回执，结束时无残余任务进程。

外置证据为 `D:/source/SonnetDB/.codex-temp/sync-20261019/issue245-fastest-evidence/manifest.json`，SHA256 `81CA1623363D82D0FEAFCF3F9E297EB0657AC09BD4EC05C49D3AE201D743630C`，保存46项源、原版/候选日志、网络记录、失败及运行时哈希。最小补丁已严格check并实际应用，规范化字节与验收候选相同。稳定NuGet/Server包验收、上游补丁发布、最终SHA CI均为NOT_RUN；本页不关闭Issue。
