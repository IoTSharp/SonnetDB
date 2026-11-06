# GH-Issue #395：固定 OwnsMany 元素的 JSON 批量更新

日期：2026-11-06（Asia/Shanghai）。源码基线为 `2a2f77c762badcad2a16dc2cad2c8e9abd09eebe`。本切片接续固定对象标量路径，不重做已有 `SaveChanges` 集合生命周期，也不表示旧公开 5.0.0 已具备新行为。

## 变更与合同

`ExecuteUpdate` / `ExecuteUpdateAsync` 可以更新根 JSON `OwnsMany` 数组和 `OwnsOne` 内集合中的固定元素标量属性，包含嵌套集合。索引必须是表达式中的非负 Int32 常量，且 EF 最终目标必须是直接 `JsonScalarExpression`。已验证列表索引器和适用的 `ElementAt`；生成条件目标的嵌套 `ElementAtOrDefault` 继续拒绝。

仅在 EF 已标明 `PathSegment.ArrayIndex` 时写出 `[n]`；属性名仍按 `PropertyName` 精确引用，因此名称 `0`、引号、点和 Unicode 不会被误认成数组下标。路径深度仍最多 64 段，整体最多 1024 字符。完整路径由既有运行时 DbParameter 传入，值继续沿用既有参数与类型映射。没有新增 JSON 序列化入口、Core 修改、数据/协议格式、依赖或公开方法签名。

同列多个 setter 按声明顺序合成 `json_set_value`；重复目标以后一次为准。普通 JSON 标量右值读取更新前的行，允许与普通关系列在同一 SQL 中更新。缺失数组元素、空数组或错误父级使整条更新失败，不扩容、不追加默认对象，也不先读取实体再写回。显式事务、回滚和调用者显式 Token 谓词/影响行数合同沿用。

## 拒绝与未完成范围

动态/闭包/逐行下标、负数、条件元素目标、转换器、Guid、日期、二进制、对象/数组子树与集合元素右值保持未支持。原始标量集合（例如 `List<string>`）的元素目标在当前 EF 翻译入口仍以 `InvalidOperationException` / `could not be translated` 拒绝；其它已列出的形状以 provider `NotSupportedException` 拒绝。负向回归均核对未发送任何 SQL 命令。

此处没有开放集合元素 LINQ 投影，既有 #394 跟踪查询与 EF 10 ordinal 重排限制不变。EF Provider 自身仍为独立的非 NativeAOT 边界；没有通过 suppression 或反射 JSON 回退弱化其它组件 AOT 合同。

`#395` 继续 OPEN：对象/数组子树、其余类型/转换器、完整集合表达式及公开新 Provider 包/真实应用验收尚未完成。公开发布、NuGet-only 消费者、Linux 精确 head CI 和生产运行须按各自实际结果单列，不能用本地源码回归替代。

## 验证与证据

修复前新增真实旅程在 embedded、REST、精确 HTTP/2 Frame 上 **3/3 失败**，均在原 `SetterShapeValidator` 写前拒绝。最小放行后原旅程 **3/3 通过**。扩大范围后对 primitive 集合和条件元素目标的错误支持假设有失败记录，最终保留明确拒绝合同，没有删除原有业务断言。

最终 ExecuteUpdate 定向 **50/50 PASS，0 FAIL，0 SKIP**，TRX 为 `artifacts/issue395-20261106/issue395-targeted-final-v4.trx`。其中新增 28 项：三协议元素更新与事务/重开、三协议根数组、三协议条件目标拒绝、三协议与 OwnsOne/Complex 组合的 primitive 元素拒绝、六项缺失/错误父级跨两行原子拒绝、七项变量/负数/转换器/Guid/数组右值拒绝。

成功旅程核对单条实际 UPDATE、路径/值参数、中文/引号、数字属性名 `0`、未知成员和大整数/数值原文保留、同列顺序、原行右值、Token 0/1 影响行、事务外可见性、回滚、关闭重开及实际协议。现有固定标量 20 项与其它两项既有 ExecuteUpdate 用例仍包含在上述 50 项中，未将组间重叠相加。

完整 EF **388/388 PASS，0 FAIL，0 SKIP**，包含原有 360 项；Windows / .NET SDK 10.0.403 / Release，完整命令墙钟约 350 秒，单测试无进展上限两分钟，TRX 为 `artifacts/issue395-20261106/issue395-full-ef.trx`。最终代码未再修改后，完整 `dotnet restore SonnetDB.slnx` 退出 0（7.2 秒），规定 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 退出 0（191.0 秒），保留 workspace 加载警告。native 回执为 `restore-native.json` / `format-native.json`，各自进程树已回收；源码回归不代替精确 PR head 的远端 CI。原始失败与通过 TRX 保留于本独立工作树 `artifacts/issue395-20261106/`。

必要的两个子模块只初始化仓库固定 commit（CoAP `797a8599`、MQTT routing `9e0c8202`），gitdir 位于本 worktree 独占 `.git/worktrees/SonnetDB7/modules`，未修改子模块指针或共享 `core.worktree`。所有有界 native 执行结束均记录进程树回收结果；夹具沿用 owner 标记并只删除自己的临时目录。隔离工作树和审计证据保留供复核。
