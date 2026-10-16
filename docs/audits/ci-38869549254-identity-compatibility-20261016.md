# CI 38869549254：Identity 旧列映射与 JOIN CAST 输入修复

远端 `b568988e5b568ba5bb3abea9574c048a02e0e3f8` 的精确 CI 已完成且保持 failure。Windows rest 和 Ubuntu 各有同一个 Identity 模型差异失败，其他五个作业成功。两平台 EF 295/295、Server 1389/1389 已通过，先前日期映射问题已修复；不能将整轮 CI 写成通过。完整两平台 TRX、Windows 原日志和 run 回读保存在 `artifacts/issue-supervision-20261016/ci38869549254`。

## 负基线与实际修复

在已独立提交 #249 的 `edd23c1d72c472c71ea0b8d4939df0ec5c2311e4` 上，原样例构建零警告错误，完整 Identity 测试为 17/18，精确复现 `HasPendingModelChanges()` 为 true。已有迁移和快照把 `LockoutEnd` 声明为 DATETIME，而新的默认 DTO 映射为 DATETIMEOFFSET。

ApplicationDbContext 调用基类模型配置后显式声明旧 DATETIME。没有修改迁移、snapshot、Identity 业务流程或原测试断言，旧列仍保持 UTC 毫秒精度。转为原生 ticks 类型必须另作显式迁移。

首轮配置修正后迁移检查通过，但完整 18 项出现另一个失败：官方 Identity Store 的 GetUsersForClaimAsync 生成 JOIN 与 `CAST(a0.LockoutEnd AS DATETIMEOFFSET)`，输入裁剪没有遍历 CAST，因而裁掉实际需要的列。该轮仍为 17/18，原失败未覆盖。

RSE 的局部标识符枚举递归进入 CAST 操作数，保持必要列；函数检测同步进入 CAST，避免绕过既有函数谓词下推限制。只新增这两个遍历分支，没有取消列裁剪、禁用优化或放宽类型守卫。新增 INNER/LEFT JOIN 嵌套 CAST 和 CAST 内函数谓词三项，用旧 Core 运行全部失败于缺列，原整类 12/15；修正后整类 15/15，保外连接 NULL、正确结果及实际裁剪列数。

## 验证与边界

- 修正后同一 Core DLL 的全部 SQL 测试 3190/3190，零失败/跳过；三项新增用例包含于该集合，不另累计。
- 最终 Identity 全套 18/18，覆盖原迁移差异断言和官方 Store 的 JOIN，以及原有注册、登录、锁定、删除、Cookie 重开等流程；测试代码未改。
- EF 全套运行和各构建的精确计数、进程、时间、退出码、TRX 与 DLL SHA 见同名 JSON；仅实际通过的结果纳入最终提交。
- 未新增依赖或修改格式版本；catalog16/snapshot4/result6 保持。样例继续使用运行时模型，不声明该宿主 NativeAOT、SMTP/OAuth/Passkey 或正式发行已验收。

本次与 #249 分开提交。最终树须通过完整 solution restore 和原 CI Format 命令后才提交；门禁和提交回执在本地证据目录的 `Gate`、`commit.json`。推送后以新精确 SHA 的 CI 单独验收，不重写原失败 run。外部 ORM 候选没有外发。
