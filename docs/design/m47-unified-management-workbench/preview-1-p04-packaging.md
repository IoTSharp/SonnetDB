# M47-P04：独立 Web / NativeAOT Server 组包

2026-10-11（Asia/Shanghai）。本片实现独立组包器；产品仍 **NOT_READY**。候选01～05保留，当前继任版本 `4.5.0-preview.1.6`。本次构建仅支持 Windows x64，同源环回根部署及 `/admin/` 入口，R1 七模型专用读取继续延期。

## 来源与独立构建

[手动 CI 入口](../../../.github/workflows/workbench-preview-build.yml)只接受完整40位source SHA和新版本，在干净checkout按gitlink提交恢复两个子模块。权限只有contents read；只上传Actions构建产物，无tag/Release/NuGet/容器/安装器入口。[组包器](../../../eng/workbench-preview/build.ps1)拒绝dirty、预存ignored输出与旧web/dist，输出必须在checkout之外。源码清单保存SHA/tree/gitlinks和逐文件SHA256，构建后复核输入未变化。

Server按原专用`SonnetDbPublishAot=true`开关真实publish，warning按error处理；检查win-x64 PE32+与空CLR目录，再从原生文件ProductVersion回读发行号+完整SHA。Web锁定npm ci、类型检查和生产构建，编译固定preview-1/R1、版本和SHA；静态资源按本次fresh dist精确比对后进入同一publish库存。旧Release/WB运行目录不参与来源。

## 组包与证据

包内含NativeAOT Server、明确白名单原生依赖、同源wwwroot、环回appsettings模板、相邻`../data`布局、profile/预算、Start-Preview与启动说明、LICENSE/notices及实际NuGet/npm依赖版本/完整性哈希/可取得的原文。缺许可原文或元数据的依赖显式标记待P07审查，不伪造许可验收。Debug PDB、Development配置及无运行用途的ONNX import lib只进入排除收据。

精确库存检查拒绝额外或旧Web、managed apphost、Studio/CLI/SDK/VSIX/系统安装器、日志、凭据、数据、重复大小写路径及错误profile/SHA/hash。ZIP逐entry核路径、大小和SHA256；manifest包含实际版本、工具/依赖、构建参数、CI run/attempt、包内文件与ZIP的大小/hash和兼容预算。包内VerifyOnly再次核所有文件和Server/Web/profile身份，不启动或安装Server。

P04只生成ZIP、manifest与对这两项的SHA256SUMS。合同的第四附件`notes.md`归P06；当前公开四附件集合未闭合，不能上传公开Release，P06完成后须对发行说明补checksum并重核附件。

## 当前验证与剩余边界

本地20项组包合同通过；许可证采集与Job最小试运行通过。每条本地长命令在挂起根先入不可breakaway Windows Job后运行；记录PID/创建时间/完整命令/父进程，数量与墙钟有界，finally只清当前Job并核成员为空。原微试发现的StrictMode缺dev字段失败收据保留，修正后另建PASS，不覆盖旧证据。

实现提交`f84e3a6dcede6789fe088dbef4123d8d0c83ef19`已正常推送。候选05 CI run `38075483025` / attempt 1在组包入口失败：将npm位置假定为Node相邻目录，在Hosted runner没有找到CLI。尚未运行NativeAOT或产生ZIP，evidence上传也因输出未建立失败。原日志保存在`artifacts/m47-p04-20261011/ci-failure-05.stdout.log`；[候选05](preview-1-candidate-05.json)冻结失败，禁止复用版本。修正通过Get-Command按PATH顺序取首个Node/dotnet，并从已登记npm/Node的两个固定目录定位CLI，不递归扫描或安装软件；继任候选06在新的源码提交上构建。

候选06当前实物待构建，不以本地测试/analyzer代替实际publish。最终SHA、CI和资产回执将在本报告后继冻结记录中填写。证据目录`artifacts/m47-p04-20261011`。

P05首次安装、登录/导航/查询/导出/审批、错配/停止重开/回退；P06发行说明/上传器；P07现有完整门禁；P08实际公开发布均未执行。P03真实旅程只属其原输入与SHA，不继承成新候选完整门禁。旧FAIL、P02进程事故、P03旧cleanup=false和编辑器恢复未知保持。Workbench/WB98自动化继续PAUSED。
