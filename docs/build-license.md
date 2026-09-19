# 构建不再需要图片库许可证

SonnetDB 已移除 SixLabors.ImageSharp 及其构建许可任务。Server、测试、图片示例、Docker 和 Parity 均不再需要许可证账户、`SixLaborsLicenseKey`、`SixLaborsLicenseFile` 或 BuildKit 许可 secret；外部 fork 可以使用相同构建命令。

```powershell
dotnet restore SonnetDB.slnx
dotnet build SonnetDB.slnx -c Release --no-restore
pwsh -File eng/verify-dependency-licenses.ps1
```

```bash
docker build -f src/SonnetDB/Dockerfile -t iotsharp/sonnetdb .
```

图片格式、原生资产、第三方声明和已有图片索引的升级步骤见[图片处理与迁移](image-codecs.md)。Git 和 Docker 的历史许可证忽略规则继续保留，以防旧本地密钥误入仓库；它们不再是构建输入。
