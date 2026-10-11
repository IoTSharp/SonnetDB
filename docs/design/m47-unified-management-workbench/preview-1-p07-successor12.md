# M47-P07 继任候选12源码准备

候选11的 P04 run 38143254625 attempt1 已成功；290 项 ZIP 库存、NativeAOT 身份及全部字节独立校验通过。ZIP 为 46791093 bytes，SHA256 `05cc9809299f229ba5deb9b0252516c8bb8265a4c78ce71ab3ed0d27fc1e4894`。243 项依赖库存的原通知、vdirs 未发出证明、四项微软运行库来源和 Community 用户报告资格输入已审查；不把资格报告写成微软权益证明。P05 尚未对候选11运行，原失败与产物不覆盖。

原发布预检 38143275378 因 API 兼容性失败。项目在 4.0 发布后仍选 3.0.1 基线；先对官方 4.0.0 包诊断，确认仍缺旧构造器、解构器、表工厂和已废弃的 WalTruncator。当前恢复这些入口，旧入口转发时仅使用现有新增字段默认值；WalTruncator 恢复 v4.0.0 原语义与 Obsolete 标记。保持 EnablePackageValidation、原十二工作流、原发布 policy/live verifier、AOT 警告门禁，包基线切换为已发布 4.0.0。

已取得原兼容性打包 PASS 与 86 项发布合同 PASS；测试以保存的 TRX 计数为准。修正 P07 收集器访问实际 manifest.build.dependencyInventory 的路径；数量绑定继续保留。最终完整 restore/原 CI format 和准确提交回读以本轮收据为准。源码 profile 推进 4.5.0-preview.1.12；须在新源码 SHA 上一次构建、取齐全部原门禁、对新实物完成 P05/P06/P07，然后才能获精确 P08 授权并按 maikebing 审批保护发布。尚未创建 tag/Release 或运行公开首跑。

最终定向 TRX：7/7 PASS、0 FAIL，覆盖旧对象/建表/查询/估算/表工厂及 WAL 保留/删除归档选项。编译为 0 warning/0 error；仅本机 API 与合成 WAL 文件合同，不替代新候选实物验收。
