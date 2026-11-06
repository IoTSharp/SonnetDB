# 共同 Provider 候选三线验收与发行评审

2026-11-07（Asia/Shanghai）。候选应用验收 **PASS**，正式发行继续按用户决定延期。公开 NuGet.org-only 新包消费 **NOT_RUN**，#392/#394/#395 继续 OPEN；实际完成的关联 issue 已核对关闭状态，见文末。

## 冻结身份

- source：`eb5c0e5f938372109b473db2194670761dc26a96`，基于全部产品分支整合后的 main `537dc9bb`；[PR #414](https://github.com/IoTSharp/SonnetDB/pull/414)。本次只增强消费者和证据，没有修改产品公共代码。
- 候选版本：`5.1.0-rc.20261107.1`；四包为 `SonnetDB.EntityFrameworkCore`、`SonnetDB`（`SonnetDB.Data.dll`）、`SonnetDB.Core`、`SonnetDB.Protocol`。
- 本地 feed 与 CI feed 是两组实际构建的制品，各有独立包/DLL SHA。两组均核对 nuspec commit、空缓存来源、assets 的 package 类型、实际输出与已加载 DLL；不能交叉使用摘要。
- [Publish 42265079795 attempt 1](https://github.com/IoTSharp/SonnetDB/actions/runs/42265079795) SUCCESS，生成十包及 Windows/Linux 实际发行物；本应用消费其中四包，不声称逐项消费十包。
- CI `win-x64` NativeAOT Server 产品版本为 `5.1.0-rc.20261107.1+eb5c0e5f938372109b473db2194670761dc26a96`，执行文件 SHA256 为 `363A9795A57EB574334BBEBB7E50F8C309FB8985A1716648490C0E23E192364A`。原始 full archive SHA256 为 `041B4B36A6CE01C87457CF1249B2572FE55CCB7C314CC0D90DCA65DEF5875E1A`。
- 完整身份、四包/DLL及原始归档摘要见[机器清单](provider-candidate-20261107.json)。旧 `0d687bd9` 候选结果未用于本轮通过判定。

## 两组独立消费结果

三位 worker 先并行准备合同和消费者，再交叉审查；所有还原、构建、运行由主监督持同一 `FileShare.None` 重负载锁串行执行，CPU2、MSBuild单节点、禁持久构建服务。消费者全部移出仓库，每个应用各有独立 NuGet.Config、packages、http-cache，候选 source mapping 不允许 SonnetDB 包回落公开旧版本。

| 应用线 | 每组实际步骤 | 本地四包/JIT Server | CI四包/NativeAOT Server |
| --- | --- | --- | --- |
| #392 库存迁移/恢复 | 34：还原/构建、三协议 roundtrip/独立重开、三组 after-ACK 强杀/Down/Up、两远程 Server 重开读回 | PASS | PASS |
| #395 JSON补货 | 22：还原/构建、三协议各六个独立进程、两远程 Server 重开读回 | PASS | PASS |
| #394 OwnsMany跟踪 | 13：还原/构建、三协议 seed/mutate/verify、两远程 Server 重开读回 | PASS | PASS |
| 总计 | 每组69步骤，全部status=PASS | PASS | PASS |

每组有3个预期强杀步骤退出 `-1`，其余66步骤退出0；不能表述为69个进程全部正常退出。所有强杀 marker 均同时匹配 after-ACK、PID与创建时间。两组各有两代唯一归属Server，结束回收；实际日志分别证明 HTTP/1.1 REST SQL 与原生 `HTTP/2 POST /v1/frame`，没有以配置的协议名称替代线传输证据。

- [迁移专题](../audits/provider-candidate-20261107-migrations.md)：真实历史CHECK失败后完整DDL/DML回滚，同一显式打开连接 `opens=1/closes=0`，幂等重试、Down/Up及独立重开。包级强杀只证明after-ACK，内部提交/恢复阶段强杀由本轮精确源码CI的原始TRX分别记录。
- [JSON专题](../audits/provider-candidate-20261107-json-update.md)：25参数单条UPDATE、路径/值参数化，库存12→14但RHS下限15和旧供应商；准确写前拒绝及越界原子失败；两独立并发者影响行数恰[0,1]。
- [集合专题](../audits/provider-candidate-20261107-ownsmany.md)：根/嵌套集合插入、读取、增删、元素变更、null/空/转换器；父ordinal变化后EF10 identifying-key写前拒绝，Clear/重载后核对准确目标7和相邻项1，再继续保存；显式并发实际影响0行。

EF应用是JIT PackageReference消费者。匹配Server为NativeAOT，不因此宣称EF Provider NativeAOT已经通过。旧5.0的历史负控和部分迁移状态不被本轮追溯改变；旧库须停写备份，对账schema/业务/history，恢复一致备份或执行经审查的修复，不能盲补history或盲重试。新段格式v7的回退继续要求与旧程序匹配的升级前备份。

## 门禁与剩余工作

最终完整restore、规定Format及`git diff --check`均已通过。精确候选的12类发行工作流与原始Parity已由现有 `verify-release-readiness.ps1` 独立核验为 **12/12 READY**，机器清单记录各run，不以PR绿色代替发行事件合同。手动CI `42268465828` 的27份原始TRX已下载并解析：Ubuntu/Windows Core各8131、EF各438、Server各1603，另行迁移强杀各10，全部通过且零失败/未执行；Windows Studio90/90通过。scheduled七天Parity为6/7，按既有政策记录非阻断观察，不冒称固定硬件或长期生产验收。

正式tag、NuGet/Release上传、生产部署/重启/迁移本轮未执行。正式发行恢复前，还须审查本次准确source/制品和当前授权；公开后使用仅NuGet.org的全新缓存与匹配正式Server重跑三线，再按真实最低公开版本判断母issue关闭。物理掉电、固定现场硬件和生产验收仍为NOT_RUN。

已只读核对#227/#266/#194及此前#367/#393/#400/#407均为CLOSED/COMPLETED；没有重复关闭。#230仍缺上游合入/公开Core和Provider/全公开消费者，独立保持OPEN。#392/#394/#395的公开门未满足，本轮新增关闭0；不为关闭issue修改原验收合同。

原始证据在`artifacts/provider-next-phase-20261107/`，本地与CI消费者收据分别在机器清单列出的仓库外目录。首轮调度变量冲突和CI归档入口定位失败保留；修复的是调度脚本，未删除断言、skip或放宽业务门禁。Cordonix最终Job回收后所属进程0，原始制品、日志、TRX和独立缓存作为验收证据保留。

第一顺序收口后，下一单一工作包冻结为#397 typed `GetAndDeleteKvAsync`：复用已有GetAndDelete opcode，不将它包装成无旧值响应的纯Delete。独立SDK提交/候选及其预算、未知写入、原生HTTP2/TLS、重开与公开消费门另行验证，不混入本Provider候选。
