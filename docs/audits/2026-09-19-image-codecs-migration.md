# 图片库迁移验证记录（2026-09-19）

范围：M35 #301 图片检索/缩略图依赖维护。用户确认格式收敛为 PNG/JPEG/WebP/GIF/BMP/ICO/TIFF；替换 SixLabors.ImageSharp 和许可构建链，保持 NativeAOT。此记录对应本地工作区实现，不表示已经发布或远程 CI 已执行。

## 固定依赖与许可

- SkiaSharp、Linux.NoDependencies：4.152.1。
- TiffLibrary：0.6.65；.NET 10 传递依赖 JpegLibrary 0.4.32。
- 完整解决方案 restore 后，40 份已解析依赖图通过禁入检查，实际库节点没有 `SixLabors.*`；Core 没有新增图片库。
- 无许可证账户/激活密钥/构建 secret。原生资产不等于仅有 MIT 条款，已保留完整声明并核实实际构建边界，见 [组件记录](../../licenses/image-codecs/README.md)。历史 Apache-2.0 Six Labors 版权声明保留，不构成现代 Split License 包依赖。

## 实测结果

| 验证 | 结果与边界 |
| --- | --- |
| `dotnet build SonnetDB.slnx -c Release --no-restore /warnaserror` | 通过，0 警告、0 错误；主机 SDK 10.0.400。 |
| 图片/embedding/调度/图片端点/对象端点定向测试 | 94/94 通过、0 跳过。其中新增 39 项图片用例、3 项调度回归。 |
| 完整 Server `win-x64` NativeAOT | 发布通过，0 编译/IL/AOT 警告；发布进程未设置 Six Labors 许可变量。 |
| 完整 Server NativeAOT HTTP | 实际启动发布 EXE；创建数据库和对象桶；关闭 embedding、只开缩略图。PNG 64×32 → WebP 32×16；归向 TIFF 2×3 → WebP 3×2；均第 1 次处理成功。进程从发布目录加载 Skia DLL，没有加载 CoreCLR。 |
| 正式图片探针 `win-x64` NativeAOT | 发布并运行通过；直接链接生产 `SemanticImageCodec`。 |
| 正式图片探针 `linux-x64` NativeAOT | 临时 Docker 中发布并运行通过，0 编译/IL/AOT 警告；SDK 10.0.202，声明文件与仓库逐文件 hash 一致，无 Fontconfig 运行依赖。 |
| 正式图片探针 `linux-arm64` NativeAOT | AMD64 SDK + aarch64 工具链交叉发布通过，0 编译/IL/AOT 警告；在 Docker ARM64/QEMU 下运行通过，**不是 ARM 实体硬件证据**。 |
| Docker/Compose | 根 Compose 与 Parity light/full 配置解析通过；许可 secret 已移除。未将这项配置检查视为完整 Docker 镜像、Parity 或 nightly 通过。 |
| 格式检查 | 修改的生产 C# whitespace 检查与 `git diff --check` 通过。 |

图片探针覆盖七类格式、TIFF/JPEG 方向 1–8、RGB NCHW 预处理、WebP 尺寸上限和非法/CUR/超像素拒绝。单元测试另外验证精确归向像素、alpha=0/128/255、颜色平面、首帧、预取消、坏输入审计、后台永久失败和旧 profile 任务恢复。早期选型探针还实际验证 TIFF 未压缩/Deflate；不据此宣称所有可选 TIFF 编码均已验证。

本机默认 HTTP 代理会把 `x-amz-meta-owner` 后缀改成 `Owner`，导致三个既有 metadata 精确匹配测试失败。同一 Server 使用/禁用代理对照证实该原因；测试进程设置 `NO_PROXY=localhost,127.0.0.1,::1` 后全部通过，没有修改对象存储逻辑规避测试。

## 重现与本地产物

```powershell
dotnet restore SonnetDB.slnx
dotnet build SonnetDB.slnx -c Release --no-restore /warnaserror
pwsh -File eng/verify-dependency-licenses.ps1
$env:NO_PROXY = 'localhost,127.0.0.1,::1'
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~ImageProcessingTests|FullyQualifiedName~SemanticEmbeddingTests|FullyQualifiedName~SemanticSearchEndpointTests|FullyQualifiedName~ObjectProcessingSchedulerTests|FullyQualifiedName~ObjectStorageEndpointTests'
dotnet publish src/SonnetDB/SonnetDB.csproj -c Release -r win-x64 -p:SonnetDbPublishAot=true /warnaserror
dotnet publish eng/tools/image-codec-smoke/ImageCodecSmoke.csproj -c Release -r win-x64 -p:PublishAot=true /warnaserror
./eng/tools/image-codec-smoke/bin/Release/net10.0/win-x64/publish/ImageCodecSmoke.exe
```

本地日志存于忽略的 `artifacts/image-migration/`：

- `test-results/image-migration.trx`：94 项回归。
- `server-native-http-20260919-205950.json`：完整原生 Server HTTP 结果、发布 EXE hash、实际 native 路径与声明清单。
- `linux-probe/EVIDENCE.md`、`source-hashes.json`、`run-linux-x64.log`、`run-linux-arm64.log`：正式生产处理器的 Linux 发布/运行记录。

Linux x64 SDK 镜像 digest：`sha256:adc02be8b87957d07208a4a3e51775935b33bad3317de8c45b1e67357b4c073b`；ARM64 runtime-deps digest：`sha256:23257ea51d7c12e0d5aabecaffe24b4eccacff63a2e669ea9408ac29790d4ce1`。完整 Server 的 Windows 发布 EXE SHA-256：`33c309ff5dcc0843bae9cc2f0843fc38a7802a35caef4c9050e71482941a9f1f`。

## 保留边界

CI 已为三个正式 RID 接入 NativeAOT 图片运行门禁；本轮未执行远程 CI、完整 Linux Server 的 AOT HTTP 验证、实机安装或 ARM 实体硬件验收。未进行真实 SigLIP2 模型质量/性能对拍，也未重跑完整 Parity/light/full/nightly。新旧预处理可能产生不同向量，使用 `:skia-rgba-v1` 隔离并重新摄取；具体升级步骤见[图片处理说明](../image-codecs.md)。数据库文件格式没有变更。
