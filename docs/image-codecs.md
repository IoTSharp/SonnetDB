# 图片处理与 ImageSharp 迁移

本变更归属 M35 #301 的既有图片检索与缩略图维护。Server 使用 SkiaSharp 4.152.1 和 TiffLibrary 0.6.65，后者在 .NET 10 下仅传递引入 JpegLibrary 0.4.32。Linux 使用同版本 `SkiaSharp.NativeAssets.Linux.NoDependencies`，Windows/macOS 原生资产由 SkiaSharp 包传递提供。Core 不引用图片库。

构建和运行不需要供应商账户、商业激活或许可密钥。依赖自己的版权和分发条款仍需保留，不能将整个原生二进制标为仅 MIT。固定版本、完整上游声明、历史 Apache-2.0 来源与 Adobe DNG SDK 条款见[图片组件声明](../licenses/image-codecs/README.md)。声明通过 MSBuild 自动进入 Server、Studio 内嵌 Server、Docker 与图片样例的发布输出，不要求部署者额外申请或配置文件。

## 格式与处理合同

明确支持 PNG/APNG、JPEG、WebP、GIF、BMP、ICO、TIFF 七类；固定 MIME 别名表由 Server 维护，库升级不会自动扩大接口能力。`image/tiff-fx` 为 TIFF 别名，不表示支持所有可选 TIFF 编码。动态图像取第一帧，TIFF 取第一页；EXIF/TIFF 的八种方向均在缩放前处理。ICO 采用解码器选择的图标表示。只接受七类实际编码，其他格式即使被伪标成 `image/png` 也不会进入本地模型。

本次明确停止声明和处理 PBM/PGM/PPM、TGA、EXR、QOI、CUR、ANI；也不再把 `text/ico` 当文本处理。原始对象存储仍可保存任意字节，但这些格式不能用于内置图片 embedding 或缩略图。PDF/音视频等不属于此组件职责。

两条图片路径都在完整像素分配前检查最多 100,000,000 像素。模型输入先归向、转 RGBA8，再忽略 alpha 并双线性 Stretch 到配置尺寸（默认 224×224），输出 RGB NCHW，`pixel / 127.5 - 1` 归一化。Skia 可对支持的色彩配置转为 sRGB；托管 TIFF 输出其 RGBA8 转换结果，不承诺任意 TIFF ICC/HDR 保真。

缩略图按配置上界等比缩小、不放大小图，使用 Mitchell cubic 采样并编码为单帧 WebP；它与原 ImageSharp Lanczos3 的像素和编码字节不保证一致。原始对象、既有 WebP 和数据库二进制格式不变。

损坏、未知和超限图片归为永久输入失败；HTTP/审计仍分别返回输入错误和 `semantic_invalid_input`，后台不会对它们反复重试。对象存储的 I/O 故障不转换成图片输入错误。取消在阶段间和托管像素循环中检查，TIFF 解码传入取消令牌；Skia 原生解码/缩放/编码没有硬中断接口，只能在调用前后观察取消，不能把时间预算当作原生 CPU 的硬截止时间。

## 已有索引升级

本地 SigLIP2 provider 自动将有效 profile 设置为 `<配置 Profile>:skia-rgba-v1`，例如 `siglip2-base-patch16-224:skia-rgba-v1`。即使管理员保留原配置名，新写入和查询也会使用独立集合，旧向量不会混入。状态 API、审计和持久摄取任务记录实际 profile；恢复升级前的未完成摄取任务时，也会记录本次有效 profile。

升级后，新 profile 初始没有旧索引记录。对对象桶执行既有语义 Backfill；直接通过 `/images/{id}` 摄取的图片需重新提交原图。旧 profile 原始对象和向量不自动删除，管理者可以保留它们供回滚。自定义 provider 的 profile 不自动添加这个本地预处理后缀。

旧缩略图仍按对象版本复用，换库不会自动重做历史 WebP。缩略图不包含 embedding，因此可以继续读取；需要更新视觉效果时应显式重建派生缩略图。

## 验证与持续门禁

`eng/verify-dependency-licenses.ps1` 在 restore 后检查实际 `project.assets.json` 的依赖节点，拒绝直接、传递和构建期 `SixLabors.*` 包；历史版权声明不属于此禁入规则。此脚本是已知禁止依赖的门禁，不是任意新增依赖的自动法律审核。

`ImageProcessingTests` 使用实际图片和独立 TIFF/PNG/GIF/BMP/ICO 协议 fixture，覆盖七类格式、方向、透明通道、NCHW、WebP、限额、坏输入和取消。调度回归覆盖只有语义摄取时的永久失败，以及旧任务恢复后的 profile。

```powershell
dotnet publish eng/tools/image-codec-smoke/ImageCodecSmoke.csproj -c Release -r win-x64 -p:PublishAot=true /warnaserror
./eng/tools/image-codec-smoke/bin/Release/net10.0/win-x64/publish/ImageCodecSmoke.exe
dotnet publish src/SonnetDB/SonnetDB.csproj -c Release -r win-x64 -p:SonnetDbPublishAot=true /warnaserror
```

Linux 使用对应 `linux-x64` / `linux-arm64` RID 和无扩展名的 `ImageCodecSmoke`。CI 对 `win-x64`、`linux-x64`、`linux-arm64` 发布完整 Server 和链接同一生产处理器的探针，并实际运行后者；仅 restore/build 通过不算原生库加载成功。`NoDependencies` 不表示没有原生库或第三方声明，只表示不要求另外安装 Fontconfig 等系统组件。

图片和 AOT 探针证明编解码/部署合同，不证明真实 SigLIP2 模型 Recall@K、排序、容量或 Parity/nightly 已通过；这些证据仍按各自门禁记录。
