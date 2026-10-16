# CI 38862841705：原生日期偏移映射兼容修复

基线 `67a5c8c8d64c36002aee5ebcfcb40634e7d5f482` 的完整 CI 最终为 failure：Windows rest 和 Ubuntu 失败；Windows Core、三平台 NativeAOT 发布和 Format 成功。原失败 run、两平台完整 TRX 和日志保留；本修复不改写旧 CI，也不并入 #249。

## 实际失败与负基线

两平台均为同一 EF 用例失败（291 PASS / 1 FAIL）及四个 Server 用例失败（1385 PASS / 4 FAIL）。Ubuntu Core 7472/7472 已通过。原始 TRX 逐项计数、失败正文及 SHA 保存在同名 JSON。下载首次 EOF 及终端转义输出失败也保留，不计产品测试。

- EF 将 DTO 属性映射到旧 DATETIME；#251 默认参数和函数已采用原生 DATETIMEOFFSET，比较触发正确的引擎类型守卫。旧源码独立构建零警告/错误，原失败用例本地 0/1 复现。
- 旧远程 DataTypes 断言仍为 11；DURATION 已使目录达到 12，同时发现目录确实漏报 DATETIMEOFFSET。不能只把断言调到 12。
- 旧远程有界 DELETE 将默认 DTO 参数用于 DATETIME 列。直接运行提交前冻结 DLL，四个远程失败本地 0/4 复现。

## 修复合同

EF 默认 DTO 属性改用 DATETIMEOFFSET，原生参数与带 CAST 的 SQL 字面量保 UTC 完整 ticks；DateTime 映射不变。显式 `HasColumnType("DATETIME")` 保留旧存储布局，读取/谓词/函数引用对该列生成显式 CAST 进入原生表达式类型，写入仍只有旧毫秒精度。查询 CAST 可能影响索引选择，不隐式迁移已有列，不恢复丢失精度，也不承诺连接旧服务器。原 EF 失败用例保持原样并通过，没有改 DDL 掩盖兼容缺口。

ADO DataTypes 新增 DATETIMEOFFSET，校验 CLR/DbType/scale 和 measurement FIELD 边界，总目录为 13。原 DELETE 测试先证明默认 DTO 明确拒绝，再显式 DbType.DateTime2 执行原有有界删除并验证剩余行；没有放宽引擎类型守卫。

## 修复验证

- EF 全套初次 295/295；补充 ExecuteUpdate 的左值与 DTO 表达式回归后再次全套 295/295，零失败/跳过。三个新增用例覆盖原生/显式旧映射、参数和字面量、非零 offset/100ns、NULL、日期函数、SaveChanges、ExecuteUpdate、ExecuteDelete、重开和 DateTime 映射区分。
- 真实 Server 的 RemoteRelationTypeBoundaryTests 与 RemoteAdoEndToEndTests 两整类 80/80，零失败/跳过，包含四个原失败和完整原有断言。
- EF、Server Release 和 Data 显式 AOT/Trim 构建均零警告/错误。Data 分析不等同此修复的 NativeAOT 发布运行。
- 最终提交前仍须完整 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`；只有通过且最终树未变才提交，门禁/提交/推送回执保存在 `artifacts/issue-supervision-20261016/ci38862841705`。

新修复尚需正常推送后的精确 SHA CI。正式包和各 ORM 上游交付没有由本地测试推定完成。维护者另要求关闭已实现历史 Issue，16 项的明确关闭范围、评论及逐条状态回读见 `issue-implemented-closures-20261016.json`；关闭不等于发行。
