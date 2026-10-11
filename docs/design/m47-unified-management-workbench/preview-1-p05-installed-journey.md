# M47-P05：实际 NativeAOT 包的首次使用与重开

2026-10-11（Asia/Shanghai）。当前正在验收，产品 **NOT_READY**，P06～P08未执行。R1范围和30秒/1000行/4 MiB/64 KiB预算保持，七个模型专用读取继续延期。

候选09三资产、279项库存、ZIP大小/hash与原始P04记录保持不可变。独立解压预检发现包内profile的candidate字段为candidate-08，而实际版本为4.5.0-preview.1.9；原启动器未检查该字段。当前源码也未进行浏览器与运行中Server版本协商，旧缓存的Preview Web不能据此拒绝错配。原包的实际首次设置/管理员登录已通过，但本片工具的第一轮夹具准备错误导致该轮FAIL；原结果保存在artifacts/m47-p05-20261011/c09，不当作产品缺陷或完整P05通过。

修复加入candidate/version一致性检查、Server source-generated包身份读取和匿名no-store身份端点。编译Web在挂载登录/数据视图前进行5秒/4096字节身份核验；Preview请求携带同版本与完整源码SHA，包内Server拒绝错配请求。失败清除认证、草稿与历史，不自动重放。完整模式没有Preview包时保留原API行为。

继任候选10/4.5.0-preview.1.10将在新的已提交SHA上干净CI重包，不覆盖候选09；本段不冒称实物已完成。验收工具使用实际安装的Chrome/Edge、任务独占浏览器目录和内核Job；Server独占隐藏控制台收到Ctrl+C后必须实际退出0，再单独记录Job清理。正常停止失败不会以强制终止冒称PASS。

后续收据须分列真实UI/Server、操作员夹具API、故障注入和隔离数据副本回退；回退只针对同候选数据副本，不让旧Server打开新数据。原FAIL/UNKNOWN、安全事故、cleanup=false及PAUSED自动化保持。
