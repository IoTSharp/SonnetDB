# 配置操作租约的正式包探针

探针使用 .NET 10、NuGet.org 的 SonnetDB 5.2.0，以及 Docker Hub 的同源服务器。两个独立客户端进程通过标准输入协调，在同一个 Gateway 的真实 REST Serializable 会话内完成竞争。

## 本轮产物

2026-11-28（Asia/Shanghai）使用的源码提交为 `cbbe5ed2e64879ea6fb7650d1d7b3ff4eee2909d`。NuGet.org 包的 nuspec 与镜像 OCI `org.opencontainers.image.revision` 均指向该提交。镜像为 `linux/amd64`，manifest digest 为 `sha256:bc0c473ba905d60c7d4db4f0d48b53dccb0069f31884a3d1ccadb5a2cc5da449`，registry config digest 为 `sha256:0ce42b6d4672a35ff66fc4b20146544b38ef74531c36f3d6dd1dfbc8b15f37e6`。[正式发布](https://github.com/IoTSharp/SonnetDB/releases/tag/v5.2.0) · [镜像发布流水线](https://github.com/IoTSharp/SonnetDB/actions/runs/45139065091)

| NuGet.org 包 5.2.0 | 下载的 .nupkg SHA-256 | 所选 net10.0 DLL SHA-256 |
| --- | --- | --- |
| SonnetDB | `F5905DCE63933100005EFF08A7CCA2C88E6B9EAA4B68226D0C26FABED1AE56CA` | `8B4EB16919591857B845D01477DFD9DB0D15AE210D89C50BCD832AD07A0906E5` |
| SonnetDB.EntityFrameworkCore | `9970F2D4236D9D633BF42A7B670C28A8064E0B6E578E519046FB2A6251F2C572` | `153154647EB2DFB820D60DE8F031266E239C0E48E0637253D478B746159DBEF5` |
| SonnetDB.Core | `16EC49EF62689AC4EF40A01F13754A21E36D25AA50D4ABCBC7FA61CC61CBDB82` | `47003286512756B4447C4100A5E55D3EE4B6C9E516DB8BE88ECED7AFA188C93F` |
| SonnetDB.Protocol | `A588C3EE13C7186DDEE81892CBB8CD95644E71F678BA66B97A3AE77ED824A937` | `19C7333193791743DFC1A24068A6A1D804CEC9B2B2221F66995673F45F324BBF` |

恢复后的 Microsoft.EntityFrameworkCore 依赖为 10.0.15，主要 Microsoft.Extensions 依赖也为 10.0.15，Microsoft.Extensions.AI.Abstractions 与 VectorData.Abstractions 为 10.11.0，System.IO.Hashing 与 System.Numerics.Tensors 为 10.0.14。

## 复现

在本目录用 PowerShell 7 执行。新建隔离包目录，避免共享缓存里同版本号的候选包被选中。在 Windows 上使用较短的绝对路径，确保传递依赖文件路径可以被 MSBuild 读取。

```powershell
$leasePackageRoot = Join-Path (Resolve-Path ../../..) 'obj/issue425-official-nuget'
dotnet restore LeaseConsumer.csproj --source https://api.nuget.org/v3/index.json --packages $leasePackageRoot --no-http-cache --disable-parallel -m:1 /nr:false
dotnet build LeaseConsumer.csproj -c Release --no-restore -m:1 /nr:false /p:UseSharedCompilation=false
```

将正式镜像启动到任务专用的容器。使用容器名、标签及精确 ID 核对归属再重启或清理。镜像已存在时可省略 pull。

```powershell
$leaseImage = 'iotsharp/sonnetdb@sha256:bc0c473ba905d60c7d4db4f0d48b53dccb0069f31884a3d1ccadb5a2cc5da449'
$leaseSuffix = [guid]::NewGuid().ToString('N').Substring(0, 8)
$leaseContainer = "sndb-issue425-$leaseSuffix"
$leaseDatabase = "issue425_$leaseSuffix"
$leaseToken = "issue425-test-admin-$leaseSuffix"
docker pull --platform linux/amd64 $leaseImage
$leaseContainerId = docker run --detach --pull never --name $leaseContainer --label "codex.owner=$leaseContainer" --publish '127.0.0.1::5080' --cpus 1.5 --memory 3g --env "SONNETDB_DB=$leaseDatabase" --env "SONNETDB_SonnetDBServer__Tokens__$leaseToken=admin" $leaseImage
docker port $leaseContainerId 5080/tcp
```

读取本地映射端口。就绪后设置 `SONNETDB_LEASE_PROBE_CONNECTION`，值形如 `Host=127.0.0.1;Port=<mapped-port>;Database=<issue425_database>;Protocol=rest;Token=<test-token>;Timeout=10`。保留同一数据库并按顺序运行：

```powershell
dotnet bin/Release/net10.0/LeaseConsumer.dll setup
dotnet bin/Release/net10.0/LeaseConsumer.dll verify
dotnet bin/Release/net10.0/LeaseConsumer.dll race
```

重启测试容器，等待就绪并重新读取端口。更新连接串后运行 `dotnet bin/Release/net10.0/LeaseConsumer.dll readback`。探针每次调用的执行时限为 45 秒。

## 结果

- **PASS** · NuGet.org 隔离 restore、Release build。构建 0 警告、0 错误。运行时加载的 ADO.NET 与 EF Core DLL 的信息版本均为 `5.2.0+cbbe5ed2e64879ea6fb7650d1d7b3ff4eee2909d`。
- **PASS** · REST 真实事务中的 EF 条件更新覆盖 nullable GUID、UTC DateTime、布尔条件与多字段赋值。可取得租约的行影响数为 1，忙、已删除、缺失及错误 owner 为 0。多目标中途失败回滚了首目标租约和 receipt，两个目标按指定 owner 释放时影响数为 2，过期租约可以恢复。
- **PASS** · 两个独立进程各自在自己的 Serializable 会话观察到 `affected=1`，仅一笔提交成功，另一笔收到 `table_serialization_conflict`。独立连接查到唯一获胜的业务分配和 receipt，租约字段均为空。
- **PASS** · Docker 服务器进程重启后从新的客户端进程查询，获胜业务分配和 receipt 一致，租约字段仍为空。测试容器已按原 ID 删除并查证。正式镜像保留给后续消费者复验。
- **NOT_RUN（本探针）** · IoTSharp 控制器业务流、提交响应丢失、未知提交、取消与超时注入。这个探针覆盖配置租约所依赖的关系事务及简化业务模型，这些场景由对应的产品回归执行。
