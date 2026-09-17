# ImageSharp 构建许可证

SonnetDB Server 引用 ImageSharp 4.1.1，编译 Server、引用 Server 的测试和发布包时需要有效的 Six Labors 构建许可证。SonnetDB 的 MIT 开源项目符合其 [Split License](https://github.com/SixLabors/ImageSharp/blob/main/LICENSE) 的开源使用条款，可在[官方社区许可入口](https://licensing.sixlabors.com/)免费申请；是否批准及有效期以官方签发结果为准。

## 本地编译

将官方 `sixlabors.lic` 保存在仓库外，通过绝对路径环境变量传给 MSBuild。PowerShell 7 示例：

```powershell
$env:SixLaborsLicenseFile = 'C:\licenses\sixlabors.lic'
dotnet build SonnetDB.slnx -c Release
```

Linux/macOS 示例：

```bash
export SixLaborsLicenseFile=/absolute/path/sixlabors.lic
dotnet build SonnetDB.slnx -c Release
```

许可证文件已被 Git 和 Docker 构建上下文忽略。不要提交许可证、将密钥写入项目文件，或跳过包内校验；缺少有效许可证时 Release 构建应失败。

## GitHub Actions

维护者将官方签发的一行密钥保存为仓库 Actions secret `SIXLABORS_LICENSE_KEY`。引用 Server 的构建 job 通过环境变量 `SixLaborsLicenseKey` 读取该 secret，无需创建仓库内的许可证文件。Dependabot 的 PR 构建还需要配置同名 **Dependabot repository secret**。

外部 fork PR 无法读取仓库 secret，涉及 Server 的 Release 构建会缺少许可证。维护者应先审查变更，再在受信任分支验证；不使用 `pull_request_target` 执行未经审查的 PR 代码，也不向 fork 提供仓库许可证。只依赖 Core、Data 或已发布工具的构建不需要此许可。

## Docker 与 Parity

Dockerfile 仅在 `dotnet publish` 步骤通过 BuildKit secret 挂载许可证，使用 `SixLaborsLicenseFile` 读取；密钥不会写入 Dockerfile 的 `ARG` / `ENV`、构建层或运行镜像。手工构建示例：

```bash
docker build --secret id=sixlabors-license,src=/absolute/path/sixlabors.lic \
  -f src/SonnetDB/Dockerfile -t iotsharp/sonnetdb .
```

仓库根目录的 `docker compose` 构建读取上述 `SixLaborsLicenseFile`；CI 的 Docker 发布与 Parity Compose 则将 `SixLaborsLicenseKey` 作为 BuildKit secret 来源。Parity 的 harness 测试容器也通过只读 secret 文件编译引用 Server 的测试。本地运行 Parity 前，可在当前进程读取自己的许可证文件：

```powershell
$env:SixLaborsLicenseKey = (Get-Content -LiteralPath $env:SixLaborsLicenseFile -Raw).Trim()
docker compose -f tests/SonnetDB.Parity/docker-compose.parity.yml --profile light build sonnetdb
```

许可证到期后更新本地文件、Actions secret 和 Dependabot secret；不要把密钥或包含环境变量的诊断转储上传为构建产物。
