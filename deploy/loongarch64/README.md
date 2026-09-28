# LoongArch64 Docker image

Build this image on a LoongArch64 host after publishing SonnetDB with a matching
native .NET 10 SDK. The official Microsoft .NET images do not currently provide
`linux/loong64`, so the image takes the .NET host and shared runtimes from the
verified native SDK installation. Do not copy its SDK or packs into the runtime
image.

The first verified device is a Loongson-3A6000 running Loongnix 25. It uses
`chenguohui/dotnet-loongarch` SDK 10.0.112 (ABI2.0, downloaded archive
SHA-256 `ca66b50dda347cacd0adbe6a091abbe4a9de6fb3de75f1ed20a21f68576a9fb7`)
with .NET and ASP.NET Core runtimes 10.0.12. Verify the SDK archive before
installing it and keep the runtime version matched to the published app.

The `dotnet` and `published` BuildKit contexts must point to the native SDK root
and the `dotnet publish` output. Run from the repository root on the LoongArch64
host:

```bash
docker build --platform linux/loong64 \
  --build-context dotnet=/opt/sonnetdb/dotnet/10.0.112 \
  --build-context published=/opt/sonnetdb/builds/827d6cff/publish \
  -f deploy/loongarch64/Dockerfile \
  -t sonnetdb:827d6cff-loong64 .
```

Use the SDK and publication paths for the commit being deployed. The default
Debian base is pinned to the `linux/loong64` manifest digest. On a host with the
base image already loaded under another tag, pass
`--build-arg LOONG_BASE_IMAGE=<local-tag>`. If the package mirror requires a
proxy, pass `--build-arg http_proxy=<proxy-url>` and
`--build-arg https_proxy=<proxy-url>` to `docker build`.

Keep the initial HTTP port bound to loopback until an administrator has been
created and authentication is verified. Preserve `/data` across container
recreation with a named volume or bind mount. The Copilot chat provider requires
separate configuration; an unset endpoint can make `/healthz/ready` report
`Degraded` while `/healthz/live` remains healthy.
The container configuration leaves semantic image search disabled. The bundled
ONNX Runtime, USearch, and Skia native assets have not been validated for
`linux/loong64`, so this image does not establish support for those paths.

For commit `827d6cff`, the native build used a disposable source copy with
`Microsoft.CodeAnalysis.CSharp` pinned to 5.0.0 to match the community SDK's
Roslyn compiler. The repository package versions were not changed. Native
`dotnet test` compiled the core test assembly, but `Microsoft.NET.Test.Sdk`
18.10.1 stopped before test discovery because VSTest does not recognize the
LoongArch64 process architecture. Container API and persistence checks were
performed directly on the device; this does not replace the missing native unit
test run.
