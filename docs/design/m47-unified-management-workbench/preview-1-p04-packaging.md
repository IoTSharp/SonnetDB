# M47-P04：独立 Web / NativeAOT Server 组包

2026-10-11（Asia/Shanghai）。本片已完成候选09独立组包及下载后校验；产品仍 **NOT_READY**。候选01～08的来源与记录保留，当前继任版本 `4.5.0-preview.1.9`。首版目标限定 Windows 11 x64、Edge/Chrome，同源环回根部署及 `/admin/` 入口，R1 七模型专用读取继续延期。

## 来源与独立构建

[手动 CI 入口](../../../.github/workflows/workbench-preview-build.yml)只接受完整40位source SHA和新版本，在干净checkout按gitlink提交恢复两个子模块。权限为contents/actions read；只上传Actions构建产物，无tag/Release/NuGet/容器/安装器入口。[组包器](../../../eng/workbench-preview/build.ps1)拒绝dirty、预存ignored输出与旧web/dist，输出必须在checkout之外。源码清单保存SHA/tree/gitlinks和逐文件SHA256，构建后复核输入未变化。

Server按原专用`SonnetDbPublishAot=true`开关真实publish，warning按error处理；检查win-x64 PE32+与空CLR目录，再从原生文件ProductVersion回读发行号+完整SHA。Web锁定npm ci、类型检查和生产构建，编译固定preview-1/R1、版本和SHA；静态资源按本次fresh dist精确比对后进入同一publish库存。旧Release/WB运行目录不参与来源。

## 组包与证据

包内含NativeAOT Server、明确白名单原生依赖、同源wwwroot、环回appsettings模板、相邻`../data`布局、profile/预算、Start-Preview与启动说明、LICENSE/notices及实际NuGet/npm依赖版本/完整性哈希/可取得的原文。缺许可原文或元数据的依赖显式标记待P07审查，不伪造许可验收。Debug PDB、Development配置及无运行用途的ONNX import lib只进入排除收据。

精确库存检查拒绝额外或旧Web、managed apphost、Studio/CLI/SDK/VSIX/系统安装器、日志、凭据、数据、重复大小写路径及错误profile/SHA/hash。ZIP逐entry核路径、大小和SHA256；manifest包含实际版本、工具/依赖、构建参数、CI run/attempt、包内文件与ZIP的大小/hash和兼容预算。包内VerifyOnly再次核所有文件和Server/Web/profile身份，不启动或安装Server。

P04只生成ZIP、manifest与对这两项的SHA256SUMS。合同的第四附件`notes.md`归P06；当前公开四附件集合未闭合，不能上传公开Release，P06完成后须对发行说明补checksum并重核附件。

## 当前验证与剩余边界

本地21项组包合同通过（早期20项回执保持）；许可证采集与Job最小试运行通过。每条本地长命令在挂起根先入不可breakaway Windows Job后运行；记录PID/创建时间/完整命令/父进程，数量与墙钟有界，finally只清当前Job并核成员为空。原微试发现的StrictMode缺dev字段失败收据保留，修正后另建PASS，不覆盖旧证据。

实现提交`f84e3a6dcede6789fe088dbef4123d8d0c83ef19`已正常推送。候选05 CI run `38075483025` / attempt 1在组包入口失败：将npm位置假定为Node相邻目录，在Hosted runner没有找到CLI。尚未运行NativeAOT或产生ZIP，evidence上传也因输出未建立失败。原日志保存在`artifacts/m47-p04-20261011/ci-failure-05.stdout.log`；[候选05](preview-1-candidate-05.json)冻结失败，禁止复用版本。修正通过Get-Command按PATH顺序取首个Node/dotnet，并从已登记npm/Node的两个固定目录定位CLI，不递归扫描或安装软件；继任候选06在新的源码提交上构建。

候选06修正提交`a0917daf65a49b88613bf16aadf37de31ea72409`已推送，run `38076076784` / attempt1正在真实构建。兼容审查补齐明确的Windows 11/Edge/Chrome、同候选同版本、旧Server不接受、实际浏览器版本仍未知，以及C05包身份已实现/P05未验、D01组包已实现/P06上传器未验的状态；按不可变规则进入候选07，不覆盖06的来源或构建记录。20项合同增加了平台扩张和任意Server错配拒绝场景。

候选06实际NativeAOT publish已PASS、398秒/25个观察身份/cleanupComplete=true、日志包含Generating native code且无IL告警；随后精确库存拒绝Web SDK隐式复制的`appsettings.Container.json`，run整体FAIL/ZIP未产生。[不可变候选06](preview-1-candidate-06.json)与完整CI evidence保留，不把原生发布PASS升级成包PASS。候选07源码`9b8c8a7b8fba3ce3485bb982f9a7a951295364a5` / run `38076686113`已派发，记录独立保留。

继任候选08明确排除Container/Development配置、debug PDB/import lib及当前UseStaticFiles不消费的SDK静态资产索引；它们只写入排除收据，不混入Preview配置。新增完整publish库存证据，在任何后续拒绝之前保存。21项合同增加Container配置包内拒绝与选择阶段剔除；未放宽未知文件白名单。

候选08当前实物待构建，不以本地测试/analyzer代替实际publish。最终SHA、CI和资产回执将在本报告后继冻结记录中填写。证据目录`artifacts/m47-p04-20261011`。

P05首次安装、登录/导航/查询/导出/审批、错配/停止重开/回退；P06发行说明/上传器；P07现有完整门禁；P08实际公开发布均未执行。P03真实旅程只属其原输入与SHA，不继承成新候选完整门禁。旧FAIL、P02进程事故、P03旧cleanup=false和编辑器恢复未知保持。Workbench/WB98自动化继续PAUSED。

## 候选07/08实际失败与候选09

候选07 run 38076686113整体FAIL，真实NativeAOT publish PASS后拒绝Container配置；候选08 run 38077182154整体FAIL，真实NativeAOT publish PASS（360.50秒/23观察身份/cleanupComplete=true）后拒绝SDK生成的llms-full.txt.br，没有ZIP。两份不可变失败候选和CI evidence均保留，失败不升级为包通过。

候选09/4.5.0-preview.1.9只排除四个不被UseStaticFiles消费的llms压缩派生物，保留原txt及排除收据；增加PE普通/延迟导入闭包检查，未解析依赖fail closed。按VS注册元数据精确定位x64 CRT，按实际导入只复制四个所需VC runtime，记录来源、登记/文件/产品版本、SHA256及导入边；不依赖Hosted runner预装CRT证明自包含。Windows11内置系统/UCRT/API-set为明确OS前提。原生DLL动态装载不由PE表推断，P05实机旅程仍待验。Microsoft runtime不归MIT；原许可条款/可再分发资格显式待P07复核。

24项组包合同、实际NuGet DLL加登记CRT的本地闭包微验证通过；候选09实物待独立CI构建。命令上限16、总墙钟40分钟、单命令Job有界；无工具扫描/安装/共享进程终止。最终树restore/原CI format及精确staged whitespace必须通过再提交。NOT_READY，P05～P08未执行，自动化保持PAUSED。

## P04完成实物：候选09

2026-10-11（Asia/Shanghai），源码提交 `a44058adbe640d9ae04b93af388dccff6a17a961` 已正常推送，干净CI run `38078118031` / attempt1整体success。实际NativeAOT publish PASS，402.06秒/29观察身份/cleanupComplete=true；Generating native code且0 IL/AOT告警。Node22.23.3、.NET SDK10.0.401、登记MSVC14.51.36231（实际cl19.51.36260.0）；11项命令回执均PASS/cleanupComplete=true，24项组包合同PASS。版本4.5.0-preview.1.9、Native FileVersion4.5.0.9、ProductVersion带完整源码SHA，profile R1与两项gitlink固定。

从该run下载ZIP、manifest、SHA256SUMS到`artifacts/m47-p04-20261011/delivery-09`，三项精确资产集合；ZIP46,617,728 bytes，SHA256 `f943c05f5015b2ac2ae56596d8df6839dcd09bb58a88e0effe0326d917b5a6f6`。外部checksum、279个ZIP entry路径/大小/逐文件SHA、PE与原生版本、编译JS中的版本/源码/profile全部独立PASS；新目录解压后Start-Preview -VerifyOnly PASS，没有启动或安装Server。另用本机登记MSVC dumpbin核10个原生文件的普通/延迟导入，与包内PE清单逐边一致；四个CRT实际文件/产品版本回读PASS。

[不可变候选09](preview-1-candidate-09.json)冻结三资产的hash/size、完整profile/预算/平台/组件、来源tree/gitlinks、实际工具、11项CI回执、原生导入/依赖及下载后校验。完整逐源码输入/包文件和243条依赖/许可记录在manifest与包内，16条缺原文/仅元数据/Microsoft条款待P07，不能称许可完整验收。Git source、CI artifact、独立下载回执各自保存。三资产留本地及30天Actions保留，不是公开Release或长期分发；P06需保存正式notes、补四附件校验及上传器。

**M47-P04仅组包完成；产品NOT_READY。** 下一片P05必须使用此包，实验首次设置/登录/导航/查询/导出/审批、错配拒绝、停止重开与回退。P06发行说明/四附件上传合同、P07原完整门禁和许可复核、P08授权公开发布仍待验。P03旧局部证据不继承为新包整体验收；七模型专用读取继续延期，05～08原CI失败、P02事故、旧cleanup=false/UNKNOWN和PAUSED自动化保持。
